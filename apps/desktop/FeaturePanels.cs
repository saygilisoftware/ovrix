using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Ovrix.Engine;
using Path = System.IO.Path;

namespace Ovrix;

public partial class MainWindow
{
    private void FramePanel()
    {
        Toggle(L("Kare interpolasyonu", "Frame interpolation"), Config.Interpolate,
            on => Change(o => o with { Interpolate = on }));
        Choice(L("Çıkış hedefi", "Output target"), new[] { 60, 120 }.Select(n => ($"{n} FPS", n)),
            Config.TargetFps, value => Change(o => o with { TargetFps = value }));
        Choice(L("Performans profili", "Performance preset"), [
            (L("Düşük gecikme", "Low latency"), FramePreset.LowLatency),
            (L("Dengeli", "Balanced"), FramePreset.Balanced),
            (L("Kalite", "Quality"), FramePreset.Quality),
            (L("Özel", "Custom"), FramePreset.Custom)], Config.Preset, preset =>
        {
            Change(o => o with { Preset = preset });
            RenderPanel();
        });
        if (Config.Preset == FramePreset.Custom)
            SliderField(L("Hareket arama yarıçapı", "Motion search radius"),
                Config.SearchRadius, 2, 24, 1, value => Change(o => o with { SearchRadius = (int)value }));
        Section(L("Görüntü", "Picture"));
        Toggle(L("Çözünürlüğü yükselt", "Upscale resolution"), Config.Upscale, enabled => Change(o => o with { Upscale = enabled }));
        Choice(L("Ölçekleme hedefi", "Scaling target"), new[] { 1080, 1440, 2160 }.Select(n => ($"{n}p", n)),
            Config.OutputHeight, value => Change(o => o with { OutputHeight = value }));
        SliderField(L("Keskinlik", "Sharpening"), Config.Sharpness, 0, .6, .02, value => Change(o => o with { Sharpness = value }));
        Help(L("Oynatıcı SDR sunar. Tanınan HDR video dosyalarında ton eşleme uygulanır.",
            "The player presents SDR. Recognized HDR video files are tone mapped."));
        Action(L("Kare grafiğini aç", "Open frame graph"), () => SelectPage(6));
    }

