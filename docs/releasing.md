# GitHub Actions 发布

## 发布时机与产物

分支提交与 PR 使用 `Windows build and integration` 做构建、真实 Windows 集成检查、发布脚本检查和安装/卸载验证，不对外创建 Release。推送 `vMAJOR.MINOR.PATCH` 标签触发 `Release Windows packages`，重新测试标签对应源码，通过后自动发布。标签提交必须已合入默认分支（目前是 `master`）。

版本必须像 `v0.3.1` 一样使用三个非负整数，每部分不超过 65535，不允许前导零、`v0.0.0` 或 `-rc` 等后缀。程序和 EXE 使用 `0.3.1`，MSIX 使用 `0.3.1.0`。使用更高的数字版本发布更新；目前不提供预发布版本通道，以避免多个预发布标签映射到同一个 MSIX 版本。

每份 Release 提供以下文件：

| 文件 | 内容 |
|---|---|
| `ShellTrack-<版本>-win-x64.zip` | 完整自包含可执行目录，解压即可运行 |
| `ShellTrack-<版本>-win-x64-Setup.exe` | 当前用户安装程序 |
| `ShellTrack-<版本>.0-win-x64.msix` | Windows 管理的应用安装包 |
| `ShellTrack.cer` | 公开签名证书，供自签名 MSIX 首次安装信任 |
| `INSTALL.md`、`INNO-LICENSE.txt` | 安装说明与安装引擎许可证 |
| `release.json` | 标签、源码 commit、版本、签名发布者与证书指纹 |
| `SHA256SUMS.txt` | 所有上述附件的 SHA-256 摘要 |

运行时、窗口资源与第三方许可包含在 ZIP 和安装包内。发布附件只从固定名单收集，不上传私钥、构建目录或内部日志。自有应用二进制、EXE 安装器及卸载器、MSIX 均签名，应用二进制验证时间戳，安装包验证签名与内容。Windows 可见通知和完整交互式 UI 仍需人工验收。

