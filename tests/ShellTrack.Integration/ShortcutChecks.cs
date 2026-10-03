using ShellTrack.Windows;

internal static class ShortcutChecks
{
    public static void Run(string root, Action<bool, string> check)
    {
        // Exercise real Shell properties inside the fixture, never the user's Start menu.
        string target = Environment.ProcessPath!;
        string icon = Path.Combine(Path.GetDirectoryName(target)!, "ShellTrack.Integration.dll");
        string shortcut = Path.Combine(root, "shortcut 中文", "Shell Track.lnk");
        Guid activator = Guid.NewGuid();
        check(ApplicationShortcut.Ensure(shortcut, target, icon, "ShellTrack.Test", activator), "首次创建带通知身份的 Shell 快捷方式");
        var info = ApplicationShortcut.Inspect(shortcut);
        check(info.Target == target && info.Arguments == "" && info.Icon == icon && info.AppId == "ShellTrack.Test" && info.Activator == activator,
            "真实快捷方式保留目标、图标、AUMID 和通知激活器");
        var modified = File.GetLastWriteTimeUtc(shortcut);
        check(!ApplicationShortcut.Ensure(shortcut, target, icon, "ShellTrack.Test", activator) && File.GetLastWriteTimeUtc(shortcut) == modified,
            "重复注册不重写已匹配的快捷方式");
        Guid replacement = Guid.NewGuid();
        check(ApplicationShortcut.Ensure(shortcut, target, icon, "ShellTrack.Test.Updated", replacement) &&
            ApplicationShortcut.Inspect(shortcut).Activator == replacement && ApplicationShortcut.Inspect(shortcut).AppId == "ShellTrack.Test.Updated",
            "已有快捷方式更新到当前通知身份");
        check(Directory.GetFiles(Path.GetDirectoryName(shortcut)!).Length == 1, "快捷方式更新不留下临时文件");
        check(File.ReadAllBytes(PhysicalPath.ResolveFile(shortcut)).SequenceEqual(File.ReadAllBytes(shortcut)),
            "跨进程路径解析返回可读取的真实文件路径，支持中文");
        string localFixture = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ShellTrack.PathTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(localFixture);
        string localFile = Path.Combine(localFixture, "probe.txt");
        try
        {
            File.WriteAllText(localFile, "PATH_PROBE");
            check(File.ReadAllText(PhysicalPath.ResolveFile(localFile)) == "PATH_PROBE",
                "LocalAppData 文件解析到相同内容，兼容调用方文件虚拟化");
        }
        finally { File.Delete(localFile); Directory.Delete(localFixture); }
    }
}