    private void TranslatePanel()
    {
        Toggle(L("Altyazı çevirisi", "Subtitle translation"), Config.Translate, enabled =>
        {
            Change(o => o with { Translate = enabled });
            translationStatus = "";
        });
        Toggle(L("Otomatik altyazı bölgesi", "Automatic subtitle region"), Config.AutoRegion,
            enabled => Change(o => o with { AutoRegion = enabled }));
        var regionButton = Action(L("Önizlemede OCR bölgesi çiz", "Draw OCR region on preview"), () =>
        {
            OverlayEditCanvas.Visibility = Visibility.Collapsed;
            RegionCanvas.Visibility = Visibility.Visible;
            PlaceRect(SelectionRect, Config.Region, VisibleVideo());
            SetStatus(L("Video üzerinde alanı sürükleyerek çizin. Esc ile iptal edin.",
                "Drag on the preview to mark a region. Press Esc to cancel."));
        });
        regionButton.IsEnabled = bitmap is not null;
        previewActions.Add(regionButton);
        try
        {
            var recognizers = Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
                .Select(language => (language.DisplayName, language.LanguageTag));
            Choice(L("OCR dili", "OCR language"), new[] { (L("Kaynak dili kullan", "Use source language"), "") }.Concat(recognizers),
                Config.OcrLanguage, language => Change(o => o with { OcrLanguage = language }));
        }
        catch (System.Runtime.InteropServices.COMException ex) { Help(ex.Message); }
        Section(L("Çeviri sağlayıcısı", "Translation provider"));
        Field(L("Kaynak dil kodu", "Source language tag"), Config.SourceLanguage,
            value => { if (value.Trim().Length > 0) Change(o => o with { SourceLanguage = value.Trim() }); });
        Field(L("Hedef dil kodu", "Target language tag"), Config.TargetLanguage,
            value => { if (value.Trim().Length > 0) Change(o => o with { TargetLanguage = value.Trim() }); });
        Choice(L("Sağlayıcı", "Provider"), [
            (L("Çevrimdışı model", "Offline model"), ProviderKind.Offline),
            (L("Yerel LLM", "Local LLM"), ProviderKind.LocalAi),
            (L("Bulut AI", "Cloud AI"), ProviderKind.CloudAi)], Config.Provider, provider =>
        {
            Change(o => o with { Provider = provider });
            RenderPanel();
        });
        SliderField(L("Zaman aşımı (sn)", "Timeout (s)"), Config.Timeout, 1, 60, 1,
            value => Change(o => o with { Timeout = (int)value }));
        switch (Config.Provider)
        {
            case ProviderKind.Offline: ModelControls(); break;
            case ProviderKind.LocalAi:
                Field(L("Yerel API adresi", "Local API endpoint"), Config.LocalEndpoint,
                    value => Change(o => o with { LocalEndpoint = value.Trim() }));
                Field(L("Yerel model adı", "Local model name"), Config.LocalModel,
                    value => Change(o => o with { LocalModel = value.Trim() }));
                break;
            case ProviderKind.CloudAi:
                Field(L("HTTPS API adresi", "HTTPS API endpoint"), Config.CloudEndpoint,
                    value => Change(o => o with { CloudEndpoint = value.Trim() }));
                Field(L("Bulut model adı", "Cloud model name"), Config.CloudModel,
                    value => Change(o => o with { CloudModel = value.Trim() }));
                var api = Password(L("API anahtarı", "API key"));
                Action(L("API anahtarını kaydet", "Save API key"), () =>
                {
                    if (api.Password.Length == 0) throw new ArgumentException(L("API anahtarını girin.", "Enter the API key."));
                    var secret = Encoding.UTF8.GetBytes(api.Password);
                    try { secrets.Write(TranslationService.CredentialId(Config.CloudEndpoint), secret); }
                    finally { CryptographicOperations.ZeroMemory(secret); api.Clear(); }
                    SetStatus(L("API anahtarı Windows DPAPI ile saklandı.", "API key stored with Windows DPAPI."));
                });
                Action(L("Kayıtlı API anahtarını kaldır", "Remove stored API key"), () =>
                {
                    secrets.Delete(TranslationService.CredentialId(Config.CloudEndpoint));
                    SetStatus(L("API anahtarı kaldırıldı.", "API key removed."));
                });
                Help(L("Bulut seçildiğinde algılanan metin ve yakın diyalog bağlamı bu adrese gönderilir.",
                    "Cloud mode sends recognized text and recent dialogue context to this endpoint."));
                break;
        }
        Field(L("Çeviride korunacak adlar", "Names to preserve"), Config.Glossary,
            value => Change(o => o with { Glossary = value }), true);
        Section(L("Altyazı görünümü", "Overlay appearance"));
        Choice(L("Metin", "Text"), [
            (L("Çeviri", "Translated"), SubtitleMode.Translated),
            (L("Çift dil", "Bilingual"), SubtitleMode.Bilingual),
            (L("Özgün", "Original"), SubtitleMode.Original)], Config.SubtitleMode,
            value => Change(o => o with { SubtitleMode = value }));
        Choice(L("Yazı tipi", "Font"), Fonts.SystemFontFamilies.OrderBy(f => f.Source)
            .Select(f => (f.Source, f.Source)), Config.Font, value => Change(o => o with { Font = value }));
        Choice(L("Metin rengi", "Text color"), [
            (L("Beyaz", "White"), "#FFFFFF"), (L("Sarı", "Yellow"), "#FFE184"), (L("Turkuaz", "Turquoise"), "#69EBD5")],
            Config.SubtitleColor, value => Change(o => o with { SubtitleColor = value }));
        SliderField(L("Yazı boyutu", "Font size"), Config.FontSize, 12, 72, 1,
            value => Change(o => o with { FontSize = value }));
        SliderField(L("Arka plan opaklığı", "Background opacity"), Config.BackgroundOpacity, 0, 1, .05,
            value => Change(o => o with { BackgroundOpacity = value }));
        Toggle(L("Kontur", "Outline"), Config.Outline, value => Change(o => o with { Outline = value }));
        Toggle(L("Gölge", "Shadow"), Config.Shadow, value => Change(o => o with { Shadow = value }));
        Toggle(L("Özgün altyazıyı maskele", "Mask original subtitles"), Config.MaskOriginal,
            value => Change(o => o with { MaskOriginal = value }));
        var placementButton = Action(L("Altyazıyı sürükleyerek konumlandır", "Drag to place subtitles"), () =>
        {
            RegionCanvas.Visibility = Visibility.Collapsed;
            OverlayEditCanvas.Visibility = Visibility.Visible;
            SetStatus(L("Altyazıyı video üzerinde sürükleyin.", "Drag over the video to place the caption."));
        });
        placementButton.IsEnabled = bitmap is not null;
        previewActions.Add(placementButton);
        Section(L("Altyazı geçmişi", "Subtitle history"));
        historyList = new ListBox { MaxHeight = 220, Margin = new Thickness(0, 0, 0, 9) };
        AutomationProperties.SetName(historyList, L("Altyazı geçmişi", "Subtitle history"));
        PanelBody.Children.Add(historyList);
        UpdateHistory();
        var correction = Field(L("Seçili satırın düzeltilmiş çevirisi", "Correction for selected line"), "", multiline: true);
        historyList.SelectionChanged += (_, _) =>
        {
            if (historyList.SelectedIndex >= 0 && historyList.SelectedIndex < history.Count)
                correction.Text = history[historyList.SelectedIndex].Translation;
        };
        Action(L("Düzeltmeyi kaydet", "Save correction"), () =>
        {
            if (historyList.SelectedIndex < 0 || correction.Text.Trim().Length == 0)
                throw new ArgumentException(L("Bir satır ve düzeltme girin.", "Select a subtitle and enter a correction."));
            var line = history[historyList.SelectedIndex];
            memory.Correct(Config, line.Original, correction.Text.Trim());
            SetStatus(L("Düzeltme çeviri belleğine kaydedildi.", "Correction saved in translation memory."));
        });
    }

