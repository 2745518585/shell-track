#Requires -Version 7.0
param([switch]$NoLaunch, [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Local desktop testing requires Windows.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$devRoot = Join-Path $repoRoot 'work/dev'
$bundleRoot = Join-Path $devRoot 'bin'
$dataRoot = Join-Path $devRoot 'data'
$workspaceRoot = Join-Path $devRoot 'workspace'
$stageRoot = Join-Path $devRoot ('publish-' + [Guid]::NewGuid().ToString('N'))
$previousRoot = Join-Path $devRoot ('previous-' + [Guid]::NewGuid().ToString('N'))

# These fixed directories must remain inside the checkout, even when replacing
# an existing bundle. Refuse junctions/symlinks rather than following them.
function Assert-LocalDirectory([string]$Path, [switch]$CheckContents) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Directory is outside the checkout: $fullPath"
    }
    for ($item = $fullPath; $item -ne $repoRoot; $item = Split-Path $item -Parent) {
        if (Test-Path -LiteralPath $item) {
            $entry = Get-Item -LiteralPath $item -Force
            if (-not $entry.PSIsContainer -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Expected a regular local directory: $item"
            }
        }
    }
    if ($CheckContents -and (Test-Path -LiteralPath $fullPath)) {
        $links = Get-ChildItem -LiteralPath $fullPath -Recurse -Force -Attributes ReparsePoint
        if ($links) { throw "Directory contains a junction or symlink: $fullPath" }
    }
}
function Get-DevProcesses {
    Get-Process | Where-Object {
        try {
            $_.Path -and $_.Path.StartsWith($bundleRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)
        } catch { $false }
    }
}
function Stop-DevProcess($Process) {
    if ($Process.HasExited) { return }
    Stop-Process -InputObject $Process -Force
    if (-not $Process.WaitForExit(5000)) { throw "Test process did not exit: $($Process.Id)" }
}

foreach ($path in @($devRoot, $bundleRoot, $dataRoot, $workspaceRoot, $stageRoot, $previousRoot)) { Assert-LocalDirectory $path }
New-Item -ItemType Directory -Force -Path $devRoot, $dataRoot, $workspaceRoot | Out-Null
# Prevent two invocations from replacing the same binaries simultaneously.
$lock = [IO.File]::Open((Join-Path $devRoot 'dev.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    Write-Host "Building local test binaries: $bundleRoot"
    & (Join-Path $PSScriptRoot 'publish.ps1') -OutputDirectory $stageRoot -SelfContained:$SelfContained

    # Stop only executables from this dedicated development bundle. Close the
    # viewer first so it cannot restart the host during shutdown.
    foreach ($process in @(Get-DevProcesses | Where-Object ProcessName -eq 'ShellTrack.Desktop')) { Stop-DevProcess $process }
    $cli = Join-Path $bundleRoot 'shelltrack.exe'
    if ((Test-Path -LiteralPath $cli) -and @(Get-DevProcesses | Where-Object ProcessName -eq 'ShellTrack.Host').Count -gt 0) {
        Write-Host 'Stopping the local test host and its running tasks.'
        & $cli --data-dir $dataRoot shutdown
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Graceful shutdown failed; stopping the local test executables.' }
    }
    foreach ($process in @(Get-DevProcesses)) { Stop-DevProcess $process }

    Assert-LocalDirectory $bundleRoot -CheckContents
    if (Test-Path -LiteralPath $bundleRoot) { Move-Item -LiteralPath $bundleRoot -Destination $previousRoot }
    try { Move-Item -LiteralPath $stageRoot -Destination $bundleRoot }
    catch {
        if (Test-Path -LiteralPath $previousRoot) { Move-Item -LiteralPath $previousRoot -Destination $bundleRoot }
        throw
    }
    if (Test-Path -LiteralPath $previousRoot) {
        Assert-LocalDirectory $previousRoot -CheckContents
        Remove-Item -LiteralPath $previousRoot -Recurse -Force
    }

    Write-Host "Binaries: $bundleRoot"
    Write-Host "Task data: $dataRoot"
    Write-Host "Working directory: $workspaceRoot"
    if (-not $NoLaunch) {
        $start = [Diagnostics.ProcessStartInfo]::new((Join-Path $bundleRoot 'desktop/ShellTrack.Desktop.exe'))
        $start.UseShellExecute = $false
        $start.WorkingDirectory = $workspaceRoot
        $start.ArgumentList.Add('--data-dir'); $start.ArgumentList.Add($dataRoot)
        $start.Environment['SHELLTRACK_DATA_DIR'] = $dataRoot
        $viewer = [Diagnostics.Process]::Start($start)
        $viewer.Dispose()
    }
} finally {
    try {
        if (Test-Path -LiteralPath $stageRoot) {
            Assert-LocalDirectory $stageRoot -CheckContents
            Remove-Item -LiteralPath $stageRoot -Recurse -Force
        }
    } finally { $lock.Dispose() }
}
