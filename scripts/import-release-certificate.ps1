#Requires -Version 7.0
param([string]$GitHubOutput = $env:GITHUB_OUTPUT)
$ErrorActionPreference = 'Stop'
# Secrets enter through the environment, never through a command line or log.
foreach ($name in @('SHELLTRACK_SIGNING_PFX_BASE64', 'SHELLTRACK_SIGNING_PFX_PASSWORD', 'SHELLTRACK_SIGNING_THUMBPRINT')) {
    if (-not [Environment]::GetEnvironmentVariable($name)) { throw "Release signing configuration is missing: $name" }
}
$expected = $env:SHELLTRACK_SIGNING_THUMBPRINT.Replace(' ', '').ToUpperInvariant()
if ($expected -notmatch '^[0-9A-F]{40}$') { throw 'Invalid expected signing thumbprint.' }
if (Test-Path -LiteralPath "Cert:/CurrentUser/My/$expected") { throw 'Refusing to replace an existing signing certificate; use a fresh runner.' }
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$pfxPath = Join-Path $temporaryRoot ('shelltrack-signing-' + [Guid]::NewGuid().ToString('N') + '.pfx')
$imported = @()
try {
    [IO.File]::WriteAllBytes($pfxPath, [Convert]::FromBase64String($env:SHELLTRACK_SIGNING_PFX_BASE64))
    $password = ConvertTo-SecureString $env:SHELLTRACK_SIGNING_PFX_PASSWORD -AsPlainText -Force
    $imported = @(Import-PfxCertificate -FilePath $pfxPath -Password $password -CertStoreLocation Cert:/CurrentUser/My)
    $signer = @($imported | Where-Object HasPrivateKey)
    if ($signer.Count -ne 1 -or $signer[0].Thumbprint -ne $expected) { throw 'PFX must contain exactly one private key matching the configured thumbprint.' }
    if ($signer[0].NotBefore -gt (Get-Date) -or $signer[0].NotAfter -lt (Get-Date).AddDays(7)) { throw 'Signing certificate is not valid for at least the next seven days.' }
    if ('1.3.6.1.5.5.7.3.3' -notin $signer[0].EnhancedKeyUsageList.ObjectId) { throw 'Certificate must allow Code Signing.' }
    if ($GitHubOutput) { "thumbprint=$expected" | Add-Content -LiteralPath $GitHubOutput }
    Write-Host 'Release signing certificate imported and checked; no trust store was modified.'
} catch {
    foreach ($certificate in $imported) {
        Remove-Item -LiteralPath "Cert:/CurrentUser/My/$($certificate.Thumbprint)" -DeleteKey -ErrorAction SilentlyContinue
    }
    throw
} finally {
    if (Test-Path -LiteralPath $pfxPath) { Remove-Item -LiteralPath $pfxPath -Force }
    $env:SHELLTRACK_SIGNING_PFX_BASE64 = $null
    $env:SHELLTRACK_SIGNING_PFX_PASSWORD = $null
}
