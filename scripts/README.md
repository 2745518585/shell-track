# 开发和发布脚本

在仓库根目录使用 PowerShell 7。`global.json` 指定 .NET SDK；构建 WinUI 还需要 Windows 开发环境。

```powershell
./scripts/build.ps1
./scripts/test.ps1
./scripts/publish.ps1
```

- `build.ps1 -Configuration Release` 构建核心、CLI、集成检查和 WinUI。`-CoreOnly` 跳过 WinUI。
- `test.ps1 -Configuration Release -NoBuild` 运行已有 Release 构建的真实 Windows 集成检查。省略 `-NoBuild` 会先构建测试程序与依赖。
- `publish.ps1` 创建全新的 `artifacts/shelltrack-win-x64-<时间>` 发布目录，包含 CLI/Host、三种 shell 包装入口、`desktop` 下的 WinUI、README、许可证及依赖声明。`-CoreOnly` 跳过 WinUI；`-OutputDirectory artifacts/my-bundle` 指定新的目录，已有目录会被拒绝，避免混入旧版本文件。
- `verify-bundle.ps1 -BundleDirectory artifacts/my-bundle` 检查发布包的窗口资源，并实际运行包内 CLI/Host 验证输出、退出码与记录。使用独立测试数据目录，不要求可见桌面。

发布包依赖 .NET 10 和 ASP.NET Core 10 运行时；Windows App SDK 随 WinUI 发布目录一起提供。脚本不会安装系统运行时或修改用户环境。

`local-environment.ps1` 将本次脚本进程的 NuGet 缓存和临时文件放到 `work` 内，结束后恢复原来的环境变量。`work` 和 `artifacts` 都不会提交到 Git。
