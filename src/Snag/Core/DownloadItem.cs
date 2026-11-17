using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Snag.Core;

public enum ItemState { Queued, Running, Done, Failed, Canceled }

/// <summary>One row in the downloads list.</summary>
public sealed class DownloadItem : INotifyPropertyChanged
{
    public DownloadItem(DownloadRequest request, string? title, string? thumbnail)
    {
        Request = request;
        _title = title ?? request.Url;
        _thumbnail = YtDlp.SafeImageUrl(thumbnail);
        _statusText = "Waiting…";
    }

    public DownloadRequest Request { get; }
    public CancellationTokenSource? Cts { get; set; }
    public string? LastErrorLine { get; set; }

    string _title;
    public string Title { get => _title; set => Set(ref _title, value); }

    string? _thumbnail;
    public string? Thumbnail { get => _thumbnail; set => Set(ref _thumbnail, YtDlp.SafeImageUrl(value)); }

    public string Subtitle => Request.Mode == DownloadMode.Audio
        ? $"Audio · {(Request.AudioFormat == "best" ? "original" : Request.AudioFormat.ToUpperInvariant())}"
        : $"Video · {(Request.MaxHeight > 0 ? Request.MaxHeight + "p" : "best")} · {Request.VideoFormat.ToUpperInvariant()}"
          + (Request.WholePlaylist ? " · playlist" : "");

    string _statusText;
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    double _progress;
    public double Progress { get => _progress; set => Set(ref _progress, value); }

    bool _indeterminate;
    public bool IsIndeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }

    string? _filePath;
    public string? FilePath { get => _filePath; set { Set(ref _filePath, value); Notify(nameof(CanOpen)); } }

    ItemState _state = ItemState.Queued;
    public ItemState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Notify(nameof(CanCancel)); Notify(nameof(CanRetry)); Notify(nameof(CanRemove));
            Notify(nameof(CanOpen)); Notify(nameof(IsActive)); Notify(nameof(IsFailed)); Notify(nameof(IsDone));
        }
    }

    public bool IsActive => State is ItemState.Queued or ItemState.Running;
    public bool IsFailed => State == ItemState.Failed;
    public bool IsDone => State == ItemState.Done;
    public bool CanCancel => IsActive;
    public bool CanRetry => State is ItemState.Failed or ItemState.Canceled;
    public bool CanRemove => !IsActive;
    public bool CanOpen => State == ItemState.Done && FilePath is not null && (File.Exists(FilePath) || Directory.Exists(FilePath));

    /// <summary>Parses one line of yt-dlp output (our tagged progress template).</summary>
    public void HandleLine(string line)
    {
        if (!line.StartsWith(YtDlp.Tag, StringComparison.Ordinal))
        {
            if (line.StartsWith("ERROR:", StringComparison.Ordinal)) LastErrorLine = line;
            return;
        }
        var body = line[YtDlp.Tag.Length..];
        if (body.StartsWith("D|")) ParseDownload(body[2..]);
        else if (body.StartsWith("P|")) ParsePostprocess(body[2..]);
        else if (body.StartsWith("F|")) FilePath = body[2..].Trim();
    }

    void ParseDownload(string s)
    {
        // downloaded|total|estimate|speed|eta|vcodec|acodec|playlist_index|n_entries|title(may contain '|')
        var p = s.Split('|', 10);
        if (p.Length < 10) return;
        double? done = YtDlp.Num(p[0]);
        double? total = YtDlp.Num(p[1]) ?? YtDlp.Num(p[2]);
        double? speed = YtDlp.Num(p[3]);
        double? eta = YtDlp.Num(p[4]);
        string vcodec = p[5], acodec = p[6];
        int? idx = (int?)YtDlp.Num(p[7]);
        int? n = (int?)YtDlp.Num(p[8]);
        var title = p[9].Trim();
        if (title.Length > 0 && title != "NA") Title = title;

        double frac = done is not null && total is > 0 ? Math.Clamp(done.Value / total.Value, 0, 1) : 0;

        // Video mode downloads the picture first, then the sound: weight them 85/15 so the bar moves once.
        string part = "";
        if (Request.Mode == DownloadMode.Video)
        {
            if (acodec == "none") { part = "video"; frac *= 0.85; }
            else if (vcodec == "none") { part = "audio"; frac = 0.85 + frac * 0.15; }
        }

        string prefix = "";
        if (idx is int i && n is int count && count > 1)
        {
            frac = ((i - 1) + frac) / count;
            prefix = $"{i} of {count} · ";
        }

        IsIndeterminate = total is null;
        Progress = frac * 100;

        var bits = new List<string> { prefix + (part.Length > 0 ? $"Downloading {part}" : "Downloading") };
        if (total is > 0) bits.Add($"{YtDlp.FormatBytes(done ?? 0)} of {YtDlp.FormatBytes(total.Value)}");
        if (speed is > 0) bits.Add(YtDlp.FormatSpeed(speed.Value));
        if (eta is > 0) bits.Add(YtDlp.FormatTime(TimeSpan.FromSeconds(eta.Value)) + " left");
        StatusText = string.Join(" · ", bits);
    }

    void ParsePostprocess(string s)
    {
        var p = s.Split('|');
        if (p.Length < 2 || p[1] == "finished") return;
        StatusText = YtDlp.StepName(p[0]);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    bool Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}