    private void UpdateHistory()
    {
        if (historyList is null) return;
        var current = historyList.SelectedItem as string;
        historyList.ItemsSource = history.Select(s => s.Time.ToString("HH:mm:ss", CultureInfo.CurrentCulture) +
            "  " + (s.Translation.Length > 0 ? s.Translation : s.Original)).ToArray();
        if (current is not null) historyList.SelectedItem = current;
    }

    private void ModelControls()
    {
        Help(Config.OfflineModel.Length > 0 ? Path.GetFileName(Config.OfflineModel) :
            L("Çevrimdışı model seçilmedi.", "No offline model selected."));
        AsyncAction(L("İngilizce → Türkçe modelini indir", "Download English → Turkish model"), async () =>
        {
            SetStatus(L("Model indiriliyor ve doğrulanıyor…", "Downloading and verifying model…"));
            var path = await OfflineModel.Download(Path.Combine(SettingsStore.DefaultFolder, "models"), appLifetime.Token);
            Change(o => o with { OfflineModel = path, OfflineHash = OfflineModel.EnglishTurkishHash,
                SourceLanguage = "en", TargetLanguage = "tr" });
            SetStatus(L("Model SHA-256 ile doğrulandı.", "Model verified with SHA-256."));
            RenderPanel();
        });
        var hash = Field(L("Özel model SHA-256", "Custom model SHA-256"), "");
        AsyncAction(L("Yerel model dosyası seç", "Select local model file"), async () =>
        {
            var expected = hash.Text.Trim();
            if (expected.Length != 64 || !expected.All(Uri.IsHexDigit)) throw new ArgumentException(
                L("Model yayıncısının SHA-256 değerini girin.", "Enter the model publisher's SHA-256."));
            var picker = new OpenFileDialog { Filter = "Argos model|*.argosmodel" };
            if (picker.ShowDialog(this) != true) return;
            var actual = await OfflineModel.Hash(picker.FileName, appLifetime.Token);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Model SHA-256 mismatch.");
            Change(o => o with { OfflineModel = picker.FileName, OfflineHash = actual });
            RenderPanel();
        });
        if (Config.OfflineModel.Length > 0)
            Action(L("Modeli ayardan kaldır", "Remove model from settings"), () =>
            { Change(o => o with { OfflineModel = "", OfflineHash = "" }); RenderPanel(); });
    }

