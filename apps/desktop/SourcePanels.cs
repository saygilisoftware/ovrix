using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Ovrix.Engine;

namespace Ovrix;

public partial class MainWindow
{
    private void RenderPanel()
    {
        PanelBody.Children.Clear();
        dashboardDetails = monitorDetails = controllerDetails = null;
        inputSelector = null;
        previewActions.Clear();
        historyList = null;
        graph = null;
        switch (page)
        {
            case 0: DashboardPanel(); break;
            case 1: CapturePanel(); break;
            case 2: FramePanel(); break;
            case 3: TranslatePanel(); break;
            case 4: ControllerPanel(); break;
            case 5: ProfilesPanel(); break;
            case 6: MonitorPanel(); break;
            case 7: SettingsPanel(); break;
        }
    }

    private void Section(string label)
    {
        PanelBody.Children.Add(new TextBlock
        {
            Text = label, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Foreground = Color("Accent"), Margin = new Thickness(0, 11, 0, 8)
        });
    }

    private TextBlock Help(string message)
    {
        var text = new TextBlock { Text = message, FontSize = 12, LineHeight = 17,
            Foreground = Color("Muted"), Margin = new Thickness(0, 3, 0, 12) };
        PanelBody.Children.Add(text);
        return text;
    }

    private void AddField(string name, FrameworkElement input)
    {
        var label = new TextBlock { Text = name, FontSize = 12, Foreground = Color("Muted"),
            Margin = new Thickness(0, 0, 0, 4) };
        PanelBody.Children.Add(label);
        AutomationProperties.SetLabeledBy(input, label);
        input.Margin = new Thickness(0, 0, 0, 10);
        PanelBody.Children.Add(input);
    }

    private Button Action(string label, Action action)
    {
        var button = AddButton(label);
        button.Click += (_, _) => { try { action(); } catch (Exception ex) { SetStatus(ex.Message); } };
        return button;
    }

