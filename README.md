# Shell Track

通过一个命令启动 Windows shell，在原终端中照常操作，再用独立窗口查看所有任务的状态、实时输出和历史记录。

Shell Track 使用 ConPTY 代理终端。CLI 负责输入输出转发，后台负责执行和记录，WinUI 3 窗口只查看输出并执行管理操作。自动化程序可以使用同一套本地 HTTP/WebSocket API。

这是可运行的初版，当前主要面向 Windows x64。

## 已实现

- 交互式 `pwsh`、`powershell`、`cmd`，以及通过选项执行单次命令；等待执行时返回 shell 的退出码。
- CLI 自动启动当前用户的后台；支持并发任务、终止、后台执行、历史查看和导出。
- 原生 WinUI 3 窗口、任务栏图标和托盘。关闭窗口后留在托盘，退出查看器时后台任务继续运行。
- 默认打开总览，不选中任务时卡片铺满可用宽度，同时显示各任务的输出尾部。卡片预览按实际换行后的可见行数裁切，可逐卡片或统一增减，设置会保留。
- 选择任务后显示完整日志和可收起的详情。日志按逻辑行编号，长行自动换行并保留同一个行号，支持悬停高亮、选中、复制和跟随输出；“总览”或 Esc 可取消选择。
- 通知条件支持任务结束、多个输出正则，任意条件首次满足即通知一次；运行中可在窗口修改条件。触发原因、匹配文本与 Windows 发送诊断单独记录。
- 受当前用户文件权限保护的本地 API；查询、管理与终端输入使用不同能力的凭据。查看器不启用终端输入能力。
- 输出记录、7 天历史保留、存储上限和后台异常退出后的记录恢复。
- Windows 集成测试及 GitHub Actions 构建检查。
- 自包含 EXE 与 MSIX 安装包，开始菜单入口、可选用户 PATH、MSIX 执行别名和保留任务数据的卸载。

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
| `--shell` / `-s` `pwsh\|powershell\|cmd` | 选择 shell |
| `--command` / `-c` `<script>` | 执行单次命令；没有该选项时进入交互模式 |
| `--cwd` / `-w` `<path>` | 指定任务工作目录 |
| `--detach` / `-b` | 单次命令在后台运行，立即返回任务 ID |
| `--keep` / `-k` | 交互 CLI 断开后保留 shell；目前没有 CLI 重新接管输入的命令 |
| `--notify` / `-n` | 请求任务结束后发送 Windows 通知 |
| `--notify-match` / `-nm` `<regex>` | 输出匹配时通知，可重复添加，与结束条件为 OR |
| `--data-dir` / `-d` `<path>` | 使用独立的数据目录与后台实例 |
| `--json` / `-j` | `list` 输出结构化任务列表 |
| `--output` / `-o` `<file>` | 指定 `export` 的导出路径 |
| `--help` / `-h` | 查看完整命令帮助 |

子命令也支持简写：`list` → `ls`、`show` → `sh`、`stop` → `st`、`delete` → `rm`、`export` → `ex`、`ui` → `u`、`shutdown` → `sd`。`stop` 终止任务及其所属进程；`delete` 只能删除已结束任务；`shutdown` 关闭当前数据目录的后台及其所有运行任务。

```powershell
.\shelltrack.exe -s cmd -c 'echo Hello' -w .
$taskId = .\shelltrack.exe -c 'Start-Sleep -Seconds 10' -b -n
.\shelltrack.exe ls -j
.\shelltrack.exe ex $taskId -o .\output.bin
.\shelltrack.exe u
```

长选项和简写可以混用，每个选项单独书写，例如 `-b -n`。当前不支持 `-bn` 合并或 `-s=cmd` 写法。直接启动后台时支持 `--data-dir/-d`、`--quiet/-q`、`--output-limit/-l` 和 `--help/-h`；直接启动窗口时支持 `--data-dir/-d`、`--task/-t`，通知辅助入口 `--notify/-n` 需要任务 ID。

