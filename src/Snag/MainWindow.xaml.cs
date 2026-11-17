using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using Microsoft.Win32;
using Snag.Core;

namespace Snag;

public partial class MainWindow : Window
{
    readonly Settings _s = Settings.Load();
    readonly ObservableCollection<DownloadItem> _items = [];
    string? _ytdlp, _ffmpegDir;
    string _locateTarget = "yt-dlp";
    bool _quiet = true;                 // true while the UI is being filled from code
    CancellationTokenSource? _lookupCts;
    string? _infoUrl;
    VideoInfo? _info;
    string? _lastClipboard;
    bool _closingConfirmed;
    readonly CancellationTokenSource _shutdown = new();

    public MainWindow()
    {
        InitializeComponent();
        ItemsList.ItemsSource = _items;
        _items.CollectionChanged += (_, _) => EmptyText.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DataObject.AddPastingHandler(UrlBox, OnUrlPaste);
        PlaylistCheck.Click += (_, _) => UpdateDownloadButton();

        FillStaticCombos();
        LoadSettingsToUi();
        RestoreWindowPlacement();
        _quiet = false;

        RefreshTools();
        UrlBox.Focus();
    }

    // ---------------- setup ----------------

    void FillStaticCombos()
    {
        SetQualityList(null, resetToBest: true);
        Fill(NameStyleBox, ("title", "Video title"), ("title-id", "Title [video ID]"), ("uploader-title", "Channel - Title"));
        Fill(ParallelBox, (1, "1 download"), (2, "2 downloads"), (3, "3 downloads"), (4, "4 downloads"));
    }

    static readonly int[] CommonQualities = [2160, 1440, 1080, 720, 480, 360];

    static string QualityName(int q) => q switch
    {
        >= 4320 => $"{q}p (8K)",
        >= 2160 => $"{q}p (4K)",
        >= 1440 => $"{q}p (2K)",
        >= 1080 => $"{q}p (Full HD)",
        >= 720 => $"{q}p (HD)",
        _ => $"{q}p",
    };

    /// <summary>Shows only the qualities this video really has (or the usual list when it isn't known yet).
    /// Best is always first and is picked for every new link.</summary>
    void SetQualityList(IReadOnlyList<int>? available, bool resetToBest)
    {
        var was = _quiet;
        _quiet = true;
        int keep = resetToBest ? 0 : TagOf<int>(QualityBox);
        var items = new List<(int, string)>
        {
            (0, available is { Count: > 0 } ? $"Best available ({available[0]}p)" : "Best available"),
        };
        foreach (var q in available is { Count: > 0 } ? available : CommonQualities)
            items.Add((q, QualityName(q)));
        Fill(QualityBox, items.ToArray());
        Select(QualityBox, keep);
        _s.MaxHeight = TagOf<int>(QualityBox);
        _quiet = was;
    }

    void RefreshFormatOptions()
    {
        var was = _quiet;
        _quiet = true;
        bool audio = AudioRadio.IsChecked == true;
        if (audio)
        {
            Fill(FormatBox,
                ("mp3", "MP3 · plays everywhere"),
                ("m4a", "M4A (AAC) · smaller, same quality"),
                ("opus", "Opus · smallest file"),
                ("flac", "FLAC · big, no extra quality"),
                ("wav", "WAV · uncompressed, huge"),
                ("best", "Original · no conversion"));
            Select(FormatBox, _s.AudioFormat);
        }
        else
        {
            Fill(FormatBox, ("mp4", "MP4 · best quality"), ("mkv", "MKV · best quality, best for subtitles"));
            Select(FormatBox, _s.VideoFormat);
        }
        QualityBox.Visibility = audio ? Visibility.Collapsed : Visibility.Visible;
        FormatBox.Margin = new Thickness(audio ? 0 : 10, 0, 0, 0);
        QualityLabel.Text = audio ? "Format" : "Quality";
        ChaptersCheck.IsEnabled = SubsCheck.IsEnabled = SubsLangBox.IsEnabled = !audio;
        _quiet = was;
    }

