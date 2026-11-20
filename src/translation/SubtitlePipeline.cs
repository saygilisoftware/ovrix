using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Ovrix.Engine;

public sealed record OcrSample(byte[] Bgra, int Width, int Height, SubtitleRegion Region, int Scene);
public sealed record OcrReading(string Text, SubtitleRegion Region, double Quality);

/// <summary>Samples and crops original video frames without holding ownership of the source buffer.</summary>
public sealed partial class SubtitlePipeline : IAsyncDisposable
{
    private readonly Channel<OcrSample> samples = Channel.CreateBounded<OcrSample>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<(int Version, string Text, SubtitleRegion Region, string[] Context)> dialogue =
        Channel.CreateBounded<(int, string, SubtitleRegion, string[])>(
            new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Func<Options> options;
    private readonly Action<Subtitle?> display;
    private readonly Action<string> status;
    private readonly Action<double, double> timing;
    private readonly TranslationService translator;
    private readonly CancellationTokenSource lifetime;
    private readonly Task recognizerTask;
    private readonly Task translatorTask;
    private readonly object sync = new();
    private CancellationTokenSource? request;
    private int version;
    private int scene;
    private long lastSample;
    private bool visible;
    [GeneratedRegex(@"[^\S\r\n]+")]
    private static partial Regex ExtraSpaces();

    public SubtitlePipeline(Func<Options> options, TranslationService translator, Action<Subtitle?> display,
        Action<string> status, Action<double, double> timing, CancellationToken token)
    {
        this.options = options;
        this.translator = translator;
        this.display = display;
        this.status = status;
        this.timing = timing;
        lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        recognizerTask = Task.Run(Recognize);
        translatorTask = Task.Run(Translate);
    }

    public void Offer(VideoFrame frame)
    {
        var settings = options();
        if (!settings.Translate) return;
        var now = Stopwatch.GetTimestamp();
        if (lastSample != 0 && Stopwatch.GetElapsedTime(lastSample, now).TotalMilliseconds <
            (Volatile.Read(ref visible) ? 160 : settings.AutoRegion ? 240 : 300)) return;
        lastSample = now;
        var area = settings.AutoRegion ? new SubtitleRegion(.08, .52, .84, .45) : settings.Region;
        var x = (int)(area.X * frame.Width);
        var y = (int)(area.Y * frame.Height);
        var w = Math.Clamp((int)(area.Width * frame.Width), 1, frame.Width - x);
        var h = Math.Clamp((int)(area.Height * frame.Height), 1, frame.Height - y);
        var crop = new byte[w * h * 4];
        var pixels = frame.Buffer;
        for (int row = 0; row < h; row++)
            pixels.AsSpan(((y + row) * frame.Width + x) * 4, w * 4).CopyTo(crop.AsSpan(row * w * 4));
        samples.Writer.TryWrite(new(crop, w, h, area, Volatile.Read(ref scene)));
    }

    public void Reset()
    {
        Interlocked.Increment(ref scene);
        lock (sync)
        {
            version++;
            request?.Cancel();
            Volatile.Write(ref visible, false);
            display(null);
        }
    }

    private async Task Recognize()
    {
        var history = new Queue<string>();
        string current = "", candidate = "", language = "";
        int misses = 0, repetitions = 0, currentScene = -1;
        OcrEngine? engine = null;
        await foreach (var sample in samples.Reader.ReadAllAsync(lifetime.Token))
        {
            if (sample.Scene != Volatile.Read(ref scene)) continue;
            if (sample.Scene != currentScene)
            {
                currentScene = sample.Scene;
                current = candidate = "";
                misses = repetitions = 0;
            }
            try
            {
                var config = options();
                var selectedLanguage = config.OcrLanguage.Length == 0 ? config.SourceLanguage : config.OcrLanguage;
                if (language != selectedLanguage || engine is null)
                {
                    language = selectedLanguage;
                    engine = CreateOcr(language);
                }
                var started = Stopwatch.GetTimestamp();
                var reading = await Read(engine, sample);
                timing(Stopwatch.GetElapsedTime(started).TotalMilliseconds, -1);
                var text = reading.Text;
                if (sample.Scene != Volatile.Read(ref scene)) continue;
                if (text.Length == 0)
                {
                    candidate = "";
                    if (++misses >= 2 && current.Length > 0) { Reset(); current = ""; }
                    continue;
                }
                misses = 0;
                if (text == candidate) repetitions++;
                else { candidate = text; repetitions = 1; }
                if (text == current || repetitions < 2) continue;
                current = text;
                int revision;
                lock (sync)
                {
                    if (sample.Scene != Volatile.Read(ref scene)) continue;
                    revision = ++version;
                    request?.Cancel();
                    Volatile.Write(ref visible, true);
                    display(config.SubtitleMode == SubtitleMode.Original
                        ? new Subtitle(text, "", DateTimeOffset.Now, reading.Region) : null);
                    if (config.SubtitleMode != SubtitleMode.Original)
                        dialogue.Writer.TryWrite((revision, text, reading.Region, history.ToArray()));
                }
                history.Enqueue(text);
                if (history.Count > 4) history.Dequeue();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { break; }
            catch (Exception ex) { status(ex.Message); }
        }
    }

    private async Task Translate()
    {
        await foreach (var item in dialogue.Reader.ReadAllAsync(lifetime.Token))
        {
            using var local = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            lock (sync)
            {
                if (item.Version != version) continue;
                request = local;
            }
            try
            {
                var started = Stopwatch.GetTimestamp();
                var text = await translator.Translate(options(), item.Text, item.Context, local.Token);
                lock (sync)
                {
                    if (item.Version != version || local.IsCancellationRequested) continue;
                    display(new(item.Text, text, DateTimeOffset.Now, item.Region));
                    status("OCR + " + options().Provider);
                    timing(-1, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
            }
            catch (OperationCanceledException) when (local.IsCancellationRequested) { }
            catch (Exception ex)
            {
                lock (sync) if (item.Version == version && !local.IsCancellationRequested) status(ex.Message);
            }
            finally
            {
                lock (sync) if (ReferenceEquals(request, local)) request = null;
            }
        }
    }

    public static OcrEngine CreateOcr(string code)
    {
        var list = OcrEngine.AvailableRecognizerLanguages;
        var language = list.FirstOrDefault(l => l.LanguageTag.Equals(code, StringComparison.OrdinalIgnoreCase) ||
            l.LanguageTag.Split('-')[0].Equals(code, StringComparison.OrdinalIgnoreCase));
        return (language is null ? null : OcrEngine.TryCreateFromLanguage(new Language(language.LanguageTag)))
            ?? throw new InvalidOperationException($"Install the Windows OCR language pack for {code}.");
    }

    public static async Task<OcrReading> Read(OcrEngine engine, OcrSample sample)
    {
        var variant = sample.Bgra;
        int width = sample.Width, height = sample.Height;
        if (width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension)
        {
            var ratio = Math.Min(OcrEngine.MaxImageDimension / (double)width, OcrEngine.MaxImageDimension / (double)height);
            width = Math.Max(1, (int)(width * ratio));
            height = Math.Max(1, (int)(height * ratio));
            variant = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                sample.Bgra.AsSpan(((int)(y / ratio) * sample.Width + (int)(x / ratio)) * 4, 4)
                    .CopyTo(variant.AsSpan((y * width + x) * 4, 4));
        }
        var best = await Scan(variant);
        if (best.Quality < 8)
        {
            var contrast = new byte[variant.Length];
            for (int i = 0; i < variant.Length; i += 4)
            {
                var luminance = (variant[i] * 19 + variant[i + 1] * 183 + variant[i + 2] * 54) / 256;
                byte value = (byte)Math.Clamp((luminance - 110) * 3 + 90, 0, 255);
                contrast[i] = contrast[i + 1] = contrast[i + 2] = value;
                contrast[i + 3] = 255;
            }
            var enhanced = await Scan(contrast);
            if (enhanced.Quality > best.Quality) best = enhanced;
        }
        return best;

        async Task<OcrReading> Scan(byte[] pixels)
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(),
                BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Ignore);
            var result = await engine.RecognizeAsync(bitmap);
            var lines = result.Lines.Select(l => (Text: ExtraSpaces().Replace(l.Text, " ").Trim(),
                Bounds: l.Words.Select(w => w.BoundingRect).ToArray()))
                .Where(l => l.Text.Any(char.IsLetter) && l.Bounds.Length > 0).Take(5).ToArray();
            if (lines.Length == 0) return new("", sample.Region, 0);
            var text = string.Join('\n', lines.Select(l => l.Text));
            if (text.Length > 700) text = text[..700];
            var boxes = lines.SelectMany(l => l.Bounds).ToArray();
            double left = Math.Clamp(boxes.Min(r => r.Left) / width, 0, 1),
                right = Math.Clamp(boxes.Max(r => r.Right) / width, 0, 1),
                top = Math.Clamp(boxes.Min(r => r.Top) / height, 0, 1),
                bottom = Math.Clamp(boxes.Max(r => r.Bottom) / height, 0, 1);
            var area = sample.Region;
            var region = new SubtitleRegion(area.X + left * area.Width, area.Y + top * area.Height,
                (right - left) * area.Width, (bottom - top) * area.Height);
            return new(text, region, text.Count(char.IsLetter) / (1 + lines.Length * .25));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync();
        Reset();
        try { await Task.WhenAll(recognizerTask, translatorTask); }
        catch (OperationCanceledException) { }
        lifetime.Dispose();
    }
}
