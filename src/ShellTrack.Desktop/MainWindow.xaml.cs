using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using ShellTrack.Client;
using ShellTrack.Contracts;
using ShellTrack.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.System;

namespace ShellTrack.Desktop;

public partial class MainWindow : Window
{
    private ShellTrackClient? client;
    private readonly string? dataRoot;
    private readonly ObservableCollection<TaskRow> rows = [];
    private readonly ObservableCollection<LogRow> logRows = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly CancellationTokenSource closing = new();
    private CancellationTokenSource? selection;
    private TrayIcon? tray;
    private bool refreshing, exit, notificationUpdating;
    private string? selectedId, pendingId;
    private TextLog? selectedLog;
    private sealed record Preferences(bool DetailsVisible = true, bool FollowOutput = true);
    private static string TaskTitle(CreateSessionRequest request) => request.Command ??
        (string.IsNullOrEmpty(request.RawArguments) ? request.Shell + " · 交互会话" : request.Shell + " " + request.RawArguments);

    private sealed class TaskRow(SessionInfo initial) : INotifyPropertyChanged
    {
        public SessionInfo Session { get; private set; } = initial;
        public string Title => TaskTitle(Session.Request);
        public string Subtitle => $"{StateName(Session.State)} · {Session.StartedAt.LocalDateTime:MM-dd HH:mm:ss}" + (Session.ExitCode is null ? "" : $" · 退出码 {Session.ExitCode}");
        public string Preview => string.IsNullOrWhiteSpace(Session.LatestOutputLine) ? (Session.IsFinished ? "没有可显示的输出" : "等待输出…") : Session.LatestOutputLine;
        private int NotificationConditionCount => (Session.Request.Notify ? 1 : 0) + Session.Request.NotifyPatterns.Length;
        public string NotificationLabel => NotificationConditionCount > 0 ? $"通知条件 {NotificationConditionCount} 项" : "通知已关闭";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(SessionInfo session)
        {
            if (Session == session) return;
            Session = session;
            PropertyChanged?.Invoke(this, new(null));
        }
    }
    private sealed class LogRow(long number, string initialText) : INotifyPropertyChanged
    {
        public long Number { get; } = number;
        public string Text { get; private set; } = initialText;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string text)
        {
            if (Text == text) return;
            Text = text; PropertyChanged?.Invoke(this, new(nameof(Text)));
        }
    }

    public MainWindow(string? root, string? id)
    {
        dataRoot = root; pendingId = id;
        InitializeComponent();
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(handle) / 96d;
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32((int)Math.Round(1280 * scale), (int)Math.Round(800 * scale)));
        string icon = Path.Combine(AppContext.BaseDirectory, "assets", "shelltrack.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        TaskList.ItemsSource = rows; LogList.ItemsSource = logRows;
        ReadPreferences();
        timer.Tick += async (_, _) => await Refresh();
        AppWindow.Closing += (_, e) => { if (!exit && tray is not null) { e.Cancel = true; AppWindow.Hide(); } };
        Closed += (_, _) => { timer.Stop(); closing.Cancel(); selection?.Cancel(); tray?.Dispose(); client?.Dispose(); };
        _ = Initialize();
    }
    private async Task Initialize()
    {
        try
        {
            client = await ShellTrackClient.ConnectAsync(dataRoot, cancellation: closing.Token);
            tray = new TrayIcon(WinRT.Interop.WindowNative.GetWindowHandle(this), BringToFront, Exit);
            ConnectionStatus.Text = "后台已连接 · 关闭窗口后留在托盘 · 输出只读";
            await Refresh(); timer.Start();
        }
        catch (Exception ex) { ConnectionStatus.Text = "连接失败：" + ex.Message; }
    }
    private async Task Refresh()
    {
        if (client is null || refreshing) return;
        refreshing = true;
        try
        {
            var tasks = await client.ListAllAsync(closing.Token);
            var ids = tasks.Select(t => t.Id).ToHashSet();
            for (int i = rows.Count - 1; i >= 0; i--) if (!ids.Contains(rows[i].Session.Id)) rows.RemoveAt(i);
            for (int i = 0; i < tasks.Length; i++)
            {
                var row = rows.FirstOrDefault(r => r.Session.Id == tasks[i].Id);
                if (row is null) { row = new(tasks[i]); rows.Insert(i, row); }
                else { row.Update(tasks[i]); int position = rows.IndexOf(row); if (position != i) rows.Move(position, i); }
            }
            TaskCount.Text = $"{tasks.Count(t => !t.IsFinished)} 个运行中 · {tasks.Length} 个任务";
            if (selectedId is not null && !ids.Contains(selectedId)) ClearSelection();
            string? wanted = pendingId ?? selectedId;
            var target = rows.FirstOrDefault(r => r.Session.Id == wanted);
            if (target is not null) { TaskList.SelectedItem = target; pendingId = null; }
            else if (TaskList.SelectedItem is null && pendingId is null) TaskList.SelectedItem = rows.FirstOrDefault();
            if (selectedId is not null && tasks.FirstOrDefault(s => s.Id == selectedId) is { } info) UpdateDetails(info);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ConnectionStatus.Text = "刷新失败：" + ex.Message + "；点击刷新可重新连接。"; }
        finally { refreshing = false; }
    }
    private static string StateName(SessionState state) => state switch
    {
        SessionState.Starting => "正在启动", SessionState.Running => "运行中", SessionState.Stopping => "正在终止",
        SessionState.Exited => "已结束", SessionState.Failed => "失败", SessionState.Interrupted => "已中断", _ => state.ToString()
    };
    private void UpdateDetails(SessionInfo s)
    {
        LogTitle.Text = DetailTitle.Text = TaskTitle(s.Request);
        LogSummary.Text = $"{StateName(s.State)} · {s.Request.Shell}" + (s.ExitCode is null ? "" : $" · 退出码 {s.ExitCode}") + (s.OutputTruncated ? " · 历史输出不完整" : "");
        DetailStatus.Text = $"{StateName(s.State)}\n\n开始  {s.StartedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}" + (s.EndedAt is null ? "" : $"\n结束  {s.EndedAt.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss}") + $"\n退出码  {s.ExitCode?.ToString() ?? "—"}\n输出  {s.OutputLength:N0} 字节\n保存  {s.RecordedLength:N0} 字节\n\n工作目录\n{s.Request.WorkingDirectory}\n\n任务 ID\n{s.Id}" + (s.Error is null ? "" : "\n\n" + s.Error) + NotificationText(s);
        ActionsButton.IsEnabled = true;
        StopButton.IsEnabled = !s.IsFinished; DeleteButton.IsEnabled = s.IsFinished;
        NotificationToggle.IsChecked = s.Request.Notify;
        NotificationToggle.IsEnabled = !s.IsFinished && !notificationUpdating;
        ToolTipService.SetToolTip(NotificationToggle, s.IsFinished ? "任务已结束，显示执行时的通知设置。" : "在任务结束前开启或关闭通知");
    }
    private static string NotificationText(SessionInfo s)
    {
        if (s.NotificationStatus is null) return "\n\n通知已关闭";
        string status = s.NotificationStatus switch
        {
            "pending" => s.NotificationTrigger is null ? "等待任意条件满足" : "正在发送", "notMatched" => "任务结束，未匹配条件", "submitted" => "已提交给 Windows", "blocked" => "被系统设置阻止",
            "unavailable" => "通知组件不可用", "failed" => "发送失败", "interrupted" => "发送已中断", _ => s.NotificationStatus
        };
        string conditions = (s.Request.Notify ? "\n• 任务结束" : "") + string.Concat(s.Request.NotifyPatterns.Select(p => "\n• 输出正则：" + p));
        string trigger = s.NotificationTrigger is null ? "" : s.NotificationTrigger.Kind == "outputMatch" ? $"\n触发正则：{s.NotificationTrigger.Pattern}\n匹配文本：{s.NotificationTrigger.MatchedText}" : "\n触发条件：任务结束";
        return "\n\n通知\n" + status + conditions + trigger + (s.NotificationError is null ? "" : "\n" + s.NotificationError) + (s.NotificationConditionError is null ? "" : "\n" + s.NotificationConditionError);
    }
    private async void TaskList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TaskList.SelectedItem is not TaskRow row || client is null || selectedId == row.Session.Id) return;
        selectedId = row.Session.Id; selection?.Cancel(); selection = CancellationTokenSource.CreateLinkedTokenSource(closing.Token);
        var cancellation = selection.Token; var source = client; string id = selectedId;
        UpdateDetails(row.Session); logRows.Clear();
        var text = new TextLog(256_000); selectedLog = text; var watch = Stopwatch.StartNew();
        Action<long, long> gap = (from, to) =>
        {
            if (cancellation.IsCancellationRequested || selectedId != id) return;
            text.ResetAfterGap();
            ConnectionStatus.Text = $"输出存在缺口：字节 {from:N0}–{to:N0} 已超过记录与实时缓冲范围。";
        };
        source.OutputGap += gap;
        try
        {
            await foreach (var bytes in source.FollowOutputAsync(id, cancellation: cancellation))
            {
                if (cancellation.IsCancellationRequested || selectedId != id) return;
                text.Append(bytes);
                if (watch.ElapsedMilliseconds >= 100) { RenderLog(text); watch.Restart(); }
            }
            if (!cancellation.IsCancellationRequested && selectedId == id) RenderLog(text);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!cancellation.IsCancellationRequested) ConnectionStatus.Text = "读取失败：" + ex.Message; }
        finally { source.OutputGap -= gap; }
    }
    private void RenderLog(TextLog log)
    {
        var lines = log.GetLines();
        const int maximumRows = 10_000;
        int start = Math.Max(0, lines.Count - maximumRows);
        long first = lines.Count == 0 ? 0 : lines[start].Number;
        if (logRows.Count > 0 && logRows[0].Number != first) logRows.Clear();
        for (int i = start; i < lines.Count; i++)
        {
            int index = i - start;
            if (index < logRows.Count) logRows[index].Update(lines[i].Text);
            else logRows.Add(new(lines[i].Number, lines[i].Text));
        }
        while (logRows.Count > lines.Count - start) logRows.RemoveAt(logRows.Count - 1);
        if (FollowToggle.IsChecked == true && logRows.Count > 0) LogList.ScrollIntoView(logRows[^1], ScrollIntoViewAlignment.Default);
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            selection?.Cancel(); client?.Dispose();
            client = await ShellTrackClient.ConnectAsync(dataRoot, cancellation: closing.Token);
            pendingId = selectedId; selectedId = null;
            TaskList.SelectedItem = null;
            ConnectionStatus.Text = "后台已连接 · 输出只读";
            await Refresh(); timer.Start();
        }
        catch (Exception ex) { ConnectionStatus.Text = ex.Message; }
    }
    private async void NotificationToggle_Click(object sender, RoutedEventArgs e)
    {
        if (selectedId is null || client is null || notificationUpdating) return;
        string id = selectedId; bool enabled = NotificationToggle.IsChecked == true;
        notificationUpdating = true; NotificationToggle.IsEnabled = false;
        try
        {
            var info = await client.SetNotificationAsync(id, enabled, closing.Token);
            rows.FirstOrDefault(r => r.Session.Id == id)?.Update(info);
            if (selectedId == id) UpdateDetails(info);
            ConnectionStatus.Text = enabled ? "已开启完成通知。" : "已关闭完成通知。";
        }
        catch (Exception ex) { ConnectionStatus.Text = "更新通知设置失败：" + ex.Message; }
        finally { notificationUpdating = false; await Refresh(); }
    }
    private async void NotificationConditions_Click(object sender, RoutedEventArgs e)
    {
        if (selectedId is null || client is null || notificationUpdating) return;
        string id = selectedId; var source = client;
        try
        {
            var info = await source.GetAsync(id, closing.Token);
            var completion = new CheckBox { Content = "任务结束时通知", IsChecked = info.Request.Notify, IsEnabled = !info.IsFinished };
            var patterns = new TextBox { Header = "输出正则（每行一个条件）", Text = string.Join("\n", info.Request.NotifyPatterns), AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap, Height = 180, IsReadOnly = info.IsFinished, PlaceholderText = "例如：ERROR|Exception\n服务已启动" };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var content = new StackPanel { Spacing = 12, MinWidth = 300, MaxWidth = 480 };
            content.Children.Add(new TextBlock { Text = "任意一个条件满足即通知，每个任务最多通知一次。修改正则仅检查保存后的新输出。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(completion); content.Children.Add(patterns); content.Children.Add(error);
            var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "通知条件", Content = content, PrimaryButtonText = "保存",
                IsPrimaryButtonEnabled = !info.IsFinished, CloseButtonText = info.IsFinished ? "关闭" : "取消", DefaultButton = ContentDialogButton.Close };
            dialog.PrimaryButtonClick += async (senderDialog, click) =>
            {
                click.Cancel = true; var deferral = click.GetDeferral();
                try
                {
                    string[] expressions = patterns.Text.Split('\n').Select(p => p.TrimEnd('\r')).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
                    _ = new OutputNotificationMatcher(expressions);
                    var updated = await source.SetNotificationConditionsAsync(id, new(completion.IsChecked == true, expressions), closing.Token);
                    rows.FirstOrDefault(r => r.Session.Id == id)?.Update(updated);
                    if (selectedId == id) UpdateDetails(updated);
                    ConnectionStatus.Text = "通知条件已保存。"; click.Cancel = false;
                }
                catch (Exception ex) { error.Text = ex.Message; }
                finally { deferral.Complete(); }
            };
            notificationUpdating = true; await dialog.ShowAsync();
        }
        catch (Exception ex) { ConnectionStatus.Text = "读取通知条件失败：" + ex.Message; }
        finally { notificationUpdating = false; await Refresh(); }
    }
    private async void Stop_Click(object sender, RoutedEventArgs e) => await Operate(async () => { if (selectedId is not null) await client!.TerminateAsync(selectedId); });
    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (selectedId is null) return;
        string id = selectedId;
        var dialog = new ContentDialog { XamlRoot = Content.XamlRoot, Title = "删除任务历史？", Content = "输出记录将一并删除。", PrimaryButtonText = "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        await Operate(async () => { await client!.DeleteAsync(id); if (selectedId == id) ClearSelection(); });
    }
    private async void Folder_Click(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        if (selectedId is null) return;
        string folder = (await client!.GetAsync(selectedId)).Request.WorkingDirectory;
        using var process = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    });
    private async void Export_Click(object sender, RoutedEventArgs e) => await Operate(async () =>
    {
        if (selectedId is null) return;
        string id = selectedId;
        var picker = new FileSavePicker { SuggestedFileName = "shelltrack-" + id };
        picker.FileTypeChoices.Add("原始终端输出", new List<string> { ".bin" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        long length = (await client!.GetAsync(id)).RecordedLength, offset = 0;
        await using var output = new FileStream(file.Path, FileMode.Create, FileAccess.Write);
        while (offset < length)
        {
            var page = await client.OutputAsync(id, offset, closing.Token);
            var bytes = Convert.FromBase64String(page.Data);
            int count = (int)Math.Min(bytes.Length, length - offset);
            if (count == 0) throw new IOException("输出记录在导出期间发生变化，请重试。");
            await output.WriteAsync(bytes.AsMemory(0, count)); offset += count;
        }
        ConnectionStatus.Text = "已导出：" + file.Path;
    });
    private void Copy_Click(object sender, RoutedEventArgs e) => CopyLog();
    private void CopyLog()
    {
        if (selectedLog is null) return;
        var package = new DataPackage(); package.SetText(selectedLog.ToString()); Clipboard.SetContent(package);
        ConnectionStatus.Text = "已复制当前日志视图。";
    }
    private void LogList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.C || !Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(global::Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        // TextBlock handles copying a text selection; otherwise copy the selected logical line.
        if (e.OriginalSource is TextBlock) return;
        if (LogList.SelectedItem is LogRow row)
        {
            var package = new DataPackage(); package.SetText(row.Text); Clipboard.SetContent(package); e.Handled = true;
        }
    }
    private async Task Operate(Func<Task> action)
    {
        try { await action(); await Refresh(); }
        catch (Exception ex) { ConnectionStatus.Text = "操作失败：" + ex.Message; }
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();
    private void Exit() { exit = true; Close(); Application.Current.Exit(); }
    public void BringToFront()
    {
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        AppWindow.Show();
        if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
        Activate();
        SetForegroundWindow(handle);
    }
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    private void DetailsToggle_Click(object sender, RoutedEventArgs e) { ApplyLayout(); SavePreferences(); }
    private void FollowToggle_Click(object sender, RoutedEventArgs e)
    {
        if (FollowToggle.IsChecked == true && logRows.Count > 0) LogList.ScrollIntoView(logRows[^1]);
        SavePreferences();
    }
    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyLayout();
    private void ApplyLayout()
    {
        if (TaskColumn is null || DetailsColumn is null) return;
        double width = RootGrid.ActualWidth;
        TaskColumn.Width = new GridLength(width < 900 ? 220 : 290);
        RootGrid.Padding = new Thickness(width < 900 ? 16 : 24);
        bool visible = DetailsToggle.IsChecked == true && width >= 1100;
        DetailsColumn.Width = new GridLength(visible ? 280 : 0);
        DetailsPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(DetailsToggle, width < 1100 ? "加宽窗口后显示右侧详情" : "显示或收起任务详情");
    }
    private string PreferencesPath => Path.Combine(dataRoot ?? ShellTrackClient.DefaultDataRoot, "desktop-settings.json");
    private void ReadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return;
            var settings = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(PreferencesPath));
            if (settings is not null) { DetailsToggle.IsChecked = settings.DetailsVisible; FollowToggle.IsChecked = settings.FollowOutput; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
    }
    private void SavePreferences()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencesPath)!);
            File.WriteAllText(PreferencesPath, JsonSerializer.Serialize(new Preferences(DetailsToggle.IsChecked == true, FollowToggle.IsChecked == true)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ConnectionStatus.Text = "视图设置未保存：" + ex.Message; }
    }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    private void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyLayout();
        if (Environment.GetEnvironmentVariable("SHELLTRACK_DIAGNOSTICS") != "1") return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var diagnostics = new
        {
            dpi = GetDpiForWindow(hwnd), xamlScale = RootGrid.XamlRoot.RasterizationScale,
            perMonitorV2 = AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(hwnd), new IntPtr(-4)),
            width = RootGrid.ActualWidth, height = RootGrid.ActualHeight, readOnly = true, numberedLogs = true,
            detailsVisible = DetailsPanel.Visibility == Visibility.Visible,
            packageFamily = ShellTrack.Windows.PackageIdentity.FamilyName,
            physicalWidth = AppWindow.Size.Width, physicalHeight = AppWindow.Size.Height
        };
        Directory.CreateDirectory(dataRoot ?? ShellTrackClient.DefaultDataRoot);
        File.WriteAllText(Path.Combine(dataRoot ?? ShellTrackClient.DefaultDataRoot, "ui-diagnostics.json"), JsonSerializer.Serialize(diagnostics));
    }
    public void SelectTask(string id) { pendingId = id; _ = Refresh(); }
    private void ClearSelection()
    {
        selection?.Cancel(); selectedId = null; selectedLog = null; logRows.Clear();
        LogTitle.Text = DetailTitle.Text = "选择一个任务"; DetailStatus.Text = ""; LogSummary.Text = "选择左侧任务，查看实时输出和历史日志。";
        ActionsButton.IsEnabled = NotificationToggle.IsEnabled = false;
        NotificationToggle.IsChecked = false;
    }
}
