# Windows 条件通知

任务创建时使用 `--notify` / `-n` 添加结束条件，重复使用 `--notify-match` / `-nm <regex>` 添加多个输出匹配条件，也可以从窗口“操作 → 通知条件”配置。任意条件首次满足时，Host 启动短时桌面辅助进程提交 Windows 通知；查看窗口不必预先打开。每个任务最多触发一次，不因重复输出、随后结束或条件修改重发，发送失败也不会自动重试。通知发送不会改变任务退出码。

输出匹配在任务运行中立即触发，通知正文包含正则与匹配摘要，点击定位对应任务。匹配使用去掉 ANSI/OSC 等控制序列并处理回车覆盖后的文本，支持跨片段 UTF-8、跨行正则；不是区分 stdout/stderr 的解析。最近文本窗口最多 65536 字符，超过窗口范围的匹配不保证识别，原始输出记录不受影响。匹配在保存配额之外继续；修改条件只检查之后的新输出，不回扫历史。

正则采用 .NET 语法，默认 Multiline、CultureInvariant 和区分大小写。可用 `(?i)` 忽略大小写、`(?s)` 让点号跨行。最多 16 个表达式，每个最多 2048 字符，配置时拒绝空值与无效表达式；一次匹配超过 25ms 时停用该表达式，记录诊断并继续其他条件。实时匹配在当前未结束行上进行，`$` 可能匹配该行当前末尾；需要等待行结束时可显式匹配 `\n`。窗口每行输入一个表达式，换行匹配使用 `\n` 转义。

## 启动和身份

未打包版本使用固定 AUMID `ShellTrack.Desktop`。Windows App SDK 注册显示名、图标和 COM 激活器；程序将激活路径更新到当前目录，并创建或更新当前用户的 `开始菜单\程序\Shell Track\Shell Track.lnk`，写入 AUMID 与通知激活器 CLSID。已有匹配快捷方式不会反复重写。

ZIP 版本首次打开窗口或发送通知后会留下开始菜单入口。删除 ZIP 目录时可一并删除此快捷方式；EXE 安装器默认使用同一位置。未打包版本共用通知身份，最近启动的版本会更新入口路径。MSIX 使用包清单身份，不执行这一步注册。

部分 MSIX 应用会把子进程的注册表写入也重定向到私有视图。`GetCurrentPackageFullName` 返回“没有包身份”，不能单独证明子进程没有继承虚拟化。若辅助进程在这种环境里注册 COM，Windows 桌面可能完全看不到注册项。

因此，未打包 Host 优先通过当前桌面的 Explorer 自动化对象启动通知辅助进程，让注册写入真实用户环境。Host 等待辅助进程生成的任务诊断，不把启动成功当作发送成功。没有桌面或桌面代理不可用时，回退到直接启动；包版本继续通过其 GUI 执行别名直接启动。窗口恢复和通知转发同时处理前台权限，点击通知会请求将对应查看器带到前台。

调用方也可能虚拟化 LocalAppData。Host 通过已写入的 `connection.json` 文件句柄解析实际存储路径，将该目录传给桌面辅助进程，确保辅助进程能读取同一后台的连接凭据，并将诊断写入相同数据位置。

## 手动复测

在完整发布目录中以普通用户执行：

```powershell
.\shelltrack.exe -s cmd -c "echo Shell Track notification check" -n
```

等待约十秒，检查弹窗和 Win+N 通知中心。点击应打开查看器并选择对应任务，同时出现托盘图标。分别检查已有窗口、查看器完全退出和 EXE/MSIX 安装后的行为；无交互的 CI 无法证明通知可见。

如果从 MSIX 应用的开发终端执行，也应从该应用外的 PowerShell 对比注册结果。读取 `HKCU\Software\Classes\AppUserModelId\ShellTrack.Desktop` 的 `CustomActivator`，再检查对应的 `HKCU\Software\Classes\CLSID\<激活器>\LocalServer32`：两个环境应看到指向仍存在的桌面程序的路径。

