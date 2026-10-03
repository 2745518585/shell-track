#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$Commit,
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$InstallerDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory
)
$ErrorActionPreference = 'Stop'
$release = & (Join-Path $PSScriptRoot 'release-version.ps1') -Tag $Tag
if ($Commit -notmatch '^[0-9a-fA-F]{40}$') { throw 'Release commit must be a full Git SHA.' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output directory already exists: $output" }
$details = Get-Content -LiteralPath (Join-Path $InstallerDirectory 'installers.json') -Raw | ConvertFrom-Json
if ($details.version -ne $release.msixVersion) { throw 'Installer version does not match the release tag.' }
foreach ($binary in @('shelltrack.exe', 'ShellTrack.Host.dll', 'desktop/ShellTrack.Desktop.dll')) {
    $information = [Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::GetFullPath((Join-Path $BundleDirectory $binary)))
    if (($information.ProductVersion -split '\+')[0] -ne $release.version) { throw "Binary version does not match release: $binary" }
}
New-Item -ItemType Directory -Path $output | Out-Null
$zip = Join-Path $output "ShellTrack-$($release.version)-win-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($BundleDirectory), $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
foreach ($name in @("ShellTrack-$($release.version)-win-x64-Setup.exe", "ShellTrack-$($release.msixVersion)-win-x64.msix", 'ShellTrack.cer', 'INSTALL.md', 'INNO-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $InstallerDirectory $name) -Destination $output
}
# Public metadata omits runner paths and build logs.
[ordered]@{
    tag = $Tag; version = $release.version; msixVersion = $release.msixVersion; commit = $Commit.ToLowerInvariant()
    publisher = $details.publisher; certificateThumbprint = $details.certificateThumbprint; selfSigned = $details.selfSigned
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
$hashes = foreach ($file in Get-ChildItem -LiteralPath $output -File | Sort-Object Name) {
    (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $file.Name
}
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), ($hashes -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release assets: $output"
