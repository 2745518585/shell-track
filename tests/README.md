# 自动化验证

`ShellTrack.Integration` 是不依赖额外测试框架的 Windows 控制台测试程序。它使用真实的 Host、HTTP/WebSocket 接口、CMD、PowerShell 7 和 ConPTY，任一检查失败会返回非零退出码。

在仓库根目录使用 PowerShell 7：

```powershell
./scripts/build.ps1 -Configuration Release
./scripts/test.ps1 -Configuration Release -NoBuild
```

只验证核心时可以用 `build.ps1 -CoreOnly`。`test.ps1` 默认允许重新构建；`-NoBuild` 使用指定配置的现有构建，适合 CI。

检查覆盖：

- UTF-8、ANSI 控制序列跨块解码，逻辑行号、空行、回车和退格覆写、长行、有界整行裁剪和输出缺口。
- CMD/PowerShell 的真实输出、引号、中文与退出码，以及 CLI 输出/退出码透传。
- PowerShell 将 CLI 输出存入变量时，自动启动后台不会阻塞管道；独立数据目录包含中文与空格。
- 幂等创建、并发输出隔离、交互输入、终端尺寸、唯一输入所有者、断开时终止或保留任务。
- 输出录制上限、仍可转发实时输出、慢读取者的缺口标记，以及运行中/溢出后的最新输出预览。
- 本地 API 的身份验证、只读与管理权限、网页来源拒绝、自动化事件订阅和通知设置切换。
- Host 被终止后的会话恢复、子进程清理、历史输出/预览/通知设置保留，以及正常关闭清理连接描述。

每次运行使用独立的 `work/tests-<随机编号>` 数据目录，不连接现有任务后台；完成时停止测试 Host。失败时保留该目录，并输出它的路径。Host 标准输出和错误保存在 `host-<序号>-stdout.log`、`host-<序号>-stderr.log`。

GitHub Actions 的 `windows.yml` 在 `windows-latest` 上构建核心和 WinUI，再运行同一组检查，并用 `verify-bundle.ps1` 验证发布包内的 CLI/Host 和 XAML 资源。失败附件只收集测试日志与会话数据，不上传连接凭据。手动运行可选择上传 x64 发布包。

CI 不要求可见桌面，不验证通知横幅或 WinUI 的视觉效果。通知相关测试只改变任务设置，并在任务结束前关闭通知；字体缩放、托盘菜单、日志换行与鼠标高亮仍需要人工确认。
