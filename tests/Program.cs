using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ovrix.Engine;

internal static class Program
{
    private static string directory = "";
    private static int failed;

    [STAThread]
    private static int Main(string[] args)
    {
        directory = Path.Combine(Path.GetTempPath(), "ovrix-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            if (args is [var hudMode, var output, var python, var modelPath] && hudMode is "--hud" or "--record-hud")
            {
                Test("Live file playback, OCR, translation and HUD",
                    () => LiveHudTest.Run(directory, output, python, modelPath, hudMode == "--record-hud"));
                return failed == 0 ? 0 : 1;
            }
            Test("Profile and DPAPI round trip", Profiles);
            Test("Frame duplicates, scene cut, pooled disposal", Frames);
            Test("Software motion and aspect ratio", Software);
            Test("Protocol framing and malformed packet", Ipc);
            Test("Local HTTP translation, cache, correction, cancellation", LocalTranslation);
            if (args.Contains("--gpu")) Test("D3D11 motion, scaling, and benchmark", Gpu);
            else Console.WriteLine("BLOCKED GPU · --gpu required");
            if (args.Contains("--ocr")) Test("Windows OCR glyph and bounding box", Ocr);
            else Console.WriteLine("BLOCKED OCR · --ocr requires an installed Latin-script OCR language");
            if (args.Contains("--media")) Test("FFmpeg video, audio, timeline, invalid source", Media);
            if (args.Contains("--performance")) Test("Whole-pipeline 1080p30/60 performance matrix", Performance);
            var ui = Array.IndexOf(args, "--ui");
            if (ui >= 0 && ui + 1 < args.Length) Test("WPF navigation, layout and subtitle composition", () => Ui(args[ui + 1]));
            var model = Array.IndexOf(args, "--model");
            if (model >= 0 && model + 2 < args.Length)
            {
                Test("Offline English → Turkish model", () => Offline(args[model + 1], args[model + 2]));
                if (args.Contains("--ocr") && args.Contains("--media"))
                    Test("Video → OCR → offline Turkish subtitle", () => SubtitleFromVideo(args[model + 1], args[model + 2]));
            }
            if (args.Contains("--nixani"))
            {
                var exe = args[Array.IndexOf(args, "--nixani") + 1];
                Test("Real Nixani process handshake and LAN scan", async () =>
                {
                    using var token = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await using var client = await NixaniClient.Connect(exe, token.Token);
                    var devices = await client.Discover(token.Token);
                    Console.WriteLine($"  PS5 discovered: {devices.Length}");
                });
            }
            Console.WriteLine($"Failures: {failed}");
            return failed == 0 ? 0 : 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(directory, true);
        }
    }

