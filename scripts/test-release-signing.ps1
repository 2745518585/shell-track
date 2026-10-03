#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Certificate import tests require Windows.' }
$root = Join-Path (Split-Path $PSScriptRoot -Parent) ('work/signing-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$saved = @{}
foreach ($name in @('SHELLTRACK_SIGNING_PFX_BASE64', 'SHELLTRACK_SIGNING_PFX_PASSWORD', 'SHELLTRACK_SIGNING_THUMBPRINT', 'RUNNER_TEMP')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}
$certificate = $null
$thumbprint = $null
try {
    # Only this disposable test key is exported, never a developer/release key.
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject ('CN=ShellTrack CI Fixture ' + [Guid]::NewGuid().ToString('N')) -CertStoreLocation Cert:/CurrentUser/My -NotAfter (Get-Date).AddDays(30)
    $thumbprint = $certificate.Thumbprint
    $passwordText = [Guid]::NewGuid().ToString('N')
    $pfx = Join-Path $root 'fixture.pfx'
    Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-SecureString $passwordText -AsPlainText -Force) | Out-Null
    $base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($pfx))
    Remove-Item -LiteralPath $pfx
    Remove-Item -LiteralPath "Cert:/CurrentUser/My/$thumbprint" -DeleteKey
    $env:RUNNER_TEMP = $root
    $env:SHELLTRACK_SIGNING_PFX_BASE64 = $base64
    $env:SHELLTRACK_SIGNING_PFX_PASSWORD = $passwordText
    $env:SHELLTRACK_SIGNING_THUMBPRINT = '0' * 40
    $rejected = $false
    try { & (Join-Path $PSScriptRoot 'import-release-certificate.ps1') -GitHubOutput (Join-Path $root 'wrong-output.txt') } catch { $rejected = $true }
    if (-not $rejected -or (Test-Path "Cert:/CurrentUser/My/$thumbprint")) { throw 'Wrong signer was accepted or its key was left behind.' }
    $env:SHELLTRACK_SIGNING_PFX_BASE64 = $base64
    $env:SHELLTRACK_SIGNING_PFX_PASSWORD = $passwordText
    $env:SHELLTRACK_SIGNING_THUMBPRINT = $thumbprint
    $output = Join-Path $root 'output.txt'
    & (Join-Path $PSScriptRoot 'import-release-certificate.ps1') -GitHubOutput $output
    if (-not (Get-Item "Cert:/CurrentUser/My/$thumbprint").HasPrivateKey -or (Get-Content $output).Trim() -ne "thumbprint=$thumbprint") { throw 'Valid signing key import failed.' }
    if (@(Get-ChildItem $root -Filter '*.pfx').Count -or $env:SHELLTRACK_SIGNING_PFX_BASE64 -or $env:SHELLTRACK_SIGNING_PFX_PASSWORD) { throw 'Temporary signing secrets were not cleaned up.' }
    if (Test-Path "Cert:/CurrentUser/TrustedPeople/$thumbprint") { throw 'Import unexpectedly trusted the signer.' }
    Write-Host 'PASS: disposable PFX import, wrong signer rejection, private-key/temp-file cleanup and no added trust.'
} finally {
    if ($thumbprint -and (Test-Path -LiteralPath "Cert:/CurrentUser/My/$thumbprint")) { Remove-Item -LiteralPath "Cert:/CurrentUser/My/$thumbprint" -DeleteKey }
    foreach ($name in $saved.Keys) { [Environment]::SetEnvironmentVariable($name, $saved[$name]) }
    Get-ChildItem -LiteralPath $root -Filter '*.pfx' | Remove-Item
}
