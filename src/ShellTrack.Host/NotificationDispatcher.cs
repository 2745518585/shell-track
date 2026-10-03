using System.Diagnostics;
using System.Text.Json;
using ShellTrack.Contracts;

namespace ShellTrack.Host;

internal sealed class NotificationDispatcher(string dataRoot, ILogger logger)
{
    public async Task<NotificationResult> SendAsync(SessionInfo session)
    {
        try
        {
            string? path = FindDesktop();
            if (path is null) return new("unavailable", "未找到桌面程序，请先构建或发布 WinUI 3 客户端。");
            var start = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(dataRoot);
            start.ArgumentList.Add("--notify"); start.ArgumentList.Add(session.Id);
            using var child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Do not leave failed notification helper processes behind.
                child.Kill(); return new("failed", "通知程序超时，查看 desktop-errors.log。");
            }
            string audit = Path.Combine(dataRoot, "notifications", session.Id + ".json");
            if (File.Exists(audit)) return JsonSerializer.Deserialize<NotificationResult>(await File.ReadAllTextAsync(audit), Protocol.Json)
                ?? new("failed", "通知诊断记录为空。");
            return child.ExitCode == 0 ? new("submitted", "Windows 已接受通知；弹窗显示由系统决定。") : new("failed", "通知发送失败，查看 desktop-errors.log。");
        }
        catch (Exception ex) { logger.LogWarning(ex, "通知发送失败 {Id}", session.Id); return new("failed", ex.Message); }
    }
    private static string? FindDesktop()
    {
        string name = "ShellTrack.Desktop.exe";
        string adjacent = Path.Combine(AppContext.BaseDirectory, "desktop", name);
        if (File.Exists(adjacent)) return adjacent;
        adjacent = Path.Combine(AppContext.BaseDirectory, name);
        if (File.Exists(adjacent)) return adjacent;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "ShellTrack.slnx"))) continue;
            string bin = Path.Combine(directory.FullName, "src", "ShellTrack.Desktop", "bin");
            if (!Directory.Exists(bin)) return null;
            return Directory.EnumerateFiles(bin, name, SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        return null;
    }
}
