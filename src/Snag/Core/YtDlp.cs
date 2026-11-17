using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snag.Core;

public sealed record VideoInfo(
    string Title,
    string? Uploader,
    TimeSpan? Duration,
    string? ThumbnailUrl,
    /// <summary>Available video qualities (short side in pixels, like yt-dlp's "res"), highest first.</summary>
    IReadOnlyList<int> Qualities,
    bool IsPlaylist,
    int? PlaylistCount)
{
    public int MaxQuality => Qualities.Count > 0 ? Qualities[0] : 0;
}

/// <summary>Everything one download needs, captured when the user presses Download.</summary>
public sealed record DownloadRequest(
    string Url,
    string Folder,
    DownloadMode Mode,
    int MaxHeight,
    string VideoFormat,
    string AudioFormat,
    bool WholePlaylist,
    bool EmbedThumbnail,
    bool EmbedChapters,
    bool EmbedMetadata,
    bool EmbedSubtitles,
    string SubtitleLanguages,
    string FileNameStyle,
    string CookiesBrowser);

public static partial class YtDlp
{
    public const string Tag = "SNAG|";

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    /// <summary>All http(s) links in a piece of text, in order, without duplicates.</summary>
    public static List<string> ExtractUrls(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : UrlRegex().Matches(text)
                .SelectMany(m => Regex.Split(m.Value, @"(?<=.)(?=https?://)", RegexOptions.IgnoreCase)) // "link1,link2" glued together
                .Select(u => u.TrimEnd('.', ',', ')', ']', ';'))
                .Where(u => u.Length > 10)
                .Distinct().ToList();

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|live/|embed/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex YouTubeIdRegex();

    /// <summary>Instant thumbnail for YouTube links, before the lookup finishes.</summary>
    public static string? YouTubeThumbnail(string url)
    {
        var m = YouTubeIdRegex().Match(url);
        return m.Success ? $"https://i.ytimg.com/vi/{m.Groups[1].Value}/mqdefault.jpg" : null;
    }

    /// <summary>Only web images. A site could hand us file:// or \\server\share paths (which would make
    /// Windows send login hashes to that server), so anything else is dropped.</summary>
    public static string? SafeImageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? u.AbsoluteUri : null;

    static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".avi", ".flv", ".m4v", ".ts",
        ".mp3", ".m4a", ".opus", ".ogg", ".oga", ".flac", ".wav", ".aac", ".wma", ".alac",
    };

    public static bool IsMediaFile(string path) => MediaExtensions.Contains(Path.GetExtension(path));

    public static bool LooksLikePlaylistLink(string url) =>
        url.Contains("list=", StringComparison.OrdinalIgnoreCase) || url.Contains("/playlist", StringComparison.OrdinalIgnoreCase);

    // ---------- lookup ----------

    public static async Task<VideoInfo> FetchInfoAsync(string exe, string url, string cookiesBrowser, CancellationToken ct)
    {
        var args = new List<string> { "-J", "--no-warnings", "--flat-playlist", "--no-playlist", "--color", "never" };
        if (!string.IsNullOrEmpty(cookiesBrowser)) args.AddRange(["--cookies-from-browser", cookiesBrowser]);
        args.AddRange(["--", url]);

        var (code, output, err) = await ProcessRunner.CaptureAsync(exe, args, ct);
        if (code != 0 || string.IsNullOrWhiteSpace(output))
            throw new InvalidOperationException(FriendlyError(LastError(err)) ?? "Couldn't read this link.");

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        bool isPlaylist = Str(root, "_type") == "playlist";
        int? count = null;
        if (isPlaylist)
        {
            count = Int(root, "playlist_count");
            if (count is null && root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array)
                count = entries.GetArrayLength();
        }

        // Short side of each video format, so a vertical 1080x1920 Short counts as 1080p (same rule yt-dlp's "res" uses).
        var qualities = new SortedSet<int>(Comparer<int>.Create((a, b) => b.CompareTo(a)));
        if (root.TryGetProperty("formats", out var formats) && formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var f in formats.EnumerateArray())
            {
                if (Str(f, "vcodec") is "none") continue;
                if (Int(f, "height") is not int h || h <= 0) continue;
                int q = Int(f, "width") is int w && w > 0 ? Math.Min(w, h) : h;
                qualities.Add(q);
            }
        }

