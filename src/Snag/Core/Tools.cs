namespace Snag.Core;

/// <summary>Finds yt-dlp.exe and ffmpeg.exe: explicit setting, then a tools\ folder next to Snag, then PATH.</summary>
public static class Tools
{
    public static string? FindYtDlp(Settings s) =>
        FirstFile(s.YtDlpPath, Path.Combine(AppContext.BaseDirectory, "tools", "yt-dlp.exe"), OnPath("yt-dlp.exe"));

    /// <summary>Returns the folder holding ffmpeg.exe, or null.</summary>
    public static string? FindFfmpegDir(Settings s)
    {
        var exe = FirstFile(
            s.FfmpegPath,
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe"),
            OnPath("ffmpeg.exe"));
        return exe is null ? null : Path.GetDirectoryName(exe);
    }

    static string? FirstFile(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(c));

    /// <summary>Looks through the current process PATH plus the user and machine PATH from the registry,
    /// so a tool installed after Snag started is still found.</summary>
    static string? OnPath(string file)
    {
        var all = string.Join(';',
            Environment.GetEnvironmentVariable("PATH") ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "",
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "");
        foreach (var dir in all.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var p = Path.Combine(Environment.ExpandEnvironmentVariables(dir), file);
                if (File.Exists(p)) return p;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }
}
