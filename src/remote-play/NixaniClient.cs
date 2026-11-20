using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Ovrix.Engine;

public sealed record RemoteHost(string Name, string Address, uint Target, byte State);
public sealed record RemoteDevice(string Name, byte[] Uid)
{
    public override string ToString() => Name;
}
public sealed record Registration(byte[] Account, byte[] RegistrationKey, byte[] RemoteKey, string Nickname);
public sealed record RemoteStats(ulong Received, ulong Lost, double RoundTripMs, ulong FecFailures);
public sealed record NixaniPacket(ushort Type, byte[] Payload)
{
    public BinaryReader Reader() => new(new MemoryStream(Payload, false), Encoding.UTF8);
}

/// <summary>Client for Nixani's documented MIT-licensed protocol v2 over private process pipes.</summary>
public sealed class NixaniClient : IAsyncDisposable
{
    private readonly Process process;
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Task<string> stderr;
    private const int MaximumPacket = 16 * 1024 * 1024;

    private NixaniClient(string path)
    {
        process = ProcessTools.Start(path, [], true);
        stderr = process.StandardError.ReadToEndAsync();
    }

    public static async Task<NixaniClient> Connect(string path, CancellationToken token)
    {
        var client = new NixaniClient(ProcessTools.Find(path, "nixani-engine"));
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(token);
        handshake.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            await client.Send(1, w => w.Write(2u), handshake.Token);
            var packet = await client.WaitFor(101, handshake.Token);
            using var reply = packet.Reader();
            if (reply.ReadUInt32() != 2) throw new IOException("Nixani protocol version 2 is required.");
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public static byte[] Encode(ushort type, Action<BinaryWriter>? write)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, true);
        writer.Write(0u);
        writer.Write(type);
        write?.Invoke(writer);
        if (bytes.Length - 4 > MaximumPacket) throw new InvalidDataException("Nixani packet too large.");
        bytes.Position = 0;
        writer.Write((uint)bytes.Length - 4);
        return bytes.ToArray();
    }

    public static async Task<NixaniPacket> Read(Stream stream, CancellationToken token)
    {
        var header = new byte[6];
        await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is < 2 or > MaximumPacket) throw new InvalidDataException("Invalid Nixani packet size.");
        var payload = new byte[length - 2];
        await stream.ReadExactlyAsync(payload, token);
        return new(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4)), payload);
    }

    public async Task Send(ushort type, Action<BinaryWriter>? write, CancellationToken token)
    {
        var message = Encode(type, write);
        await sendLock.WaitAsync(token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(message, token);
            await process.StandardInput.BaseStream.FlushAsync(token);
        }
        finally { sendLock.Release(); }
    }

    private Task<NixaniPacket> Read(CancellationToken token) => Read(process.StandardOutput.BaseStream, token);

    private async Task<NixaniPacket> WaitFor(ushort type, CancellationToken token)
    {
        while (true)
        {
            var packet = await Read(token);
            if (packet.Type == type) return packet;
        }
    }

    public static void WriteString(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > 65536) throw new ArgumentException("Nixani string too long.");
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    public static string ReadString(BinaryReader reader)
    {
        var size = reader.ReadUInt32();
        if (size > 65536) throw new InvalidDataException("Nixani string too long.");
        return Encoding.UTF8.GetString(ReadExact(reader, (int)size));
    }

    private static byte[] ReadExact(BinaryReader reader, int count)
    {
        var values = reader.ReadBytes(count);
        if (values.Length != count) throw new EndOfStreamException();
        return values;
    }

    public async Task<RemoteHost[]> Discover(CancellationToken token)
    {
        await Send(10, null, token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            while (true)
            {
                using var reply = (await WaitFor(110, timeout.Token)).Reader();
                var count = reply.ReadUInt32();
                if (count > 64) throw new InvalidDataException("Too many discovered consoles.");
                var consoles = new List<RemoteHost>();
                for (int i = 0; i < count; i++)
                {
                    var state = reply.ReadByte();
                    var name = ReadString(reply);
                    var address = ReadString(reply);
                    _ = ReadString(reply);
                    _ = ReadString(reply);
                    _ = ReadString(reply);
                    bool ps5 = reply.ReadBoolean();
                    var target = reply.ReadUInt32();
                    if (ps5) consoles.Add(new(name, address, target, state));
                }
                if (consoles.Count != 0) return [.. consoles];
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return []; }
    }

    public async Task<Registration> Register(RemoteHost host, string accountBase64, string code, uint pin,
        CancellationToken token)
    {
        var account = Convert.FromBase64String(accountBase64);
        if (account.Length != 8 || code.Length != 8 || code.Any(c => !char.IsAsciiDigit(c)))
            throw new ArgumentException("Provide the 8-byte Base64 PSN account ID and the 8-digit PS5 pairing code.");
        await Send(20, w =>
        {
            WriteString(w, host.Address);
            w.Write(host.Target);
            w.Write(account);
            w.Write(uint.Parse(code, System.Globalization.CultureInfo.InvariantCulture));
            w.Write(pin);
        }, token);
        using var reply = (await WaitFor(120, token)).Reader();
        bool ok = reply.ReadBoolean();
        var error = ReadString(reply);
        if (!ok) throw new IOException(error.Length > 0 ? error : "PS5 registration failed.");
        var nickname = ReadString(reply);
        _ = ReadExact(reply, 6);
        _ = reply.ReadUInt32();
        return new(account, ReadExact(reply, 16), ReadExact(reply, 16), nickname);
    }

    public async Task<RemoteDevice[]> AccountConsoles(string authorizedToken, CancellationToken token)
    {
        await Send(30, w => { w.Write(1u); WriteString(w, authorizedToken); }, token);
        using var reply = (await WaitFor(130, token)).Reader();
        _ = reply.ReadUInt32();
        var error = ReadString(reply);
        if (error.Length > 0) throw new IOException("Nixani could not list the consoles for this PSN token.");
        var count = reply.ReadUInt32();
        if (count > 64) throw new InvalidDataException("Too many account devices.");
        var devices = new List<RemoteDevice>();
        for (int i = 0; i < count; i++)
        {
            var name = ReadString(reply);
            var uid = ReadExact(reply, 32);
            bool ps5 = reply.ReadBoolean(), enabled = reply.ReadBoolean();
            if (ps5 && enabled) devices.Add(new(name, uid));
        }
        return [.. devices];
    }

    public static string RegistrationId(string host) => "ps5:" + host.Trim().ToLowerInvariant();

    public static Registration LoadRegistration(SecretStore store, string host)
    {
        var data = store.Read(RegistrationId(host)) ?? throw new InvalidOperationException("Pair this PS5 first.");
        return JsonSerializer.Deserialize<Registration>(data) ?? throw new InvalidDataException("Invalid PS5 registration.");
    }

    public async Task Session(Options options, Registration registration, string authorizedToken, byte[]? uid,
        Func<string, Task<string?>> pin, Action<string> status, Action<RemoteStats> stats,
        Action<int> lostFrames, Action<VideoFrame> video, AudioOutput? audio, CancellationToken token)
    {
        if (options.InternetPlay && (string.IsNullOrWhiteSpace(authorizedToken) || uid?.Length != 32))
            throw new InvalidOperationException("Select an authorized PSN console first.");
        if (registration.Account.Length != 8 || registration.RegistrationKey.Length != 16 ||
            registration.RemoteKey.Length != 16) throw new InvalidDataException("Invalid PS5 registration.");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = lifetime.Token;
        var queue = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(32)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
        });
        var ffmpeg = ProcessTools.Find(options.Ffmpeg, "ffmpeg");
        var width = options.RemoteHeight * 16 / 9;
        var info = new VideoInfo(width, options.RemoteHeight, options.RemoteFps, true, "");
        await Send(40, w =>
        {
            WriteString(w, options.ConsoleHost);
            w.Write(true);
            w.Write(registration.RegistrationKey);
            w.Write(registration.RemoteKey);
            w.Write(registration.Account);
            w.Write((uint)options.RemoteHeight);
            w.Write((uint)options.RemoteFps);
            w.Write((uint)options.Bitrate);
            w.Write((byte)0); // Annex B H.264 decoder.
            w.Write(true);
            w.Write(options.InternetPlay);
            WriteString(w, options.InternetPlay ? authorizedToken : "");
            w.Write(options.InternetPlay ? uid! : new byte[32]);
        }, ct);
        var reader = Receive();
        var decode = FfmpegCapture.RunRemote(ffmpeg, info, options.HardwareDecode, video, Feed, ct);
        var controller = SendController();
        try { await await Task.WhenAny(reader, decode, controller); }
        finally
        {
            await lifetime.CancelAsync();
            try
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                await Send(41, null, stop.Token);
            }
            catch (IOException) { }
            catch (OperationCanceledException) { }
            try { await Task.WhenAll(reader, decode, controller); }
            catch (Exception) when (ct.IsCancellationRequested) { }
        }

        async Task Feed(Stream stream, CancellationToken c)
        {
            await foreach (var packet in queue.Reader.ReadAllAsync(c)) await stream.WriteAsync(packet, c);
        }

        async Task Receive()
        {
            bool awaitingKeyframe = false;
            int audioChannels = 0, audioRate = 0;
            while (!ct.IsCancellationRequested)
            {
                var packet = await Read(ct);
                using var message = packet.Reader();
                switch (packet.Type)
                {
                    case 140:
                        byte state = message.ReadByte();
                        bool error = message.ReadBoolean();
                        var description = ReadString(message);
                        status(state == 2 ? "PS5 Remote Play · " + registration.Nickname : description);
                        if (state == 3)
                        {
                            if (error) throw new IOException("PS5 Remote Play session ended: " + description);
                            return;
                        }
                        break;
                    case 141:
                        _ = message.ReadUInt64();
                        int lost = message.ReadInt32();
                        if (lost > 0) lostFrames(lost);
                        _ = message.ReadBoolean();
                        var payload = packet.Payload.AsMemory(13);
                        if (awaitingKeyframe && !ContainsIdr(payload.Span)) break;
                        if (queue.Writer.TryWrite(payload)) awaitingKeyframe = false;
                        else if (!awaitingKeyframe)
                        {
                            awaitingKeyframe = true;
                            lostFrames(1);
                            await Send(44, null, ct);
                        }
                        break;
                    case 142:
                        audioChannels = (int)message.ReadUInt32();
                        audioRate = (int)message.ReadUInt32();
                        audio?.SetFormat(audioRate, audioChannels);
                        break;
                    case 143 when audioChannels > 0 && audioRate > 0:
                        audio?.Write(packet.Payload, 0, packet.Payload.Length);
                        break;
                    case 148:
                        var value = await pin(message.ReadBoolean() ? "PS5 PIN incorrect" : "PS5 login PIN");
                        if (string.IsNullOrEmpty(value)) throw new IOException("PS5 login PIN required.");
                        await Send(43, w => WriteString(w, value), ct);
                        break;
                    case 150:
                        status(message.ReadBoolean() ? "PS5: protected content is unavailable for streaming." : "PS5 Remote Play");
                        break;
                    case 151:
                        stats(new(message.ReadUInt64(), message.ReadUInt64(), message.ReadUInt32() / 1000d,
                            message.ReadUInt64()));
                        break;
                }
            }
        }

        async Task SendController()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(8));
            while (await timer.WaitForNextTickAsync(ct))
            {
                var pad = ControllerInput.Read();
                await Send(42, pad.Write, ct);
            }
        }
    }

    private static bool ContainsIdr(ReadOnlySpan<byte> data)
    {
        for (int i = 2; i + 1 < data.Length; i++)
            if (data[i - 2] == 0 && data[i - 1] == 0 && data[i] == 1 && (data[i + 1] & 31) == 5)
                return true;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        // Terminate before closing a pipe that may still be blocked in an engine write.
        ProcessTools.Terminate(process);
        await process.WaitForExitAsync();
        await stderr;
        process.Dispose();
        sendLock.Dispose();
    }
}
