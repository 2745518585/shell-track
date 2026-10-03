using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using Microsoft.Windows.AppLifecycle;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Runtime.InteropServices;
using ShellTrack.Contracts;
using ShellTrack.Client;
using Microsoft.Win32;

namespace ShellTrack.Desktop;

public partial class App : Application
{
    private static bool HasPackageIdentity
    {
        get { uint length = 0; return GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700; }
    }
    private MainWindow? window;
    private DispatcherQueue? dispatcher;
    private AppInstance? instance;
    public App()
    {
        // Keep taskbar and notification identity stable across build/publish paths.
        if (!HasPackageIdentity) Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID("ShellTrack.Desktop"));
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        dispatcher = DispatcherQueue.GetForCurrentThread();
        string? root = null, notifyId = null, showId = null;
        var arguments = Environment.GetCommandLineArgs();
        for (int i = 1; i < arguments.Length; i++)
        {
            if (arguments[i] is "--data-dir" or "-d" && i + 1 < arguments.Length) root = arguments[++i];
            else if (arguments[i] is "--notify" or "-n" && i + 1 < arguments.Length) notifyId = arguments[++i];
            else if (arguments[i] is "--task" or "-t" && i + 1 < arguments.Length) showId = arguments[++i];
        }
        Exception? registrationError = null;
        bool shortcutChanged = false;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, invocation) =>
                dispatcher.TryEnqueue(() =>
                {
                    string? notificationRoot = invocation.Arguments.TryGetValue("dataRoot", out var value) ? value : root;
                    string? taskId = invocation.Arguments.TryGetValue("taskId", out var id) ? id : null;
                    if (notifyId is not null || !SameRoot(notificationRoot, root)) LaunchViewer(notificationRoot, taskId);
                    else if (window is not null) Show(notificationRoot, taskId);
                });
            string icon = Path.Combine(AppContext.BaseDirectory, "assets", "shelltrack.png");
            if (!HasPackageIdentity && File.Exists(icon)) AppNotificationManager.Default.Register("Shell Track", new Uri(icon));
            else AppNotificationManager.Default.Register();
            if (!HasPackageIdentity) shortcutChanged = UpdateActivationPath();
        }
        catch (Exception ex) { registrationError = ex; WriteDiagnostic(root, "通知注册失败：" + ex); }
        if (notifyId is not null)
        {
            try
            {
                if (registrationError is not null) throw new InvalidOperationException("通知注册失败。", registrationError);
                if (!AppNotificationManager.IsSupported()) throw new InvalidOperationException("当前进程不支持 Windows 通知；请以普通用户运行。");
                if (shortcutChanged) await Task.Delay(500);
                using var client = await ShellTrackClient.ConnectAsync(root, autoStart: false, readOnly: true);
                var task = await client.GetAsync(notifyId);
                var setting = AppNotificationManager.Default.Setting;
                SHQueryUserNotificationState(out int systemState);
                if (setting != AppNotificationSetting.Enabled)
                {
                    WriteNotification(root, notifyId, new("blocked", "Windows 通知设置阻止了应用通知。", Setting: setting.ToString(), SystemState: NotificationStateName(systemState)));
                    Environment.ExitCode = 1; Exit(); return;
                }
                var notification = new AppNotificationBuilder()
                    .AddArgument("taskId", task.Id)
                    .AddArgument("dataRoot", client.DataRoot)
                    .AddText(task.ExitCode == 0 ? "Shell Track：执行完成" : "Shell Track：任务结束")
                    .AddText(task.Request.Command ?? (string.IsNullOrEmpty(task.Request.RawArguments) ? "交互会话" : task.Request.RawArguments))
                    .AddText($"{task.Request.Shell} · {task.State} · 退出码 {task.ExitCode?.ToString() ?? "无"}")
                    .BuildNotification();
                AppNotificationManager.Default.Show(notification);
                if (notification.Id == 0) throw new InvalidOperationException("Windows 未接受该通知。");
                await Task.Delay(300);
                bool inHistory = (await AppNotificationManager.Default.GetAllAsync()).Any(n => n.Id == notification.Id);
                WriteNotification(root, notifyId, new("submitted", "Windows 已接受通知；弹窗显示由系统决定。", notification.Id, setting.ToString(), NotificationStateName(systemState), inHistory));
                await Task.Delay(8000);
            }
            catch (Exception ex) { Environment.ExitCode = 1; WriteDiagnostic(root, "通知发送失败：" + ex); WriteNotification(root, notifyId, new("failed", ex.Message)); }
            try { AppNotificationManager.Default.Unregister(); } catch (Exception ex) { WriteDiagnostic(root, ex.Message); }
            Exit(); return;
        }
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs notificationArgs)
        {
            if (notificationArgs.Arguments.TryGetValue("dataRoot", out var notificationRoot)) root = notificationRoot;
            if (notificationArgs.Arguments.TryGetValue("taskId", out var notificationTask)) showId = notificationTask;
        }
        string instanceRoot = Path.GetFullPath(root ?? ShellTrackClient.DefaultDataRoot).ToUpperInvariant();
        instance = AppInstance.FindOrRegisterForKey("ShellTrack.UI." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instanceRoot))));
        if (!instance.IsCurrent)
        {
            AllowSetForegroundWindow(instance.ProcessId);
            await instance.RedirectActivationToAsync(activation);
            Exit(); return;
        }
        instance.Activated += (_, incoming) => dispatcher.TryEnqueue(() =>
        {
            string? task = null;
            if (incoming.Kind == ExtendedActivationKind.AppNotification && incoming.Data is AppNotificationActivatedEventArgs notification)
                notification.Arguments.TryGetValue("taskId", out task);
            else if (incoming.Data is global::Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
            {
                var match = Regex.Match(launch.Arguments, @"--task\s+([a-fA-F0-9]{32})");
                if (match.Success) task = match.Groups[1].Value;
            }
            Show(root, task);
        });
        Show(root, showId);
    }
    private void Show(string? root, string? id)
    {
        window ??= new MainWindow(root, id);
        window.BringToFront();
        if (id is not null) window.SelectTask(id);
    }
    private static void WriteDiagnostic(string? root, string message)
    {
        try
        {
            string directory = root ?? ShellTrackClient.DefaultDataRoot;
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "desktop-errors.log"), DateTimeOffset.UtcNow + " " + message + "\n");
        }
        catch (IOException) { }
    }
    private static void LaunchViewer(string? root, string? id)
    {
        var start = new ProcessStartInfo(ShellTrack.Windows.PackageIdentity.ViewerExecutable ?? Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (root is not null) { start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(root); }
        if (id is not null) { start.ArgumentList.Add("--task"); start.ArgumentList.Add(id); }
        using var child = Process.Start(start);
        if (child is not null) AllowSetForegroundWindow((uint)child.Id);
    }
    private static bool SameRoot(string? first, string? second) => string.Equals(
        Path.GetFullPath(first ?? ShellTrackClient.DefaultDataRoot).TrimEnd(Path.DirectorySeparatorChar),
        Path.GetFullPath(second ?? ShellTrackClient.DefaultDataRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
    private static bool UpdateActivationPath()
    {
        // The SDK reuses a COM activator for a fixed AUMID. Keep its launch path
        // current when a portable build moves, without clearing notification history.
        using var identity = Registry.CurrentUser.OpenSubKey(@"Software\Classes\AppUserModelId\ShellTrack.Desktop");
        if (identity?.GetValue("CustomActivator") is not string value || !Guid.TryParse(value, out var activator))
            throw new InvalidOperationException("Windows 通知激活器注册缺失。");
        using var server = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\" + activator.ToString("B") + @"\LocalServer32", writable: true);
        server?.SetValue("", "\"" + Environment.ProcessPath + "\" ----AppNotificationActivated:", RegistryValueKind.ExpandString);
        // The notification database may accept a toast even when Shell cannot
        // resolve the application. Register its Start menu identity as well.
        return ShellTrack.Windows.ApplicationShortcut.Ensure(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Shell Track", "Shell Track.lnk"),
            Environment.ProcessPath!, Path.Combine(AppContext.BaseDirectory, "assets", "shelltrack.ico"), "ShellTrack.Desktop", activator);
    }
    private static void WriteNotification(string? root, string id, NotificationResult result)
    {
        string directory = Path.Combine(root ?? ShellTrackClient.DefaultDataRoot, "notifications");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, id + ".json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(result, Protocol.Json));
        File.Move(path + ".tmp", path, true);
    }
    private static string NotificationStateName(int state) => state switch
    {
        1 => "notPresent", 2 => "busy", 3 => "fullScreenGame", 4 => "presentation", 5 => "acceptsNotifications", 6 => "quietTime", 7 => "app", _ => "unknown"
    };
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern int GetCurrentPackageFullName(ref uint length, IntPtr name);
}
