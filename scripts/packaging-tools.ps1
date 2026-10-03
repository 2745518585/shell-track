# Shared read-only discovery for installer builds.
function Find-WindowsSdkTool([string]$Name) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
    $tool = Get-ChildItem -LiteralPath $sdkRoot -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+\.\d+$' |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName ('x64/' + $Name) } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $tool) { throw "Windows SDK tool missing: $Name. Install the Windows 10/11 SDK." }
    return $tool
}
function Find-InnoCompiler([string]$Specified) {
    if ($Specified) {
        if (-not (Test-Path -LiteralPath $Specified)) { throw "Inno compiler missing: $Specified" }
        if (-not (Test-Path -LiteralPath (Join-Path (Split-Path $Specified -Parent) 'license.txt'))) {
            throw 'Specify the actual Inno Setup installation ISCC.exe, with its license.txt; PATH shims are not supported.'
        }
        return [IO.Path]::GetFullPath($Specified)
    }
    # Prefer the pinned/verified tool, not a runner's Chocolatey PATH shim.
    foreach ($candidate in @((Join-Path $PSScriptRoot '../work/tools/InnoSetup/ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe'))) {
        if ((Test-Path -LiteralPath $candidate) -and (Test-Path -LiteralPath (Join-Path (Split-Path $candidate -Parent) 'license.txt'))) {
            return [IO.Path]::GetFullPath($candidate)
        }
    }
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command -and (Test-Path -LiteralPath (Join-Path (Split-Path $command.Source -Parent) 'license.txt'))) { return $command.Source }
    throw 'Inno Setup compiler missing. Run scripts/install-packaging-tools.ps1 or supply -InnoCompiler.'
}
