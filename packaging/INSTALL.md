# Shell Track 安装

两种安装包都包含 x64 .NET/ASP.NET Core 运行时和 Windows App SDK。支持 Windows 10 2004（19041）及以上、Windows 11 x64。PowerShell 7 本身不包含在安装包中；没有 `pwsh` 时可以使用 Windows 自带的 `cmd` 或 `powershell`。

## EXE 安装

运行 `ShellTrack-<版本>-win-x64-Setup.exe`。默认安装在 `%LOCALAPPDATA%\Programs\ShellTrack`，无需管理员权限；可以选择其他位置。开始菜单提供窗口入口，可选择创建桌面快捷方式和加入当前用户 PATH。修改 PATH 后请重新打开终端。

静默安装：`Setup.exe /VERYSILENT /SUPPRESSMSGBOXES /NORESTART`。加入 PATH：附加 `/TASKS="userpath"`。卸载使用 Windows 的“已安装的应用”，或安装目录中的 `unins000.exe`。

## MSIX 安装

自签名测试包需要先信任随包提供的 `ShellTrack.cer`。请核对 `installers.json` 中的证书指纹，确认来自可信发布者。用管理员 PowerShell 导入公开证书：

```powershell
Import-Certificate -FilePath .\ShellTrack.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

然后双击 `ShellTrack-<版本>-win-x64.msix` 安装，也可以运行 `Add-AppxPackage -Path .\ShellTrack-<版本>-win-x64.msix`。证书是公钥，不需要密码；不应向使用者提供 `.pfx` 或私钥。受信任生产证书签名的包通常不需要手动导入证书。

MSIX 安装位置由 Windows 管理，通常位于 `C:\Program Files\WindowsApps`；开始菜单提供窗口，四个命令行入口通过应用执行别名注册。若无法调用，检查 Windows“应用执行别名”设置及 `%LOCALAPPDATA%\Microsoft\WindowsApps` 是否在 PATH 中。

## 更新、卸载和数据

任务数据与设置保留在 `%LOCALAPPDATA%\ShellTrack`，也支持原来的自定义数据目录；两种格式不会删除任务记录。MSIX 禁用 AppData 写入虚拟化，以便外部自动化仍能读取同一份端口和凭据。安装目录不用于保存日志。

更新或卸载前，请先退出查看器并用 `shelltrack shutdown` 结束后台；这会终止正在运行的任务。自定义数据目录的后台也需要分别关闭。MSIX 更新使用相同包名、发布者和更高版本；重新生成不同发布者的证书会改变包身份。

建议选择一种安装格式使用。同时安装 EXE 和 MSIX 时，PATH 中的先后顺序可能决定调用哪个版本；它们还会共享默认数据目录和后台，因此不要同时运行不同版本的后台。

本次默认签名是开发用自签名证书，不代表 Windows 或第三方机构验证了发布者身份。EXE 也可能出现 SmartScreen 提示。正式公开分发请使用受信任的代码签名证书或签名服务。
