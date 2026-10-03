#Requires -Version 7.0
param([switch]$CoreOnly, [string]$OutputDirectory, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
# Every run creates a fresh directory, so stale runtime files cannot contaminate the bundle.
if ($OutputDirectory) {
    $bundleRoot = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory)) }
} else {
    $bundleRoot = Join-Path $repoRoot ('artifacts/shelltrack-win-x64-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
if (Test-Path -LiteralPath $bundleRoot) { throw "Output directory already exists: $bundleRoot" }
New-Item -ItemType Directory -Path $bundleRoot | Out-Null
Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'local-environment.ps1') {
        $runtimeMode = if ($SelfContained) { 'true' } else { 'false' }
        dotnet publish src/ShellTrack.Host --configuration Release --runtime win-x64 --self-contained $runtimeMode --output $bundleRoot --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Host publish failed.' }
        dotnet publish src/ShellTrack.Cli --configuration Release --runtime win-x64 --self-contained $runtimeMode --output $bundleRoot --nologo
        if ($LASTEXITCODE -ne 0) { throw 'CLI publish failed.' }
        foreach ($wrapper in @('ShellTrack.Pwsh', 'ShellTrack.PowerShell', 'ShellTrack.Cmd')) {
            dotnet publish (Join-Path 'src' $wrapper) --configuration Release --runtime win-x64 --self-contained $runtimeMode --output $bundleRoot --nologo
            if ($LASTEXITCODE -ne 0) { throw "$wrapper publish failed." }
        }
        if (-not $CoreOnly) {
            dotnet publish src/ShellTrack.Desktop --configuration Release --runtime win-x64 --self-contained $runtimeMode --output (Join-Path $bundleRoot 'desktop') --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
            foreach ($required in @('App.xbf', 'MainWindow.xbf', 'ShellTrack.Desktop.pri', 'assets/shelltrack.ico', 'assets/shelltrack.png')) {
                if (-not (Test-Path -LiteralPath (Join-Path $bundleRoot ('desktop/' + $required)))) { throw "Desktop publish resource missing: $required" }
            }
        }
        Copy-Item -LiteralPath LICENSE,THIRD-PARTY-NOTICES.md,README.md -Destination $bundleRoot
        $documentationRoot = Join-Path $bundleRoot 'docs'
        New-Item -ItemType Directory -Path $documentationRoot | Out-Null
        Get-ChildItem -LiteralPath docs -Filter '*.md' -File | Copy-Item -Destination $documentationRoot
        $installationDocs = Join-Path $bundleRoot 'packaging'
        New-Item -ItemType Directory -Path $installationDocs | Out-Null
        Copy-Item -LiteralPath packaging/INSTALL.md -Destination $installationDocs
        $licenseRoot = Join-Path $bundleRoot 'licenses'
        New-Item -ItemType Directory -Force -Path $licenseRoot | Out-Null
        foreach ($assets in @('src/ShellTrack.Host/obj/project.assets.json', 'src/ShellTrack.Cli/obj/project.assets.json', 'src/ShellTrack.Desktop/obj/project.assets.json')) {
            if ($CoreOnly -and $assets -like '*Desktop*') { continue }
            $dependencies = Get-Content -LiteralPath $assets -Raw | ConvertFrom-Json -AsHashtable
            $packagePaths = [Collections.Generic.HashSet[string]]::new()
            foreach ($library in $dependencies.libraries.Values) {
                if ($library.type -eq 'package') { [void]$packagePaths.Add($library.path) }
            }
            foreach ($framework in $dependencies.project.frameworks.Values) {
                foreach ($download in $framework.downloadDependencies) {
                    # SDK targeting packs are downloads rather than library entries.
                    $version = ($download.version.Trim('[', ']') -split ',')[0].Trim()
                    [void]$packagePaths.Add($download.name.ToLowerInvariant() + '/' + $version)
                }
            }
            foreach ($packagePath in $packagePaths) {
                foreach ($cache in $dependencies.packageFolders.Keys) {
                    $packageRoot = Join-Path $cache $packagePath
                    if (-not (Test-Path -LiteralPath $packageRoot)) { continue }
                    $destination = Join-Path $licenseRoot ($packagePath -replace '/', '-')
                    New-Item -ItemType Directory -Force -Path $destination | Out-Null
                    Get-ChildItem -LiteralPath $packageRoot -File | Where-Object { $_.Name -match 'license|notice|\.nuspec$' } | Copy-Item -Destination $destination
                    break
                }
            }
        }
    }
    Write-Host "Bundle: $bundleRoot"
} finally { Pop-Location }
