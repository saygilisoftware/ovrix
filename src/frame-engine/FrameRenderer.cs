using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ovrix.Engine;

public sealed class FrameRenderer : IDisposable
{
    private IntPtr native;
    private int adaptiveRadius = 24;
    public bool GpuAvailable => native != IntPtr.Zero;

    public FrameRenderer(bool gpu = true)
    {
        if (!gpu) return;
        try { native = Create(); }
        catch (DllNotFoundException) { }
        catch (BadImageFormatException) { }
        catch (EntryPointNotFoundException) { }
    }

    public VideoFrame Render(VideoFrame a, VideoFrame? b, Options options, double midpoint = .5)
    {
        var outHeight = options.Upscale ? Math.Max(options.OutputHeight, a.Height) : a.Height;
        var outWidth = (int)Math.Round(a.Width * (double)outHeight / a.Height);
        if (outWidth > 4096)
        {
            outHeight = Math.Max(1, (int)Math.Round(outHeight * (4096d / outWidth)));
            outWidth = 4096;
        }
        var result = new VideoFrame(outWidth, outHeight,
            b is null ? a.Seconds : a.Seconds + (b.Seconds - a.Seconds) * midpoint, b is not null);
        try
        {
            if (b is null && a.Width == outWidth && a.Height == outHeight && options.Sharpness <= 0)
            {
                a.Buffer.AsSpan(0, a.Length).CopyTo(result.Buffer);
                return result;
            }
            var requestedRadius = options.Preset switch
            {
                FramePreset.LowLatency => 6, FramePreset.Balanced => options.TargetFps == 120 ? 8 : 12,
                FramePreset.Quality => 24, _ => options.SearchRadius
            };
            var radius = Math.Min(requestedRadius, adaptiveRadius);
            var start = Stopwatch.GetTimestamp();
            if (native != IntPtr.Zero && RenderGpu(native, a.Buffer, (b ?? a).Buffer, result.Buffer,
                a.Width, a.Height, outWidth, outHeight, radius, b is null ? 0 : (float)midpoint,
                b is null ? (float)options.Sharpness : 0) != 0)
            {
                if (b is not null && options.Preset != FramePreset.Custom)
                {
                    var budget = 1000d / options.TargetFps;
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    if (elapsed > budget * .9) adaptiveRadius = Math.Max(4, radius - 2);
                    else if (elapsed < budget * .35) adaptiveRadius = Math.Min(24, adaptiveRadius + 1);
                }
            }
            else
            {
                Dispose();
                RenderSoftware(a, b, result, Math.Min(radius, 8), midpoint, options.Sharpness);
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static void RenderSoftware(VideoFrame a, VideoFrame? b, VideoFrame result, int radius,
        double phase, double sharpen)
    {
        int dx = 0, dy = 0;
        if (b is not null)
        {
            // Search for a global motion vector on a sparse grid for the compatibility path.
            long best = long.MaxValue;
            int sample = Math.Max(4, a.Width / 80);
            int stride = a.Width * 4;
            for (int y = -radius; y <= radius; y += 2)
            for (int x = -radius; x <= radius; x += 2)
            {
                long cost = (Math.Abs(x) + Math.Abs(y)) * 32L;
                for (int row = radius; row < a.Height - radius; row += sample)
                for (int col = radius; col < a.Width - radius; col += sample)
                {
                    int p = row * stride + col * 4 + 1;
                    int q = p + y * stride + x * 4;
                    cost += Math.Abs(a.Buffer[p] - b.Buffer[q]);
                }
                if (cost < best) { best = cost; dx = x; dy = y; }
            }
        }

        var target = result.Buffer;
        var left = a.Buffer;
        var right = b?.Buffer;
        Parallel.For(0, result.Height, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(8, Environment.ProcessorCount) }, y =>
        {
            double sy = (y + .5) * a.Height / result.Height - .5;
            for (int x = 0; x < result.Width; x++)
            {
                double sx = (x + .5) * a.Width / result.Width - .5;
                int pixel = (y * result.Width + x) * 4;
                for (int channel = 0; channel < 3; channel++)
                {
                    double first = Sample(left, a.Width, a.Height, sx - dx * phase, sy - dy * phase, channel);
                    if (right is not null)
                        first = first * (1 - phase) +
                            phase * Sample(right, a.Width, a.Height, sx + dx * (1 - phase), sy + dy * (1 - phase), channel);
                    else if (sharpen > 0)
                    {
                        double neighbors = Sample(left, a.Width, a.Height, sx - 1, sy, channel) +
                            Sample(left, a.Width, a.Height, sx + 1, sy, channel) +
                            Sample(left, a.Width, a.Height, sx, sy - 1, channel) +
                            Sample(left, a.Width, a.Height, sx, sy + 1, channel);
                        first += (first - neighbors * .25) * sharpen;
                    }
                    target[pixel + channel] = (byte)Math.Clamp(first, 0, 255);
                }
                target[pixel + 3] = 255;
            }
        });
    }

    private static double Sample(byte[] data, int w, int h, double x, double y, int channel)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int x0 = (int)x, y0 = (int)y;
        int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
        double fx = x - x0, fy = y - y0;
        return (1 - fy) * ((1 - fx) * data[(y0 * w + x0) * 4 + channel] + fx * data[(y0 * w + x1) * 4 + channel]) +
            fy * ((1 - fx) * data[(y1 * w + x0) * 4 + channel] + fx * data[(y1 * w + x1) * 4 + channel]);
    }

    public void Dispose()
    {
        if (native == IntPtr.Zero) return;
        Destroy(native);
        native = IntPtr.Zero;
    }

    [DllImport("ovrix_gpu", EntryPoint = "ovrix_create")]
    private static extern IntPtr Create();
    [DllImport("ovrix_gpu", EntryPoint = "ovrix_render")]
    private static extern int RenderGpu(IntPtr engine, byte[] a, byte[] b, [Out] byte[] output,
        int w, int h, int outW, int outH, int radius, float phase, float sharpen);
    [DllImport("ovrix_gpu", EntryPoint = "ovrix_destroy")]
    private static extern void Destroy(IntPtr engine);
}