    private static void Test(string name, Func<Task> test)
    {
        try { test().GetAwaiter().GetResult(); Console.WriteLine("PASS " + name); }
        catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + "\n" + ex); }
    }

    private static void Check(bool value, string description)
    {
        if (!value) throw new InvalidOperationException(description);
    }

    private static Task Profiles()
    {
        var store = new SettingsStore(directory);
        var options = new Options { Source = SourceKind.Hdmi, AudioDelay = 12, TargetFps = 120 };
        store.Save(options);
        store.SaveProfile("Fixture", options);
        Check(store.Load() == options && store.LoadProfile("Fixture") == options, "Profile round trip");
        try { store.SaveProfile("../outside", options); throw new Exception("Traversal accepted"); }
        catch (ArgumentException) { }
        var secrets = new SecretStore(Path.Combine(directory, "secrets"));
        secrets.Write("example", [1, 2, 3]);
        Check(secrets.Read("example")!.SequenceEqual(new byte[] { 1, 2, 3 }), "DPAPI round trip");
        var path = Directory.GetFiles(Path.Combine(directory, "secrets")).Single();
        Check(!File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "Secret stored unencrypted");
        secrets.Delete("example");
        Check(secrets.Read("example") is null, "Secret deletion");
        return Task.CompletedTask;
    }

    private static VideoFrame Pattern(int w, int h, int offset = 0)
    {
        var frame = new VideoFrame(w, h, offset / 120d);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int p = (y * w + x) * 4;
            var b = (byte)(((x - offset + 8192) / 7 & 1) == 0 ^ ((y / 11 & 1) == 0) ? 190 : 50);
            frame.Buffer[p] = frame.Buffer[p + 1] = frame.Buffer[p + 2] = b;
            frame.Buffer[p + 3] = 255;
        }
        return frame;
    }

    private static Task Frames()
    {
        using var a = Pattern(800, 450);
        using var b = Pattern(800, 450, 4);
        using var c = Pattern(800, 450);
        Check(FrameAnalysis.Compare(a, c) == (true, false), "Identical frames differ");
        Check(!FrameAnalysis.Compare(a, b).Duplicate, "Motion misclassified as duplicate");
        a.Buffer.AsSpan(0, a.Length).Fill(0);
        c.Buffer.AsSpan(0, c.Length).Fill(255);
        Check(FrameAnalysis.Compare(a, c).SceneCut, "Scene cut missed");
        a.Dispose();
        try { _ = a.Buffer; throw new Exception("Disposed buffer accessible"); }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    private static Task Software()
    {
        using var renderer = new FrameRenderer(false);
        using var a = Pattern(144, 81);
        using var b = Pattern(144, 81, 4);
        using var midpoint = renderer.Render(a, b, new Options());
        Check(midpoint.Generated && Math.Abs(midpoint.Seconds - 1d / 60) < .00001, "Midpoint timestamp");
        int center = (40 * midpoint.Width + 60) * 4;
        Check(midpoint.Buffer[center + 3] == 255, "Invalid alpha");
        using var resized = renderer.Render(a, null, new Options { Upscale = true, OutputHeight = 1080 });
        Check(resized.Width == 1920 && resized.Height == 1080, "Invalid aspect ratio");
        using var sharp = renderer.Render(a, null, new Options { Sharpness = .5 });
        Check(!sharp.Buffer.AsSpan(0, sharp.Length).SequenceEqual(a.Buffer.AsSpan(0, a.Length)), "Software sharpening did not affect pixels");
        return Task.CompletedTask;
    }

    private static async Task Ipc()
    {
        var bytes = NixaniClient.Encode(101, w => { w.Write(2u); NixaniClient.WriteString(w, "1.1"); });
        using var stream = new MemoryStream(bytes);
        var packet = await NixaniClient.Read(stream, default);
        using var reader = packet.Reader();
        Check(packet.Type == 101 && reader.ReadUInt32() == 2 &&
              NixaniClient.ReadString(reader) == "1.1", "Invalid handshake round trip");
        using var invalid = new MemoryStream(new byte[] { 0, 0, 0, 2, 0, 0 });
        try { await NixaniClient.Read(invalid, default); throw new Exception("Oversized payload accepted"); }
        catch (InvalidDataException) { }
        using var state = new MemoryStream();
        using var writer = new BinaryWriter(state);
        new PadSnapshot("Fixture", 16, 255, 0, 1, 2, 3, 4).Write(writer);
        Check(state.Length == 57 && state.ToArray()[14] == 255, "Pad wire format");
    }

    private static async Task LocalTranslation()
    {
        var portFinder = new TcpListener(IPAddress.Loopback, 0);
        portFinder.Start();
        var port = ((IPEndPoint)portFinder.LocalEndpoint).Port;
        portFinder.Stop();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var options = new Options { Provider = ProviderKind.LocalAi, LocalEndpoint = $"http://127.0.0.1:{port}/v1/", LocalModel = "fixture" };
        var memory = new TranslationMemory(Path.Combine(directory, "translation.db"));
        using var client = new TranslationService(memory, new SecretStore(Path.Combine(directory, "secrets")));
        var request = client.Translate(options, "Good evening.", ["Welcome."], default);
        var ctx = await server.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(3));
        using (var body = new StreamReader(ctx.Request.InputStream))
            Check((await body.ReadToEndAsync()).Contains("Good evening."), "Dialogue missing from request");
        Check(ctx.Request.RawUrl == "/v1/chat/completions", "Unexpected endpoint");
        var result = Encoding.UTF8.GetBytes("{\"choices\":[{\"message\":{\"content\":\"İyi akşamlar.\"}}]}");
        await ctx.Response.OutputStream.WriteAsync(result);
        ctx.Response.Close();
        Check(await request == "İyi akşamlar.", "Invalid HTTP response parsing");
        Check(await client.Translate(options, "Good evening.", ["Welcome."], default) == "İyi akşamlar.", "Cache miss");
        memory.Correct(options, "Good evening.", "İyi geceler.");
        Check(await client.Translate(options, "Good evening.", [], default) == "İyi geceler.", "Correction ignored");
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await client.Translate(options, "Please stop.", [], stop.Token); throw new Exception("Cancellation ignored"); }
        catch (OperationCanceledException) { }
        try { TranslationService.Endpoint("http://example.com/v1", true); throw new Exception("External local endpoint accepted"); }
        catch (ArgumentException) { }
    }

    private static Task Gpu()
    {
        using var gpu = new FrameRenderer();
        Check(gpu.GpuAvailable, "D3D11 device or shader initialization failed");
        using var a = Pattern(1920, 1080);
        using var b = Pattern(1920, 1080, 4);
        using var expected = Pattern(1920, 1080, 2);
        using (var result = gpu.Render(a, b, new Options { Preset = FramePreset.Custom }))
        {
            Check(gpu.GpuAvailable && result.Generated, "GPU fallback or missing synthesis");
            double left = Error(a, expected), middle = Error(result, expected);
            Console.WriteLine($"  synthetic midpoint MAE from prior {left:0.0}, produced {middle:0.0}");
            Check(middle < left * .85, "Synthesized midpoint does not improve motion alignment");
        }
        foreach (var radius in new[] { 6, 12, 24 })
        {
            var start = Stopwatch.GetTimestamp();
            for (int i = 0; i < 18; i++)
            {
                using var generated = gpu.Render(a, b, new Options { Preset = FramePreset.Custom, SearchRadius = radius });
            }
            Console.WriteLine($"  1080p GPU radius={radius}: {Stopwatch.GetElapsedTime(start).TotalMilliseconds / 18:0.00} ms/frame");
        }
        using var upscale = gpu.Render(a, null, new Options { Upscale = true, OutputHeight = 1440 });
        Check(upscale.Width == 2560 && upscale.Height == 1440 && gpu.GpuAvailable, "GPU scaling");
        return Task.CompletedTask;

        static double Error(VideoFrame frame, VideoFrame wanted)
        {
            double sum = 0;
            for (int y = 32; y < frame.Height - 32; y += 3)
            for (int x = 32; x < frame.Width - 32; x += 3)
            {
                var p = (y * frame.Width + x) * 4;
                sum += Math.Abs(frame.Buffer[p] - wanted.Buffer[p]);
            }
            return sum / ((frame.Width - 64d) * (frame.Height - 64d) / 9);
        }
    }

    private static async Task Ocr()
    {
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, 930, 180));
            context.DrawText(new FormattedText("Meet me at the station.", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 57, Brushes.White, 1), new Point(33, 50));
        }
        var bitmap = new RenderTargetBitmap(930, 180, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var bytes = new byte[930 * 180 * 4];
        bitmap.CopyPixels(bytes, 930 * 4, 0);
        var language = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
            .FirstOrDefault(l => l.LanguageTag.StartsWith("en"))?.LanguageTag ?? "tr";
        var result = await SubtitlePipeline.Read(SubtitlePipeline.CreateOcr(language),
            new(bytes, 930, 180, new SubtitleRegion(0, 0, 1, 1), 0));
        Console.WriteLine("  " + language + " OCR → " + result.Text);
        Check(result.Text.Contains("station", StringComparison.OrdinalIgnoreCase) && result.Region.IsValid &&
            result.Region.Width < .9, "OCR result or word bounds incorrect");
    }

    private static async Task<string> MakeFixture(string ffmpeg, int height, int fps, double seconds, bool hdr,
        CancellationToken token)
    {
        var width = height * 16 / 9;
        var path = Path.Combine(directory, $"test-{height}-{fps}-{hdr}.mp4");
        if (File.Exists(path)) return path;
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
            $"testsrc2=size={width}x{height}:rate={fps}", "-f", "lavfi", "-i", "sine=frequency=880:sample_rate=48000",
            "-t", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "-c:v", hdr ? "libx265" : "libx264",
            "-preset", "ultrafast", "-threads", "4", "-c:a", "aac", "-pix_fmt", hdr ? "yuv420p10le" : "yuv420p" };
        if (hdr) args.AddRange(["-x265-params",
            "log-level=error:pools=2:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc"]);
        args.Add(path);
        var build = await ProcessTools.CaptureAsync(ffmpeg, args, token);
        Check(build.ExitCode == 0, build.Errors);
        return path;
    }

    private static async Task Media()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var ffmpeg = ProcessTools.Find("", "ffmpeg");
        var path = await MakeFixture(ffmpeg, 360, 30, 2, false, token);
        var stamps = new List<double>();
        long bytes = 0;
        await FfmpegCapture.RunFile(ffmpeg, path, false, frame => { using (frame) stamps.Add(frame.Seconds); },
            (_, _, count) => Interlocked.Add(ref bytes, count), _ => { }, token);
        Console.WriteLine($"  {stamps.Count} decoded frames, end PTS {stamps.LastOrDefault():0.000}s, {bytes} PCM bytes");
        Check(stamps.Count == 60 && Math.Abs(stamps[59] - 59 / 30d) < .002, "Frame count or presentation timestamps");
        Check(bytes >= 375000, "Audio PCM missing or truncated");
        var annexB = Path.Combine(directory, "test-stream.h264");
        var remux = await ProcessTools.CaptureAsync(ffmpeg, [
            "-v", "error", "-y", "-i", path, "-an", "-c:v", "copy", "-bsf:v", "h264_mp4toannexb", "-f", "h264", annexB
        ], token);
        Check(remux.ExitCode == 0, remux.Errors);
        int streamed = 0;
        await FfmpegCapture.RunRemote(ffmpeg, new VideoInfo(640, 360, 30, false, ""), false,
            frame => { using (frame) streamed++; }, async (stream, stop) =>
            {
                await using var source = File.OpenRead(annexB);
                await source.CopyToAsync(stream, stop);
            }, token);
        Check(streamed == 60, "Annex B stream did not decode to 60 frames");
        var hdr = await MakeFixture(ffmpeg, 360, 30, 1, true, token);
        var info = await FfmpegCapture.Probe(ffmpeg, hdr, token);
        Check(info.IsHdr, "HDR transfer not detected");
        int converted = 0;
        await FfmpegCapture.RunFile(ffmpeg, hdr, false, frame => { frame.Dispose(); converted++; }, null, _ => { }, token);
        Check(converted == 30, "HDR tone map did not return frames");
        try
        {
            await FfmpegCapture.Probe(ffmpeg, path + ".missing", token);
            throw new Exception("Missing file was accepted");
        }
        catch (FileNotFoundException) { }
    }

    private static async Task Performance()
    {
        var ffmpeg = ProcessTools.Find("", "ffmpeg");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(100));
        var memory = new TranslationMemory(Path.Combine(directory, "perf.db"));
        var secrets = new SecretStore(Path.Combine(directory, "perf-secrets"));
        var failedScenarios = new List<string>();
        foreach (var (fps, doubling, upscale, preset) in new[]
        {
            (30, false, false, FramePreset.Balanced), (30, true, false, FramePreset.LowLatency),
            (30, true, false, FramePreset.Quality), (60, false, false, FramePreset.Balanced),
            (60, true, false, FramePreset.Balanced),
            (30, true, true, FramePreset.LowLatency)
        })
        {
            var path = await MakeFixture(ffmpeg, 1080, fps, 4, false, timeout.Token);
            var options = new Options { File = path, TargetFps = fps * 2, Interpolate = doubling, HardwareDecode = false,
                Upscale = upscale, Preset = preset };
            Playback? pipeline = null;
            int originals = 0, generated = 0;
            pipeline = new Playback(options, () => options, secrets, memory, frame =>
            {
                using (frame)
                {
                    pipeline!.ReportPresented(frame);
                    if (frame.Generated) generated++;
                    else originals++;
                }
            }, _ => { }, Console.WriteLine, _ => { }, _ => Task.FromResult<string?>(null), null);
            await pipeline.Run(timeout.Token);
            var stats = pipeline.Snapshot();
            Console.WriteLine($"  input={fps} ×2={doubling} 1440p={upscale} preset={preset} " +
                $"originals={originals} synth={generated} drops={stats.Dropped} lastGPU={stats.GenerationMs:0.00}ms " +
                $"audio={stats.AudioBufferMs:0.0}ms D3D11={stats.Gpu}");
            if (originals < fps * 3.5 || (doubling ? generated < fps * 2.75 : generated != 0))
                failedScenarios.Add($"{fps} FPS · interpolation={doubling} · upscale={upscale} · {preset}");
        }
        Check(failedScenarios.Count == 0, "Insufficient delivered originals or intermediate frames: " +
            string.Join(" / ", failedScenarios));
    }

    private static Task Ui(string output)
    {
        AppContext.SetData("OVRIX.DataFolder", Path.Combine(directory, "ui"));
        var app = new Ovrix.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.InitializeComponent();
        var window = new Ovrix.MainWindow();
        window.Show();
        try
        {
            window.UpdateLayout();
            var root = (FrameworkElement)window.FindName("Root");
            var nav = (System.Windows.Controls.StackPanel)window.FindName("Navigation");
            Check(root.ActualWidth > 900 && nav.Children.Count == 8, "Dashboard layout");
            Check(window.FindResource("BrandRing") is Geometry, "Missing branding geometry");
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth),
                (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var dump = File.Create(output)) png.Save(dump);
            var translate = (System.Windows.Controls.Button)nav.Children[3];
            translate.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(((System.Windows.Controls.TextBlock)window.FindName("PanelTitle")).Text == "Çeviri", "Translation page routing");
            var field = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var change = window.GetType().GetMethod("Change", field)!;
            var caption = window.GetType().GetMethod("ShowSubtitle", field)!;
            change.Invoke(window, [new Func<Options, Options>(o => o with { Translate = true, SubtitleMode = SubtitleMode.Bilingual })]);
            caption.Invoke(window, [new Subtitle("Go now.", "Şimdi git.", DateTimeOffset.Now, new SubtitleRegion())]);
            var overlay = (System.Windows.Controls.TextBlock)window.FindName("SubtitleText");
            Check(overlay.Text == "Go now.\nŞimdi git." &&
                  ((System.Windows.Controls.Border)window.FindName("SubtitleBox")).Visibility == Visibility.Visible,
                  "Bilingual subtitle composition");
        }
        finally
        {
            window.Close();
            app.Shutdown();
        }
        return Task.CompletedTask;
    }

    private static async Task Offline(string python, string model)
    {
        var o = new Options { Python = python, OfflineModel = model, OfflineHash = OfflineModel.EnglishTurkishHash, Timeout = 30 };
        using var translation = new TranslationService(new TranslationMemory(Path.Combine(directory, "offline.db")),
            new SecretStore(Path.Combine(directory, "secrets")));
        var result = await translation.Translate(o, "Hello, we should go now.", [], default);
        Console.WriteLine("  " + result);
        Check(result.Contains("Merhaba", StringComparison.OrdinalIgnoreCase) && result.Contains("gitmeliyiz"), "Not Turkish");
    }

    private static async Task SubtitleFromVideo(string python, string model)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var ffmpeg = ProcessTools.Find("", "ffmpeg");
        var video = Path.Combine(directory, "caption.mp4");
        var create = await ProcessTools.CaptureAsync(ffmpeg, [
            "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=0x10161a:s=960x540:r=30",
            "-vf", "drawtext=font=Segoe UI:text='Hello, we should go now.':fontsize=42:fontcolor=white:x=(w-tw)/2:y=h*0.79",
            "-t", "5", "-c:v", "libx264", "-preset", "ultrafast", "-threads", "2", video
        ], ct);
        Check(create.ExitCode == 0, create.Errors);
        var settings = new Options
        {
            File = video, HardwareDecode = false, Interpolate = false, Translate = true,
            Python = python, OfflineModel = model, OfflineHash = OfflineModel.EnglishTurkishHash, Timeout = 20,
            OcrLanguage = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .First(l => l.LanguageTag.StartsWith("tr") || l.LanguageTag.StartsWith("en")).LanguageTag
        };
        var rendered = 0;
        Subtitle? found = null;
        var pipeline = new Playback(settings, () => settings, new SecretStore(Path.Combine(directory, "secrets")),
            new TranslationMemory(Path.Combine(directory, "captions.db")), frame => { frame.Dispose(); rendered++; },
            caption => { if (caption is not null) found = caption; }, _ => { },
            message => Console.WriteLine("  " + message), _ => Task.FromResult<string?>(null), null);
        await pipeline.Run(ct);
        Console.WriteLine($"  Video frames {rendered}, subtitle: {found?.Translation}");
        Check(rendered >= 125 && found?.Translation.Contains("gitmeliyiz", StringComparison.OrdinalIgnoreCase) == true,
            "Concurrent video and subtitle output");
    }
}
