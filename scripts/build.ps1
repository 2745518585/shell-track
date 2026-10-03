#Requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [switch]$CoreOnly)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
Push-Location $repoRoot
try {
    & (Join-Path $PSScriptRoot 'local-environment.ps1') {
        dotnet build ShellTrack.slnx --configuration $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
        if (-not $CoreOnly) {
            dotnet build ShellTrack.Desktop.slnx --configuration $Configuration --nologo
            if ($LASTEXITCODE -ne 0) { throw 'Desktop build failed.' }
        }
    }
} finally { Pop-Location }