    void LoadSettingsToUi()
    {
        FolderBox.Text = _s.OutputFolder;
        (_s.Mode == DownloadMode.Audio ? AudioRadio : VideoRadio).IsChecked = true;
        RefreshFormatOptions();
        ThumbCheck.IsChecked = _s.EmbedThumbnail;
        MetaCheck.IsChecked = _s.EmbedMetadata;
        ChaptersCheck.IsChecked = _s.EmbedChapters;
        SubsCheck.IsChecked = _s.EmbedSubtitles;
        SubsLangBox.Text = _s.SubtitleLanguages;
        Select(NameStyleBox, _s.FileNameStyle);
        Select(ParallelBox, Math.Clamp(_s.MaxParallel, 1, 4));
        CookiesCheck.IsChecked = _s.CookiesBrowser == "firefox";
        ClipboardCheck.IsChecked = _s.WatchClipboard;
        MoreExpander.IsExpanded = _s.ShowMoreOptions;
    }

    void ReadUiToSettings()
    {
        _s.Mode = AudioRadio.IsChecked == true ? DownloadMode.Audio : DownloadMode.Video;
        _s.MaxHeight = TagOf<int>(QualityBox);
        if (_s.Mode == DownloadMode.Audio) _s.AudioFormat = TagOf<string>(FormatBox) ?? "mp3";
        else _s.VideoFormat = TagOf<string>(FormatBox) ?? "mp4";
        _s.EmbedThumbnail = ThumbCheck.IsChecked == true;
        _s.EmbedMetadata = MetaCheck.IsChecked == true;
        _s.EmbedChapters = ChaptersCheck.IsChecked == true;
        _s.EmbedSubtitles = SubsCheck.IsChecked == true;
        _s.SubtitleLanguages = string.IsNullOrWhiteSpace(SubsLangBox.Text) ? "en.*" : SubsLangBox.Text.Trim();
        _s.FileNameStyle = TagOf<string>(NameStyleBox) ?? "title";
        _s.MaxParallel = Math.Max(1, TagOf<int>(ParallelBox));
        _s.CookiesBrowser = CookiesCheck.IsChecked == true ? "firefox" : "";
        _s.WatchClipboard = ClipboardCheck.IsChecked == true;
    }

    async void RefreshTools()
    {
        _ytdlp = Tools.FindYtDlp(_s);
        _ffmpegDir = Tools.FindFfmpegDir(_s);

        if (_ytdlp is null)
        {
            _locateTarget = "yt-dlp";
            ToolBannerText.Text = "yt-dlp wasn't found. Snag needs it to download anything. Install it (github.com/yt-dlp/yt-dlp) or point Snag to yt-dlp.exe.";
            LocateYtDlpButton.Content = "Find yt-dlp.exe…";
            ToolBanner.Visibility = Visibility.Visible;
        }
        else if (_ffmpegDir is null)
        {
            _locateTarget = "ffmpeg";
            ToolBannerText.Text = "ffmpeg wasn't found. Without it you only get lower quality video and can't convert audio.";
            LocateYtDlpButton.Content = "Find ffmpeg.exe…";
            ToolBanner.Visibility = Visibility.Visible;
        }
        else ToolBanner.Visibility = Visibility.Collapsed;

        UpdateDownloadButton();

        if (_ytdlp is null) { FooterText.Text = "yt-dlp missing"; UpdateButton.IsEnabled = false; return; }
        UpdateButton.IsEnabled = true;
        try
        {
            var (_, ver, _) = await ProcessRunner.CaptureAsync(_ytdlp, ["--version"], CancellationToken.None);
            var snag = typeof(MainWindow).Assembly.GetName().Version;
            FooterText.Text = $"Snag {snag?.ToString(3)}  ·  yt-dlp {ver.Trim()}  ·  {(_ffmpegDir is null ? "ffmpeg missing" : "ffmpeg ready")}";
        }
        catch (Exception ex) { FooterText.Text = "yt-dlp couldn't start: " + ex.Message; }
    }

    // ---------------- link box ----------------

