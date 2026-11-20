using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ovrix.Engine;
using Path = System.IO.Path;

namespace Ovrix;

public partial class MainWindow : Window
{
    private readonly SettingsStore store;
    private readonly SecretStore secrets;
    private readonly TranslationMemory memory;
    private readonly DispatcherTimer refresh;
    private readonly DispatcherTimer save;
    private readonly CancellationTokenSource appLifetime = new();
    private Options options;
    private int page;
    private bool full, closing, readyToClose;
    private string status = "", translationStatus = "";
    private Playback? playback;
    private CancellationTokenSource? session;
    private Task? playbackTask;
    private VideoFrame? pendingFrame;
    private int paintQueued;
    private WriteableBitmap? bitmap;
    private PlaybackMetrics? latest;
    private Subtitle? currentSubtitle;
    private readonly List<Subtitle> history = [];
    private readonly Queue<double> frameIntervals = new();
    private readonly TextBlock[] readouts = new TextBlock[4];
    private readonly SystemProbe system = new();
    private TextBlock? dashboardDetails, monitorDetails, controllerDetails;
    private ComboBox? inputSelector;
    private readonly List<Button> previewActions = [];
    private System.Windows.Shapes.Polyline? graph;
    private ListBox? historyList;
    private Point dragStart;
    private bool dragging;
    private RemoteDevice? selectedAccountConsole;
    private RemoteHost[] hosts = [];
    private RemoteDevice[] accountConsoles = [];
    private CaptureDevice[] captureDevices = [];
    private Ovrix.Engine.CaptureMode[] captureModes = [];
    private string modesDevice = "";
    private WindowState beforeFullscreen;
    private WindowStyle beforeFullscreenStyle;
    private ResizeMode beforeFullscreenResize;

    public MainWindow()
    {
        InitializeComponent();
        var stateFolder = SettingsStore.DefaultFolder;
        store = new(stateFolder);
        secrets = new(Path.Combine(stateFolder, "secrets"));
        memory = new(Path.Combine(stateFolder, "translations.db"));
        try { options = store.Load(); }
        catch (Exception ex) { options = new(); status = ex.Message; }

        save = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        save.Tick += (_, _) => { save.Stop(); Persist(); };
        refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        refresh.Tick += (_, _) => RefreshMetrics();
        refresh.Start();
        SelectPage(0);
        SourceInitialized += (_, _) => ApplyCaptionTheme();
        Loaded += (_, _) =>
        {
            if (AppContext.GetData("OVRIX.File") is string file && File.Exists(file))
            {
                Change(o => o with { Source = SourceKind.File, File = file });
                RenderPanel();
            }
            AnimateIdle(true);
        };
    }

    private Options Config => Volatile.Read(ref options);
    private string L(string tr, string en) => Config.Language == "tr" ? tr : en;
    private Brush Color(string name) => (Brush)FindResource(name);

    private void Change(Func<Options, Options> change)
    {
        Volatile.Write(ref options, change(Config));
        save.Stop();
        save.Start();
        RefreshChrome();
        LayoutCaption();
    }

