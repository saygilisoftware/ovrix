using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Ovrix.Engine;

public sealed record CaptureDevice(string Name, string Id, bool Audio)
{
    public override string ToString() => Name;
}

public sealed record CaptureMode(int Width, int Height, double Fps, string Format, bool Compressed)
{
    public string Key => FormattableString.Invariant($"{Width}x{Height}|{Fps:0.###}|{Format}|{Compressed}");
    public override string ToString() => FormattableString.Invariant($"{Width}×{Height} · {Fps:0.###} FPS · {Format}");
}

public sealed record VideoInfo(int Width, int Height, double Fps, bool Audio, string Transfer)
{
    public bool IsHdr => Transfer is "smpte2084" or "arib-std-b67";
}

public static partial class FfmpegCapture
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    [GeneratedRegex("\"(?<name>.+)\" \\((?<type>video(?:, audio)?|audio|none)\\)")]
    private static partial Regex DevicePattern();
    [GeneratedRegex("Alternative name \"(?<id>.+)\"")]
    private static partial Regex IdPattern();
    [GeneratedRegex("(?<type>pixel_format|vcodec)=(?<format>\\S+).*?max s=(?<width>\\d+)x(?<height>\\d+) fps=(?<fps>[0-9.]+)")]
    private static partial Regex ModePattern();
    [GeneratedRegex(@"\bn:\s*(\d+).*?\bpts_time:([-+0-9.eE]+)")]
    private static partial Regex PtsPattern();

    public static async Task<CaptureDevice[]> Devices(string ffmpeg, CancellationToken token)
    {
        var result = await ProcessTools.CaptureAsync(ffmpeg,
            ["-hide_banner", "-list_devices", "true", "-f", "dshow", "-i", "dummy"], token);
        var devices = new List<CaptureDevice>();
        CaptureDevice? current = null;
        foreach (var line in result.Errors.Split('\n'))
        {
            var match = DevicePattern().Match(line);
            if (match.Success)
            {
                if (current != null) devices.Add(current);
                var name = match.Groups["name"].Value;
                current = new(name, name, match.Groups["type"].Value == "audio");
            }
            else if (current is not null && IdPattern().Match(line) is { Success: true } id)
            {
                devices.Add(current with { Id = id.Groups["id"].Value });
                current = null;
            }
        }
        if (current != null) devices.Add(current);
        return [.. devices];
    }

    public static async Task<CaptureMode[]> Modes(string ffmpeg, string device, CancellationToken token)
    {
        var result = await ProcessTools.CaptureAsync(ffmpeg,
            ["-hide_banner", "-list_options", "true", "-f", "dshow", "-i", "video=" + device], token);
        return ModePattern().Matches(result.Errors).Select(m => new CaptureMode(
            int.Parse(m.Groups["width"].Value, Invariant), int.Parse(m.Groups["height"].Value, Invariant),
            double.Parse(m.Groups["fps"].Value, Invariant), m.Groups["format"].Value,
            m.Groups["type"].Value == "vcodec")).Where(mode => mode.Width <= 4096 && mode.Height <= 4096)
            .Distinct().OrderByDescending(m => m.Height).ThenByDescending(m => m.Fps).ToArray();
    }

    public static async Task<VideoInfo> Probe(string ffmpeg, string path, CancellationToken token)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
            throw new FileNotFoundException("Select a local video file.");
        var probe = ProcessTools.Find(Path.Combine(Path.GetDirectoryName(ffmpeg)!, "ffprobe.exe"), "ffprobe");
        var result = await ProcessTools.CaptureAsync(probe,
            ["-v", "error", "-protocol_whitelist", "file,crypto", "-show_streams", "-of", "json", path], token);
        if (result.ExitCode != 0) throw new IOException("FFprobe: " + result.Errors.Trim());
        using var doc = JsonDocument.Parse(result.Output);
        var streams = doc.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video = streams.FirstOrDefault(s => IsType(s, "video") &&
            (!s.TryGetProperty("disposition", out var disposition) ||
             !disposition.TryGetProperty("attached_pic", out var attached) || attached.GetInt32() == 0));
        if (video.ValueKind == JsonValueKind.Undefined) throw new IOException("No video stream.");
        var dimensions = video.TryGetProperty("side_data_list", out var sideData) &&
            sideData.EnumerateArray().Any(s => s.TryGetProperty("rotation", out var rotation) &&
                Math.Abs(rotation.GetInt32()) % 180 == 90);
        int w = video.GetProperty("width").GetInt32(), h = video.GetProperty("height").GetInt32();
        var fps = video.GetProperty("avg_frame_rate").GetString()!.Split('/');
        double rate = fps.Length == 2 && double.TryParse(fps[0], Invariant, out var top) &&
            double.TryParse(fps[1], Invariant, out var bottom) && bottom > 0 ? top / bottom : 30;
        if (!double.IsFinite(rate) || rate <= 0) rate = 30;
        return new(dimensions ? h : w, dimensions ? w : h, rate, streams.Any(s => IsType(s, "audio")),
            video.TryGetProperty("color_transfer", out var transfer) ? transfer.GetString() ?? "" : "");
    }

    private static bool IsType(JsonElement element, string type) =>
        element.TryGetProperty("codec_type", out var value) && value.GetString() == type;

    public static async Task RunFile(string ffmpeg, string path, bool hardware, Action<VideoFrame> frame,
        Action<byte[], int, int>? audio, Action<VideoInfo> ready, CancellationToken token)
    {
        var info = await Probe(ffmpeg, path, token);
        ready(info);
        string[] input = ["-protocol_whitelist", "file,crypto", "-readrate", "1", "-i", path];
        var started = false;
        void Forward(VideoFrame value) { started = true; frame(value); }
        try { await Run(ffmpeg, input, info, hardware, Forward, info.Audio ? audio : null, null, token); }
        catch (IOException) when (hardware && !started && !token.IsCancellationRequested)
        {
            await Run(ffmpeg, input, info, false, Forward, info.Audio ? audio : null, null, token);
        }
    }

    public static async Task RunDevice(string ffmpeg, string video, string audioDevice, string modeKey,
        Action<VideoFrame> frame, Action<byte[], int, int>? audio, Action<VideoInfo> ready, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(video)) throw new ArgumentException("Select a DirectShow video device.");
        var modes = await Modes(ffmpeg, video, token);
        var mode = modes.FirstOrDefault(m => m.Key == modeKey) ?? modes.FirstOrDefault(m =>
            m.Height == 1080 && m.Fps is >= 29 and <= 61) ?? modes.FirstOrDefault()
            ?? throw new IOException("The driver reported no compatible capture modes.");
        var info = new VideoInfo(mode.Width, mode.Height, mode.Fps, audioDevice.Length > 0, "");
        ready(info);
        var args = new List<string> { "-f", "dshow", "-rtbufsize", "64M", "-video_size",
            $"{mode.Width}x{mode.Height}", "-framerate", mode.Fps.ToString(Invariant) };
        args.AddRange(mode.Compressed ? ["-vcodec", mode.Format] : ["-pixel_format", mode.Format]);
        args.AddRange(["-i", "video=" + video + (info.Audio ? ":audio=" + audioDevice : "")]);
        await Run(ffmpeg, args, info, false, frame, info.Audio ? audio : null, null, token);
    }

    public static Task RunRemote(string ffmpeg, VideoInfo info, bool hardware,
        Action<VideoFrame> frame, Func<Stream, CancellationToken, Task> feed, CancellationToken token) =>
        Run(ffmpeg, ["-probesize", "32768", "-analyzeduration", "100000", "-f", "h264", "-i", "pipe:0"],
            info, hardware, frame, null, feed, token);

    private static async Task Run(string executable, IEnumerable<string> input, VideoInfo info, bool hardware,
        Action<VideoFrame> frame, Action<byte[], int, int>? audio,
        Func<Stream, CancellationToken, Task>? feed, CancellationToken token)
    {
        if (info.Width is < 1 or > 4096 || info.Height is < 1 or > 4096)
            throw new IOException("Video dimensions exceed the supported 4096×4096 limit.");

        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = lifetime.Token;
        var name = "ovrix-audio-" + Guid.NewGuid().ToString("N");
        await using var pipe = audio is null ? null : new NamedPipeServerStream(name, PipeDirection.In,
            1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var filter = info.IsHdr
            ? "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=mobius:desat=2,zscale=t=bt709:m=bt709:r=tv,format=yuv420p,showinfo=checksum=0"
            : "showinfo=checksum=0";
        if (feed is not null) filter = $"scale={info.Width}:{info.Height}:flags=fast_bilinear," + filter;
        var args = new List<string> { "-hide_banner", "-loglevel", "info", "-nostats", "-nostdin", "-y", "-filter_threads", "2" };
        if (hardware) args.AddRange(["-hwaccel", "auto"]);
        args.AddRange(input);
        args.AddRange(["-map", "0:V:0", "-an", "-sn", "-dn", "-vf", filter,
            "-pix_fmt", "bgra", "-fps_mode", "passthrough", "-threads:v", "1", "-c:v", "rawvideo", "-f", "rawvideo", "pipe:1"]);
        if (pipe is not null)
            args.AddRange(["-map", "0:a:0", "-vn", "-sn", "-af", "aresample=async=1000:first_pts=0",
                "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-f", "s16le", @"\\.\pipe\" + name]);

        using var child = ProcessTools.Start(executable, args, feed is not null);
        using var kill = ct.Register(() => ProcessTools.Terminate(child));
        var timestamps = Channel.CreateBounded<(int Number, double Seconds)>(new BoundedChannelOptions(48)
        {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
        });
        var recentErrors = new Queue<string>(10);
        var stderr = ReadMetadata();
        var audioTask = pipe is null ? Task.CompletedTask : ReadAudio(pipe, audio!, ct);
        var stdin = feed is null ? Task.CompletedTask : FeedInput();
        var bytes = child.StandardOutput.BaseStream;
        int count = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
                watchdog.CancelAfter(TimeSpan.FromSeconds(count == 0 ? 20 : 8));
                byte[] header = new byte[4];
                int read;
                try { read = await bytes.ReadAtLeastAsync(header, 4, false, watchdog.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException("No video frames received. Verify the source, capture mode and protection status.");
                }
                if (read == 0) break;
                if (read != 4) throw new EndOfStreamException("Incomplete video frame from FFmpeg.");
                double seconds = count / info.Fps;
                while (await timestamps.Reader.WaitToReadAsync(watchdog.Token) &&
                       timestamps.Reader.TryRead(out var timestamp))
                {
                    if (timestamp.Number >= count)
                    {
                        if (timestamp.Number == count) seconds = timestamp.Seconds;
                        break;
                    }
                }
                var packet = new VideoFrame(info.Width, info.Height, seconds);
                try
                {
                    header.CopyTo(packet.Buffer, 0);
                    await bytes.ReadExactlyAsync(packet.Buffer.AsMemory(4, packet.Length - 4), watchdog.Token);
                    frame(packet);
                }
                catch { packet.Dispose(); throw; }
                count++;
            }
            await child.WaitForExitAsync(ct);
            await stderr;
            if (child.ExitCode != 0 || count == 0)
                throw new IOException("FFmpeg: " + string.Join(" | ", recentErrors));
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(stderr, audioTask, stdin); }
            catch (OperationCanceledException) { }
            catch (IOException) when (ct.IsCancellationRequested) { }
        }

        async Task ReadMetadata()
        {
            try
            {
                while (await child.StandardError.ReadLineAsync(ct) is { } line)
                {
                    var match = PtsPattern().Match(line);
                    if (match.Success && double.TryParse(match.Groups[2].Value, Invariant, out var seconds))
                        timestamps.Writer.TryWrite((int.Parse(match.Groups[1].Value, Invariant), seconds));
                    else if (!line.Contains("showinfo", StringComparison.OrdinalIgnoreCase))
                    {
                        if (recentErrors.Count == 10) recentErrors.Dequeue();
                        recentErrors.Enqueue(line.Length > 300 ? line[..300] : line);
                    }
                }
            }
            finally { timestamps.Writer.TryComplete(); }
        }

        async Task FeedInput()
        {
            try { await feed!(child.StandardInput.BaseStream, ct); }
            finally { await child.StandardInput.BaseStream.DisposeAsync(); }
        }
    }

    private static async Task ReadAudio(NamedPipeServerStream pipe, Action<byte[], int, int> audio, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token);
        var buffer = new byte[3840];
        while (!token.IsCancellationRequested)
        {
            var size = await pipe.ReadAtLeastAsync(buffer, buffer.Length, false, token);
            if (size == 0) break;
            audio(buffer, 0, size);
        }
    }
}
