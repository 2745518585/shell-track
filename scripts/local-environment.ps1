param([Parameter(Mandatory)][scriptblock]$Action)
$repoRoot = Split-Path $PSScriptRoot -Parent
$variables = @{
    NUGET_PACKAGES = Join-Path $repoRoot 'work/nuget/packages'
    NUGET_HTTP_CACHE_PATH = Join-Path $repoRoot 'work/nuget/http'
    TEMP = Join-Path $repoRoot 'work/temp'
    TMP = Join-Path $repoRoot 'work/temp'
}
$saved = @{}
foreach ($name in $variables.Keys) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    New-Item -ItemType Directory -Force -Path $variables[$name] | Out-Null
    [Environment]::SetEnvironmentVariable($name, $variables[$name], 'Process')
}
try { & $Action } finally {
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
