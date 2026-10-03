#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'packaging-tools.ps1')
$expected = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../work/tools/InnoSetup/ISCC.exe'))
if (-not (Test-Path -LiteralPath $expected)) { throw 'Run install-packaging-tools.ps1 before this regression check.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('../work/compiler-tests-' + [Guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $root | Out-Null
$shim = Join-Path $root 'ISCC.exe'
Set-Content -LiteralPath $shim -Value 'fixture PATH shim, must never execute'
$savedPath = $env:PATH
try {
    $env:PATH = $root + [IO.Path]::PathSeparator + $env:PATH
    if ((Get-Command ISCC.exe).Source -ne $shim) { throw 'PATH shim fixture was not selected by normal command discovery.' }
    if ((Find-InnoCompiler) -ne $expected) { throw 'Pinned compiler lost precedence to a PATH shim.' }
    $rejected = $false
    try { Find-InnoCompiler $shim | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Explicit PATH shim was accepted despite missing compiler resources/license.' }
    if ((Find-InnoCompiler $expected) -ne $expected) { throw 'Explicit real compiler was rejected.' }
    Write-Host 'PASS: pinned compiler precedence, PATH shim rejection and explicit compiler discovery.'
} finally { $env:PATH = $savedPath }
