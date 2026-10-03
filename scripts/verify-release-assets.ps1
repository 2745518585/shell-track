#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$Tag, [Parameter(Mandatory)][string]$Commit)
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $Tag
$metadata = Get-Content -LiteralPath (Join-Path $Directory 'release.json') -Raw | ConvertFrom-Json
if ($metadata.tag -cne $Tag -or $metadata.commit -ne $Commit -or $metadata.version -ne $release.version -or $metadata.msixVersion -ne $release.msixVersion) { throw 'Release metadata does not match the requested tag/commit.' }
$expected = @("ShellTrack-$($release.version)-win-x64.zip", "ShellTrack-$($release.version)-win-x64-Setup.exe", "ShellTrack-$($release.msixVersion)-win-x64.msix", 'ShellTrack.cer', 'INSTALL.md', 'INNO-LICENSE.txt', 'release.json')
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath (Join-Path $Directory 'SHA256SUMS.txt')) {
    if ($line -cnotmatch '^([0-9a-f]{64})  ([A-Za-z0-9._-]+)$') { throw 'Invalid SHA256SUMS entry.' }
    $hash = $Matches[1]; $name = $Matches[2]
    if ($name -cnotin $expected -or -not $seen.Add($name)) { throw "Unexpected or duplicate asset: $name" }
    if ((Get-FileHash -LiteralPath (Join-Path $Directory $name) -Algorithm SHA256).Hash -ne $hash) { throw "Release asset checksum mismatch: $name" }
}
if ($seen.Count -ne $expected.Count) { throw 'Release is missing a required asset.' }
$actual = @(Get-ChildItem -LiteralPath $Directory -Force)
if ($actual.Count -ne ($expected.Count + 1) -or @($actual | Where-Object { $_.PSIsContainer -or $_.Name -cnotin ($expected + 'SHA256SUMS.txt') }).Count) { throw 'Release directory contains unexpected files.' }
$zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $Directory $expected[0]))
try {
    foreach ($name in @('shelltrack.exe', 'shelltrack-cmd.exe', 'shelltrack-pwsh.exe', 'shelltrack-powershell.exe', 'coreclr.dll', 'desktop/ShellTrack.Desktop.exe', 'desktop/App.xbf', 'desktop/MainWindow.xbf', 'desktop/coreclr.dll', 'LICENSE', 'packaging/INSTALL.md')) {
        if (-not $zip.GetEntry($name)) { throw "Portable ZIP is missing: $name" }
    }
} finally { $zip.Dispose() }
Write-Host 'PASS: complete release asset set, tag/commit, SHA256 checksums and portable ZIP layout.'
