# 第三方组件与许可文件

Shell Track 的自有代码、文档和原创图标采用根目录 [MIT 许可证](LICENSE)。以下组件不因被本项目引用而改用 MIT。清单根据当前 `ShellTrack.Desktop.csproj`、恢复后的 `project.assets.json` 和本地 NuGet 包中的 `.nuspec`、许可证与 NOTICE 核对。

## 直接依赖

| NuGet 包 | 当前版本 | 包内许可 |
|---|---|---|
| Microsoft.WindowsAppSDK.WinUI | 1.8.260803003 | `license.txt`：Microsoft Software License Terms — Microsoft Windows App SDK；另有 `NOTICE.txt` |
| Microsoft.WindowsAppSDK.Runtime | 1.8.260921001 | `license.txt`：Microsoft Software License Terms — Microsoft Windows App SDK；另有 `NOTICE.txt` |

本项目使用发行 NuGet 包提供的 WinUI 与运行组件。Windows App SDK 开源仓库中的源码许可不代替这些发行包附带的条款。相应 `license.txt` 的 Distributable Code 部分描述发行条件，发布包保留原文。

## 当前间接依赖与 SDK 包

| NuGet 包 | 当前版本 | 包内许可或 nuspec 声明 |
|---|---|---|
| Microsoft.WindowsAppSDK.Base | 1.8.251216001 | Microsoft Windows App SDK 条款，`license.txt`、`NOTICE.txt` |
| Microsoft.WindowsAppSDK.Foundation | 1.8.260803002 | Microsoft Windows App SDK 条款，`license.txt` |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 1.8.260708001 | Microsoft Windows App SDK 条款，`license.txt` |
| Microsoft.Web.WebView2 | 1.0.3179.45 | `LICENSE.txt` 中的 Microsoft 版权与 BSD 三条款形式的许可 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | `.nuspec` 指向 [Windows SDK 许可地址](https://aka.ms/WinSDKLicenseURL)，包根未附独立许可文本 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.20250829.1 | `sdk_license.txt`：Microsoft Windows Software Development Kit (SDK) for Windows 10 条款 |
| Microsoft.Windows.SDK.NET.Ref | 10.0.19041.57 | `.nuspec` 指向 [Windows SDK 许可地址](https://aka.ms/WinSDKLicenseURL)，包根未附独立许可文本 |

`Microsoft.Windows.SDK.NET.Ref` 是目标框架自动下载的 targeting pack，列在 assets 的 `downloadDependencies` 中。桌面产物中的 Windows API 投影也来自该工具链，应保留其包声明。BuildTools 与 MSIX 包用于构建，列入清单便于重现工具链；清单并不表示每个构建工具都会进入最终应用目录。

WebView2 是 WinUI 的间接依赖，当前窗口没有使用 WebView 显示日志。发布目录可能包含对应投影和 loader，其许可文件仍予以保留。

Windows App SDK 的 NOTICE 包含它内部使用的开源组件声明。这些声明随原文件分发，不在本文件重新归纳或改写。版本升级后应以实际恢复与发布的包为准重新核对。

## 发布与运行时

`scripts/publish.ps1` 将实际依赖包根目录中的许可证、NOTICE 和 `.nuspec` 复制到发布产物的 `licenses/<包名>-<版本>/`，并复制本文件与根目录 LICENSE。请整体保留这些文件，不把第三方二进制文件描述为本项目 MIT 代码。

默认发布采用 framework-dependent 部署；`-SelfContained` 与安装包构建会包含 .NET/ASP.NET Core 运行时。相应 runtime NuGet 包的许可证、NOTICE 与 `.nuspec` 随发布目录保存；Windows 系统组件遵循自身条款。项目未引入 SQLite、xterm.js 或其他终端渲染库。

EXE 安装器由 Inno Setup 6.7.3 构建。Inno Setup 的安装/卸载引擎遵循其自身许可，不属于本项目 MIT 代码；打包脚本会随安装产物保留编译器的 `license.txt`。工具来源及商业使用说明见 [Inno Setup 官方页面](https://jrsoftware.org/isinfo.php)。MSIX 使用 Windows SDK 的 MakeAppx 与 SignTool 构建。

## 原创图标

`assets/shelltrack.svg`、PNG、ICO 和图标生成脚本是本项目原创内容，采用 MIT。未使用第三方字体、照片或商标素材。
