using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ovrix.Engine;

internal static class LiveHudTest
{
    private sealed record Cue(int Id, double From, double Until, string English);
    private sealed record Result(int Id, string English, string Ocr, string Turkish, int Milliseconds);
    private static readonly Cue[] Cues =
    [
        new(1, 1.5, 5.5, "Hello, we should go now."),
        new(2, 5.9, 9.1, "Do not move until the guard leaves."),
        new(3, 9.5, 12.7, "Meet me at the station."),
        new(4, 13.1, 16.3, "You have three minutes."),
        new(5, 16.7, 19.9, "I am running out of time."),
        new(6, 20.3, 23.5, "That was a close call."),
        new(7, 23.9, 27.1, "Hello, we should go now."),
        new(8, 27.5, 27.68, "Go!")
    ];
    private const int Width = 1280, Height = 720;

    public static Task Run(string scratch, string output, string python, string model, bool record)
    {
        Directory.CreateDirectory(output);
        var scene = Path.Combine(scratch, "scene.png");
        DrawScene(scene);
        var video = Path.Combine(output, "OVRIX-hud-test.mp4");
        BuildVideo(scene, video);
        var home = Path.Combine(scratch, "hud");
        var recognizers = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages;
        var language = recognizers.FirstOrDefault(l => l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) ??
            recognizers.First(l => l.LanguageTag.StartsWith("tr", StringComparison.OrdinalIgnoreCase));
        new SettingsStore(home).Save(new Options
        {
            File = video, HardwareDecode = false, Interpolate = true, TargetFps = 60,
            Preset = FramePreset.LowLatency, Translate = true, SubtitleMode = SubtitleMode.Translated,
            OcrLanguage = language.LanguageTag, Python = python, OfflineModel = model,
            OfflineHash = OfflineModel.EnglishTurkishHash, Timeout = 15, FontSize = 26
        });
        AppContext.SetData("OVRIX.DataFolder", home);

        var app = new Ovrix.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var player = new Ovrix.MainWindow();
        player.Show();
        var start = (Button)player.FindName("PlayButton");
        var preview = (Image)player.FindName("Video");
        var root = (FrameworkElement)player.FindName("Root");
        var videoArea = (FrameworkElement)player.FindName("VideoArea");
        var captionBox = (Border)player.FindName("SubtitleBox");
        var captionField = player.GetType().GetField("currentSubtitle",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ??
            throw new InvalidOperationException("The player subtitle inspection point is missing.");
        var stopwatch = Stopwatch.StartNew();
        var frame = new DispatcherFrame();
        var firstFrame = new Dictionary<int, long>();
        var results = new Dictionary<int, Result>();
        var pixel = new byte[4];
        Subtitle? previous = null;
        bool screenshot = false;
        double? runStart = null;
        var recording = Path.Combine(output, "hud-frames");
        if (record) Directory.CreateDirectory(recording);
        int captured = 0;
        long lastCapture = 0;
        var poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        poll.Tick += (_, _) =>
        {
            if (preview.Source is WriteableBitmap bitmap)
            {
                runStart ??= stopwatch.Elapsed.TotalMilliseconds;
                bitmap.CopyPixels(new Int32Rect(8, 8, 1, 1), pixel, 4, 0);
                var id = (int)Math.Round(pixel[2] / 30d);
                var now = stopwatch.ElapsedMilliseconds;
                if (record && id == 4 && now - lastCapture >= 67)
                {
                    lastCapture = now;
                    SaveVisual(root, Path.Combine(recording, $"frame{captured++:D3}.jpg"), jpeg: true);
                }
                if (id > 0 && id <= Cues.Length && Math.Abs(pixel[2] - id * 30) <= 10)
                {
                    firstFrame.TryAdd(id, now);
                    if (captionBox.IsVisible && captionField.GetValue(player) is Subtitle subtitle &&
                        !ReferenceEquals(subtitle, previous))
                    {
                        previous = subtitle;
                        var cue = Cues[id - 1];
                        var row = new Result(id, cue.English, subtitle.Original, subtitle.Translation,
                            checked((int)(now - firstFrame[id])));
                        results.TryAdd(id, row);
                        if (id == 3 && !screenshot)
                        {
                            screenshot = true;
                            SaveVisual(root, Path.Combine(output, "OVRIX-hud.png"));
                            SaveVisual(videoArea, Path.Combine(output, "OVRIX-hud-detail.png"));
                        }
                    }
                }
            }
            if ((runStart is not null && stopwatch.Elapsed.TotalMilliseconds - runStart > 29000) ||
                stopwatch.Elapsed.TotalSeconds > 45)
                frame.Continue = false;
        };
        try
        {
            start.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            poll.Start();
            Dispatcher.PushFrame(frame);
            var report = new
            {
                ocrLanguage = language.LanguageTag,
                mode = "Offline, fresh SQLite cache, file input. Polls the presented source marker and WPF caption.",
                cues = Cues.Select(cue => new
                {
                    id = cue.Id, source = cue.English, durationMs = (int)Math.Round((cue.Until - cue.From) * 1000),
                    sourceObserved = firstFrame.ContainsKey(cue.Id),
                    observed = results.GetValueOrDefault(cue.Id)
                }).ToArray()
            };
            File.WriteAllText(Path.Combine(output, "results.json"),
                JsonSerializer.Serialize(report, new JsonSerializerOptions
                {
                    WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                }));
            foreach (var row in report.cues)
                Console.WriteLine(row.observed is { } result
                    ? $"#{row.id} {result.Milliseconds} ms | {result.Ocr} → {result.Turkish}"
                    : $"#{row.id} no subtitle observed | {row.source}");
            if (Cues.Any(cue => !firstFrame.ContainsKey(cue.Id)) ||
                Cues.Where(cue => cue.Until - cue.From >= 3).Any(cue => !results.ContainsKey(cue.Id)) ||
                !screenshot || (record && captured < 24))
                throw new InvalidOperationException("A source marker, sustained caption, or HUD capture was missing.");
        }
        finally
        {
            poll.Stop();
            player.Close();
            app.Shutdown();
        }
        return Task.CompletedTask;
    }

    private static void BuildVideo(string scene, string output)
    {
        var filters = new List<string>
        {
            "zoompan=z=1.045:x='(iw-iw/zoom)*(0.5+0.34*sin(on/95))':y='(ih-ih/zoom)*(0.45+0.28*sin(on/130))':d=1:s=1280x720:fps=30",
            "drawbox=x=4:y=4:w=10:h=10:color=black:t=fill"
        };
        foreach (var cue in Cues)
        {
            var range = FormattableString.Invariant($"between(t,{cue.From},{cue.Until})");
            filters.Add($"drawbox=x=4:y=4:w=10:h=10:color=0x{cue.Id * 30:X2}0000:t=fill:enable='{range}'");
            filters.Add($"drawtext=font=Segoe UI:text='{cue.English}':fontsize=40:fontcolor=white:borderw=2:" +
                $"bordercolor=black:shadowx=1:shadowy=2:x=(w-tw)/2:y=h*0.78:enable='{range}'");
        }
        var ffmpeg = ProcessTools.Find("", "ffmpeg");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result = ProcessTools.CaptureAsync(ffmpeg,
            ["-v", "error", "-y", "-loop", "1", "-framerate", "30", "-i", scene, "-vf", string.Join(',', filters),
             "-t", "29", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "4", "-pix_fmt", "yuv420p", output],
            timeout.Token).GetAwaiter().GetResult();
        if (result.ExitCode != 0) throw new IOException(result.Errors);
    }

    private static void DrawScene(string path)
    {
        var visual = new DrawingVisual();
        using (var d = visual.RenderOpen())
        {
            d.DrawRectangle(new LinearGradientBrush(Color.FromRgb(12, 23, 43), Color.FromRgb(96, 61, 79), 90),
                null, new Rect(0, 0, Width, Height));
            var glow = new RadialGradientBrush(Color.FromArgb(160, 235, 109, 116), Colors.Transparent);
            d.DrawEllipse(glow, null, new Point(827, 275), 430, 260);
            d.DrawEllipse(Brush("#F1AC9C"), null, new Point(827, 237), 65, 65);
            var seed = new Random(14);
            for (int layer = 0; layer < 3; layer++)
            {
                for (int x = -55; x < Width; )
                {
                    var w = seed.Next(35, 113) + layer * 15;
                    var roof = 340 - seed.Next(30, 116) - layer * 12;
                    var street = 390 + layer * 33;
                    d.DrawRectangle(Brush(layer == 0 ? "#42354E" : layer == 1 ? "#252B43" : "#132235"),
                        null, new Rect(x, roof, w, street - roof));
                    if (layer == 2)
                    {
                        for (int row = roof + 19; row < street - 12; row += 18)
                        for (int column = x + 14; column < x + w - 11; column += 21)
                            if (seed.Next(6) <= 2)
                                d.DrawRectangle(Brush(seed.Next(3) == 0 ? "#B46A73" : "#418396"),
                                    null, new Rect(column, row, 7, 4));
                    }
                    x += w + seed.Next(7, 22);
                }
            }
            d.DrawRectangle(new LinearGradientBrush(Color.FromRgb(20, 34, 46), Color.FromRgb(7, 14, 25), 90),
                null, new Rect(0, 432, Width, Height - 432));
            d.DrawGeometry(Brush("#223242"), null, Geometry.Parse("M 563,429 L 702,429 1124,720 136,720 Z"));
            d.DrawGeometry(Brush("#324655"), null, Geometry.Parse("M 601,429 L 667,429 887,720 356,720 Z"));
            d.DrawGeometry(Brush("#D1917E"), null, Geometry.Parse("M 632,440 L 635,440 647,545 636,545 Z M 638,575 L 652,575 677,720 645,720 Z"));
            d.DrawLine(new Pen(Brush("#478292"), 2), new Point(566, 432), new Point(145, 720));
            d.DrawLine(new Pen(Brush("#478292"), 2), new Point(703, 432), new Point(1124, 720));
            for (var i = 0; i < 13; i++)
            {
                double x = 20 + i * 105, y = 467 + seed.Next(-27, 32);
                d.DrawEllipse(new RadialGradientBrush(Color.FromArgb(135, 63, 205, 200), Colors.Transparent),
                    null, new Point(x, y), 77, 22);
                d.DrawEllipse(Brush("#87D1D0"), null, new Point(x, y), 3, 2);
            }
        }
        SaveVisual(visual, path, Width, Height);
    }

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));

    private static void SaveVisual(FrameworkElement view, string path, bool jpeg = false)
    {
        view.UpdateLayout();
        SaveVisual(view, path, (int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), jpeg);
    }

    private static void SaveVisual(Visual view, string path, int width, int height, bool jpeg = false)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(view);
        BitmapEncoder encoder = jpeg ? new JpegBitmapEncoder { QualityLevel = 92 } : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}
