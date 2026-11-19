using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
namespace M3UPlayer;

public partial class MainWindow : Window
{
    private IPlaybackEngine? engine;
    private System.Windows.Forms.Panel? videoSurface;
    private bool usingOffiEngine;
    private readonly SemaphoreSlim playbackGate = new(1, 1);
    private int playbackRequest;
    private bool playbackBusy, closeReady;
    private List<Channel> all = [];
    private Channel? current;
    private HlsRelay? relay;
    private string? sourceError;
    private SourceLibrary library = new([], null);
    private PlaylistSource? ActiveSource => library.Sources.FirstOrDefault(item => item.Id == library.ActiveId);
    private bool sourcePickerUpdating;
    private bool sourcesReady;
    private bool loading, closing, fullscreen;
    private WindowState previousState;
    private VideoDoubleClick? videoDoubleClick;
    private static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LightM3UPlayer");
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = true }) { Timeout = TimeSpan.FromSeconds(25) };
    private sealed record Preferences(string? Source, string? LastChannel, double Volume);
    public MainWindow() { InitializeComponent(); }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var surface = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.Black };
            Video.Child = surface;
            videoSurface = surface;
            videoDoubleClick = new VideoDoubleClick(
                () => Video.IsVisible && videoSurface is { IsHandleCreated: true } panel ? panel.Handle : IntPtr.Zero,
                () => Dispatcher.BeginInvoke(() => { if (!closing) SetFullscreen(!fullscreen); }));
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            timer.Tick += (_, _) => {
                if (closing) { timer.Stop(); return; }
                if (playbackBusy) return;
                engine?.Poll(message => Status.Text = (message.StartsWith("播放失败") ? sourceError ?? message : message) + (usingOffiEngine ? " · mpv" : " · MDK"));
            };
            timer.Start();
            var prefsFile = Path.Combine(DataDir, "settings.json");
            var prefs = File.Exists(prefsFile)
                ? JsonSerializer.Deserialize<Preferences>(File.ReadAllText(prefsFile))
                : null;
            if (prefs != null) Volume.Value = Math.Clamp(prefs.Volume, 0, 100);
            library = SourceLibrary.Load(DataDir, prefs?.Source);
            sourcesReady = true;
            ShowActiveSource();
            current = all.FirstOrDefault(c => c.Url == prefs?.LastChannel);
            if (current != null) NowPlaying.Text = $"上次：{current.Name} · 点击重试播放";
            if (ActiveSource != null) Status.Text = "已恢复播放来源 · 点击频道开始播放";
        }
        catch { Status.Text = "初始化失败，无法读取本机播放来源或启动播放器。"; }
    }
    private void Ui(Action action) { if (!closing) Dispatcher.BeginInvoke(() => { if (!closing) action(); }); }
    private static Uri? SourceUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;

    private async Task LoadPlaylist(string location, string? name = null)
    {
        if (loading || !sourcesReady) return;
        loading = true; Status.Text = "正在导入播放列表…";
        try
        {
            byte[] bytes; var baseUri = SourceUri(location);
            if (baseUri?.Scheme is "http" or "https")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, baseUri);
                request.Headers.UserAgent.ParseAdd("LightM3UPlayer/0.1");
                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();
                bytes = await response.Content.ReadAsByteArrayAsync();
                baseUri = response.RequestMessage?.RequestUri ?? baseUri;
            }
            else bytes = await File.ReadAllBytesAsync(location);
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException)
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                text = Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            var parsed = Playlist.Parse(text, baseUri);
            if (parsed.Count == 0) throw new InvalidDataException();
            if (closing) return;
            var existing = library.Sources.FirstOrDefault(item => SameLocation(item.Location, location));
            var entry = new PlaylistSource(existing?.Id ?? Guid.NewGuid(),
                name ?? existing?.Name ?? DefaultSourceName(location), location, parsed);
            var entries = library.Sources.ToList();
            if (existing == null) entries.Add(entry);
            else entries[entries.IndexOf(existing)] = entry;
            SaveLibrary(new(entries, entry.Id));
            Save();
            Status.Text = $"已保存 {entry.Name} · {parsed.Count} 个频道";
        }
        catch (HttpRequestException ex) { Status.Text = ex.StatusCode.HasValue ? $"导入失败：服务器返回 HTTP {(int)ex.StatusCode.Value}" : "导入失败：无法连接订阅服务器"; }
        catch (TaskCanceledException) { Status.Text = "导入超时，请检查网络后重试。"; }
        catch (InvalidDataException) { Status.Text = "没有找到频道，请选择有效的 M3U 播放列表。"; }
        catch { Status.Text = "导入或保存失败，请检查文件及目录权限。"; }
        finally { loading = false; }
    }
    private static bool SameLocation(string? left, string right)
    {
        if (left == null) return false;
        if (SourceUri(right)?.Scheme is "http" or "https")
            return string.Equals(SourceUri(left)?.AbsoluteUri, SourceUri(right)?.AbsoluteUri, StringComparison.Ordinal);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string DefaultSourceName(string location) =>
        SourceUri(location) is { Scheme: "http" or "https" } uri
            ? uri.Host : Path.GetFileNameWithoutExtension(location);

    private void SaveLibrary(SourceLibrary updated)
    {
        updated.Save(DataDir);
        library = updated;
        ShowActiveSource();
    }

    private void ShowActiveSource()
    {
        sourcePickerUpdating = true;
        try
        {
            Sources.ItemsSource = library.Sources;
            Sources.SelectedItem = ActiveSource;
        }
        finally { sourcePickerUpdating = false; }
        ApplyPlaylist(ActiveSource?.Channels ?? []);
    }

    private void SourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sourcePickerUpdating || Sources.SelectedItem is not PlaylistSource selected) return;
        if (loading) { ShowActiveSource(); return; }
        try
        {
            Search.Clear();
            SaveLibrary(library with { ActiveId = selected.Id });
            Save();
            Status.Text = $"已切换到 {selected.Name} · {selected.Channels.Count} 个频道";
        }
        catch
        {
            ShowActiveSource();
            Status.Text = "无法保存来源选择。";
        }
    }

    private void ApplyPlaylist(List<Channel> channels)
    {
        var newsSource = 0;
        all = channels.Select(channel => channel.Name == "無綫新聞台" && channel.Group == "香港"
            ? channel with { SourceNumber = ++newsSource }
            : channel).ToList();
        Groups.ItemsSource = new[] { "全部频道" }.Concat(all.Select(c => c.Group).Distinct()).ToList();
        Groups.SelectedIndex = 0; Filter();
    }
    private void Filter()
    {
        if (Channels == null || Groups == null) return;
        var query = Search.Text.Trim(); var group = Groups.SelectedItem as string;
        var filtered = all.Where(c => (Groups.SelectedIndex <= 0 || c.Group == group) && c.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Channels.ItemsSource = filtered; Count.Text = $"{filtered.Count} / {all.Count} 个频道";
    }
    private void FilterChanged(object sender, TextChangedEventArgs e) => Filter();
    private void GroupChanged(object sender, SelectionChangedEventArgs e) => Filter();
    private async void ChannelSelected(object sender, SelectionChangedEventArgs e) { if (Channels.SelectedItem is Channel channel) await Play(channel); }
    private async Task Play(Channel channel)
    {
        if (videoSurface == null || closing) return;
        var request = ++playbackRequest;
        sourceError = null;
        current = channel; NowPlaying.Text = channel.DisplayName; Status.Text = "正在连接频道…";
        Welcome.Visibility = Visibility.Collapsed; Video.Visibility = Visibility.Visible;
        await playbackGate.WaitAsync();
        try
        {
            if (request != playbackRequest || closing) return;
            playbackBusy = true;
            relay?.Dispose(); relay = null;
            await SelectEngine(channel);
            if (request != playbackRequest || closing) return;
            var address = new Uri(channel.Url);
            if ((channel.Name == "無綫新聞台" || channel.Group.Contains("4gtv", StringComparison.OrdinalIgnoreCase)) && address.Scheme is ("http" or "https"))
            {
                relay = new HlsRelay(channel);
                var active = relay;
                active.Notice += message => Ui(() => { if (relay == active) { Status.Text = message; if (message.Contains("HTTP")) sourceError = message; } });
                address = new Uri(active.Register(address, ".m3u8"));
            }
            var selectedEngine = engine!;
            await Task.Run(() => selectedEngine.Play(address.AbsoluteUri, channel));
            if (request == playbackRequest && !closing) Save();
        }
        catch
        {
            if (request == playbackRequest && !closing) Status.Text = "播放失败，请重试或切换频道。";
        }
        finally { FinishPlaybackOperation(); }
    }

    private void FinishPlaybackOperation()
    {
        try
        {
            var refresh = playbackBusy;
            playbackBusy = false;
            if (refresh && !closing)
            {
                engine?.SetVolume(Volume.Value);
                (engine as Mdk)?.RefreshSurface();
            }
        }
        finally { playbackGate.Release(); }
    }

    private async Task SelectEngine(Channel channel)
    {
        var useOffiEngine = channel.Group.Equals("台湾offi", StringComparison.OrdinalIgnoreCase);
        if ((engine != null && useOffiEngine == usingOffiEngine) || videoSurface == null) return;

        var oldSurface = videoSurface;
        var previous = engine;
        engine = null;
        if (previous != null)
        {
            (previous as Mdk)?.DetachResizeHandler();
            var cleanup = Task.Run(previous.Dispose);
            if (await Task.WhenAny(cleanup, Task.Delay(2000)) == cleanup)
            {
                await cleanup;
                Video.Child = null;
                oldSurface.Dispose();
            }
            else
            {
                // Keep the native handle alive until its engine finishes.
                Video.Child = null;
                _ = ReleaseRetiredSurface(cleanup, oldSurface);
            }
            videoSurface = null;
        }
        if (closing) return;
        if (videoSurface == null)
        {
            videoSurface = new System.Windows.Forms.Panel { BackColor = System.Drawing.Color.Black };
            Video.Child = videoSurface;
        }
        try
        {
            engine = useOffiEngine
                ? new Mpv(unchecked((uint)videoSurface.Handle.ToInt64()))
                : new Mdk(videoSurface, () => !playbackBusy);
            usingOffiEngine = useOffiEngine;
        }
        catch
        {
            engine = new Mdk(videoSurface, () => !playbackBusy);
            usingOffiEngine = false;
            engine.SetVolume(Volume.Value);
            throw;
        }
        engine.SetVolume(Volume.Value);
    }

    private static async Task ReleaseRetiredSurface(Task cleanup, System.Windows.Forms.Panel panel)
    {
        try { await cleanup; }
        catch { /* The old engine can no longer affect the current channel. */ }
        finally { panel.Dispose(); }
    }

    private async void ImportFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "播放列表|*.m3u;*.m3u8|所有文件|*.*" };
        if (dialog.ShowDialog(this) == true) await LoadPlaylist(dialog.FileName);
    }
    private async void ImportUrl(object sender, RoutedEventArgs e)
    {
        var dialog = new Window { Title = "添加订阅", Width = 560, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, Owner = this, WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Background, Foreground = Foreground };
        var panel = new StackPanel { Margin = new Thickness(20) };
        var name = new TextBox { Style = (Style)FindResource("DialogInput"), Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(new Label { Content = "来源名称（可选）", Target = name, Padding = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(name);
        var input = new TextBox { Style = (Style)FindResource("DialogInput"), Margin = new Thickness(0, 0, 0, 14) };
        panel.Children.Add(new Label { Content = "M3U / M3U8 订阅地址", Target = input, Padding = new Thickness(0, 0, 0, 5) });
        panel.Children.Add(input);
        var ok = new Button { Content = "导入", IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
        ok.Click += (_, _) => { if (Uri.TryCreate(input.Text.Trim(), UriKind.Absolute, out var u) && u.Scheme is "http" or "https") dialog.DialogResult = true; else MessageBox.Show(dialog, "请输入有效的 HTTP 或 HTTPS 地址。"); };
        panel.Children.Add(ok); dialog.Content = panel;
        dialog.Loaded += (_, _) => input.Focus();
        if (dialog.ShowDialog() == true) await LoadPlaylist(input.Text.Trim(), string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim());
    }
    private async void RefreshPlaylist(object sender, RoutedEventArgs e) { if (ActiveSource?.Location is string location) await LoadPlaylist(location); else Status.Text = "请先导入文件或添加订阅。"; }
    private void ManageSources(object sender, RoutedEventArgs e)
    {
        if (loading || !sourcesReady) return;
        var dialog = new Window
        {
            Title = "管理播放来源", Owner = this, Width = 480, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background, Foreground = Foreground
        };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var controls = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(controls, Dock.Bottom);
        layout.Children.Add(controls);
        var name = new TextBox { Style = (Style)FindResource("DialogInput") };
        controls.Children.Add(name);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        controls.Children.Add(buttons);
        var rename = new Button { Content = "保存名称", IsEnabled = false };
        var remove = new Button { Content = "移除来源", IsEnabled = false };
        var done = new Button { Content = "完成", IsCancel = true };
        buttons.Children.Add(rename);
        buttons.Children.Add(remove);
        buttons.Children.Add(done);
        var list = new ListBox { ItemsSource = library.Sources };
        layout.Children.Add(list);
        list.SelectionChanged += (_, _) =>
        {
            var selected = list.SelectedItem as PlaylistSource;
            name.Text = selected?.Name ?? "";
            rename.IsEnabled = remove.IsEnabled = selected != null;
        };
        bool Update(SourceLibrary updated, Guid? selectedId)
        {
            try { SaveLibrary(updated); Save(); }
            catch
            {
                MessageBox.Show(dialog, "无法保存播放来源。", "轻播");
                return false;
            }
            list.ItemsSource = library.Sources;
            list.SelectedItem = library.Sources.FirstOrDefault(item => item.Id == selectedId);
            return true;
        }
        rename.Click += (_, _) =>
        {
            if (list.SelectedItem is not PlaylistSource selected || string.IsNullOrWhiteSpace(name.Text)) return;
            Update(library with
            {
                Sources = library.Sources.Select(item => item.Id == selected.Id ? item with { Name = name.Text.Trim() } : item).ToList()
            }, selected.Id);
        };
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is not PlaylistSource selected ||
                MessageBox.Show(dialog, $"从轻播移除「{selected.Name}」？", "移除来源",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var remaining = library.Sources.Where(item => item.Id != selected.Id).ToList();
            var activeId = library.ActiveId == selected.Id ? remaining.FirstOrDefault()?.Id : library.ActiveId;
            Update(new(remaining, activeId), activeId);
        };
        list.SelectedItem = ActiveSource;
        dialog.Content = layout;
        dialog.ShowDialog();
    }
    private async void OnDrop(object sender, DragEventArgs e) { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) await LoadPlaylist(files[0]); }
    private void TogglePause(object sender, RoutedEventArgs e) { if (!playbackBusy && !closing) engine?.TogglePause(); }
    private async void StopPlayback(object sender, RoutedEventArgs e)
    {
        var request = ++playbackRequest;
        Status.Text = "正在停止…";
        await playbackGate.WaitAsync();
        try
        {
            if (request != playbackRequest || closing) return;
            playbackBusy = true;
            relay?.Dispose(); relay = null;
            var previous = engine;
            if (previous != null) await Task.Run(previous.Stop);
            if (request != playbackRequest || closing) return;
            Video.Visibility = Visibility.Hidden;
            Welcome.Visibility = Visibility.Visible;
            Status.Text = "已停止";
        }
        catch { if (request == playbackRequest && !closing) Status.Text = "停止失败，请切换频道。"; }
        finally { FinishPlaybackOperation(); }
    }
    private async void RetryPlayback(object sender, RoutedEventArgs e) { if (current != null) await Play(current); }
    private void VolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!playbackBusy && !closing) engine?.SetVolume(e.NewValue);
    }
    private void ToggleFullscreen(object sender, RoutedEventArgs e) => SetFullscreen(!fullscreen);

    private void SetFullscreen(bool value)
    {
        if (value == fullscreen) return;
        fullscreen = value;
        if (value)
        {
            previousState = WindowState;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowState = WindowState.Normal;
            Topmost = false;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = previousState;
        }
        var chrome = value ? Visibility.Collapsed : Visibility.Visible;
        Header.Visibility = Sidebar.Visibility = PlaybackBar.Visibility = StatusBar.Visibility = chrome;
        SidebarColumn.Width = new GridLength(value ? 0 : 260);
        LayoutRoot.Margin = new Thickness(value ? 0 : 20);
        VideoFrame.CornerRadius = new CornerRadius(value ? 0 : 12);
        Background = value ? System.Windows.Media.Brushes.Black
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 245, 247));
        Keyboard.Focus(this);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 || (e.Key == Key.Escape && fullscreen)) { ToggleFullscreen(sender, e); e.Handled = true; }
        else if (e.Key == Key.Space && e.OriginalSource is not TextBox) { TogglePause(sender, e); e.Handled = true; }
    }
    private void Save()
    {
        try { Directory.CreateDirectory(DataDir); File.WriteAllText(Path.Combine(DataDir, "settings.json"), JsonSerializer.Serialize(new Preferences(ActiveSource?.Location, all.Any(c => c.Url == current?.Url) ? current?.Url : null, Volume.Value))); }
        catch { /* Playback should remain available when preferences cannot be saved. */ }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeReady) return;
        e.Cancel = true;
        if (closing) return;
        closing = true;
        ++playbackRequest;
        videoDoubleClick?.Dispose();
        // Let WPF finish the cancelled Closing event before hiding the HWND.
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            Hide();
            await CloseInBackground();
        }));
    }

    private async Task CloseInBackground()
    {
        // Yield so the hidden window disappears before writing settings.
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        Save();
        var shutdown = ShutdownPlayback();
        if (await Task.WhenAny(shutdown, Task.Delay(4000)) != shutdown)
        {
            Environment.Exit(0);
            return;
        }
        closeReady = true;
        Close();
    }

    private async Task ShutdownPlayback()
    {
        await playbackGate.WaitAsync();
        try
        {
            playbackBusy = true;
            relay?.Dispose();
            var previous = engine;
            engine = null;
            (previous as Mdk)?.DetachResizeHandler();
            if (previous != null) await Task.Run(previous.Dispose);
        }
        catch { /* Close even if the native engine reports a shutdown failure. */ }
        finally { playbackGate.Release(); }
    }
}
