# Shell Track

通过一个命令启动 Windows shell，在原终端中照常操作，再用独立窗口查看所有任务的状态、实时输出和历史记录。

Shell Track 使用 ConPTY 代理终端。CLI 负责输入输出转发，后台负责执行和记录，WinUI 3 窗口只查看输出并执行管理操作。自动化程序可以使用同一套本地 HTTP/WebSocket API。

这是可运行的初版，当前主要面向 Windows x64。

## 已实现

- 交互式 `pwsh`、`powershell`、`cmd`，以及通过选项执行单次命令；等待执行时返回 shell 的退出码。
- CLI 自动启动当前用户的后台；支持并发任务、终止、后台执行、历史查看和导出。
- 原生 WinUI 3 窗口、任务栏图标和托盘。关闭窗口后留在托盘，退出查看器时后台任务继续运行。
- 任务卡片显示最新一行可读输出。日志按逻辑行编号，长行自动换行并保留同一个行号，支持悬停高亮、选中、复制和跟随输出。右侧详情可以收起。
- 创建时选择完成通知，也能在窗口中修改运行中任务的通知开关。通知发送结果与 Windows 诊断单独记录。
- 受当前用户文件权限保护的本地 API；查询、管理与终端输入使用不同能力的凭据。查看器不启用终端输入能力。
- 输出记录、7 天历史保留、存储上限和后台异常退出后的记录恢复。
- Windows 集成测试及 GitHub Actions 构建检查。

## 快速开始

在发布目录运行。默认交互 shell 是 PowerShell 7，需要已安装 `pwsh`；也可以直接选择 Windows 自带的 `cmd` 或 `powershell`。

```powershell
# 打开交互式终端
.\shelltrack.exe
.\shelltrack.exe --shell cmd

# 执行命令，转发输出并返回实际退出码
.\shelltrack.exe --command 'Write-Output "Hello from Shell Track"; exit 0'
.\shelltrack.exe --shell cmd --command 'dir & exit /b 0'

# 打开任务管理窗口
.\shelltrack.exe ui
```

后台执行会立即打印任务 ID，原终端无需一直保持打开：

```powershell
$taskId = .\shelltrack.exe --command 'Start-Sleep -Seconds 10; Write-Output "完成"' --detach --notify
.\shelltrack.exe list
.\shelltrack.exe show $taskId
.\shelltrack.exe export $taskId --output .\task-output.bin
.\shelltrack.exe delete $taskId
```

`show` 和 CLI `export` 从起点读取输出，任务尚未结束时会持续跟随。导出文件必须不存在。窗口中的导出保存当前已落盘输出的快照；原始输出包含终端控制序列，适合进一步处理。

常用选项与操作：

| 命令或选项 | 行为 |
|---|---|
| `--shell pwsh\|powershell\|cmd` | 选择 shell |
| `--command <script>`、`-c <script>` | 执行单次命令；没有该选项时进入交互模式 |
| `--cwd <path>` | 指定任务工作目录 |
| `--detach` | 单次命令在后台运行，立即返回任务 ID |
| `--keep` | 交互 CLI 断开后保留 shell；目前没有 CLI 重新接管输入的命令 |
| `--notify` | 请求任务结束后发送 Windows 通知 |
| `--data-dir <path>` | 使用独立的数据目录与后台实例 |
| `list --json` | 输出结构化任务列表 |
| `stop <taskId>` | 终止该任务及其所属进程 |
| `delete <taskId>` | 删除已结束任务与记录；运行中任务不能删除 |
| `shutdown` | 关闭该数据目录的后台，并终止它拥有的所有运行任务 |
| `--help` | 查看完整命令帮助 |

单次命令在客户端断开后继续执行。交互会话默认在输入客户端断开后终止，`--keep` 可改变这个策略。Shell Track 自身发生错误时 CLI 返回 `125`。

## 构建、测试和发布

开发使用 .NET 10 SDK 和 PowerShell 7。仓库 `global.json` 的起始版本是 `10.0.103`，允许使用后续 .NET 10 feature band。已验证环境为 Windows 11 x64、Visual Studio 2022 和 Windows SDK。WinUI 编译需要 Windows；纯核心解决方案不包含桌面项目。

```powershell
# 构建 CLI、后台和桌面窗口
pwsh -NoProfile -File scripts/build.ps1

# 只构建 CLI、后台与集成测试
pwsh -NoProfile -File scripts/build.ps1 -CoreOnly

# 运行真实 ConPTY 与本地 API 集成测试
pwsh -NoProfile -File scripts/test.ps1

# 创建新的 Release 发布目录
pwsh -NoProfile -File scripts/publish.ps1
```

