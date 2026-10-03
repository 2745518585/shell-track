#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$root = Join-Path $repoRoot ('work/release-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$script:checks = 0
function Assert-Rejected([scriptblock]$Action, [string]$Name) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw "Expected rejection: $Name" }
    $script:checks++
}
foreach ($tag in @('v0.3.1', 'v1.2.3', 'v65535.65535.65535')) {
    $version = & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $tag
    if ($version.msixVersion -ne ($tag.Substring(1) + '.0')) { throw 'Version mapping failed.' }
    $script:checks++
}
foreach ($tag in @('v0.0.0', 'v01.2.3', '1.2.3', 'v1.2.3-rc.1', 'v65536.0.0', 'v1.2.3/other', 'v1.2.3;bad', 'v999999999999999999999999.0.0')) {
    Assert-Rejected { & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $tag } "tag $tag"
}
$bundle = Join-Path $root 'bundle'
New-Item -ItemType Directory -Path (Join-Path $bundle 'desktop'), (Join-Path $bundle 'packaging') | Out-Null
foreach ($name in @('shelltrack.exe', 'shelltrack-cmd.exe', 'shelltrack-pwsh.exe', 'shelltrack-powershell.exe', 'coreclr.dll', 'desktop/ShellTrack.Desktop.exe', 'desktop/App.xbf', 'desktop/MainWindow.xbf', 'desktop/coreclr.dll', 'LICENSE', 'packaging/INSTALL.md')) {
    Set-Content -LiteralPath (Join-Path $bundle $name) -Value 'fixture'
}
$assets = Join-Path $root 'assets'
New-Item -ItemType Directory -Path $assets | Out-Null
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, (Join-Path $assets 'ShellTrack-0.3.1-win-x64.zip'))
foreach ($name in @('ShellTrack-0.3.1-win-x64-Setup.exe', 'ShellTrack-0.3.1.0-win-x64.msix', 'ShellTrack.cer', 'INSTALL.md', 'INNO-LICENSE.txt')) { Set-Content -LiteralPath (Join-Path $assets $name) -Value 'fixture' }
$commit = 'a' * 40
@{ tag = 'v0.3.1'; version = '0.3.1'; msixVersion = '0.3.1.0'; commit = $commit; dryRun = $true } | ConvertTo-Json | Set-Content (Join-Path $assets 'release.json')
$hashes = @(Get-ChildItem $assets -File | Sort-Object Name | ForEach-Object { (Get-FileHash $_.FullName).Hash.ToLowerInvariant() + '  ' + $_.Name })
$sums = Join-Path $assets 'SHA256SUMS.txt'
[IO.File]::WriteAllText($sums, ($hashes -join "`n") + "`n")
$verify = Join-Path $PSScriptRoot 'verify-release-assets.ps1'
& $verify -Directory $assets -Tag v0.3.1 -Commit $commit
$script:checks++
Assert-Rejected { & $verify -Directory $assets -Tag v0.3.1 -Commit $commit -RequirePublishable } 'publishing dry-run assets'
Assert-Rejected { & $verify -Directory $assets -Tag v0.3.1 -Commit ('b' * 40) } 'wrong commit'
Add-Content (Join-Path $assets 'INSTALL.md') 'tampered'
Assert-Rejected { & $verify -Directory $assets -Tag v0.3.1 -Commit $commit } 'tampered attachment'
Set-Content (Join-Path $assets 'INSTALL.md') 'fixture'
Set-Content (Join-Path $assets 'private.pfx') 'must never be uploaded'
Assert-Rejected { & $verify -Directory $assets -Tag v0.3.1 -Commit $commit } 'extra private key file'
Remove-Item -LiteralPath (Join-Path $assets 'private.pfx')
Add-Content $sums $hashes[0]
Assert-Rejected { & $verify -Directory $assets -Tag v0.3.1 -Commit $commit } 'duplicate checksum'
[IO.File]::WriteAllText($sums, (($hashes | Select-Object -Skip 1) -join "`n") + "`n")
Assert-Rejected { & $verify -Directory $assets -Tag v0.3.1 -Commit $commit } 'missing checksum'
[IO.File]::WriteAllText($sums, ($hashes -join "`n") + "`n")
Write-Host "PASS: $script:checks release version, asset integrity and publication boundary checks."
Write-Host "Fixtures: $root"
