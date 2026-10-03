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
try {
    $output = & $cliPath --data-dir $dataRoot --shell cmd --command 'echo BUNDLE_SMOKE_OK & exit /b 7'
    if ($LASTEXITCODE -ne 7 -or ($output -join "`n") -notmatch 'BUNDLE_SMOKE_OK') { throw 'Published CLI did not forward output and exit code.' }
    $tasks = & $cliPath --data-dir $dataRoot list --json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $tasks.Count -ne 1 -or $tasks[0].exitCode -ne 7) { throw 'Published Host did not record the command.' }
    Write-Host 'PASS: published CLI/Host smoke check and WinUI resource validation.'
} finally {
    & $cliPath --data-dir $dataRoot shutdown
}