发布产物位于 `artifacts/shelltrack-win-x64-<时间>/`。运行入口为根目录的 `shelltrack.exe`，桌面文件位于 `desktop/`。请整体保留该目录结构；`shelltrack ui` 会找到配套窗口，窗口会找到根目录的后台。

发布采用 framework-dependent .NET，目标机器需要 .NET 10 Runtime 和 ASP.NET Core 10 Runtime；.NET 10 SDK 已包含这些运行时。桌面目录包含 Windows App SDK 的必要运行组件，无需另外安装 Windows App SDK。当前没有安装器、自动更新或自动设置 PATH。

构建脚本把 NuGet 缓存与临时目录放在仓库 `work/` 中，并在结束后恢复当前进程的原环境变量，不修改全局配置。发布脚本会复制本项目许可证、第三方说明与依赖包的许可文件。

GitHub Actions 在 Windows runner 上构建核心、运行集成测试并编译 WinUI，也会验证发布包的 CLI、后台和窗口资源；手动运行工作流时可选上传 x64 发布附件。通知实际显示、托盘操作和 DPI 清晰度需要交互式 Windows 桌面验收，不能由无交互的 CI 证明。

## 记录与接口

默认数据目录是 `%LOCALAPPDATA%\ShellTrack`。CLI 和窗口也接受 `SHELLTRACK_DATA_DIR` 环境变量；显式 `--data-dir` 优先。每个数据目录有独立的后台和凭据。

- 已结束任务默认保留 7 天，启动后台、创建任务和每小时检查时清理。
- 默认每任务记录最多 64 MiB，总记录配额为 1 GiB，最多同时运行 16 个任务。创建时会为运行任务预留其记录额度；空间不足时需要清理历史。
- 达到记录上限后 shell 仍继续运行。额外输出只保留在有界的 4 MiB 实时缓冲中，查看器过慢或后台重启时可能产生缺口，API 与 CLI 会明确报告。
- 原始输入不保存。命令、工作目录与输出可能含敏感内容；历史记录和导出文件应按这些内容管理。

自动化接口只监听 `127.0.0.1` 的动态端口，不开放远程访问。端口发现、认证、创建与输出分页示例见 [API 文档](docs/protocol.md)，后台与数据结构见 [架构](docs/architecture.md)。

## 当前限制

任务单位是整个 shell 会话或单次命令，交互式 shell 内部的逐条命令没有独立状态。记录的是 ConPTY 的终端输出，stdout/stderr 已合并，不能保证原程序两个通道的字节完全不变。

窗口提供纯文本日志视图，处理 UTF-8 跨块解码、常见 ANSI 控制序列与回车覆盖，尚未实现完整终端模拟器。因此 `vim`、`top` 一类全屏交互程序的历史显示不会完全重现原屏幕。窗口里的自动换行仅影响查看形式，不改变运行 shell 的终端尺寸。

完成通知仍需进一步验证：Windows API 在当前机器接受过通知，通知历史查询也报告存在，但用户在弹窗与通知中心中均未看到它。`submitted` 只表示提交给系统，不能作为实际显示成功的证明。这个问题不影响任务执行、记录与通知开关；后续工作见 [路线图](docs/roadmap.md)。

后台异常退出会结束它拥有的进程，下次启动将未完成记录标记为 `Interrupted`，不会恢复仍在运行的交互会话。存储目前采用 JSON 元数据和独立输出文件，没有 SQLite，也没有事务日志或跨后台重启的事件回放。

## 项目结构

```text
ShellTrack.slnx                  CLI、Host、共享库与集成测试
ShellTrack.Desktop.slnx          WinUI 3 桌面解决方案
src/
  ShellTrack.Core/               终端接口、增量纯文本日志
  ShellTrack.Contracts/          API 请求、响应和事件
  ShellTrack.Windows/            ConPTY、控制台模式、Job Object
  ShellTrack.Client/             共用客户端、发现、认证与输出跟随
  ShellTrack.Host/               本地 API、任务编排、持久化、通知调度
  ShellTrack.Cli/                命令入口、终端转发和退出码
  ShellTrack.Desktop/            原生只读查看器、托盘和通知辅助入口
tests/                          核心日志与 Windows 进程集成检查
scripts/                        构建、测试、发布
assets/                         原创图标与可复现生成脚本
docs/                           架构、协议和路线图
```

## 许可证

自有代码、文档和原创图标采用 [MIT](LICENSE)。第三方发行组件遵循各自条款，参见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与发布目录的 `licenses/`。
