using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace VgmDesk;

public sealed class TrackRow(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path);
    public AudioInfo? Info { get; private set; }
    public Dictionary<int, AudioInfo> Inspected { get; } = new();
    public int Selected { get; set; } = 1;
    public string Duration => Info is null ? "—" : MainWindow.Clock(Info.Duration);
    public string Channels => Info?.Channels.ToString() ?? "—";
    public string Tracks => Info?.Total.ToString() ?? "—";
    public string State { get; private set; } = "正在分析";
    public string Error { get; private set; } = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Complete(AudioInfo? info, string error = "")
    {
        Info = info; Error = error;
        if (info is not null) Inspected[1] = info;
        SetState(info is not null ? "就绪" : "无法解码");
        PropertyChanged?.Invoke(this, new(null));
    }
    public void SetState(string state)
    {
        State = state; PropertyChanged?.Invoke(this, new(nameof(State)));
    }
}

public partial class MainWindow : Window
{
    private readonly ObservableCollection<TrackRow> rows = [];
    private readonly HashSet<string> known = new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? view;
    private readonly List<string> log = [];
    private readonly MediaPlayer player = new();
    private readonly DispatcherTimer timer;
    private readonly string? bundled;
    private readonly string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VgmDesk", "settings.json");
    private readonly string previewFolder = Path.Combine(Path.GetTempPath(), "VgmDesk-" + Guid.NewGuid().ToString("N"));
    private Engine? engine;
    private EngineInfo? engineInfo;
    private CancellationTokenSource? job;
    private bool closing, initialized, syncing, running, paused, mediaReady;
    private string? previewFile;
    private TrackRow? playing;
    private int playingTrack;
    private TrackRow? Current => TrackGrid.SelectedItem as TrackRow;
    private sealed record Settings(string? Engine, string? Output);