    private Button AsyncAction(string label, Func<Task> action)
    {
        var button = AddButton(label);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try { await Guard(action); }
            finally { button.IsEnabled = true; }
        };
        return button;
    }

    private Button AddButton(string label)
    {
        var button = new Button { Content = label, Margin = new Thickness(0, 1, 0, 7),
            HorizontalContentAlignment = HorizontalAlignment.Left };
        PanelBody.Children.Add(button);
        return button;
    }

    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) when (appLifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { SetStatus(L("İşlem zaman aşımına uğradı.", "Operation timed out.")); }
        catch (Exception ex) { SetStatus(ex.Message); }
    }

    private TextBox Field(string label, string current, Action<string>? changed = null, bool multiline = false)
    {
        var input = new TextBox
        {
            Text = current, AcceptsReturn = multiline, Height = multiline ? 75 : double.NaN,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden
        };
        if (changed is not null) input.TextChanged += (_, _) => changed(input.Text);
        AddField(label, input);
        return input;
    }

    private PasswordBox Password(string label)
    {
        var input = new PasswordBox();
        AddField(label, input);
        return input;
    }

    private ComboBox Choice<T>(string label, IEnumerable<(string Name, T Value)> values, T current, Action<T> changed)
    {
        var choices = values.ToArray();
        var selection = new ComboBox { ItemsSource = choices.Select(x => x.Name).ToArray() };
        selection.SelectedIndex = Array.FindIndex(choices, x => EqualityComparer<T>.Default.Equals(x.Value, current));
        selection.SelectionChanged += (_, _) =>
        {
            if (selection.SelectedIndex >= 0) changed(choices[selection.SelectedIndex].Value);
        };
        AddField(label, selection);
        return selection;
    }

    private void Toggle(string label, bool enabled, Action<bool> change)
    {
        var input = new CheckBox { Content = label, IsChecked = enabled };
        input.Checked += (_, _) => change(true);
        input.Unchecked += (_, _) => change(false);
        PanelBody.Children.Add(input);
    }

    private void SliderField(string label, double value, double min, double max, double tick, Action<double> change)
    {
        var input = new Slider { Minimum = min, Maximum = max, Value = value, TickFrequency = tick,
            IsSnapToTickEnabled = true, SmallChange = tick, LargeChange = tick * 5,
            AutoToolTipPlacement = System.Windows.Controls.Primitives.AutoToolTipPlacement.TopLeft };
        input.ValueChanged += (_, _) => change(input.Value);
        AddField(label, input);
    }

    private void DashboardPanel()
    {
        Section(L("Video kaynağı", "Video source"));
        SourceSelector();
        if (Config.Source == SourceKind.File)
        {
            Help(Path.GetFileName(Config.File) is { Length: > 0 } title ? title : L("Dosya seçilmedi.", "No file selected."));
            Action(L("Video dosyası aç", "Open video file"), () => { if (PickVideo()) RenderPanel(); });
        }
        else Action(L("Kaynağı ayarla", "Configure source"), () => SelectPage(1));
        Section(L("Oynatma", "Playback"));
        Toggle(L("Kare interpolasyonu", "Frame interpolation"), Config.Interpolate,
            on => Change(o => o with { Interpolate = on }));
        Toggle(L("Altyazı çevirisi", "Subtitle translation"), Config.Translate,
            on => Change(o => o with { Translate = on }));
        AudioControls(false);
        Help(L("F11 · Tam ekran    Esc · Geri dön", "F11 · Fullscreen    Esc · Return"));
        dashboardDetails = Help("");
        dashboardDetails.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SourceSelector()
    {
        var box = Choice(L("Giriş", "Input"), [
            (L("Video dosyası", "Video file"), SourceKind.File),
            (L("HDMI yakalama", "HDMI capture"), SourceKind.Hdmi),
            ("PS5 Remote Play", SourceKind.RemotePlay)], Config.Source, source =>
        {
            Change(o => o with { Source = source });
            RenderPanel();
        });
        box.IsEnabled = session is null;
        inputSelector = box;
    }

    private bool PickVideo()
    {
        var picker = new OpenFileDialog { Title = L("Video dosyası seç", "Choose video file"),
            Filter = "Video|*.mp4;*.mkv;*.mov;*.avi;*.webm;*.m4v;*.ts;*.wmv|*.*|*.*" };
        if (picker.ShowDialog(this) != true) return false;
        Change(o => o with { File = picker.FileName, Source = SourceKind.File });
        return true;
    }

    private void CapturePanel()
    {
        SourceSelector();
        switch (Config.Source)
        {
            case SourceKind.File:
                Help(Config.File.Length > 0 ? Path.GetFileName(Config.File) : L("Dosya seçilmedi.", "No file selected."));
                Action(L("Video dosyası aç", "Open video file"), () => { if (PickVideo()) RenderPanel(); }).IsEnabled = session is null;
                break;
            case SourceKind.Hdmi:
                HdmiPanel();
                break;
            case SourceKind.RemotePlay:
                RemotePanel();
                break;
        }
        Section(L("Ses çıkışı", "Audio output"));
        AudioControls(true);
    }

    private void HdmiPanel()
    {
        AsyncAction(L("Yakalama aygıtlarını tara", "Scan capture devices"), async () =>
        {
            var ffmpeg = ProcessTools.Find(Config.Ffmpeg, "ffmpeg");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            captureDevices = await FfmpegCapture.Devices(ffmpeg, timeout.Token);
            if (captureDevices.Any(d => !d.Audio && d.Id == Config.CaptureDevice))
                await ScanModes(Config.CaptureDevice);
            if (page == 1) RenderPanel();
            SetStatus(L($"{captureDevices.Count(d => !d.Audio)} video kaynağı bulundu.",
                $"Found {captureDevices.Count(d => !d.Audio)} video sources."));
        }).IsEnabled = session is null;
        if (captureDevices.Length == 0)
        {
            Help(L("Kartı bağlayın ve aygıtları tarayın. Korumalı görüntü yakalanamaz.",
                "Connect the card and scan for devices. Protected video cannot be captured."));
            return;
        }
        Choice(L("Video aygıtı", "Video device"), captureDevices.Where(d => !d.Audio).Select(d => (d.Name, d.Id)),
            Config.CaptureDevice, id =>
            {
                Change(o => o with { CaptureDevice = id, CaptureMode = "" });
                _ = Guard(async () => { await ScanModes(id); if (page == 1 && Config.CaptureDevice == id) RenderPanel(); });
            }).IsEnabled = session is null;
        if (modesDevice == Config.CaptureDevice)
            Choice(L("Çözünürlük ve biçim", "Resolution and format"),
                new[] { (L("Otomatik", "Automatic"), "") }.Concat(captureModes.Select(m => (m.ToString(), m.Key))),
                Config.CaptureMode, mode => Change(o => o with { CaptureMode = mode })).IsEnabled = session is null;
        Choice(L("Yakalama sesi", "Capture audio"), new[] { (L("Kapalı", "Off"), "") }
            .Concat(captureDevices.Where(d => d.Audio).Select(d => (d.Name, d.Id))),
            Config.CaptureAudio, id => Change(o => o with { CaptureAudio = id })).IsEnabled = session is null;
    }

    private async Task ScanModes(string device)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        var modes = await FfmpegCapture.Modes(ProcessTools.Find(Config.Ffmpeg, "ffmpeg"), device, timeout.Token);
        if (Config.CaptureDevice != device) return;
        captureModes = modes;
        modesDevice = device;
        if (modes.Length == 0) SetStatus(L("Sürücü uyumlu bir video modu bildirmedi.", "The driver reported no compatible modes."));
    }

    private void RemotePanel()
    {
        AsyncAction(L("Yerel ağda PS5 ara", "Discover PS5 on local network"), async () =>
        {
            await using var client = await NixaniClient.Connect(Config.Nixani, appLifetime.Token);
            hosts = await client.Discover(appLifetime.Token);
            SetStatus(hosts.Length == 0 ? L("Yerel ağda PS5 bulunamadı.", "No PS5 discovered on the local network.") :
                L($"{hosts.Length} PS5 bulundu.", $"Found {hosts.Length} PS5 consoles."));
            if (page == 1) RenderPanel();
        }).IsEnabled = session is null;
        if (hosts.Length > 0)
            Choice(L("Yerel PS5", "Local PS5"), hosts.Select(h => (h.Name + " · " + h.Address, h.Address)),
                Config.ConsoleHost, host => { selectedAccountConsole = null; accountConsoles = []; Change(o => o with { ConsoleHost = host }); RenderPanel(); })
                .IsEnabled = session is null;
        var address = Field(L("Konsol IP veya ana makine adı", "Console IP or hostname"), Config.ConsoleHost,
            value => Change(o => o with { ConsoleHost = value.Trim() }));
        address.IsEnabled = session is null;
        var id = Field(L("PSN hesap kimliği (Base64)", "PSN account ID (Base64)"), "");
        var pairing = Password(L("8 haneli PS5 eşleştirme kodu", "8-digit PS5 pairing code"));
        AsyncAction(L("PS5 eşleştir", "Pair PS5"), async () =>
        {
            var host = Config.ConsoleHost;
            if (host.Length == 0) throw new ArgumentException(L("Konsol adresini girin.", "Enter the console address."));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var client = await NixaniClient.Connect(Config.Nixani, timeout.Token);
            var discovered = hosts.FirstOrDefault(h => h.Address == host);
            var registration = await client.Register(discovered ?? new RemoteHost(host, host, 1000100, 0),
                id.Text.Trim(), pairing.Password, 0, timeout.Token);
            secrets.Write(NixaniClient.RegistrationId(host), JsonSerializer.SerializeToUtf8Bytes(registration));
            id.Clear();
            pairing.Clear();
            SetStatus(L("Eşleştirildi · ", "Paired · ") + registration.Nickname);
        }).IsEnabled = session is null;
        Toggle(L("İnternet üzerinden bağlan", "Connect over the internet"), Config.InternetPlay,
            on => { Change(o => o with { InternetPlay = on }); RenderPanel(); });
        if (Config.InternetPlay)
        {
            var tokenBox = Password(L("Yetkili PSN Remote Play erişim belirteci", "Authorized PSN Remote Play access token"));
            AsyncAction(L("PSN konsollarını listele", "List PSN consoles"), async () =>
            {
                var host = Config.ConsoleHost.Trim();
                if (host.Length == 0) throw new ArgumentException(L("Önce eşleştirilen konsol adresini girin.", "Enter the paired console address first."));
                var access = tokenBox.Password;
                if (access.Length == 0)
                {
                    var existing = secrets.Read("psn-token:" + host);
                    if (existing is not null) { access = Encoding.UTF8.GetString(existing); CryptographicOperations.ZeroMemory(existing); }
                }
                if (access.Length == 0) throw new ArgumentException(L("PSN erişim belirteci girin.", "Enter the PSN access token."));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                await using var client = await NixaniClient.Connect(Config.Nixani, timeout.Token);
                accountConsoles = await client.AccountConsoles(access, timeout.Token);
                secrets.Write("psn-token:" + host, Encoding.UTF8.GetBytes(access));
                tokenBox.Clear();
                selectedAccountConsole = accountConsoles.Length == 1 ? accountConsoles[0] : null;
                if (page == 1) RenderPanel();
            }).IsEnabled = session is null;
            Choice(L("PSN konsolu", "PSN console"), accountConsoles.Select(d => (d.Name, d)),
                selectedAccountConsole!, d => selectedAccountConsole = d).IsEnabled = session is null;
        }
        Choice(L("Yayın çözünürlüğü", "Stream resolution"), new[] { 360, 540, 720, 1080 }.Select(n => ($"{n}p", n)),
            Config.RemoteHeight, value => Change(o => o with { RemoteHeight = value })).IsEnabled = session is null;
        Choice(L("Yayın FPS", "Stream FPS"), new[] { 30, 60 }.Select(n => ($"{n} FPS", n)),
            Config.RemoteFps, value => Change(o => o with { RemoteFps = value })).IsEnabled = session is null;
        SliderField(L("Bit hızı (kbps, 0 = otomatik)", "Bitrate (kbps, 0 = auto)"), Config.Bitrate, 0, 50000, 1000,
            value => Change(o => o with { Bitrate = (int)value }));
    }

    private Task<string?> RequestPin(string description) => Dispatcher.InvokeAsync<string?>(() =>
    {
        var prompt = new Window { Owner = this, Title = description, Width = 335, SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Color("Panel"), Foreground = Color("Text") };
        var column = new StackPanel { Margin = new Thickness(17) };
        var pass = new PasswordBox { MaxLength = 12, Margin = new Thickness(0, 0, 0, 12) };
        AutomationProperties.SetName(pass, description);
        var submit = new Button { Content = L("Onayla", "Confirm"), IsDefault = true };
        submit.Click += (_, _) => prompt.DialogResult = true;
        column.Children.Add(pass);
        column.Children.Add(submit);
        prompt.Content = column;
        pass.Focus();
        return prompt.ShowDialog() == true ? pass.Password : null;
    }).Task;

    private void AudioControls(bool includeDevice)
    {
        if (includeDevice)
        {
            try
            {
                Choice(L("Çıkış aygıtı", "Output device"), AudioOutput.Devices().Select(d => (d.Name, d.Id)),
                    Config.AudioOutput, id => Change(o => o with { AudioOutput = id })).IsEnabled = session is null;
            }
            catch (Exception ex) { Help(ex.Message); }
            SliderField(L("Ses gecikmesi (ms)", "Audio delay (ms)"), Config.AudioDelay, -150, 500, 5,
                value => Change(o => o with { AudioDelay = (int)value }));
        }
        SliderField(L("Ses düzeyi", "Volume"), Config.Volume, 0, 1, .05, value => Change(o => o with { Volume = value }));
        Toggle(L("Sessiz", "Mute"), Config.Mute, value => Change(o => o with { Mute = value }));
    }
}
