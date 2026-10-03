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
- CLI 短选项、查询/查看/终止/导出/删除/关闭子命令简写，以及后台的数据目录与输出上限短选项。
- 三种独立 shell 包装入口与直接调用的输出/退出码对比，原始参数记录、复杂引号、脚本路径空格与中文、空参数、尾部反斜杠、编码命令、shell 帮助和无参数交互。
- 幂等创建、并发输出隔离、交互输入、终端尺寸、唯一输入所有者、断开时终止或保留任务。
- 输出录制上限、仍可转发实时输出、慢读取者的缺口标记，以及运行中/溢出后的最新输出预览。
- 本地 API 的身份验证、只读与管理权限、网页来源拒绝、自动化事件订阅和通知设置切换。
- Host 被终止后的会话恢复、子进程清理、历史输出/预览/通知设置保留，以及正常关闭清理连接描述。

每次运行使用独立的 `work/tests-<随机编号>` 数据目录，不连接现有任务后台；完成时停止测试 Host。失败时保留该目录，并输出它的路径。Host 标准输出和错误保存在 `host-<序号>-stdout.log`、`host-<序号>-stderr.log`。

GitHub Actions 的 `windows.yml` 在 `windows-2022` 上构建核心和 WinUI，再运行同一组检查，并用 `verify-bundle.ps1` 验证发布包内的 CLI/Host 和 XAML 资源。失败附件只收集测试日志与会话数据，不上传连接凭据。手动运行可选择上传 x64 发布包。

CI 不要求可见桌面，不验证通知横幅或 WinUI 的视觉效果。通知相关测试只改变任务设置，并在任务结束前关闭通知；字体缩放、托盘菜单、日志换行与鼠标高亮仍需要人工确认。

安装包另用 `scripts/verify-installers.ps1` 验证 EXE 签名、MSIX 密码学签名与所有文件块摘要，再在独立临时目录测试 EXE 安装、PATH、实际命令执行和卸载。已安装的 EXE 不会被测试覆盖。CI 构建并执行这组检查；仅输出公开证书，不上传私钥。

可选 `-TestMsixLayout` 要求已开启 Windows 开发模式，从签名包解包后进行开发注册，验证五个别名、后台、窗口身份/DPI和注销后数据保留。已有 MSIX 安装时拒绝覆盖；不会修改开发模式或导入信任证书。此检查不能替代使用者信任证书后的正式 MSIX 安装验收。

## 发布流程检查

`scripts/test-release.ps1` 检查版本边界、附件完整性、篡改/缺失/重复摘要与意外私钥附件；`scripts/test-release-signing.ps1` 在 Windows 生成并清理一次性证书，验证错误发布者拒绝、正确 PFX 导入、临时文件清理和信任库未变化。两者加入 CI。普通 CI 使用固定测试版本 `0.3.0` 实际生成 ZIP 和完整发布附件集合，并检查程序版本、摘要和布局；它使用一次性开发证书，不依赖正式签名 Secrets，也不创建 GitHub Release。`verify-release-assets.ps1` 在正式发布前及跨 job 下载后重复验证实际发布产物，发布 job 再验证 GitHub 构建来源证明。GitHub Environment、令牌权限与在线 Release 发布仍需首次远程试跑验收。
