# ShellTrack.Desktop

WinUI 3 原生任务查看器，使用 `ShellTrack.Client` 查询后台并执行管理操作，使用 `ShellTrack.Core.TextLog` 生成只读纯文本日志。它不引用 Host，不持有 ConPTY，也不启用终端输入能力。

## 界面

任务卡片显示状态、时间、退出码、通知开关与最新可读输出。选择任务后持续读取日志：每个逻辑行只显示一个行号，窄宽度下自动换行，支持悬停高亮、选中、复制与跟随。详情可以收起，释放日志宽度；详情与跟随偏好保存在数据根目录的 `desktop-settings.json`。

窗口可以终止任务、删除已完成历史、打开工作目录、导出落盘输出快照，并在任务结束前调整通知开关。原始输出和纯文本复制是不同格式；纯文本不保留终端控制序列。

关闭窗口会隐藏到托盘；双击托盘图标重新打开。“退出”仅结束查看器，不关闭后台或终止任务。同一数据目录的查看窗口使用单实例激活。

## 构建与运行

```powershell
pwsh -NoProfile -File scripts/build.ps1
dotnet run --project src/ShellTrack.Cli -- ui
```

该项目采用 unpackaged、Windows App SDK 自包含部署，目标为 Windows x64。仍需要 .NET 10 Runtime；通过 CLI 自动启动 Host 时还需要 ASP.NET Core 10 Runtime。

窗口 manifest 为 PerMonitorV2，初始物理像素尺寸按 DPI 换算，XAML 使用布局取整。150% 缩放下整体字体清晰度已由用户确认修复。

## 通知入口与诊断

Host 可启动 `ShellTrack.Desktop.exe --notify <taskId> --data-dir <path>`，辅助进程通过只读 API 获取任务并提交通知。普通启动支持 `--data-dir <path>` 和 `--task <taskId>` 定位任务。通知诊断写在数据根目录的 `notifications/<taskId>.json`，错误记录在 `desktop-errors.log`。

通知 API 接受与通知实际可见是两回事。当前机器出现过提交和历史查询成功、用户仍看不到通知的情况，尚未完成实际显示与点击激活验收。

设置 `SHELLTRACK_DIAGNOSTICS=1` 可生成 `ui-diagnostics.json`，记录 DPI、XAML 缩放、窗口尺寸和只读能力。这个诊断不替代界面人工检查。
