using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace Ovrix.Engine;

public sealed record PlaybackMetrics(double IncomingFps, double ContentFps, double OutputFps, double GeneratedFps,
    long Dropped, double GenerationMs, double PipelineMs, double OcrMs, double TranslationMs,
    double AudioBufferMs, bool Gpu, RemoteStats? Network, double SourceIntervalMs, double DeliveryMs);

/// <summary>Runs one selected source and isolates decoding, presentation and subtitle work.</summary>
public sealed class Playback(Options initial, Func<Options> live, SecretStore secrets, TranslationMemory memory,
    Action<VideoFrame> present, Action<Subtitle?> subtitle, Action<string> status, Action<string> translateStatus,
    Func<string, Task<string?>> pin, byte[]? remoteUid, Action<VideoInfo>? videoInfo = null)
{
    private long incoming, unique, shown, generated, dropped;
    private double generationMs, latencyMs, ocrMs, translationMs, audioMs, sourceInterval, deliveryMs;
    private bool gpu;
    private RemoteStats? network;
    private readonly object measurements = new();
    private long mark = Stopwatch.GetTimestamp();
    private long markIncoming, markUnique, markShown, markGenerated;

    public PlaybackMetrics Snapshot()
    {
        lock (measurements)
        {
            var now = Stopwatch.GetTimestamp();
            double elapsed = Math.Max(.001, Stopwatch.GetElapsedTime(mark, now).TotalSeconds);
            mark = now;
            double Rate(ref long total, ref long prior)
            {
                var current = Interlocked.Read(ref total);
                var rate = (current - prior) / elapsed;
                prior = current;
                return rate;
            }
            return new(Rate(ref incoming, ref markIncoming), Rate(ref unique, ref markUnique),
                Rate(ref shown, ref markShown), Rate(ref generated, ref markGenerated),
                Interlocked.Read(ref dropped), generationMs, latencyMs, ocrMs, translationMs, audioMs,
                gpu, network, sourceInterval, deliveryMs);
        }
    }

    public void ReportPresented(VideoFrame frame)
    {
        Interlocked.Increment(ref shown);
        if (frame.Generated) Interlocked.Increment(ref generated);
        if (frame.DeliveryStarted != 0)
        {
            lock (measurements) deliveryMs = Stopwatch.GetElapsedTime(frame.DeliveryStarted).TotalMilliseconds;
        }
    }

    public void ReportPresentationDrop() => Interlocked.Increment(ref dropped);

    public async Task Run(CancellationToken token)
    {
        using var translator = new TranslationService(memory, secrets);
        await using var captions = new SubtitlePipeline(live, translator, subtitle, translateStatus, (ocr, translation) =>
        {
            lock (measurements)
            {
                if (ocr >= 0) ocrMs = ocr;
                if (translation >= 0) translationMs = translation;
            }
        }, token);

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await RunSource(captions, token);
                return;
            }
            catch (IOException ex) when (initial.Source != SourceKind.File && attempt < 2 && !token.IsCancellationRequested)
            {
                status($"↻ {attempt + 1}/2 · {ex.Message}");
                captions.Reset();
                await Task.Delay(TimeSpan.FromMilliseconds(800 * (attempt + 1)), token);
            }
        }
    }

    private async Task RunSource(SubtitlePipeline captions, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        var ct = lifetime.Token;
        var frames = Channel.CreateBounded<VideoFrame>(
            new BoundedChannelOptions(6)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
            }, discarded => { discarded.Dispose(); Interlocked.Increment(ref dropped); });
        var presentation = Channel.CreateBounded<(VideoFrame Frame, long Due, long Received, bool Gpu)>(
            new BoundedChannelOptions(8)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest
            }, discarded => { discarded.Frame.Dispose(); Interlocked.Increment(ref dropped); });
        using var renderer = new FrameRenderer();
        lock (measurements) gpu = renderer.GpuAvailable;
        var source = Task.Run(Capture, CancellationToken.None);
        var delivery = Task.Run(Deliver, CancellationToken.None);
        try
        {
            VideoFrame? previous = null;
            double lastUniqueTime = 0, smoothed = 0;
            long deliveryAnchor = 0;
            double sourceAnchor = 0;
            bool wasInterpolating = false;
            bool wasTranslating = live().Translate;
            try
            {
                await foreach (var current in frames.Reader.ReadAllAsync(ct))
                {
                    var settings = live();
                    if (wasTranslating && !settings.Translate) captions.Reset();
                    wasTranslating = settings.Translate;
                    var assessment = previous is null ? (Duplicate: false, SceneCut: false) : FrameAnalysis.Compare(previous, current);
                    if (assessment.SceneCut) captions.Reset();
                    var changed = !assessment.Duplicate;
                    if (changed)
                    {
                        Interlocked.Increment(ref unique);
                        captions.Offer(current);
                        if (previous is not null && current.Seconds > lastUniqueTime)
                        {
                            double interval = current.Seconds - lastUniqueTime;
                            smoothed = smoothed == 0 || interval > .18 ? interval : interval * .25 + smoothed * .75;
                            lock (measurements) sourceInterval = smoothed * 1000;
                        }
                        lastUniqueTime = current.Seconds;
                    }
                    else captions.Offer(current); // Static subtitles still need stabilization samples.

                    if (!settings.Interpolate)
                    {
                        try { Output(current, settings, 0); }
                        catch { current.Dispose(); throw; }
                        previous?.Dispose();
                        previous = current;
                        wasInterpolating = false;
                        continue;
                    }

                    if (previous is null || !wasInterpolating)
                    {
                        previous?.Dispose();
                        previous = current;
                        wasInterpolating = true;
                        deliveryAnchor = 0;
                        continue;
                    }
                    var gap = current.Seconds - previous.Seconds;
                    if (!changed && gap is >= 0 and < .15)
                    {
                        current.Dispose();
                        continue;
                    }

                    var prior = previous;
                    previous = current;
                    using (prior)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var due = deliveryAnchor == 0 ? now :
                            deliveryAnchor + (long)((prior.Seconds - sourceAnchor) * Stopwatch.Frequency);
                        if (deliveryAnchor == 0 || Math.Abs(due - now) > Stopwatch.Frequency / 10 ||
                            gap <= 0 || gap > .20)
                        {
                            sourceAnchor = prior.Seconds;
                            deliveryAnchor = due = now;
                        }
                        Output(prior, settings, due);
                        var ratio = gap * settings.TargetFps;
                        // Synthesize at most one midpoint. Extra work during a late capture gap
                        // would compete with the next original and can prolong the drop.
                        if (!changed || assessment.SceneCut || gap > .10 || ratio < 1.75 ||
                            frames.Reader.Count > 1 || presentation.Reader.Count > 4 ||
                            (!renderer.GpuAvailable && current.Width * current.Height >= 1920 * 1080 &&
                             settings.Preset == FramePreset.LowLatency)) continue;
                        var target = due + (long)(gap * .5 * Stopwatch.Frequency);
                        if (Stopwatch.GetTimestamp() - target > Stopwatch.Frequency / settings.TargetFps)
                        {
                            Interlocked.Increment(ref dropped);
                            continue;
                        }
                        var start = Stopwatch.GetTimestamp();
                        var generatedFrame = renderer.Render(prior, current, settings);
                        lock (measurements) generationMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                        Enqueue(generatedFrame, target, current.Received);
                    }
                }
                if (previous is not null && wasInterpolating) Output(previous, live(), 0);
            }
            finally { previous?.Dispose(); }
            await source;
            presentation.Writer.TryComplete();
            await delivery;
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await Task.WhenAll(source, delivery); }
            catch (Exception) when (ct.IsCancellationRequested) { }
            while (frames.Reader.TryRead(out var remainder)) remainder.Dispose();
            while (presentation.Reader.TryRead(out var remainder)) remainder.Frame.Dispose();
        }

        void Output(VideoFrame input, Options settings, long due) =>
            Enqueue(renderer.Render(input, null, settings), due, input.Received);

        void Enqueue(VideoFrame frame, long due, long received)
        {
            if (!presentation.Writer.TryWrite((frame, due, received, renderer.GpuAvailable))) frame.Dispose();
        }

        async Task Deliver()
        {
            using var clock = new FrameClock(ct);
            await foreach (var item in presentation.Reader.ReadAllAsync(ct))
            {
                try { clock.WaitUntil(item.Due, ct); }
                catch { item.Frame.Dispose(); throw; }
                Present(item.Frame, item.Received, item.Gpu);
            }
        }

        async Task Capture()
        {
            AudioOutput? audio = null;
            try
            {
                var ffmpeg = ProcessTools.Find(initial.Ffmpeg, "ffmpeg");
                try { audio = new AudioOutput(initial.AudioOutput, live); }
                catch (Exception ex) { status("Audio: " + ex.Message); }
                bool audioReady = false, audioFailed = false;
                double capturePeriod = 0, lastCaptureTime = double.NaN;
                void Samples(byte[] pcm, int offset, int count)
                {
                    if (audio is null || audioFailed) return;
                    try
                    {
                        if (!audioReady)
                        {
                            audio.SetFormat(48000, 2);
                            audioReady = true;
                        }
                        audio.Write(pcm, offset, count);
                        lock (measurements) audioMs = audio.BufferedMs;
                    }
                    catch (Exception ex)
                    {
                        audioFailed = true;
                        status("Audio: " + ex.Message);
                    }
                }
                void Frame(VideoFrame frame)
                {
                    Interlocked.Increment(ref incoming);
                    if (initial.Source == SourceKind.Hdmi && capturePeriod > 0 && double.IsFinite(lastCaptureTime))
                    {
                        var periods = (frame.Seconds - lastCaptureTime) / capturePeriod;
                        if (periods is > 1.8 and < 120)
                            Interlocked.Add(ref dropped, (long)Math.Round(periods) - 1);
                    }
                    lastCaptureTime = frame.Seconds;
                    if (!frames.Writer.TryWrite(frame)) frame.Dispose();
                }
                void Ready(VideoInfo info)
                {
                    capturePeriod = info.Fps > 0 ? 1 / info.Fps : 0;
                    videoInfo?.Invoke(info);
                    status(FormattableString.Invariant(
                        $"{info.Width}×{info.Height} · {info.Fps:0.##} FPS · {(info.IsHdr ? "HDR → SDR" : "SDR")}"));
                }
                switch (initial.Source)
                {
                    case SourceKind.File:
                        await FfmpegCapture.RunFile(ffmpeg, initial.File, initial.HardwareDecode, Frame, Samples, Ready, ct);
                        break;
                    case SourceKind.Hdmi:
                        await FfmpegCapture.RunDevice(ffmpeg, initial.CaptureDevice, initial.CaptureAudio,
                            initial.CaptureMode, Frame, Samples, Ready, ct);
                        throw new IOException("Capture device disconnected.");
                    case SourceKind.RemotePlay:
                        var registration = NixaniClient.LoadRegistration(secrets, initial.ConsoleHost);
                        var account = secrets.Read("psn-token:" + initial.ConsoleHost);
                        var access = account is null ? "" : System.Text.Encoding.UTF8.GetString(account);
                        await using (var client = await NixaniClient.Connect(initial.Nixani, ct))
                        {
                            await client.Session(initial, registration, access, remoteUid, pin, status,
                                info => { lock (measurements) { network = info; audioMs = audio?.BufferedMs ?? 0; } },
                                count => Interlocked.Add(ref dropped, count), Frame, audio, ct);
                        }
                        break;
                }
                frames.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                frames.Writer.TryComplete(ex);
                throw;
            }
            finally { audio?.Dispose(); }
        }
    }

    private void Present(VideoFrame frame, long received, bool accelerated)
    {
        lock (measurements)
        {
            latencyMs = Stopwatch.GetElapsedTime(received).TotalMilliseconds;
            gpu = accelerated;
        }
        frame.DeliveryStarted = Stopwatch.GetTimestamp();
        try { present(frame); }
        catch { frame.Dispose(); throw; }
    }

}
