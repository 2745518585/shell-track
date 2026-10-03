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
            string audit = Path.Combine(dataRoot, "notifications", session.Id + ".json");
            // A parent MSIX application can virtualize HKCU even for an unpackaged
            // child. Explorer launches the helper outside that inherited context,
            // so the notification's COM registration is visible to Windows Shell.
            if (ShellTrack.Windows.PackageIdentity.FamilyName is null)
            {
                try
                {
                    // LocalAppData can also be virtualized by the parent package.
                    // Read the same Host credentials through their physical backing directory.
                    string physicalRoot = Path.GetDirectoryName(ShellTrack.Windows.PhysicalPath.ResolveFile(Path.Combine(dataRoot, "connection.json")))!;
                    string desktopAudit = Path.Combine(physicalRoot, "notifications", session.Id + ".json");
                    if (ShellTrack.Windows.DesktopProcessLauncher.TryLaunch(path, ["--data-dir", physicalRoot, "--notify", session.Id]))
                    {
                        var expires = DateTime.UtcNow.AddSeconds(20);
                        while (DateTime.UtcNow < expires)
                        {
                            if (File.Exists(desktopAudit)) return JsonSerializer.Deserialize<NotificationResult>(await File.ReadAllTextAsync(desktopAudit), Protocol.Json)
                                ?? new("failed", "通知诊断记录为空。");
                            await Task.Delay(100);
                        }
                        return new("failed", "桌面通知程序未在时限内生成诊断，查看 desktop-errors.log。");
                    }
                }
                catch (Exception ex) { logger.LogDebug(ex, "无法通过 Windows 桌面启动通知程序，使用直接启动。"); }
            }
            using var child = Process.Start(start)!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                // Do not leave failed notification helper processes behind.
                child.Kill(); return new("failed", "通知程序超时，查看 desktop-errors.log。");
            }
            if (File.Exists(audit)) return JsonSerializer.Deserialize<NotificationResult>(await File.ReadAllTextAsync(audit), Protocol.Json)
                ?? new("failed", "通知诊断记录为空。");
            return new("failed", child.ExitCode == 0 ? "通知程序未生成发送诊断，无法确认 Windows 接受了通知。" : "通知发送失败，查看 desktop-errors.log。");
        }
        catch (Exception ex) { logger.LogWarning(ex, "通知发送失败 {Id}", session.Id); return new("failed", ex.Message); }
    }
    private static string? FindDesktop()
    {
        if (ShellTrack.Windows.PackageIdentity.ViewerExecutable is string packaged) return packaged;
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
