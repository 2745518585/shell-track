#Requires -Version 7.0
param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Debug', [switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    & (Join-Path $PSScriptRoot 'local-environment.ps1') {
        $arguments = @('run', '--project', 'tests/ShellTrack.Integration', '--configuration', $Configuration)
        if ($NoBuild) { $arguments += '--no-build' }
        dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Integration checks failed.' }
    }
} finally { Pop-Location }
