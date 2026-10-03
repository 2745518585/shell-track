#Requires -Version 7.0
param([Parameter(Mandatory)][string]$BundleDirectory)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
foreach ($required in @('shelltrack.exe', 'ShellTrack.Host.dll', 'desktop/ShellTrack.Desktop.exe', 'desktop/App.xbf', 'desktop/MainWindow.xbf', 'desktop/ShellTrack.Desktop.pri', 'desktop/assets/shelltrack.ico', 'desktop/assets/shelltrack.png', 'docs/protocol.md', 'LICENSE', 'THIRD-PARTY-NOTICES.md')) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundleRoot $required))) { throw "Bundle file missing: $required" }
}
$dataRoot = Join-Path $repoRoot ('work/bundle-tests-' + [Guid]::NewGuid().ToString('N'))
$cliPath = Join-Path $bundleRoot 'shelltrack.exe'
foreach ($wrapper in @('shelltrack-pwsh', 'shelltrack-powershell', 'shelltrack-cmd')) {
    foreach ($extension in @('.exe', '.dll', '.runtimeconfig.json', '.deps.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $bundleRoot ($wrapper + $extension)))) { throw "Wrapper file missing: $wrapper$extension" }
    }
}
try {
    $output = & $cliPath --data-dir $dataRoot --shell cmd --command 'echo BUNDLE_SMOKE_OK & exit /b 7'
    if ($LASTEXITCODE -ne 7 -or ($output -join "`n") -notmatch 'BUNDLE_SMOKE_OK') { throw 'Published CLI did not forward output and exit code.' }
    $tasks = & $cliPath --data-dir $dataRoot list --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $tasks.Count -ne 1 -or $tasks[0].exitCode -ne 7) { throw 'Published Host did not record the command.' }
    $oldDataRoot = $env:SHELLTRACK_DATA_DIR
    try {
        $env:SHELLTRACK_DATA_DIR = $dataRoot
        $wrapped = & (Join-Path $bundleRoot 'shelltrack-cmd.exe') /D /C 'echo WRAPPER_BUNDLE_OK & exit /b 9'
        if ($LASTEXITCODE -ne 9 -or ($wrapped -join "`n") -notmatch 'WRAPPER_BUNDLE_OK') { throw 'Published shell wrapper did not preserve parameters and exit code.' }
    } finally { $env:SHELLTRACK_DATA_DIR = $oldDataRoot }
    Write-Host 'PASS: published CLI/Host smoke check and WinUI resource validation.'
} finally {
    & $cliPath --data-dir $dataRoot shutdown
}