发布先创建草稿并上传所有附件，再发布草稿，兼容 GitHub 的 [immutable releases](https://docs.github.com/en/code-security/concepts/supply-chain-security/immutable-releases)。已有同名 Release（包括草稿）时拒绝覆盖。跨任务下载后再次验证校验和、标签指向与构建来源。

## 首次配置

不需要 GitHub App、PAT、外部发布服务或 Microsoft Store 账户。工作流使用 GitHub 自动提供的 `GITHUB_TOKEN`，只在发布任务授予 `contents: write`；签名构建任务只有读取源码、OIDC 和 attestations 写入权限。当前仓库为公开仓库，支持 [构建来源证明](https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations)；转为私有仓库前请检查所在计划的证明功能支持情况。

在 [仓库 Settings → Environments](https://github.com/2745518585/shell-track/settings/environments) 创建名为 **`release`** 的 Environment，添加：

| 类型 | 名称 | 值 |
|---|---|---|
| Secret，必需 | `SHELLTRACK_SIGNING_PFX_BASE64` | 固定代码签名证书的 PFX 文件整体 Base64 内容，包含私钥 |
| Secret，必需 | `SHELLTRACK_SIGNING_PFX_PASSWORD` | 该 PFX 的非空密码 |
| Variable，必需 | `SHELLTRACK_SIGNING_THUMBPRINT` | 代码签名证书的 SHA-1 指纹（40 位十六进制；用于选择证书，不是文件 SHA-256） |
| Variable，可选 | `SHELLTRACK_TIMESTAMP_URL` | RFC 3161 时间戳服务；默认 `http://timestamp.digicert.com` |

为 Environment 的 **Deployment branches and tags** 选择 **Selected branches and tags**，分别允许 Tag `v*` 与 Branch `master`。Branch 规则用于从默认分支手动试跑。不要把签名 Secrets 配在普通 PR 可访问的其他 Environment。要求全自动时不设置 Required reviewers；希望每次正式签名前人工核对时，可以设置审核人，流程将等待审核后继续。GitHub 的具体设置见 [Environment 文档](https://docs.github.com/en/actions/how-tos/deploy/configure-and-manage-deployments/manage-environments)。

在 Settings → Actions → General 允许本仓库工作流及所用官方 `actions/*`。无需把默认工作流权限全局改成可写，工作流已逐任务声明权限。建议为默认分支开启 PR/检查保护，为 `v*` 标签限制创建者并禁止更新、删除；在 Settings → General → Releases 开启 release immutability（如果界面提供）。Actions 依赖锁定完整 commit，Dependabot 每周提出更新 PR；升级时审查并合入。

## 准备签名证书

发布流程**不会**自动生成临时开发证书；配置缺失、指纹不匹配、证书不支持代码签名或即将过期会使任务失败。必须保存同一签名身份，以便 MSIX 跨版本升级。PFX 私钥仅在受保护的 Windows runner 中导入 `CurrentUser\My`，临时 PFX 随即删除，任务结束删除私钥，不修改受信任证书库。

可以先使用固定的自签名代码签名证书，再按发布需求换成公开受信任的签名方式。自签名不会消除 SmartScreen 提示，MSIX 使用者仍需信任公开 `.cer`。受硬件令牌或云服务保护、无法导出 PFX 的生产证书，需要另外接入相应签名服务；本流程当前支持可导出的 PFX。

已经有 PFX 时，不必重新生成证书。若尚未有持久证书，可在自己的 Windows PowerShell 中生成（私钥与 PFX 都需自己妥善保存）：

```powershell
$releaseCert = New-SelfSignedCertificate -Type CodeSigningCert `
  -Subject 'CN=ShellTrack Development' -FriendlyName 'Shell Track release signing' `
  -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 `
  -KeyExportPolicy Exportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(3)
$pfxPassword = Read-Host 'PFX 密码' -AsSecureString
Export-PfxCertificate -Cert $releaseCert -FilePath .\ShellTrack-release.pfx -Password $pfxPassword
$releaseCert.Thumbprint
```

PFX 文件不要提交或作为 Release 附件。若已安装并登录 GitHub CLI，可直接将 Base64 通过管道存入 Secret，避免把私钥内容打印到终端：

```powershell
[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path .\ShellTrack-release.pfx))) |
  gh secret set SHELLTRACK_SIGNING_PFX_BASE64 --env release --repo 2745518585/shell-track
gh secret set SHELLTRACK_SIGNING_PFX_PASSWORD --env release --repo 2745518585/shell-track
gh variable set SHELLTRACK_SIGNING_THUMBPRINT --body $releaseCert.Thumbprint --env release --repo 2745518585/shell-track
```

也可通过网页添加同名 Secrets/Variables。不要每次发布重新生成证书或改变 Subject；Publisher 的变化会改变 MSIX 应用身份。若要更新现有本地测试安装，请沿用原证书/Publisher，或先卸载旧包。

## 日常发布与恢复

1. 合并代码到默认分支，确认 `windows` 与 `workflow-lint` 检查通过，并完成人工 UI 验收。
2. 决定更高的版本号，在目标 commit 上创建附注标签（可用 GPG 签标签），推送标签：

   ```powershell
   git tag -a v0.3.1 -m 'Shell Track 0.3.1'
   git push origin v0.3.1
   ```

3. Actions 自动构建、测试、签名、生成 ZIP 和安装包、建立来源证明、发布 Release。若设置了 Environment 审核人，先在运行页面批准。

需要试跑或重试时，在 Actions → Release Windows packages → Run workflow 选择默认分支，输入已有标签。`publish=false`（默认）只上传经过验证的产物，保留 14 天；`publish=true` 完成后发布。手动运行也需要签名配置，而且仍必须使用已合入默认分支的标签；不会根据任意分支自动造标签。

签名前或构建失败时，可修复环境后重跑相同标签。若源码需要修复，合并修复并使用新版本标签。发布中断留下草稿时，先检查草稿及附件；确认尚未对外发布后删除草稿，再重跑。已发布版本不覆盖、不移动标签；修复使用新版本。首个标签必须包含本发布工作流及所有脚本，不能拿更早的不完整版本标签试跑。

用户可以下载附件后运行 `sha256sum -c SHA256SUMS.txt`，或用 PowerShell `Get-FileHash` 比较摘要。来源证明使用：

```powershell
gh attestation verify .\ShellTrack-0.3.1-win-x64.zip --repo 2745518585/shell-track `
  --signer-workflow 2745518585/shell-track/.github/workflows/release.yml --deny-self-hosted-runners
```

`release.json` 记录实际构建的源码 commit；手动运行的证明所关联工作流 ref 可能是默认分支，实际构建源由标签和 metadata 指定。公开证书的信任与安装操作见 [安装说明](../packaging/INSTALL.md)。工作流不自动替使用者导入信任证书，也不自动更新已安装的软件。