        double? secs = root.TryGetProperty("duration", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetDouble() : null;

        return new VideoInfo(
            Title: Str(root, "title") ?? url,
            Uploader: Str(root, "uploader") ?? Str(root, "channel"),
            Duration: secs is > 0 ? TimeSpan.FromSeconds(secs.Value) : null,
            ThumbnailUrl: SafeImageUrl(PickThumbnail(root)),
            Qualities: qualities.ToList(),
            IsPlaylist: isPlaylist,
            PlaylistCount: count);
    }

    /// <summary>WPF can't always decode WebP, so prefer a JPG thumbnail.</summary>
    static string? PickThumbnail(JsonElement root)
    {
        string? jpg = null;
        if (root.TryGetProperty("thumbnails", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array)
        {
            int best = int.MinValue;
            foreach (var t in thumbs.EnumerateArray())
            {
                var u = Str(t, "url");
                if (u is null || !u.Split('?')[0].EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) continue;
                int w = Int(t, "width") ?? Int(t, "preference") ?? 0;
                if (w >= best) { best = w; jpg = u; }
            }
        }
        if (jpg is null && root.TryGetProperty("entries", out var entries) && entries.ValueKind == JsonValueKind.Array
            && entries.GetArrayLength() > 0)
            jpg = PickThumbnail(entries[0]);
        return jpg ?? Str(root, "thumbnail");
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    // ---------- download arguments ----------

    public static List<string> BuildDownloadArgs(DownloadRequest r, string? ffmpegDir)
    {
        var a = new List<string>
        {
            "--newline", "--progress", "--quiet", "--no-warnings", "--no-simulate",
            "--color", "never",
            "--no-mtime",          // file date = download date, so it shows up as newest
            "--progress-template",
            $"download:{Tag}D|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s|%(info.acodec)s|%(info.playlist_index)s|%(info.n_entries)s|%(info.title)s",
            "--progress-template",
            $"postprocess:{Tag}P|%(progress.postprocessor)s|%(progress.status)s",
            "--print", $"after_move:{Tag}F|%(filepath)s",
            "-P", r.Folder,
            "-o", OutputTemplate(r),
            r.WholePlaylist ? "--yes-playlist" : "--no-playlist",
        };

        if (r.Mode == DownloadMode.Video)
        {
            // Highest resolution first, then yt-dlp's own quality order (AV1 > VP9 > H.264 video, Opus > AAC audio).
            // The container (MP4/MKV) never lowers quality: streams are copied, not re-encoded.
            string container = r.VideoFormat == "mkv" ? "mkv" : "mp4";
            a.AddRange(["-f", "bv*+ba/b", "-S", r.MaxHeight > 0 ? $"res:{r.MaxHeight}" : "res",
                        "--merge-output-format", container, "--remux-video", container]);
            if (r.EmbedChapters) a.Add("--embed-chapters");
            if (r.EmbedSubtitles)
                a.AddRange(["--embed-subs", "--sub-langs", string.IsNullOrWhiteSpace(r.SubtitleLanguages) ? "en.*" : r.SubtitleLanguages.Trim()]);
            if (r.EmbedThumbnail) a.Add("--embed-thumbnail");
        }
        else
        {
            // Take the stream that already is the wanted format when there is one, so it isn't re-encoded.
            string pick = r.AudioFormat switch
            {
                "m4a" => "ba[ext=m4a]/ba/b",
                "opus" => "ba[acodec^=opus]/ba/b",
                _ => "ba/b",
            };
            a.AddRange(["-f", pick, "-x", "--audio-quality", "0"]);
            if (r.AudioFormat != "best") a.AddRange(["--audio-format", r.AudioFormat]);
            if (r.EmbedThumbnail && r.AudioFormat != "wav") a.Add("--embed-thumbnail");
        }

        if (r.EmbedMetadata) a.Add("--embed-metadata");
        if (!string.IsNullOrEmpty(r.CookiesBrowser)) a.AddRange(["--cookies-from-browser", r.CookiesBrowser]);
        if (ffmpegDir is not null) a.AddRange(["--ffmpeg-location", ffmpegDir]);
        a.AddRange(["--", r.Url]);
        return a;
    }

    static string OutputTemplate(DownloadRequest r)
    {
        string name = r.FileNameStyle switch
        {
            "title-id" => "%(title).150B [%(id)s].%(ext)s",
            "uploader-title" => "%(uploader,channel|Unknown).60B - %(title).150B.%(ext)s",
            _ => "%(title).150B.%(ext)s",
        };
        return r.WholePlaylist ? "%(playlist_title,playlist|Playlist).100B/%(playlist_index|0)03d - " + name : name;
    }

    // ---------- output parsing ----------

    public static string? LastError(string stderr) =>
        stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));

