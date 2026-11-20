using System.Buffers;
using System.Diagnostics;

namespace Ovrix.Engine;

/// <summary>Owns a rented, tightly packed BGRA frame. Consumers dispose it or transfer ownership.</summary>
public sealed class VideoFrame : IDisposable
{
    private byte[]? buffer;
    public int Width { get; }
    public int Height { get; }
    public int Length { get; }
    public double Seconds { get; }
    public long Received { get; } = Stopwatch.GetTimestamp();
    internal long DeliveryStarted { get; set; }
    public bool Generated { get; }
    public byte[] Buffer => buffer ?? throw new ObjectDisposedException(nameof(VideoFrame));

    public VideoFrame(int width, int height, double seconds, bool generated = false)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Length = checked(width * height * 4);
        buffer = ArrayPool<byte>.Shared.Rent(Length);
        Seconds = seconds;
        Generated = generated;
    }

    public void Dispose()
    {
        var rented = Interlocked.Exchange(ref buffer, null);
        if (rented is not null) ArrayPool<byte>.Shared.Return(rented);
    }
}

public static class FrameAnalysis
{
    public static (bool Duplicate, bool SceneCut) Compare(VideoFrame previous, VideoFrame current)
    {
        if (previous.Width != current.Width || previous.Height != current.Height) return (false, true);
        var a = previous.Buffer;
        var b = current.Buffer;
        long energy = 0;
        int changed = 0, pixels = 0;
        var step = Math.Max(2, current.Width / 180);
        for (int y = step / 2; y < current.Height; y += step)
        for (int x = step / 2; x < current.Width; x += step)
        {
            int p = (y * current.Width + x) * 4;
            int distance = Math.Abs(a[p] - b[p]) + Math.Abs(a[p + 1] - b[p + 1]) +
                Math.Abs(a[p + 2] - b[p + 2]);
            if (distance > 20) changed++;
            energy += distance;
            pixels++;
        }
        return pixels == 0 ? (false, false) : (changed <= Math.Max(1, pixels / 2000) &&
            energy < pixels * 7L, changed > pixels * .72 && energy > pixels * 150L);
    }
}