```powershell
# 输出命中任意正则，或者任务结束时通知；每个任务最多通知一次
.\shelltrack.exe -s pwsh -c './build.ps1' -n -nm '(?i)error|exception' -nm '服务已启动'

# 只在输出匹配时通知，不启用结束条件
.\shelltrack.exe -s cmd -c 'some-command' --notify-match 'READY'
```

窗口“操作 → 通知条件”可以添加多个正则，或切换结束条件。正则采用 .NET 语法，默认区分大小写，可用 `(?i)` 忽略大小写；匹配去掉终端控制序列后的最近文本，支持跨片段与换行。条件修改只检查保存后的新输出。详见 [通知说明](docs/notifications.md)。升级后若后台报告不支持通知条件，请先结束对应数据目录的旧后台再重试。

卡片预览默认最多 5 个可见行，使用卡片上的 `−` / `＋` 单独调整，顶部按钮让所有卡片各增减一行，范围为 1–30 行。这里的行数包含长行在当前宽度下的换行；预览与完整日志共用文字排版，显示末尾相应高度的内容，输出不足时不占用额外空白。缩放、窗口宽度和总览/选择模式改变时会重新排版。卡片从可用输出中读取最近尾部，不改变原始日志；若历史尾部已超出保存和实时缓冲范围，会提示并显示最后行摘要。

单次命令在客户端断开后继续执行。交互会话默认在输入客户端断开后终止，`--keep` 可改变这个策略。Shell Track 自身发生错误时 CLI 返回 `125`。

## 直接替换 shell 可执行文件

发布目录还提供三个独立编译的入口：

| 原调用 | 替换后的入口 |
|---|---|
| `pwsh.exe` | `shelltrack-pwsh.exe` |
| `powershell.exe` | `shelltrack-powershell.exe` |
| `cmd.exe` | `shelltrack-cmd.exe` |

仅替换可执行文件，后面的 shell 参数保持原样：

```powershell
.\shelltrack-pwsh.exe -NoProfile -Command 'Write-Output "hello"; exit 7'
.\shelltrack-pwsh.exe -NoProfile -File '.\scripts\my task.ps1' 'hello world'
.\shelltrack-powershell.exe -NoProfile -Command 'Get-Date'
.\shelltrack-cmd.exe /D /S /C 'echo hello & exit /b 3'
.\shelltrack-pwsh.exe   # 不带参数时进入真实 shell 的交互模式
```

包装入口不解析 Shell Track 选项，`-h`、`--help`、`-c` 等均由实际 shell 处理；不额外添加 `-NoProfile`、`-NoLogo`、`/D` 或命令包装。Windows 原始命令行中可执行文件之后的参数文本直接传给 shell，当前工作目录同时传入，执行结束返回真实退出码。任务照常记录在后台，窗口也会显示原始参数。

数据目录使用 `SHELLTRACK_DATA_DIR` 或默认目录，包装入口没有 `--data-dir`、通知或后台执行选项。包装进程断开时终止它的任务。请保留发布目录中的依赖文件，不要将包装入口重命名为实际 shell 的名称放入 PATH，以免影响真实 shell 的查找。若已有旧版后台运行，先用旧版 `shelltrack shutdown` 结束后台后再使用新入口；包装入口会检测后台是否支持原始参数。

这些入口仍使用 ConPTY 代理，并非完全透明的进程替身：stdout/stderr 合并且输出带终端控制序列，输入管道关闭不会映射为 shell 的标准输入 EOF；自动化程序需要精确区分输出通道或依赖 EOF 时应继续直接调用原 shell。子进程使用后台进程的环境变量，调用者临时修改的环境不会单独同步。

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

普通 `publish.ps1` 默认采用 framework-dependent .NET，目标机器需要 .NET 10 Runtime 和 ASP.NET Core 10 Runtime；.NET 10 SDK 已包含这些运行时。添加 `-SelfContained` 会将运行时一起发布。桌面目录包含 Windows App SDK 的必要运行组件，无需另外安装 Windows App SDK。