    private void ControllerPanel()
    {
        controllerDetails = Help("");
        Section(L("Remote Play eşlemesi", "Remote Play mapping"));
        Help(L("A / Cross · B / Circle\nX / Square · Y / Triangle\nLB / L1 · RB / R1\nAnalog çubuklar, tetikler ve yön tuşları",
            "A / Cross · B / Circle\nX / Square · Y / Triangle\nLB / L1 · RB / R1\nAnalog sticks, triggers and D-pad"));
        RefreshMetrics();
    }

    private void ProfilesPanel()
    {
        var profiles = store.Profiles();
        string selected = profiles.Contains(Config.ActiveProfile) ? Config.ActiveProfile : "GTA VI";
        Choice(L("Oyun profili", "Game profile"), profiles.Select(p => (p, p)), selected, name => selected = name);
        Action(L("Profili uygula", "Apply profile"), () =>
        {
            if (session is not null) throw new InvalidOperationException(L("Önce oynatmayı durdurun.", "Stop playback first."));
            Change(o => MergeProfile(o, store.LoadProfile(selected), selected));
            SetStatus(L("Profil uygulandı · ", "Profile applied · ") + selected);
            RenderPanel();
        });
        var input = Field(L("Yeni profil adı", "New profile name"), "");
        Action(L("Ayarları profil olarak kaydet", "Save settings as profile"), () =>
        {
            var name = input.Text.Trim();
            if (name.Equals("GTA VI", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(L("Yerleşik profili başka adla kaydedin.", "Save the built-in profile under a different name."));
            store.SaveProfile(name, PortableProfile(Config));
            Change(o => o with { ActiveProfile = name });
            RenderPanel();
        });
        Action(L("Profil içe aktar", "Import profile"), () =>
        {
            var picker = new OpenFileDialog { Filter = "OVRIX JSON|*.json" };
            if (picker.ShowDialog(this) != true) return;
            var name = Path.GetFileNameWithoutExtension(picker.FileName);
            if (name.Equals("GTA VI", StringComparison.OrdinalIgnoreCase)) name = "GTA VI (import)";
            store.SaveProfile(name, PortableProfile(SettingsStore.Read(picker.FileName)));
            SetStatus(L("Profil içe aktarıldı · ", "Profile imported · ") + name);
            RenderPanel();
        });
        Action(L("Seçili profili dışa aktar", "Export selected profile"), () =>
        {
            var picker = new SaveFileDialog { Filter = "OVRIX JSON|*.json", FileName = selected };
            if (picker.ShowDialog(this) == true)
                SettingsStore.Write(picker.FileName, PortableProfile(store.LoadProfile(selected)));
        });
        Toggle(L("Kaynak adına göre profili otomatik yükle", "Auto-load profile matching the source name"),
            Config.AutoProfile, value => Change(o => o with { AutoProfile = value }));
    }

    private static Options PortableProfile(Options o) => o with
    {
        Ffmpeg = "", Nixani = "", Python = "", OfflineModel = "", OfflineHash = "",
        CloudEndpoint = "", CloudModel = "", LocalEndpoint = new Options().LocalEndpoint,
        LocalModel = "", UpdateRepository = ""
    };

    private static Options MergeProfile(Options current, Options imported, string name)
    {
        var source = name == "GTA VI" ? current : imported;
        return imported with
        {
            Source = source.Source, File = source.File, CaptureDevice = source.CaptureDevice,
            CaptureMode = source.CaptureMode, CaptureAudio = source.CaptureAudio, ConsoleHost = source.ConsoleHost,
            Ffmpeg = current.Ffmpeg, Nixani = current.Nixani, Python = current.Python, Language = current.Language,
            OfflineModel = current.OfflineModel, OfflineHash = current.OfflineHash,
            CloudEndpoint = current.CloudEndpoint, CloudModel = current.CloudModel, LocalEndpoint = current.LocalEndpoint,
            LocalModel = current.LocalModel, UpdateRepository = current.UpdateRepository,
            AutoProfile = current.AutoProfile, ActiveProfile = name
        };
    }

    private void MonitorPanel()
    {
        Section(L("Özgün kare aralığı (ms)", "Unique frame interval (ms)"));
        var chart = new Canvas { Height = 83, Width = 260, Background = Color("Surface"), ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left };
        PanelBody.Children.Add(chart);
        graph = new Polyline { Stroke = Color("Accent"), StrokeThickness = 1.8 };
        chart.Children.Add(graph);
        Section(L("Ölçümler", "Measurements"));
        monitorDetails = Help("");
        RefreshMetrics();
    }

    private void SettingsPanel()
    {
        Choice(L("Arayüz dili", "Interface language"), new[] { ("Türkçe", "tr"), ("English", "en") },
            Config.Language, value => { Change(o => o with { Language = value }); SelectPage(page); });
        Section(L("Video ve modeller", "Video and models"));
        Toggle(L("Donanım kod çözmeyi dene", "Try hardware decoding"), Config.HardwareDecode,
            on => Change(o => o with { HardwareDecode = on }));
        PathPicker("FFmpeg", Config.Ffmpeg, path => Change(o => o with { Ffmpeg = path }));
        PathPicker("Nixani Engine", Config.Nixani, path => Change(o => o with { Nixani = path }));
        PathPicker("Python", Config.Python, path => Change(o => o with { Python = path }));
        AsyncAction(L("Bağımlılıkları denetle", "Verify dependencies"), async () =>
        {
            var tool = ProcessTools.Find(Config.Ffmpeg, "ffmpeg");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(appLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var version = await ProcessTools.CaptureAsync(tool, ["-version"], timeout.Token);
            if (version.ExitCode != 0) throw new IOException("FFmpeg version check failed.");
            var firstLine = version.Output.Split('\n')[0].Trim();
            SetStatus(firstLine + " · SHA-256 " + await OfflineModel.Hash(tool, timeout.Token));
        });
        Action(L("Windows OCR dil ayarlarını aç", "Open Windows OCR language settings"),
            () => Open("ms-settings:regionlanguage"));
        ModelControls();
        Section(L("Gizlilik ve tanılama", "Privacy and diagnostics"));
        Help(L("Ayarlar ve çeviri belleği bu bilgisayarda saklanır. Otomatik telemetri yoktur.",
            "Settings and translation memory stay on this computer. Automatic telemetry is disabled."));
        Action(L("Yerel günlük klasörünü aç", "Open local logs folder"), () => Open(SettingsStore.DefaultFolder));
        Section(L("Güncelleme", "Updates"));
        Field(L("GitHub deposu (sahip/depo)", "GitHub repository (owner/repo)"), Config.UpdateRepository,
            value => Change(o => o with { UpdateRepository = value.Trim() }));
        AsyncAction(L("Güncellemeleri denetle", "Check for updates"), CheckUpdates);
        Section(L("Hakkında", "About"));
        Help(L("OVRIX 1.0.0 · MIT. Bağımsız açık kaynak projesi.", "OVRIX 1.0.0 · MIT. Independent open-source project."));
        Action(L("Açık kaynak bildirimleri", "Open-source notices"), () =>
            Open(Path.Combine(AppContext.BaseDirectory, "THIRD_PARTY_NOTICES.md")));
    }

    private void PathPicker(string title, string path, Action<string> changed)
    {
        Field(title, path, value => changed(value.Trim()));
        Action(L($"{title} dosyası seç", $"Browse for {title}"), () =>
        {
            var picker = new OpenFileDialog { Filter = "Windows executable|*.exe" };
            if (picker.ShowDialog(this) == true) { changed(picker.FileName); RenderPanel(); }
        });
    }

    private async Task CheckUpdates()
    {
        var parts = Config.UpdateRepository.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p.Length > 100 ||
            !p.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
            throw new ArgumentException(L("Geçerli bir GitHub depo adı girin.", "Enter a valid GitHub repository name."));
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OVRIX/1.0");
        using var response = await http.GetAsync($"https://api.github.com/repos/{parts[0]}/{parts[1]}/releases/latest",
            appLifetime.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            SetStatus(L("Depoda herkese açık sürüm bulunamadı.", "No public release found for this repository."));
            return;
        }
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(appLifetime.Token));
        var tag = data.RootElement.GetProperty("tag_name").GetString() ?? "";
        var newer = Version.TryParse(tag.TrimStart('v'), out var release) && release > new Version(1, 0, 0);
        SetStatus(newer ? L($"Yeni sürüm var · {tag}", $"Update available · {tag}") :
            L($"Son GitHub sürümü · {tag}", $"Latest GitHub release · {tag}"));
    }

    private static void Open(string path) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
    { UseShellExecute = true });
}