## 2026-10-03 排查证据

- 基线 Windows ID `161087`，诊断为 `Enabled`、`busy`、`inHistory: true`。Windows 事件 3052 / 3153 报告已投递，辅助进程退出后通知仍在系统数据库中，但用户未看到。
- 添加带身份的开始菜单快捷方式后，通知 `161090` 的弹窗与通知中心均由用户确认显示；点击却未启动窗口。
- 查看器直接启动时窗口和托盘正常，运行中的 COM 激活可定位任务；退出后的 COM 调用返回 `REGDB_E_CLASSNOTREG`。
- Codex 内能够读取激活器 `{5115703A-CAD9-45CB-ADB5-56AF166C1106}` 的启动注册，而用户在 Codex 外执行相同查询报告不存在。当前 Codex MSIX 清单只对指定位置排除虚拟化；Windows 11 的新虚拟化声明优先于旧的 `desktop6` 声明。
- 从真实桌面环境运行完整 ZIP 后，通知 `161107`（`Shell Track OUTSIDE final test 20261003`）的显示、点击定位和托盘由用户确认正常；随后补充前台窗口处理。
- 新版直接从 Codex 内运行，通知 `161112`（`Shell Track FINAL BROKER 20261003`）的辅助进程父进程为 Explorer；用户确认点击表现正常。但当时仍有旧版查看器驻留，不能据此认定新版前台修复完成验收。
- 全部旧版查看器退出后，新版从 Codex 内使用 LocalAppData 测试目录发送 `161115`（`Shell Track FINAL FOREGROUND 20261003`）。辅助进程使用解析后的实际存储目录；用户确认点击打开对应任务、托盘图标与前台窗口三项均正常。

Release 核心与桌面构建无警告或错误，102 项集成检查通过，包含真实快捷方式身份、注册更新与 LocalAppData 路径解析。新版自包含目录、签名 EXE 与 MSIX 均已生成；两种安装方式下的通知仍需分别实际验收。

这些证据解释了“发送成功但桌面无法启动”的环境差异。没有清空通知数据库、重启系统服务或更改全局通知策略。原生 COM 激活保留；没有为排查中的失败协议方案留下生产代码。

## 结果与诊断

`shelltrack list --json` 的 `notification` 保存发送结果。数据目录中的 `notifications/<taskId>.json` 是辅助进程诊断，`desktop-errors.log` 记录注册或发送异常。注册失败和“程序退出却未留下诊断”均记录为失败。

| 字段 | 含义 |
|---|---|
| `status: submitted` | Windows API 接受，不代表用户已看到 |
| `windowsId` | Windows 分配的通知 ID，可关联系统事件 |
| `setting` | Windows App SDK 报告的应用通知设置 |
| `systemState` | `SHQueryUserNotificationState` 返回的桌面状态，不能单独判定显示失败 |
| `inHistory` | 发送后的 Windows API 历史查询结果，不等于目视确认 |

再次不可见时，先保存任务诊断与时间，再检查应用通知设置、“请勿打扰”和外部进程能否读取注册项。只筛选本应用近期事件：

```powershell
Get-WinEvent -FilterHashtable @{
    LogName = 'Microsoft-Windows-PushNotification-Platform/Operational'
    StartTime = (Get-Date).AddMinutes(-10)
} | Where-Object { $_.Message -match 'ShellTrack\.Desktop' } |
    Select-Object TimeCreated, Id, Message
```

不要清空整个 Windows 通知数据库或删除其他应用的注册。参考微软的 [MSIX 灵活虚拟化规则](https://learn.microsoft.com/windows/msix/desktop/flexible-virtualization)、[Windows App SDK 注册实现](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppNotifications/AppNotificationManager.cpp) 和 [应用身份实现](https://github.com/microsoft/WindowsAppSDK/blob/main/dev/AppNotifications/AppNotificationUtility.cpp)。
