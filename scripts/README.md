# 开发和发布脚本

在仓库根目录使用 PowerShell 7。`global.json` 指定 .NET SDK；构建 WinUI 还需要 Windows 开发环境。

```powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/publish.ps1
./scripts/dev.ps1
```

- `build.ps1 -Configuration Release` 构建核心、CLI、集成检查和 WinUI。`-CoreOnly` 跳过 WinUI。
- `test.ps1 -Configuration Release -NoBuild` 运行已有 Release 构建的真实 Windows 集成检查。省略 `-NoBuild` 会先构建测试程序与依赖。
- `publish.ps1` 创建全新的 `artifacts/shelltrack-win-x64-<时间>` 发布目录，包含 CLI/Host、三种 shell 包装入口、`desktop` 下的 WinUI、README、许可证及依赖声明。`-CoreOnly` 跳过 WinUI；`-OutputDirectory artifacts/my-bundle` 指定新的目录，已有目录会被拒绝，避免混入旧版本文件。
- `verify-bundle.ps1 -BundleDirectory artifacts/my-bundle` 检查发布包的窗口资源，并实际运行包内 CLI/Host 验证输出、退出码与记录。使用独立测试数据目录，不要求可见桌面。
- `dev.ps1` 将完整测试程序更新到固定的 `work/dev/bin`，然后启动窗口；已有产物会替换，旧文件会清理。使用 `work/dev/data` 保留任务记录和窗口设置，运行时工作目录为 `work/dev/workspace`。编译成功后会关闭从这个测试目录启动的进程，并结束其后台的运行任务，其他发布目录的实例不受影响。`-NoLaunch` 只更新程序，`-SelfContained` 携带 .NET 运行时。编译失败时保留旧程序；测试数据和工作目录不会清空。

从另一个 PowerShell 终端测试命令时，显式指定工作目录和数据目录：

```powershell
./work/dev/bin/shelltrack.exe -d "$PWD/work/dev/data" -w "$PWD/work/dev/workspace" -s cmd -c 'echo LOCAL_TEST_OK'
```

上例从仓库根目录执行。测试 shell 包装入口时，先将该终端的 `SHELLTRACK_DATA_DIR` 设为 `work/dev/data` 的绝对路径，并切换到 `work/dev/workspace`，再调用 `work/dev/bin` 中对应入口。

普通发布默认依赖 .NET 10 和 ASP.NET Core 10 运行时；`publish.ps1 -SelfContained` 会携带运行时。Windows App SDK 随 WinUI 发布目录一起提供。
`publish.ps1 -Version 0.3.1` 将标签版本写入程序文件；不传时保留开发默认版本。

- `install-packaging-tools.ps1` 下载固定版本的 Inno Setup、验证发布者签名，并将编译器安装到仓库 `work/tools/InnoSetup`。还需要 Windows SDK 的 MakeAppx 与 SignTool。
- `build-installers.ps1` 默认发布自包含目录并构建 EXE/MSIX；`-BundleDirectory <目录>` 可复用完整自包含发布包。`-Version 0.3.0.0` 设置版本，`-OutputDirectory <新目录>` 设置产物目录。默认生成/复用当前用户证书库中的开发证书，也可用 `-CertificateThumbprint` 指定已有私钥的证书；`-TimestampUrl` 指定时间戳服务。只导出公开证书，不导出私钥，不修改系统信任。
- `verify-installers.ps1 -InstallerDirectory <目录>` 验证签名/所有文件块摘要，在独立临时目录测试 EXE 安装、PATH 和卸载；已有 EXE 安装时拒绝覆盖。`-TestMsixLayout` 要求已开启开发模式，注册签名包解包后的开发布局，验证别名与窗口，再注销；已有 MSIX 安装时拒绝覆盖，不修改开发模式或证书信任设置。

`local-environment.ps1` 将本次脚本进程的 NuGet 缓存和临时文件放到 `work` 内，结束后恢复原来的环境变量。`work` 和 `artifacts` 都不会提交到 Git。

## GitHub Release

环境配置与操作见 [发布指南](../docs/releasing.md)。`.github/workflows/release.yml` 使用版本标签自动发布，也允许手动只构建产物。

- `release-version.ps1 -Tag v0.3.1` 校验标签并返回程序/MSIX 版本。
- `import-release-certificate.ps1` 从 Environment Secrets 对应的环境变量导入固定 PFX，校验指纹、有效期与代码签名用途，清除临时文件；只用于新的受保护 runner。输出公开指纹到 `GITHUB_OUTPUT`。
- `sign-release-bundle.ps1 -BundleDirectory <目录> -CertificateThumbprint <指纹> -TimestampUrl <URL>` 对自有二进制签名并检查时间戳。
- `create-release-assets.ps1 -Tag <标签> -Commit <完整SHA> -BundleDirectory <目录> -InstallerDirectory <目录> -OutputDirectory <新目录>` 检查版本一致性并生成 ZIP、固定发布附件与摘要。
- `verify-release-assets.ps1 -Directory <目录> -Tag <标签> -Commit <完整SHA>` 检查附件集合、metadata、校验和与 ZIP 布局；可在 Windows/Linux 的 PowerShell 7 中运行。
- `test-release.ps1` 验证版本边界、文件篡改、缺失/重复摘要和意外私钥附件的拒绝；`test-release-signing.ps1` 用一次性测试密钥验证 PFX 导入、错误指纹拒绝和清理，不导出开发者/发布证书。