    public MainWindow()
    {
        InitializeComponent();
        TrackGrid.ItemsSource = rows;
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = x => x is TrackRow row && row.Path.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase);
        bundled = new[] {
            Path.Combine(AppContext.BaseDirectory, "vgmstream", "vgmstream-cli.exe"),
            Path.Combine(AppContext.BaseDirectory, "vgmstream-cli.exe")
        }.FirstOrDefault(File.Exists);
        player.MediaOpened += OnMediaOpened;
        player.MediaEnded += (_, _) => StopPlayback();
        player.MediaFailed += (_, e) => {
            if (previewFile is null) return;
            WriteLog("试听失败：" + e.ErrorException.Message); StopPlayback(); Say("无法试听。请检查 Windows 音频组件或查看任务日志。");
        };
        player.Volume = Volume.Value;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        timer.Tick += (_, _) => RefreshClock();
        timer.Start();
        initialized = true;
        Refresh();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Settings? settings = null;
        try { if (File.Exists(settingsPath)) settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath)); }
        catch (Exception ex) { WriteLog("无法读取设置：" + ex.Message); }
        OutputBox.Text = settings?.Output ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "VgmDesk-WAV");
        foreach (var path in new[] { settings?.Engine, bundled }.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await Job("正在检测解码器…", async token => await LoadEngine(path!, token));
            if (engine is not null || closing) break;
        }
        if (closing) return;
        if (engine is null)
        {
            EngineLabel.Text = "未连接解码器";
            Say("点击「解码器设置」选择新版 vgmstream-cli.exe。");
        }
        else if (Environment.GetCommandLineArgs().Skip(1).ToArray() is { Length: > 0 } args) await Import(args);
    }

    private async Task Job(string status, Func<CancellationToken, Task> work)
    {
        if (job is not null || closing) return;
        using var cancel = new CancellationTokenSource(); job = cancel;
        Say(status); JobProgress.Value = 0; JobProgress.IsIndeterminate = true;
        Refresh();
        try { await work(cancel.Token); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { Say("任务已取消；已经完成的项目已保留。"); }
        catch (Exception e) { WriteLog(e.Message); Say(e.Message); }
        finally
        {
            job = null;
            if (closing) _ = Dispatcher.BeginInvoke(Close);
            else Refresh();
        }
    }

    private async Task LoadEngine(string path, CancellationToken token)
    {
        var candidate = new Engine(path);
        var info = await candidate.VersionAsync(token);
        StopPlayback(); engine = candidate; engineInfo = info;
        EngineLabel.Text = "vgmstream " + info.Version; EngineLabel.ToolTip = candidate.Exe;
        Say($"解码器已就绪 · 已报告 {info.Extensions.Count} 种扩展名");
    }

    private async void OnEngine(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Title = "选择 vgmstream CLI（配套 DLL 应放在同一文件夹）", Filter = "命令行解码器 (*.exe)|*.exe", FileName = "vgmstream-cli.exe" };
        if (engine is not null) picker.InitialDirectory = Path.GetDirectoryName(engine.Exe);
        if (picker.ShowDialog(this) != true) return;
        var inputs = rows.Select(r => r.Path).ToArray();
        await Job("正在检测解码器…", async token => {
            await LoadEngine(picker.FileName, token);
            SaveSettings();
            rows.Clear(); known.Clear();
        });
        if (rows.Count == 0 && inputs.Length > 0) await Import(inputs);
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings(engine?.Exe == bundled ? null : engine?.Exe, OutputBox.Text.Trim())));
        }
        catch (Exception e) { WriteLog("无法保存本地设置：" + e.Message); }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择游戏音频", Filter = "所有文件 (*.*)|*.*", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await Import(dialog.FileNames);
    }
    private async void OnFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要递归扫描的文件夹" };
        if (dialog.ShowDialog(this) == true) await Import([dialog.FolderName]);
    }
    private void OnOutputFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择 WAV 输出文件夹" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(this) == true) { OutputBox.Text = dialog.FolderName; SaveSettings(); }
    }
    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = job is null && engine is not null && e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) await Import(paths);
    }

    private static (string[] Paths, string[] Notes) Discover(string[] inputs, HashSet<string> extensions, CancellationToken token)
    {
        HashSet<string> files = new(StringComparer.OrdinalIgnoreCase);
        List<string> notes = [];
        foreach (var input in inputs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var path = Path.GetFullPath(input);
                if (Directory.Exists(path))
                {
                    var dirs = new Stack<string>(); dirs.Push(path);
                    while (dirs.TryPop(out var directory))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(directory))
                            {
                                token.ThrowIfCancellationRequested();
                                var ext = Path.GetExtension(file).TrimStart('.');
                                if (extensions.Contains(ext) || ext.Length == 0) files.Add(file);
                            }
                            foreach (var sub in Directory.EnumerateDirectories(directory))
                            {
                                token.ThrowIfCancellationRequested();
                                if (!File.GetAttributes(sub).HasFlag(FileAttributes.ReparsePoint)) dirs.Push(sub);
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { notes.Add($"跳过文件夹 {directory}：{e.Message}"); }
                    }
                }
                else if (File.Exists(path)) files.Add(path);
                else notes.Add($"路径不存在：{path}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { notes.Add($"跳过 {input}：{e.Message}"); }
        }
        return (files.Order(StringComparer.OrdinalIgnoreCase).ToArray(), notes.ToArray());
    }

    private Task Import(string[] inputs)
    {
        if (engine is not { } decoder || engineInfo is not { } info) { Say("请先选择解码器。"); return Task.CompletedTask; }
        return Job("正在扫描文件…", async token => {
            var (found, notes) = await Task.Run(() => Discover(inputs, info.Extensions, token), token);
            foreach (var note in notes) WriteLog(note);
            var paths = found.Where(p => !known.Contains(p)).ToArray();
            var success = 0; var failed = 0;
            Progress(0, paths.Length);
            foreach (var path in paths)
            {
                token.ThrowIfCancellationRequested();
                var row = new TrackRow(path);
                rows.Add(row); known.Add(path);
                Say($"分析 {success + failed + 1}/{paths.Length}：{row.Name}");
                RefreshTable();
                try { row.Complete(await decoder.InspectAsync(path, 1, token)); success++; }
                catch (OperationCanceledException) { rows.Remove(row); known.Remove(path); throw; }
                catch (Exception e) { row.Complete(null, e.Message); WriteLog($"无法分析 {path}：{e.Message}"); failed++; }
                if (Current is null && view?.Contains(row) == true && row.Info is not null) TrackGrid.SelectedItem = row;
                Progress(success + failed, paths.Length);
            }
            Say(paths.Length == 0 ? "没有新增文件。文件夹仅扫描已知扩展名及无扩展名文件。" : $"分析完成：{success} 个可解码，{failed} 个失败。" + (notes.Length > 0 ? $" 另有 {notes.Length} 项跳过，详见日志。" : ""));
        });
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (job is not null) return;
        foreach (var row in TrackGrid.SelectedItems.OfType<TrackRow>().ToArray()) { if (row == playing) StopPlayback(); rows.Remove(row); known.Remove(row.Path); }
        Refresh();
    }
    private void OnClear(object sender, RoutedEventArgs e)
    {
        StopPlayback(); rows.Clear(); known.Clear(); Refresh(); Say("队列已清空。");
    }
    private void OnSearch(object sender, TextChangedEventArgs e)
    {
        if (!initialized) return;
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        view?.Refresh(); Refresh();
    }
    private void OnSelection(object sender, SelectionChangedEventArgs e)
    {
        if (initialized) Refresh();
    }
    private async void OnTrackNumberKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ApplyTrack(); }
    }
    private async void OnTrackApply(object sender, RoutedEventArgs e) => await ApplyTrack();
    private Task ApplyTrack()
    {
        if (Current is not { Info: { } rootInfo } row || engine is not { } decoder) return Task.CompletedTask;
        if (!int.TryParse(TrackNumber.Text.Trim(), out var track) || track < 1 || track > rootInfo.Total)
        {
            Say($"子音轨编号应为 1 至 {rootInfo.Total}。"); return Task.CompletedTask;
        }
        if (row.Inspected.ContainsKey(track)) { row.Selected = track; Refresh(); return Task.CompletedTask; }
        return Job("正在读取子音轨…", async token => {
            row.Inspected[track] = await decoder.InspectAsync(row.Path, track, token);
            row.Selected = track; Say($"已选择第 {track}/{rootInfo.Total} 条子音轨。");
        });
    }

    private void OnLoopMode(object sender, SelectionChangedEventArgs e)
    {
        if (LoopFields is null) return;
        LoopFields.Visibility = LoopBox.SelectedIndex is 1 or 2 ? Visibility.Visible : Visibility.Collapsed;
        FadeField.Visibility = DelayField.Visibility = LoopBox.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static double Number(TextBox box, string name, double min, double max)
    {
        var raw = box.Text.Trim();
        if ((!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) &&
             !double.TryParse(raw, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) || !double.IsFinite(value) || value < min || value > max)
            throw new ArgumentException($"{name}必须在 {min} 和 {max} 之间。");
        return value;
    }
    private async void OnExportSelected(object sender, RoutedEventArgs e) => await Export(TrackGrid.SelectedItems.OfType<TrackRow>());
    private async void OnExportAll(object sender, RoutedEventArgs e) => await Export(rows);
    private Task Export(IEnumerable<TrackRow> selection)
    {
        if (engine is not { } decoder) return Task.CompletedTask;
        var ready = selection.Where(r => r.Info is not null).ToArray();
        if (ready.Length == 0) return Task.CompletedTask;
        var folder = OutputBox.Text.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(folder)) { Say("请填写或浏览选择绝对输出路径。"); return Task.CompletedTask; }
        ExportOptions options;
        try
        {
            var mode = (LoopMode)LoopBox.SelectedIndex;
            options = new(mode, mode is LoopMode.Fade or LoopMode.Tail ? Number(LoopCount, "循环次数", 0.01, 999) : 2,
                mode == LoopMode.Fade ? Number(FadeTime, "淡出时间", 0, 3600) : 10,
                mode == LoopMode.Fade ? Number(FadeDelay, "淡出延迟", 0, 3600) : 0, FormatBox.SelectedIndex + 1);
            folder = Path.GetFullPath(folder);
            Directory.CreateDirectory(folder);
            SaveSettings();
        }
        catch (Exception e) { Say(e.Message); return Task.CompletedTask; }
        var all = AllSubsongs.IsChecked == true;
        var total = ready.Sum(r => all ? (long)r.Info!.Total : 1L);
        return Job("正在导出…", async token => {
            long success = 0, failed = 0;
            Progress(0, total);
            foreach (var row in ready)
            {
                token.ThrowIfCancellationRequested();
                var count = all ? row.Info!.Total : 1;
                var errors = 0;
                row.SetState("正在导出");
                try
                {
                    for (long i = 0; i < count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var track = all ? (int)(i + 1) : row.Selected;
                        Say($"导出 {success + failed + 1}/{total}：{row.Name} [{track}/{row.Info!.Total}]");
                        try
                        {
                            var file = await decoder.ExportAsync(row.Path, track, row.Info.Total, folder, options, token);
                            WriteLog("已导出：" + file); success++;
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception e) { WriteLog($"导出失败 {row.Path} #{track}：{e.Message}"); errors++; failed++; }
                        Progress(success + failed, total);
                    }
                    row.SetState(errors == 0 ? "已导出" : errors == count ? "导出失败" : "部分失败");
                }
                catch (OperationCanceledException) { row.SetState("导出已取消"); throw; }
            }
            Say($"导出完成：{success} 条成功，{failed} 条失败。" + (failed > 0 ? " 详情见任务日志。" : $" {folder}"));
        });
    }

    private async void OnPlay(object sender, RoutedEventArgs e) => await Play();
    private async void OnTrackDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject el && ItemsControl.ContainerFromElement(TrackGrid, el) is DataGridRow) await Play();
    }
    private Task Play()
    {
        var current = Current;
        if (previewFile is not null && (current is null || (current == playing && current.Selected == playingTrack)))
        {
            if (!mediaReady) return Task.CompletedTask;
            if (running) { player.Pause(); running = false; paused = true; PlayingHint.Text = "已暂停"; }
            else { player.Play(); running = true; paused = false; PlayingHint.Text = "正在试听"; }
            Refresh(); return Task.CompletedTask;
        }
        if (job is not null || current?.Info is null || engine is not { } decoder) return Task.CompletedTask;
        var track = current.Selected;
        return Job("正在准备试听…", async token => {
            StopPlayback();
            Directory.CreateDirectory(previewFolder);
            var filename = await decoder.PreviewAsync(current.Path, track, previewFolder, token);
            playing = current; playingTrack = track; previewFile = filename;
            PlayingName.Text = current.Name + (current.Info.Total > 1 ? $" · #{track}" : "");
            PlayingName.ToolTip = PlayingName.Text;
            PlayingHint.Text = "正在打开试听…";
            try { player.Open(new Uri(filename, UriKind.Absolute)); player.Play(); }
            catch { StopPlayback(); throw; }
            Say("试听不使用导出循环设置。");
        });
    }
    private void OnMediaOpened(object? sender, EventArgs e)
    {
        if (previewFile is null) return;
        var seconds = player.NaturalDuration.HasTimeSpan ? player.NaturalDuration.TimeSpan.TotalSeconds : 0;
        mediaReady = true; running = true; PlayingHint.Text = "正在试听";
        syncing = true; Position.Maximum = Math.Max(seconds, 0.01); Position.Value = 0; syncing = false;
        Position.IsEnabled = seconds > 0;
        RefreshClock(); Refresh();
    }
    private void OnStop(object sender, RoutedEventArgs e) { StopPlayback(); Refresh(); }
    private void StopPlayback()
    {
        var file = previewFile;
        previewFile = null;
        if (file is not null) player.Close();
        Engine.TryDelete(file);
        playing = null; running = paused = mediaReady = false;
        PlayingName.Text = "未播放"; PlayingName.ToolTip = null;
        PlayingHint.Text = "试听按原始音轨播放一次";
        syncing = true; Position.Value = 0; Position.Maximum = 1; syncing = false;
        Position.IsEnabled = false; TimeLabel.Text = "0:00 / 0:00";
        if (initialized) RefreshPlayback();
    }
    private void OnSeek(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!syncing && mediaReady && Position.IsEnabled) player.Position = TimeSpan.FromSeconds(e.NewValue);
    }
    private void OnVolume(object sender, RoutedPropertyChangedEventArgs<double> e) => player.Volume = e.NewValue;
    private void RefreshClock()
    {
        if (!mediaReady || !Position.IsEnabled) return;
        var seconds = player.Position.TotalSeconds;
        syncing = true;
        if (!Position.IsMouseCaptureWithin) Position.Value = seconds;
        syncing = false;
        TimeLabel.Text = $"{Clock(seconds)} / {Clock(Position.Maximum)}";
    }
    internal static string Clock(double seconds)
    {
        var time = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return time.TotalHours >= 1 ? $"{(long)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : seconds is > 0 and < 1 ? $"0:{seconds:00.0}" : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void OnCancel(object sender, RoutedEventArgs e) { CancelButton.IsEnabled = false; job?.Cancel(); }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        closing = true;
        if (job is not null) { e.Cancel = true; job.Cancel(); return; }
        timer.Stop(); StopPlayback(); SaveSettings();
        try { if (Directory.Exists(previewFolder)) Directory.Delete(previewFolder, true); }
        catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control && AddButton.IsEnabled) { e.Handled = true; OnAdd(this, e); }
        else if (e.Key == Key.Delete && TrackGrid.IsKeyboardFocusWithin && RemoveButton.IsEnabled) { e.Handled = true; OnRemove(this, e); }
        else if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control && PlayButton.IsEnabled) { e.Handled = true; await Play(); }
    }
    private void OnLog(object sender, RoutedEventArgs e)
    {
        var text = new TextBox { Text = log.Count == 0 ? "还没有任务记录。" : string.Join(Environment.NewLine, log), IsReadOnly = true,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalContentAlignment = VerticalAlignment.Top,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(13), Padding = new Thickness(8) };
        var dialog = new Window { Owner = this, Title = "VgmDesk · 任务日志", WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Width = 750, Height = 430, MinWidth = 390, MinHeight = 240, Content = text, FontFamily = FontFamily };
        dialog.Loaded += (_, _) => text.ScrollToEnd(); dialog.ShowDialog();
    }
    private void WriteLog(string line)
    {
        if (log.Count >= 4000) log.RemoveAt(0);
        log.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
    }
    private void Say(string text) => StatusLabel.Text = text;
    private void Progress(long done, long total) { JobProgress.IsIndeterminate = total <= 0; JobProgress.Maximum = Math.Max(1, total); JobProgress.Value = done; }
    private void RefreshTable()
    {
        var shown = TrackGrid.Items.Count;
        CountLabel.Text = shown == rows.Count ? $"{rows.Count} 个文件" : $"{shown} / {rows.Count} 个文件";
        EmptyCard.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = rows.Count == 0 ? "拖入游戏音频或文件夹" : "未找到匹配的文件";
        EmptyHint.Text = rows.Count == 0 ? "也可以用「添加文件」选择多个文件。" : "试试其他文件名或路径。";
    }
    private void RefreshPlayback()
    {
        var isCurrent = previewFile is not null && (Current is null || (Current == playing && playingTrack == playing.Selected));
        PlayButton.IsEnabled = isCurrent ? mediaReady : job is null && Current?.Info is not null;
        PlayButton.Content = isCurrent && running ? "Ⅱ 暂停" : isCurrent && paused ? "▶ 继续" : "▶ 试听";
        StopButton.IsEnabled = previewFile is not null;
    }
    private void Refresh()
    {
        RefreshTable();
        var idle = job is null;
        AddButton.IsEnabled = FolderButton.IsEnabled = idle && engine is not null;
        EngineButton.IsEnabled = idle;
        TrackGrid.IsEnabled = true;
        RemoveButton.IsEnabled = idle && TrackGrid.SelectedItems.Count > 0;
        ClearButton.IsEnabled = idle && rows.Count > 0;
        ExportSelected.IsEnabled = idle && TrackGrid.SelectedItems.OfType<TrackRow>().Any(r => r.Info is not null);
        ExportAll.IsEnabled = idle && rows.Any(r => r.Info is not null);
        CancelButton.Visibility = JobProgress.Visibility = idle ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = !idle && job?.IsCancellationRequested != true;
        var row = Current;
        var info = row?.Inspected.GetValueOrDefault(row.Selected);
        DetailName.Text = row?.Name ?? "尚未选中文件"; DetailName.ToolTip = row?.Path;
        TrackNumber.IsEnabled = TrackApply.IsEnabled = idle && row?.Info is { Total: > 1 };
        var selected = (row?.Selected ?? 1).ToString();
        if (TrackNumber.Text != selected) TrackNumber.Text = selected;
        TrackNumber.ToolTip = row?.Info is { } root ? $"范围：1–{root.Total}" : null;
        var detail = info is not null
            ? $"{(string.IsNullOrWhiteSpace(info.Name) ? "" : info.Name + "\n")}{info.Encoding}\n{info.SampleRate:N0} Hz / {info.Channels} 声道 / {info.Duration:0.###} 秒" + (info.Bitrate > 0 ? $" / {info.Bitrate / 1000d:0.#} kbps" : "")
            : row?.Error is { Length: > 0 } error ? "无法解码，详情见任务日志。\n" + error.Split('\n')[0] : "选中文件后查看编码、声道和循环点。";
        if (detail.Length > 340) detail = detail[..340] + "…";
        DetailSummary.Text = detail;
        DetailSummary.ToolTip = info is not null ? $"{info.Format}\n{info.Layout}\n{info.Samples:N0} 样本" : row?.Error;
        LoopSummary.Text = info?.HasLoop == true ? $"循环点 {(double)info.LoopStart / info.SampleRate:0.###}–{(double)info.LoopEnd / info.SampleRate:0.###} 秒" : info is null ? "" : "无内置循环点";
        RefreshPlayback();
    }
}
