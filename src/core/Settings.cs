using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Ovrix.Engine;

public enum SourceKind { File, Hdmi, RemotePlay }
public enum FramePreset { LowLatency, Balanced, Quality, Custom }
public enum ProviderKind { Offline, LocalAi, CloudAi }
public enum SubtitleMode { Translated, Bilingual, Original }

public sealed record SubtitleRegion(double X = .12, double Y = .68, double Width = .76, double Height = .27)
{
    public bool IsValid => X >= 0 && Y >= 0 && Width >= .02 && Height >= .02 &&
        X + Width <= 1.000001 && Y + Height <= 1.000001;
}

public sealed record Options
{
    public string Language { get; init; } = "tr";
    public SourceKind Source { get; init; }
    public string File { get; init; } = "";
    public string CaptureDevice { get; init; } = "";
    public string CaptureMode { get; init; } = "";
    public string CaptureAudio { get; init; } = "";
    public string Ffmpeg { get; init; } = "";
    public string Nixani { get; init; } = "";
    public string ConsoleHost { get; init; } = "";
    public int RemoteHeight { get; init; } = 1080;
    public int RemoteFps { get; init; } = 60;
    public int Bitrate { get; init; }
    public bool InternetPlay { get; init; }
    public bool HardwareDecode { get; init; } = true;
    public bool Interpolate { get; init; } = true;
    public int TargetFps { get; init; } = 60;
    public FramePreset Preset { get; init; } = FramePreset.Balanced;
    public int SearchRadius { get; init; } = 12;
    public bool Upscale { get; init; }
    public int OutputHeight { get; init; } = 1440;
    public double Sharpness { get; init; }
    public bool Translate { get; init; }
    public bool AutoRegion { get; init; } = true;
    public SubtitleRegion Region { get; init; } = new();
    public string OcrLanguage { get; init; } = "";
    public string SourceLanguage { get; init; } = "en";
    public string TargetLanguage { get; init; } = "tr";
    public ProviderKind Provider { get; init; }
    public string Python { get; init; } = "";
    public string OfflineModel { get; init; } = "";
    public string OfflineHash { get; init; } = "";
    public string LocalEndpoint { get; init; } = "http://127.0.0.1:11434/v1/";
    public string LocalModel { get; init; } = "";
    public string CloudEndpoint { get; init; } = "";
    public string CloudModel { get; init; } = "";
    public int Timeout { get; init; } = 8;
    public string Glossary { get; init; } = "Jason Duval\nLucia Caminos\nVice City\nLeonida";
    public string Font { get; init; } = "Segoe UI";
    public string SubtitleColor { get; init; } = "#FFFFFF";
    public double FontSize { get; init; } = 28;
    public double BackgroundOpacity { get; init; } = .60;
    public bool Outline { get; init; } = true;
    public bool Shadow { get; init; } = true;
    public bool MaskOriginal { get; init; }
    public SubtitleMode SubtitleMode { get; init; }
    public double SubtitleX { get; init; } = .5;
    public double SubtitleY { get; init; } = .92;
    public string AudioOutput { get; init; } = "";
    public double Volume { get; init; } = .8;
    public bool Mute { get; init; }
    public int AudioDelay { get; init; }
    public string UpdateRepository { get; init; } = "";
    public string ActiveProfile { get; init; } = "";
    public bool AutoProfile { get; init; }

    public void Validate()
    {
        string[] fields = [File, CaptureDevice, CaptureMode, CaptureAudio, Ffmpeg, Nixani, ConsoleHost, Python,
            OfflineModel, OfflineHash, LocalEndpoint, LocalModel, CloudEndpoint, CloudModel, Glossary, Font,
            SubtitleColor, AudioOutput, UpdateRepository, ActiveProfile, OcrLanguage];
        if (fields.Any(field => field is null || field.Length > 16000) ||
            !Enum.IsDefined(Source) || !Enum.IsDefined(Preset) || !Enum.IsDefined(Provider) ||
            !Enum.IsDefined(SubtitleMode) || Language is not ("en" or "tr") ||
            TargetFps is not (60 or 120) || RemoteHeight is not (360 or 540 or 720 or 1080) ||
            RemoteFps is not (30 or 60) || OutputHeight is not (1080 or 1440 or 2160) ||
            SearchRadius is < 2 or > 24 || Bitrate is < 0 or > 100000 || Timeout is < 1 or > 60 ||
            AudioDelay is < -150 or > 500 || Volume is < 0 or > 1 ||
            Sharpness is < 0 or > .6 || FontSize is < 12 or > 72 ||
            BackgroundOpacity is < 0 or > 1 || SubtitleX is < 0 or > 1 || SubtitleY is < 0 or > 1 ||
            Region is null || !Region.IsValid || string.IsNullOrWhiteSpace(SourceLanguage) ||
            string.IsNullOrWhiteSpace(TargetLanguage) || string.IsNullOrWhiteSpace(Font))
            throw new InvalidDataException("Invalid settings values.");
    }
}

public sealed class SettingsStore(string folder)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static string DefaultFolder => AppContext.GetData("OVRIX.DataFolder") as string ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OVRIX");

    public Options Load() => System.IO.File.Exists(Path.Combine(folder, "settings.json"))
        ? Read(Path.Combine(folder, "settings.json")) : new();

    public void Save(Options value) => Write(Path.Combine(folder, "settings.json"), value);
    public void SaveProfile(string name, Options value) => Write(ProfilePath(name), value);
    public Options LoadProfile(string name) => name == "GTA VI" ? new Options() : Read(ProfilePath(name));
    public string[] Profiles() => Directory.Exists(Path.Combine(folder, "profiles"))
        ? ["GTA VI", .. Directory.GetFiles(Path.Combine(folder, "profiles"), "*.json")
            .Select(path => Path.GetFileNameWithoutExtension(path)!).Where(name => name != "GTA VI").Order()]
        : ["GTA VI"];

    public static Options Read(string file)
    {
        using var input = System.IO.File.OpenRead(file);
        if (input.Length > 262144) throw new InvalidDataException("Settings file is too large.");
        var value = JsonSerializer.Deserialize<Options>(input, Json)
            ?? throw new InvalidDataException("Empty settings file.");
        value.Validate();
        return value;
    }

    public static void Write(string file, Options value)
    {
        value.Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(value, Json));
            System.IO.File.Move(temporary, file, true);
        }
        finally { System.IO.File.Delete(temporary); }
    }

    private string ProfilePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64 || name is "." or ".." ||
            name != name.Trim() || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Invalid profile name.");
        return Path.Combine(folder, "profiles", name + ".json");
    }
}

/// <summary>Stores secrets with Windows DPAPI scoped to the current Windows user.</summary>
public sealed class SecretStore(string folder)
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OVRIX/windows-secrets/v1");
    private string PathFor(string id) => Path.Combine(folder,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".dpapi");

    public void Write(string id, byte[] value)
    {
        Directory.CreateDirectory(folder);
        var path = PathFor(id);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllBytes(temporary,
                ProtectedData.Protect(value, Entropy, DataProtectionScope.CurrentUser));
            System.IO.File.Move(temporary, path, true);
        }
        finally { System.IO.File.Delete(temporary); }
    }

    public byte[]? Read(string id)
    {
        var path = PathFor(id);
        return System.IO.File.Exists(path)
            ? ProtectedData.Unprotect(System.IO.File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)
            : null;
    }

    public void Delete(string id) => System.IO.File.Delete(PathFor(id));
}