    private void Persist()
    {
        try { store.Save(Config); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private string[] PageNames() => [
        L("Genel Bakış", "Dashboard"), L("Yakalama", "Capture"), L("Kare motoru", "Frame engine"),
        L("Çeviri", "Translate"), L("Kumanda", "Controller"), L("Profiller", "Profiles"),
        L("İzleme", "Monitoring"), L("Ayarlar", "Settings") ];

    private void SelectPage(int selection)
    {
        page = selection;
        Navigation.Children.Clear();
        var names = PageNames();
        for (int i = 0; i < names.Length; i++)
        {
            int target = i;
            var item = new Button
            {
                Content = IconText((Geometry)FindResource($"Nav{i}"), names[i], 17),
                Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 0, 6),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Foreground = Color(i == page ? "Accent" : "Muted"),
                Background = i == page ? Color("Selected") : Brushes.Transparent,
                BorderBrush = i == page ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 73, 75)) : Brushes.Transparent
            };
            AutomationProperties.SetName(item, names[i]);
            if (i == page) AutomationProperties.SetItemStatus(item, L("Seçili", "Selected"));
            item.Click += (_, _) => SelectPage(target);
            Navigation.Children.Add(item);
        }
        PanelTitle.Text = page == 0 ? L("Hızlı kontroller", "Quick controls") : names[page];
        BuildMetrics();
        RenderPanel();
        RefreshChrome();
        Reveal(PanelScroll);
    }

    private void BuildMetrics()
    {
        MetricCards.Children.Clear();
        string[] labels = [L("YAKALAMA FPS", "CAPTURE FPS"), L("İÇERİK FPS", "CONTENT FPS"),
            L("ÇIKIŞ FPS", "OUTPUT FPS"), L("ÜRETİLEN FPS", "GENERATED FPS")];
        for (var i = 0; i < labels.Length; i++)
        {
            var card = new Border
            {
                CornerRadius = new CornerRadius(10), Background = i == 2 ? Color("Selected") : Color("Panel"),
                BorderBrush = i == 2 ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(38, 73, 75)) : Color("Stroke"),
                BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, i == 3 ? 0 : 9, 0), Padding = new Thickness(12, 10, 8, 10)
            };
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = labels[i], Foreground = Color("Muted"), FontSize = 10 });
            readouts[i] = new TextBlock { Text = "—", FontSize = 23, FontWeight = FontWeights.SemiBold,
                Foreground = Color(i == 2 ? "Accent" : "Text") };
            stack.Children.Add(readouts[i]);
            card.Child = stack;
            MetricCards.Children.Add(card);
        }
        UpdateReadouts();
    }

    private void UpdateReadouts()
    {
        double[]? values = latest is null ? null : [latest.IncomingFps, latest.ContentFps, latest.OutputFps, latest.GeneratedFps];
        for (int i = 0; i < readouts.Length; i++)
            if (readouts[i] is { } text) text.Text = values is null ? "—" : values[i].ToString("0.0", CultureInfo.CurrentCulture);
    }

    private string SourceName() => Config.Source switch
    {
        SourceKind.File => L("Video dosyası", "Video file"),
        SourceKind.Hdmi => L("HDMI yakalama", "HDMI capture"),
        _ => "PS5 Remote Play"
    };

    private void RefreshChrome()
    {
        TitleText.Text = page == 0 ? PageNames()[0] : L("Önizleme", "Preview");
        var source = Config.Source switch
        {
            SourceKind.File => Path.GetFileName(Config.File),
            SourceKind.Hdmi => captureDevices.FirstOrDefault(d => d.Id == Config.CaptureDevice)?.Name ?? "",
            _ => Config.ConsoleHost
        };
        SourceText.Text = SourceName() + (source.Length == 0 ? "" : "  /  " + source);
        StatusText.Text = status.Length > 0 ? status : L("Oynatıcı hazır", "Player ready");
        AutomationProperties.SetName(StatusText, StatusText.Text);
        ToolTipService.SetToolTip(StatusText, StatusText.Text);
        var starting = bitmap is null && session is not null;
        EmptyTitle.Text = starting ? L("Video akışı açılıyor", "Opening video stream") :
            L("Video kaynağı bekleniyor", "Waiting for a video source");
        EmptyCopy.Text = starting ? SourceName() :
            L("Bir video dosyası açın, HDMI yakalama kartını seçin veya PS5 Remote Play bağlantısı kurun.",
                "Open a video file, select an HDMI capture card, or connect to your PS5 with Remote Play.");
        EmptyButton.Visibility = starting ? Visibility.Collapsed : Visibility.Visible;
        if (inputSelector is not null) inputSelector.IsEnabled = session is null;
        foreach (var action in previewActions) action.IsEnabled = bitmap is not null;
        EmptyButton.Content = L("Kaynak seç", "Choose source");
        var playingLabel = session is null ? L("Başlat", "Start") : L("Durdur", "Stop");
        if (!Equals(AutomationProperties.GetName(PlayButton), playingLabel))
        {
            AutomationProperties.SetName(PlayButton, playingLabel);
            PlayButton.Content = IconText(Geometry.Parse(session is null
                ? "M 3,2 L 13,8 3,14 Z" : "M 3,3 H 13 V 13 H 3 Z"), playingLabel, 12);
        }
        var fullLabel = L("Tam ekran", "Fullscreen");
        if (!Equals(AutomationProperties.GetName(FullscreenButton), fullLabel))
        {
            AutomationProperties.SetName(FullscreenButton, fullLabel);
            FullscreenButton.Content = IconText(Geometry.Parse(
                "M 1,6 V 1 H 6 M 10,1 H 15 V 6 M 15,10 V 15 H 10 M 6,15 H 1 V 10"), null, 14);
            FullscreenButton.ToolTip = fullLabel + " · F11";
        }
        LiveText.Text = Config.Source == SourceKind.File ? L("DOSYA", "FILE") : L("CANLI", "LIVE");
    }

    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => SetStatus(text));
            return;
        }
        status = text;
        RefreshChrome();
        if (text.Length > 0 && session is not null) Log(text);
    }

    private void Log(string text)
    {
        try
        {
            var path = Path.Combine(SettingsStore.DefaultFolder, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 256000) File.Move(path, path + ".1", true);
            Directory.CreateDirectory(SettingsStore.DefaultFolder);
            var cleaned = text.Replace('\r', ' ').Replace('\n', ' ');
            if (Config.File.Length > 0) cleaned = cleaned.Replace(Config.File, "<video>", StringComparison.OrdinalIgnoreCase);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {cleaned}\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void OpenCapture(object sender, RoutedEventArgs e) => SelectPage(1);
    private async void TogglePlayback(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (session is not null) { await session.CancelAsync(); return; }
        if (Config.Source == SourceKind.File && !File.Exists(Config.File) && !PickVideo()) return;
        var prepared = SelectAutomaticProfile(Config);
        prepared.Validate();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        session = cancellation;
        latest = null;
        bitmap = null;
        Video.Source = null;
        EmptyState.Visibility = Visibility.Visible;
        AnimateIdle(true);
        UpdateReadouts();
        SetStatus(L("Kaynak açılıyor…", "Opening source…"));
        var run = new Playback(prepared, () => Config, secrets, memory, QueueFrame, ShowSubtitle,
            SetStatus, message => Dispatcher.BeginInvoke(() => translationStatus = message),
            RequestPin, selectedAccountConsole?.Uid);
        playback = run;
        playbackTask = Task.Run(() => run.Run(cancellation.Token));
        try
        {
            await playbackTask;
            SetStatus(L("Oynatma sona erdi.", "Playback ended."));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetStatus(L("Oynatma durduruldu.", "Playback stopped."));
        }
        finally
        {
            session = null;
            playbackTask = null;
            playback = null;
            cancellation.Dispose();
            LiveBadge.Visibility = Visibility.Collapsed;
            LiveDot.BeginAnimation(OpacityProperty, null);
            RefreshChrome();
        }
    });

    private Options SelectAutomaticProfile(Options current)
    {
        if (!current.AutoProfile) return current;
        var name = current.Source == SourceKind.File ? Path.GetFileNameWithoutExtension(current.File) :
            current.Source == SourceKind.Hdmi
            ? captureDevices.FirstOrDefault(d => d.Id == current.CaptureDevice)?.Name
            : hosts.FirstOrDefault(h => h.Address == current.ConsoleHost)?.Name;
        var match = store.Profiles().FirstOrDefault(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (match is null || match == current.ActiveProfile) return current;
        var applied = MergeProfile(current, store.LoadProfile(match), match);
        Change(_ => applied);
        return applied;
    }

    private void QueueFrame(VideoFrame frame)
    {
        var discarded = Interlocked.Exchange(ref pendingFrame, frame);
        if (discarded is not null)
        {
            discarded.Dispose();
            playback?.ReportPresentationDrop();
        }
        if (Interlocked.Exchange(ref paintQueued, 1) == 0 && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PaintFrame));
    }

    private void PaintFrame()
    {
        Volatile.Write(ref paintQueued, 0);
        using var frame = Interlocked.Exchange(ref pendingFrame, null);
        if (frame is null || closing) return;
        if (bitmap?.PixelWidth != frame.Width || bitmap.PixelHeight != frame.Height)
        {
            bitmap = new(frame.Width, frame.Height, 96, 96, PixelFormats.Bgr32, null);
            Video.Source = bitmap;
            foreach (var action in previewActions) action.IsEnabled = true;
        }
        bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Buffer, frame.Width * 4, 0);
        playback?.ReportPresented(frame);
        if (EmptyState.Visibility == Visibility.Visible)
        {
            EmptyState.Visibility = Visibility.Collapsed;
            AnimateIdle(false);
            Reveal(Video);
        }
        LiveBadge.Visibility = full || session is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowSubtitle(Subtitle? subtitle)
    {
        if (!Dispatcher.CheckAccess())
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => ShowSubtitle(subtitle));
            return;
        }
        currentSubtitle = subtitle;
        if (subtitle is not null)
        {
            history.Insert(0, subtitle);
            if (history.Count > 100) history.RemoveAt(history.Count - 1);
            UpdateHistory();
        }
        LayoutCaption();
    }

    private Rect VisibleVideo()
    {
        var w = VideoArea.ActualWidth;
        var h = VideoArea.ActualHeight;
        if (bitmap is null || w <= 0 || h <= 0) return new Rect(0, 0, Math.Max(0, w), Math.Max(0, h));
        var scale = Math.Min(w / bitmap.PixelWidth, h / bitmap.PixelHeight);
        var (iw, ih) = (bitmap.PixelWidth * scale, bitmap.PixelHeight * scale);
        return new Rect((w - iw) / 2, (h - ih) / 2, iw, ih);
    }

    private void LayoutCaption()
    {
        var text = currentSubtitle;
        var o = Config;
        var area = VisibleVideo();
        if (text is null || area.Width < 15 || !o.Translate)
        {
            SubtitleBox.Visibility = OriginalMask.Visibility = Visibility.Collapsed;
            return;
        }
        var caption = o.SubtitleMode switch
        {
            SubtitleMode.Original => text.Original,
            SubtitleMode.Bilingual when text.Translation.Length > 0 => text.Original + "\n" + text.Translation,
            _ => text.Translation
        };
        SubtitleBox.Visibility = caption.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        SubtitleBox.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(o.BackgroundOpacity * 255), 4, 8, 9));
        var font = new FontFamily(o.Font);
        SubtitleText.FontFamily = SubtitleStroke.FontFamily = font;
        SubtitleText.FontSize = SubtitleStroke.FontSize = o.FontSize;
        SubtitleText.Text = SubtitleStroke.Text = caption;
        try { SubtitleText.Foreground = (Brush)new BrushConverter().ConvertFromString(o.SubtitleColor)!; }
        catch (FormatException) { SubtitleText.Foreground = Brushes.White; }
        SubtitleText.Effect = o.Shadow ? new DropShadowEffect { BlurRadius = 5, ShadowDepth = 2, Opacity = .8 } : null;
        SubtitleStroke.Visibility = o.Outline ? Visibility.Visible : Visibility.Collapsed;
        SubtitleStroke.Foreground = Brushes.Black;
        SubtitleStroke.Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 1 };
        SubtitleBox.MaxWidth = area.Width * .93;
        SubtitleBox.Measure(new Size(SubtitleBox.MaxWidth, area.Height));
        var size = SubtitleBox.DesiredSize;
        Canvas.SetLeft(SubtitleBox, area.Left + Math.Clamp(area.Width * o.SubtitleX - size.Width / 2, 0, Math.Max(0, area.Width - size.Width)));
        Canvas.SetTop(SubtitleBox, area.Top + Math.Clamp(area.Height * o.SubtitleY - size.Height / 2, 0, Math.Max(0, area.Height - size.Height)));
        OriginalMask.Visibility = o.MaskOriginal && o.SubtitleMode == SubtitleMode.Translated && caption.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;
        PlaceRect(OriginalMask, text.Region, area);
    }

    private static void PlaceRect(FrameworkElement rectangle, SubtitleRegion r, Rect area)
    {
        Canvas.SetLeft(rectangle, area.Left + r.X * area.Width);
        Canvas.SetTop(rectangle, area.Top + r.Y * area.Height);
        rectangle.Width = r.Width * area.Width;
        rectangle.Height = r.Height * area.Height;
    }

    private void VideoAreaResized(object sender, SizeChangedEventArgs e)
    {
        LayoutCaption();
        PlaceRect(SelectionRect, Config.Region, VisibleVideo());
    }

    private void RegionDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(RegionCanvas);
        if (!VisibleVideo().Contains(point)) return;
        dragging = RegionCanvas.CaptureMouse();
        dragStart = point;
    }

    private void RegionMove(object sender, MouseEventArgs e)
    {
        if (dragging) PlaceRect(SelectionRect, DragRegion(e.GetPosition(RegionCanvas)), VisibleVideo());
    }

    private SubtitleRegion DragRegion(Point end)
    {
        var a = VisibleVideo();
        var x = Math.Clamp(end.X, a.Left, a.Right);
        var y = Math.Clamp(end.Y, a.Top, a.Bottom);
        return new((Math.Min(dragStart.X, x) - a.Left) / a.Width,
            (Math.Min(dragStart.Y, y) - a.Top) / a.Height, Math.Abs(dragStart.X - x) / a.Width,
            Math.Abs(dragStart.Y - y) / a.Height);
    }

    private void RegionUp(object sender, MouseButtonEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        RegionCanvas.ReleaseMouseCapture();
        var region = DragRegion(e.GetPosition(RegionCanvas));
        RegionCanvas.Visibility = Visibility.Collapsed;
        if (region.IsValid) Change(o => o with { Region = region, AutoRegion = false });
        if (page == 3) RenderPanel();
    }

    private void OverlayDown(object sender, MouseButtonEventArgs e)
    {
        OverlayEditCanvas.CaptureMouse();
        MoveOverlay(e.GetPosition(OverlayEditCanvas));
    }
    private void OverlayMove(object sender, MouseEventArgs e)
    {
        if (OverlayEditCanvas.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed)
            MoveOverlay(e.GetPosition(OverlayEditCanvas));
    }
    private void OverlayUp(object sender, MouseButtonEventArgs e)
    {
        OverlayEditCanvas.ReleaseMouseCapture();
        OverlayEditCanvas.Visibility = Visibility.Collapsed;
    }
    private void MoveOverlay(Point p)
    {
        var a = VisibleVideo();
        if (a.Width > 0 && a.Height > 0) Change(o => o with
        {
            SubtitleX = Math.Clamp((p.X - a.Left) / a.Width, 0, 1),
            SubtitleY = Math.Clamp((p.Y - a.Top) / a.Height, 0, 1)
        });
    }

    private void ToggleFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!full);

    private void SetFullscreen(bool value)
    {
        if (value == full) return;
        full = value;
        if (value)
        {
            beforeFullscreen = WindowState;
            beforeFullscreenStyle = WindowStyle;
            beforeFullscreenResize = ResizeMode;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            WindowStyle = beforeFullscreenStyle;
            ResizeMode = beforeFullscreenResize;
            WindowState = beforeFullscreen;
        }
        SidebarColumn.Width = new GridLength(value ? 0 : 196);
        DetailsColumn.Width = new GridLength(value ? 0 : 325);
        Sidebar.Visibility = Details.Visibility = Header.Visibility = Footer.Visibility =
            value ? Visibility.Collapsed : Visibility.Visible;
        Center.Margin = value ? new Thickness(0) : new Thickness(27, 24, 24, 21);
        PreviewBorder.BorderThickness = value ? new Thickness(0) : new Thickness(1);
        LiveBadge.Visibility = !full && session is not null && bitmap is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11)
        {
            SetFullscreen(!full);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            dragging = false;
            RegionCanvas.ReleaseMouseCapture();
            OverlayEditCanvas.ReleaseMouseCapture();
            RegionCanvas.Visibility = OverlayEditCanvas.Visibility = Visibility.Collapsed;
            if (full) SetFullscreen(false);
        }
        else if (e.Key == Key.Space && Keyboard.FocusedElement is not (TextBox or PasswordBox or Button or ComboBox))
        {
            TogglePlayback(sender, e);
            e.Handled = true;
        }
    }

    private async void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (readyToClose) return;
        if (!closing && playbackTask is null)
        {
            closing = true;
            refresh.Stop();
            save.Stop();
            Persist();
            appLifetime.Cancel();
            Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
            appLifetime.Dispose();
            return;
        }
        e.Cancel = true;
        if (closing) return;
        closing = true;
        // The WPF Closing event must finish before we can close the window again.
        await Dispatcher.Yield(DispatcherPriority.Normal);
        refresh.Stop();
        save.Stop();
        Persist();
        await appLifetime.CancelAsync();
        if (playbackTask is { } running)
        {
            try { await running.WaitAsync(TimeSpan.FromSeconds(4)); }
            catch (Exception) { }
        }
        Interlocked.Exchange(ref pendingFrame, null)?.Dispose();
        appLifetime.Dispose();
        readyToClose = true;
        Close();
    }
}
