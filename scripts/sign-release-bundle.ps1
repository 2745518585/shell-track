#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$CertificateThumbprint,
    [Parameter(Mandatory)][string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'packaging-tools.ps1')
if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Invalid signing thumbprint.' }
if ($TimestampUrl -notmatch '^https?://[A-Za-z0-9./:_-]+$') { throw 'Invalid timestamp URL.' }
$signTool = Find-WindowsSdkTool 'signtool.exe'
$ownedFiles = @(Get-ChildItem -LiteralPath $BundleDirectory -Recurse -File | Where-Object {
    $_.Name -match '^ShellTrack(?:\.[A-Za-z]+|-(?:pwsh|powershell|cmd))?\.(exe|dll)$'
})
if ($ownedFiles.Count -lt 10) { throw 'Expected a complete application bundle to sign.' }
foreach ($file in $ownedFiles) {
    & $signTool sign /fd SHA256 /sha1 $CertificateThumbprint /s My /tr $TimestampUrl /td SHA256 $file.FullName
    if ($LASTEXITCODE -ne 0) { throw "Signing failed: $($file.Name)" }
    $signature = Get-AuthenticodeSignature -LiteralPath $file.FullName
    if ($signature.SignerCertificate.Thumbprint -ne $CertificateThumbprint -or -not $signature.TimeStamperCertificate) {
        throw "Signing or timestamp verification failed: $($file.Name)"
    }
}
Write-Host "Signed and timestamped $($ownedFiles.Count) application binaries."
