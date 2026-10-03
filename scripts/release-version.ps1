#Requires -Version 7.0
param([Parameter(Mandatory)][string]$Tag)
$ErrorActionPreference = 'Stop'
if ($Tag -cnotmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Release tags must be vMAJOR.MINOR.PATCH, without leading zeros or prerelease suffixes.'
}
$parts = @($Matches[1], $Matches[2], $Matches[3])
foreach ($part in $parts) {
    if ([long]$part -gt 65535) { throw 'Each version component must fit an MSIX component (0..65535).' }
}
if ([int]$parts[0] -eq 0 -and [int]$parts[1] -eq 0 -and [int]$parts[2] -eq 0) { throw 'Version 0.0.0 cannot be released.' }
$version = $parts -join '.'
[pscustomobject]@{ tag = $Tag; version = $version; msixVersion = "$version.0" }
