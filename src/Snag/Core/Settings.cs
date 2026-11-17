using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snag.Core;

public enum DownloadMode { Video, Audio }

/// <summary>User preferences, stored as JSON next to the exe (data\settings.json),
/// or in %APPDATA%\Snag when the program folder isn't writable.</summary>
public sealed class Settings
{
    public string OutputFolder { get; set; } = KnownFolders.Downloads;
    public DownloadMode Mode { get; set; } = DownloadMode.Video;
    /// <summary>0 = best available. Chosen per link (every new link starts at best), so not saved.</summary>
    [JsonIgnore]
    public int MaxHeight { get; set; }
    public string VideoFormat { get; set; } = "mp4";
    public string AudioFormat { get; set; } = "mp3";
    public bool EmbedThumbnail { get; set; } = true;
    public bool EmbedChapters { get; set; } = true;
    public bool EmbedMetadata { get; set; } = true;
    public bool EmbedSubtitles { get; set; }
    public string SubtitleLanguages { get; set; } = "en.*";
    /// <summary>title | title-id | uploader-title</summary>
    public string FileNameStyle { get; set; } = "title";
    /// <summary>"" or "firefox"</summary>
    public string CookiesBrowser { get; set; } = "";
    public bool WatchClipboard { get; set; } = true;
    public int MaxParallel { get; set; } = 2;
    public bool ShowMoreOptions { get; set; }
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string? YtDlpPath { get; set; }
    public string? FfmpegPath { get; set; }

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() },
    };

    static string? _path;

    static string FilePath => _path ??= ResolvePath();

    static string ResolvePath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            Directory.CreateDirectory(local);
            var probe = Path.Combine(local, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(local, "settings.json");
        }
        catch
        {
            var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snag");
            Directory.CreateDirectory(roaming);
            return Path.Combine(roaming, "settings.json");
        }
    }

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), Json) ?? new Settings();
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* not fatal */ }
    }
}

public static class KnownFolders
{
    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    public static string Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p)!; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }
}