## EXE 与 MSIX 安装包

```powershell
# 首次准备 Inno Setup 编译器（Windows SDK 需预先安装）
pwsh -NoProfile -File scripts/install-packaging-tools.ps1

# 自动发布自包含目录，构建并签名两种安装包
pwsh -NoProfile -File scripts/build-installers.ps1

# 验证签名和文件摘要，实际测试 EXE 安装、执行、PATH 与卸载
pwsh -NoProfile -File scripts/verify-installers.ps1 -InstallerDirectory artifacts/installers-<时间>
```

安装包位于 `artifacts/installers-<时间>/`。EXE 默认安装到 `%LOCALAPPDATA%\Programs\ShellTrack`，只面向当前用户，可以选择加入用户 PATH；MSIX 使用 Windows 管理的安装位置与执行别名。两种格式都包含运行时，无需另装 .NET；`pwsh` 本身仍需单独安装。

默认使用开发自签名证书，私钥留在当前用户证书库，输出目录仅提供公开的 `ShellTrack.cer`。MSIX 首次安装需要使用者信任该证书；正式签名可以传入 `-CertificateThumbprint <指纹>` 使用 `CurrentUser\My` 中已有的代码签名证书，按需指定 `-TimestampUrl <时间戳服务>`。证书不会自动加入受信任证书库，也不会导出私钥。

开发模式已经开启时，可为验证脚本添加 `-TestMsixLayout`，测试 MSIX 内容的开发注册、命令别名、窗口和注销。它不替代实际签名包的证书信任和安装验收。详细安装、更新、卸载与信任流程见 [安装说明](packaging/INSTALL.md)；目前没有自动更新。

构建脚本把 NuGet 缓存与临时目录放在仓库 `work/` 中，并在结束后恢复当前进程的原环境变量，不修改全局配置。发布脚本会复制本项目许可证、第三方说明与依赖包的许可文件。

GitHub Actions 在 Windows runner 上构建核心、运行集成测试并编译 WinUI，也会构建两种安装包，验证签名、MSIX 内容和 EXE 安装/卸载；手动运行验证工作流时可选上传开发产物。推送 `vMAJOR.MINOR.PATCH` 标签后，独立发布流程自动测试、签名并发布自包含 ZIP、EXE 和 MSIX，附校验和与构建来源证明。首次需要配置 `release` Environment 和固定签名证书，详见 [发布指南](docs/releasing.md)。通知实际显示、托盘操作和 DPI 清晰度需要交互式 Windows 桌面验收，不能由无交互的 CI 证明。

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

完成通知曾受到调用方 MSIX 注册表虚拟化影响；修复后，从 Codex 内直接运行新版，弹窗、通知中心、点击定位、托盘和窗口前台显示均已由用户确认。未打包通知辅助进程现优先通过桌面环境启动，并解析实际数据目录；ZIP 版本也会维护开始菜单通知身份。`submitted` 只表示提交给系统，不能证明实际显示。复测与诊断见 [通知说明](docs/notifications.md)，其他部署场景仍需分别验收。

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
  ShellTrack.Pwsh/               pwsh 参数透传入口
  ShellTrack.PowerShell/         Windows PowerShell 参数透传入口
  ShellTrack.Cmd/                cmd 参数透传入口
  ShellTrack.Desktop/            原生只读查看器、托盘和通知辅助入口
tests/                          核心日志与 Windows 进程集成检查
scripts/                        构建、测试、发布
packaging/                      MSIX 清单、Inno Setup 脚本与安装说明
assets/                         原创图标与可复现生成脚本
docs/                           架构、协议和路线图
```

## 许可证

自有代码、文档和原创图标采用 [MIT](LICENSE)。第三方发行组件遵循各自条款，参见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) 与发布目录的 `licenses/`。