    /// <summary>Turns "ERROR: [youtube] abc123: Video unavailable" into something a person can read.</summary>
    public static string? FriendlyError(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var msg = line.StartsWith("ERROR:") ? line[6..].Trim() : line.Trim();
        msg = Regex.Replace(msg, @"^\[[^\]]+\]\s*[^:\s]+:\s*", "");
        if (msg.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase)) return "This site or link isn't supported.";
        if (msg.Contains("Private video", StringComparison.OrdinalIgnoreCase)) return "This video is private.";
        if (msg.Contains("Sign in to confirm your age", StringComparison.OrdinalIgnoreCase))
            return "Age-restricted. Turn on \"Use Firefox sign-in\" under More options and try again.";
        if (msg.Contains("Sign in to confirm you", StringComparison.OrdinalIgnoreCase))
            return "YouTube wants a sign-in. Turn on \"Use Firefox sign-in\" under More options, or update yt-dlp.";
        if (msg.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) && msg.Contains("not found", StringComparison.OrdinalIgnoreCase))
            return "ffmpeg is missing, so this can't be merged or converted.";
        if (msg.Contains("HTTP Error 404", StringComparison.OrdinalIgnoreCase))
            return "That page doesn't exist (404). Check the link.";
        if (msg.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase))
            return "This video is unavailable (removed, blocked in your country, or the link is wrong).";
        if (msg.Contains("getaddrinfo", StringComparison.OrdinalIgnoreCase) || msg.Contains("Failed to resolve", StringComparison.OrdinalIgnoreCase))
            return "No internet connection, or the site can't be reached.";
        if (msg.Contains("HTTP Error 403", StringComparison.OrdinalIgnoreCase))
            return "The site refused the download (403). Updating yt-dlp usually fixes this.";
        return msg.Length > 220 ? msg[..220] + "…" : msg;
    }

    public static double? Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public static string FormatSpeed(double bytesPerSec) => FormatBytes(bytesPerSec) + "/s";

    public static string FormatBytes(double b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return b.ToString(i == 0 ? "0" : b < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
    }

    public static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    public static string StepName(string postprocessor) => postprocessor switch
    {
        "Merger" => "Merging video and audio…",
        "ExtractAudio" => "Converting audio…",
        "VideoRemuxer" or "VideoConvertor" => "Converting video…",
        "EmbedThumbnail" => "Adding thumbnail…",
        "Metadata" or "FFmpegMetadata" => "Adding title and info…",
        "EmbedSubtitle" => "Adding subtitles…",
        "MoveFiles" or "MoveFilesAfterDownload" => "Finishing…",
        _ => "Processing…",
    };
}