    void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UrlPlaceholder.Visibility = UrlBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDownloadButton();
        _ = LookupAsync();
    }

    /// <summary>Ctrl+V anywhere in the window (not inside a text field) pastes links.</summary>
    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            Paste_Click(sender, e);
            e.Handled = true;
        }
    }

    void UrlBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { StartFromBox(); e.Handled = true; }
        else if (e.Key == Key.Escape) { UrlBox.Clear(); e.Handled = true; }
    }

    /// <summary>Pasting text that holds one or more links: keep just the links.</summary>
    void OnUrlPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (e.SourceDataObject.GetData(DataFormats.UnicodeText) is not string text) return;
        var urls = YtDlp.ExtractUrls(text);
        if (urls.Count == 0) return;
        e.CancelCommand();
        SetLinks(urls);
    }

    void Paste_Click(object sender, RoutedEventArgs e)
    {
        var urls = YtDlp.ExtractUrls(ReadClipboard());
        if (urls.Count == 0) { FlashPlaceholder("There's no link on the clipboard."); return; }
        SetLinks(urls);
    }

    void SetLinks(List<string> urls)
    {
        _lastClipboard = string.Join(" ", urls);
        UrlBox.Text = _lastClipboard;
        UrlBox.CaretIndex = UrlBox.Text.Length;
        UrlBox.Focus();
    }

    void FlashPlaceholder(string msg)
    {
        PreviewCard.Visibility = Visibility.Visible;
        PreviewSpinner.Visibility = Visibility.Collapsed;
        PlaylistCheck.Visibility = Visibility.Collapsed;
        PreviewImage.Source = null;
        PreviewTitle.Text = msg;
        PreviewMeta.Text = "Copy a video link in your browser, then come back here.";
    }

    static string? ReadClipboard()
    {
        for (int i = 0; i < 3; i++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { Thread.Sleep(30); } // clipboard busy in another app
        }
        return null;
    }

    void Window_Activated(object? sender, EventArgs e)
    {
        if (!_s.WatchClipboard || UrlBox.Text.Length > 0) return;
        var urls = YtDlp.ExtractUrls(ReadClipboard());
        if (urls.Count == 0) return;
        var key = string.Join(" ", urls);
        if (key == _lastClipboard) return;
        SetLinks(urls);
    }

    void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent("UniformResourceLocatorW")
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Window_Drop(object sender, DragEventArgs e)
    {
        var text = e.Data.GetData(DataFormats.UnicodeText) as string ?? e.Data.GetData("UniformResourceLocatorW") as string;
        var urls = YtDlp.ExtractUrls(text);
        if (urls.Count > 0) SetLinks(urls);
        e.Handled = true;
    }

    // ---------------- preview ----------------

    async Task LookupAsync()
    {
        _lookupCts?.Cancel();
        var cts = _lookupCts = new CancellationTokenSource();
        var urls = YtDlp.ExtractUrls(UrlBox.Text);

        if (urls.Count == 0)
        {
            PreviewCard.Visibility = Visibility.Collapsed;
            PlaylistCheck.Visibility = Visibility.Collapsed;
            UpdateDownloadButton();
            return;
        }
        if (urls.Count > 1)
        {
            SetQualityList(null, resetToBest: false);
            ShowPreview($"{urls.Count} links", "Each one is downloaded with the settings below.", null, spinner: false);
            PlaylistCheck.Visibility = Visibility.Collapsed;
            return;
        }

        var url = urls[0];
        if (url == _infoUrl && _info is not null) { ShowInfo(_info, url); return; }

        SetQualityList(null, resetToBest: true); // new link: start at the best quality again
        ShowPreview("Looking up the link…", url, YtDlp.YouTubeThumbnail(url), spinner: true);
        PlaylistCheck.Visibility = Visibility.Collapsed;
        try
        {
            await Task.Delay(300, cts.Token);
            if (_ytdlp is null) { ShowPreview("Can't read the link", "yt-dlp is needed first (see the yellow bar above).", null, false); return; }
            var info = await YtDlp.FetchInfoAsync(_ytdlp, url, _s.CookiesBrowser, cts.Token);
            if (cts.IsCancellationRequested) return;
            _info = info;
            _infoUrl = url;
            ShowInfo(info, url);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            ShowPreview("Couldn't read this link", ex.Message + "  You can still try Download.", null, false);
        }
    }

    void ShowInfo(VideoInfo info, string url)
    {
        var meta = new List<string>();
        if (info.IsPlaylist) meta.Add(info.PlaylistCount is int n ? $"Playlist · {n} videos" : "Playlist");
        if (!string.IsNullOrEmpty(info.Uploader)) meta.Add(info.Uploader);
        if (info.Duration is TimeSpan d) meta.Add(YtDlp.FormatTime(d));
        if (info.MaxQuality > 0) meta.Add($"up to {info.MaxQuality}p");
        SetQualityList(!info.IsPlaylist && info.Qualities.Count > 0 ? info.Qualities : null, resetToBest: false);
        ShowPreview(info.Title, string.Join("  ·  ", meta), info.ThumbnailUrl ?? YtDlp.YouTubeThumbnail(url), spinner: false);

        if (info.IsPlaylist)
        {
            PlaylistCheck.Content = info.PlaylistCount is int n ? $"Whole playlist ({n} videos) goes into its own folder" : "Whole playlist goes into its own folder";
            PlaylistCheck.IsChecked = true;
            PlaylistCheck.IsEnabled = false;
            PlaylistCheck.Visibility = Visibility.Visible;
        }
        else if (YtDlp.LooksLikePlaylistLink(url))
        {
            PlaylistCheck.Content = "This video is in a playlist: download the whole playlist instead";
            PlaylistCheck.IsChecked = false;
            PlaylistCheck.IsEnabled = true;
            PlaylistCheck.Visibility = Visibility.Visible;
        }
        else PlaylistCheck.Visibility = Visibility.Collapsed;
        UpdateDownloadButton();
    }

    void ShowPreview(string title, string meta, string? thumb, bool spinner)
    {
        PreviewCard.Visibility = Visibility.Visible;
        PreviewTitle.Text = title;
        PreviewMeta.Text = meta;
        PreviewSpinner.Visibility = spinner ? Visibility.Visible : Visibility.Collapsed;
        PreviewImage.Source = LoadImage(thumb);
    }

    static BitmapImage? LoadImage(string? url)
    {
        if (YtDlp.SafeImageUrl(url) is not string safe) return null;
        var uri = new Uri(safe);
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.DecodePixelWidth = 320;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            return bmp;
        }
        catch { return null; }
    }

    // ---------------- options ----------------

    void Mode_Changed(object sender, RoutedEventArgs e)
    {
        if (_quiet) return;
        RefreshFormatOptions();
        Option_Changed(sender, e);
    }

    void Option_Changed(object sender, RoutedEventArgs e)
    {
        if (_quiet) return;
        bool cookiesBefore = _s.CookiesBrowser.Length > 0;
        ReadUiToSettings();
        _s.Save();
        if (cookiesBefore != _s.CookiesBrowser.Length > 0) { _infoUrl = null; _ = LookupAsync(); }
        Pump(); // a higher "at the same time" limit can start waiting downloads
    }

    void More_Toggled(object sender, RoutedEventArgs e)
    {
        if (_quiet) return;
        _s.ShowMoreOptions = MoreExpander.IsExpanded;
        _s.Save();
    }

    void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Where should downloads go?" };
        if (Directory.Exists(_s.OutputFolder)) dlg.InitialDirectory = _s.OutputFolder;
        if (dlg.ShowDialog(this) != true) return;
        _s.OutputFolder = dlg.FolderName;
        FolderBox.Text = dlg.FolderName;
        _s.Save();
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_s.OutputFolder);
            Shell(_s.OutputFolder);
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Snag"); }
    }

    void LocateYtDlp_Click(object sender, RoutedEventArgs e)
    {
        var name = _locateTarget == "ffmpeg" ? "ffmpeg.exe" : "yt-dlp.exe";
        var dlg = new OpenFileDialog { Title = $"Find {name}", Filter = $"{name}|{name}|Programs (*.exe)|*.exe" };
        if (dlg.ShowDialog(this) != true) return;
        if (_locateTarget == "ffmpeg") _s.FfmpegPath = dlg.FileName; else _s.YtDlpPath = dlg.FileName;
        _s.Save();
        RefreshTools();
    }

    async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_ytdlp is null) return;
        if (_items.Any(i => i.State == ItemState.Running))
        {
            FooterText.Text = "Let the running downloads finish first, then update.";
            return;
        }
        UpdateButton.IsEnabled = false;
        FooterText.Text = "Updating yt-dlp…";
        try
        {
            var (_, o, err) = await ProcessRunner.CaptureAsync(_ytdlp, ["-U"], CancellationToken.None);
            var last = (o + "\n" + err).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "Done";
            RefreshTools();
            await Task.Delay(400);
            FooterText.Text = last.Replace("ERROR: ", "") + "   ·   " + FooterText.Text;
        }
        catch (Exception ex) { FooterText.Text = "Update failed: " + ex.Message; }
        finally { UpdateButton.IsEnabled = true; }
    }

    // ---------------- downloading ----------------

    void UpdateDownloadButton()
    {
        var urls = YtDlp.ExtractUrls(UrlBox.Text);
        DownloadButton.IsEnabled = urls.Count > 0 && _ytdlp is not null;
        DownloadButtonText.Text = urls.Count > 1 ? $"Download {urls.Count} links"
            : PlaylistCheck.Visibility == Visibility.Visible && PlaylistCheck.IsChecked == true ? "Download playlist"
            : "Download";
    }

    void Download_Click(object sender, RoutedEventArgs e) => StartFromBox();

    void StartFromBox()
    {
        var urls = YtDlp.ExtractUrls(UrlBox.Text);
        if (urls.Count == 0 || _ytdlp is null) return;
        ReadUiToSettings();
        _s.Save();

        try { Directory.CreateDirectory(_s.OutputFolder); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Can't use the folder {_s.OutputFolder}:\n{ex.Message}", "Snag");
            return;
        }

        bool whole = urls.Count == 1 && PlaylistCheck.Visibility == Visibility.Visible && PlaylistCheck.IsChecked == true;
        foreach (var url in urls)
        {
            var req = new DownloadRequest(url, _s.OutputFolder, _s.Mode, _s.MaxHeight, _s.VideoFormat, _s.AudioFormat, whole,
                _s.EmbedThumbnail, _s.EmbedChapters, _s.EmbedMetadata, _s.EmbedSubtitles, _s.SubtitleLanguages,
                _s.FileNameStyle, _s.CookiesBrowser);
            if (_items.Any(i => i.IsActive && i.Request == req)) continue; // already in the queue
            bool known = url == _infoUrl && _info is not null;
            var item = new DownloadItem(req, known ? _info!.Title : null,
                known ? _info!.ThumbnailUrl ?? YtDlp.YouTubeThumbnail(url) : YtDlp.YouTubeThumbnail(url));
            _items.Insert(0, item);
            if (!known && urls.Count <= 10) _ = FillTitleAsync(item);
        }

        _lookupCts?.Cancel();
        UrlBox.Clear();
        PreviewCard.Visibility = Visibility.Collapsed;
        UrlBox.Focus();
        Pump();
    }

    /// <summary>Queued before the lookup finished: fetch the name and picture in the background.</summary>
    async Task FillTitleAsync(DownloadItem item)
    {
        if (_ytdlp is null) return;
        try
        {
            // A running download reports its own title within a second or two; only look up the ones still waiting.
            await Task.Delay(2000, _shutdown.Token);
            if (item.Title != item.Request.Url) return;
            var info = await YtDlp.FetchInfoAsync(_ytdlp, item.Request.Url, item.Request.CookiesBrowser, _shutdown.Token);
            if (item.Title == item.Request.Url) item.Title = info.Title;
            item.Thumbnail ??= info.ThumbnailUrl;
        }
        catch { /* the download itself will report problems */ }
    }

    /// <summary>Starts waiting downloads, oldest first, up to the "at the same time" limit.</summary>
    void Pump()
    {
        int running = _items.Count(i => i.State == ItemState.Running);
        foreach (var item in _items.Reverse().Where(i => i.State == ItemState.Queued).ToList())
        {
            if (running >= _s.MaxParallel) break;
            running++;
            _ = RunAsync(item);
        }
        UpdateTaskbar();
    }

    async Task RunAsync(DownloadItem item)
    {
        item.State = ItemState.Running;
        item.Progress = 0;
        item.IsIndeterminate = true;
        item.StatusText = "Starting…";
        item.LastErrorLine = null;
        item.FilePath = null;
        var cts = item.Cts = new CancellationTokenSource();
        var err = new StringBuilder();

        try
        {
            var exe = _ytdlp ?? throw new InvalidOperationException("yt-dlp wasn't found.");
            Directory.CreateDirectory(item.Request.Folder);
            var args = YtDlp.BuildDownloadArgs(item.Request, _ffmpegDir);

            // Progress lines can arrive many times a second: show at most ~8 per second, always the newest.
            var gate = new object();
            string? pending = null;
            long lastPost = 0;
            void Post(string l) => Dispatcher.InvokeAsync(() => { item.HandleLine(l); UpdateTaskbar(); });
            void OnOut(string line)
            {
                bool isProgress = line.StartsWith(YtDlp.Tag + "D|", StringComparison.Ordinal);
                lock (gate)
                {
                    long now = Environment.TickCount64;
                    if (isProgress && now - lastPost < 120) { pending = line; return; }
                    if (isProgress) lastPost = now;
                    else if (pending is not null) { Post(pending); pending = null; }
                    if (isProgress) pending = null;
                }
                Post(line);
            }

            int code = await ProcessRunner.RunAsync(exe, args, OnOut, line => { lock (err) err.AppendLine(line); }, cts.Token);
            lock (gate) { if (pending is not null) Post(pending); }
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background); // let queued lines land

            string? error;
            lock (err) error = YtDlp.FriendlyError(YtDlp.LastError(err.ToString()));

            if (item.Request.WholePlaylist && item.FilePath is not null)
            {
                item.FilePath = Path.GetDirectoryName(item.FilePath); // "open" shows the playlist folder
                item.Title = Path.GetFileName(item.FilePath) ?? item.Title; // playlist name, not the last video
            }

            if (code == 0)
            {
                item.State = ItemState.Done;
                item.Progress = 100;
                item.StatusText = item.FilePath is null ? "Done"
                    : item.Request.WholePlaylist ? "Done · saved in " + Path.GetFileName(item.FilePath)
                    : "Done · " + Path.GetFileName(item.FilePath);
            }
            else
            {
                item.State = ItemState.Failed;
                var msg = error ?? $"yt-dlp stopped with code {code}.";
                item.StatusText = item.FilePath is not null ? "Some items failed: " + msg : msg;
            }
        }
        catch (OperationCanceledException)
        {
            item.State = ItemState.Canceled;
            item.StatusText = "Canceled";
        }
        catch (Exception ex)
        {
            item.State = ItemState.Failed;
            item.StatusText = ex.Message;
        }
        finally
        {
            item.Cts = null;
            cts.Dispose();
            item.IsIndeterminate = false;
            Pump();
            if (!_items.Any(i => i.IsActive)) Flash();
        }
    }

    void UpdateTaskbar()
    {
        var active = _items.Where(i => i.IsActive).ToList();
        if (active.Count == 0)
        {
            Taskbar.ProgressState = TaskbarItemProgressState.None;
            Title = "Snag";
            return;
        }
        double avg = active.Average(i => i.Progress) / 100;
        Taskbar.ProgressState = TaskbarItemProgressState.Normal;
        Taskbar.ProgressValue = avg;
        Title = $"Snag · {avg:P0}" + (active.Count > 1 ? $" · {active.Count} downloads" : "");
    }

    // ---------------- row buttons ----------------

    static DownloadItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as DownloadItem;

    void ItemOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.FilePath is not string p) return;
        // Only hand media files to their player; anything unexpected is shown in its folder instead of run.
        if (Directory.Exists(p) || YtDlp.IsMediaFile(p)) Shell(p);
        else ItemShow_Click(sender, e);
    }

    void ItemShow_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender)?.FilePath is not string p) return;
        if (File.Exists(p)) Process.Start("explorer.exe", $"/select,\"{p}\"");
        else if (Directory.Exists(p)) Shell(p);
    }

    void ItemRetry_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { CanRetry: true } item) return;
        item.State = ItemState.Queued;
        item.Progress = 0;
        item.StatusText = "Waiting…";
        Pump();
    }

    void ItemCancel_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is not { } item) return;
        if (item.State == ItemState.Running) { item.StatusText = "Canceling…"; item.Cts?.Cancel(); }
        else if (item.State == ItemState.Queued) { item.State = ItemState.Canceled; item.StatusText = "Canceled"; UpdateTaskbar(); }
    }

    void ItemRemove_Click(object sender, RoutedEventArgs e)
    {
        if (ItemOf(sender) is { CanRemove: true } item) _items.Remove(item);
    }

    void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        foreach (var i in _items.Where(i => !i.IsActive).ToList()) _items.Remove(i);
    }

    // ---------------- window ----------------

    void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_closingConfirmed && _items.Any(i => i.State == ItemState.Running))
        {
            var r = MessageBox.Show(this, "Downloads are still running. Quit and stop them?\nUnfinished ones can be started again later and will continue where they stopped.",
                "Snag", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (r != MessageBoxResult.Yes) { e.Cancel = true; return; }
        }
        _closingConfirmed = true;
        _lookupCts?.Cancel();
        _shutdown.Cancel();
        foreach (var i in _items) i.Cts?.Cancel();
        ReadUiToSettings();
        var b = RestoreBounds;
        _s.WindowMaximized = WindowState == WindowState.Maximized;
        if (!b.IsEmpty) { _s.WindowLeft = b.Left; _s.WindowTop = b.Top; _s.WindowWidth = b.Width; _s.WindowHeight = b.Height; }
        _s.Save();
    }

    void RestoreWindowPlacement()
    {
        var work = SystemParameters.WorkArea;
        if (_s.WindowWidth >= MinWidth && _s.WindowHeight >= MinHeight)
        {
            Width = _s.WindowWidth;
            Height = _s.WindowHeight;
            var vl = SystemParameters.VirtualScreenLeft; var vt = SystemParameters.VirtualScreenTop;
            bool onScreen = !double.IsNaN(_s.WindowLeft) && !double.IsNaN(_s.WindowTop)
                && _s.WindowLeft >= vl - 50 && _s.WindowTop >= vt - 10
                && _s.WindowLeft + 100 <= vl + SystemParameters.VirtualScreenWidth
                && _s.WindowTop + 50 <= vt + SystemParameters.VirtualScreenHeight;
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _s.WindowLeft;
                Top = _s.WindowTop;
            }
        }
        else Height = Math.Min(Height, work.Height - 20);
        if (_s.WindowMaximized) WindowState = WindowState.Maximized;
    }

    static void Shell(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    // ---------------- helpers ----------------

    static void Fill<T>(ComboBox box, params (T Tag, string Label)[] items)
    {
        box.Items.Clear();
        foreach (var (tag, label) in items) box.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
    }

    static void Select(ComboBox box, object value)
    {
        foreach (ComboBoxItem i in box.Items)
            if (Equals(i.Tag, value)) { box.SelectedItem = i; return; }
        if (box.Items.Count > 0) box.SelectedIndex = 0;
    }

    static T? TagOf<T>(ComboBox box) => box.SelectedItem is ComboBoxItem { Tag: T t } ? t : default;

    [StructLayout(LayoutKind.Sequential)]
    struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    [DllImport("user32.dll")]
    static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>Blink the taskbar button when everything finished while Snag was in the background.</summary>
    void Flash()
    {
        if (IsActive) return;
        var fi = new FLASHWINFO
        {
            cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd = new WindowInteropHelper(this).Handle,
            dwFlags = 3 | 12, // FLASHW_ALL | FLASHW_TIMERNOFG
        };
        FlashWindowEx(ref fi);
    }
}
