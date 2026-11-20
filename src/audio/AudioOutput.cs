using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Ovrix.Engine;

public sealed record AudioDevice(string Id, string Name);

public sealed class AudioOutput : IDisposable
{
    private readonly object gate = new();
    private readonly MMDeviceEnumerator enumerator = new();
    private readonly string deviceId;
    private readonly Func<Options> settings;
    private WasapiOut? output;
    private MMDevice? device;
    private BufferedWaveProvider? buffer;
    private VolumeWaveProvider16? volume;
    private int appliedDelay;
    private bool disposed;
    public long SamplesWritten { get; private set; }
    public double BufferedMs { get { lock (gate) return buffer?.BufferedDuration.TotalMilliseconds ?? 0; } }

    public AudioOutput(string deviceId, Func<Options> settings)
    {
        this.deviceId = deviceId;
        this.settings = settings;
    }

    public static AudioDevice[] Devices()
    {
        using var list = new MMDeviceEnumerator();
        var devices = new List<AudioDevice> { new("", "Windows default") };
        foreach (var entry in list.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            using (entry) devices.Add(new(entry.ID, entry.FriendlyName));
        }
        return [.. devices];
    }

    public void SetFormat(int sampleRate, int channels)
    {
        if (sampleRate is < 8000 or > 192000 || channels is < 1 or > 8)
            throw new ArgumentException("Unsupported PCM format.");
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            CloseOutput();
            try
            {
                device = deviceId.Length > 0 ? enumerator.GetDevice(deviceId) :
                    enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, channels))
                {
                    BufferDuration = TimeSpan.FromMilliseconds(950),
                    DiscardOnBufferOverflow = true
                };
                volume = new VolumeWaveProvider16(buffer);
                output = new WasapiOut(device, AudioClientShareMode.Shared, false, 40);
                output.Init(volume);
                appliedDelay = 0;
            }
            catch { CloseOutput(); throw; }
        }
    }

    public void Write(byte[] data, int offset, int count)
    {
        lock (gate)
        {
            if (buffer is null || volume is null || output is null || disposed) return;
            var options = settings();
            volume.Volume = options.Mute ? 0 : (float)options.Volume;
            var minimumDelay = Math.Max(0, options.AudioDelay + (options.Interpolate ? 34 : 0));
            var alignment = buffer.WaveFormat.BlockAlign;
            var bytesPerSecond = buffer.WaveFormat.AverageBytesPerSecond;
            if (minimumDelay > appliedDelay)
            {
                var gap = (minimumDelay - appliedDelay) * bytesPerSecond / 1000;
                var silence = new byte[gap - gap % alignment];
                buffer.AddSamples(silence, 0, silence.Length);
            }
            else if (minimumDelay < appliedDelay)
            {
                buffer.ClearBuffer();
                var gap = minimumDelay * bytesPerSecond / 1000;
                var silence = new byte[gap - gap % alignment];
                buffer.AddSamples(silence, 0, silence.Length);
            }
            appliedDelay = minimumDelay;
            count -= count % alignment;
            if (buffer.BufferedDuration.TotalMilliseconds > minimumDelay + 240)
                buffer.ClearBuffer();
            buffer.AddSamples(data, offset, count);
            SamplesWritten += count / alignment;
            if (output.PlaybackState != PlaybackState.Playing &&
                buffer.BufferedDuration.TotalMilliseconds >= minimumDelay + 24) output.Play();
        }
    }

    private void CloseOutput()
    {
        output?.Dispose();
        device?.Dispose();
        output = null;
        device = null;
        buffer = null;
        volume = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            CloseOutput();
            enumerator.Dispose();
        }
    }
}
