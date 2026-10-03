#Requires -Version 7.0
param(
    [string]$BundleDirectory,
    [string]$OutputDirectory,
    [string]$Version = '0.3.0.0',
    [string]$CertificateThumbprint,
    [string]$InnoCompiler,
    [string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'packaging-tools.ps1')
$makeAppx = Find-WindowsSdkTool 'makeappx.exe'
$signTool = Find-WindowsSdkTool 'signtool.exe'
$iscc = Find-InnoCompiler $InnoCompiler
$packageVersion = [version]$Version
if ($packageVersion.Revision -ne 0 -or (@($packageVersion.Major, $packageVersion.Minor, $packageVersion.Build) | Where-Object { $_ -lt 0 -or $_ -gt 65535 })) { throw 'Use a four-part MSIX version with revision zero, e.g. 0.3.0.0.' }
$Version = $packageVersion.ToString(4)
if ($TimestampUrl -and $TimestampUrl -notmatch '^https?://[A-Za-z0-9./:_-]+$') { throw 'Use an HTTP(S) timestamp URL without shell metacharacters.' }
$shortVersion = $packageVersion.ToString(3)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$outputRoot = if ($OutputDirectory) { [IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot "artifacts/installers-$stamp" }
if (Test-Path -LiteralPath $outputRoot) { throw "Output directory already exists: $outputRoot" }
New-Item -ItemType Directory -Path $outputRoot | Out-Null
if (-not $BundleDirectory) {
    $BundleDirectory = Join-Path $repoRoot "artifacts/installer-bundle-$stamp"
    & (Join-Path $PSScriptRoot 'publish.ps1') -SelfContained -OutputDirectory $BundleDirectory
}
$bundleRoot = [IO.Path]::GetFullPath($BundleDirectory)
foreach ($required in @('shelltrack.exe', 'ShellTrack.Host.exe', 'shelltrack-cmd.exe', 'shelltrack-pwsh.exe', 'shelltrack-powershell.exe',
    'coreclr.dll', 'hostfxr.dll', 'desktop/coreclr.dll', 'desktop/hostfxr.dll', 'desktop/App.xbf', 'desktop/MainWindow.xbf', 'desktop/ShellTrack.Desktop.pri')) {
    if (-not (Test-Path -LiteralPath (Join-Path $bundleRoot $required))) { throw "A complete self-contained bundle is required; missing $required" }
}

# Keep private keys in the current user's certificate store; only export .cer.
$selfSigned = -not $CertificateThumbprint
$certificate = $null
if ($selfSigned) {
    $stateRoot = Join-Path $repoRoot 'work/signing'
    New-Item -ItemType Directory -Force -Path $stateRoot | Out-Null
    $stateFile = Join-Path $stateRoot 'development-thumbprint.txt'
    if (Test-Path -LiteralPath $stateFile) {
        $storedThumbprint = (Get-Content -LiteralPath $stateFile -Raw).Trim()
        $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$storedThumbprint" -ErrorAction SilentlyContinue
    }
    if (-not $certificate -or -not $certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date).AddDays(30)) {
        $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=ShellTrack Development' -FriendlyName 'Shell Track installer development signing' -CertStoreLocation 'Cert:/CurrentUser/My' -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(3)
        Set-Content -LiteralPath $stateFile -Value $certificate.Thumbprint -NoNewline
    }
    $CertificateThumbprint = $certificate.Thumbprint
} else {
    $CertificateThumbprint = $CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($CertificateThumbprint -notmatch '^[0-9A-F]{40}$') { throw 'Invalid certificate thumbprint.' }
    $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
    if (-not $certificate.HasPrivateKey -or $certificate.NotAfter -lt (Get-Date)) { throw 'Signing certificate is expired or has no private key.' }
}
$publicCertificate = Join-Path $outputRoot 'ShellTrack.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
$signArguments = @('sign', '/fd', 'SHA256', '/sha1', $CertificateThumbprint, '/s', 'My')
if ($TimestampUrl) { $signArguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }

$layout = Join-Path $repoRoot "work/msix-layout-$stamp"
New-Item -ItemType Directory -Path $layout | Out-Null
Get-ChildItem -LiteralPath $bundleRoot | Copy-Item -Destination $layout -Recurse
# Packaged ms-appx URIs are rooted at the package, not the desktop subdirectory.
Copy-Item -LiteralPath (Join-Path $bundleRoot 'desktop/ShellTrack.Desktop.pri') -Destination (Join-Path $layout 'resources.pri')
Get-ChildItem -LiteralPath (Join-Path $bundleRoot 'desktop') -Filter '*.xbf' | Copy-Item -Destination $layout
[xml]$manifest = Get-Content -LiteralPath (Join-Path $repoRoot 'packaging/AppxManifest.xml') -Raw
$manifest.Package.Identity.SetAttribute('Version', $Version)
$manifest.Package.Identity.SetAttribute('Publisher', $certificate.Subject)
$manifest.Save((Join-Path $layout 'AppxManifest.xml'))
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Path $assets | Out-Null
Add-Type -AssemblyName System.Drawing
$sourceIcon = [Drawing.Image]::FromFile((Join-Path $repoRoot 'assets/shelltrack.png'))
try {
    foreach ($entry in @(@('StoreLogo.png', 50), @('Square44x44Logo.png', 44), @('Square150x150Logo.png', 150))) {
        $bitmap = [Drawing.Bitmap]::new([int]$entry[1], [int]$entry[1])
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.DrawImage($sourceIcon, 0, 0, $bitmap.Width, $bitmap.Height)
            $bitmap.Save((Join-Path $assets $entry[0]), [Drawing.Imaging.ImageFormat]::Png)
        } finally { $graphics.Dispose(); $bitmap.Dispose() }
    }
} finally { $sourceIcon.Dispose() }
$msix = Join-Path $outputRoot "ShellTrack-$Version-win-x64.msix"
$packingOutput = & $makeAppx pack /d $layout /p $msix /o 2>&1
$packingExit = $LASTEXITCODE
$packingOutput | Set-Content -LiteralPath (Join-Path $outputRoot 'makeappx.log')
if ($packingExit -ne 0) { throw ('MSIX packing failed: ' + ($packingOutput | Select-Object -Last 8 | Out-String)) }
& $signTool @signArguments $msix
if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
$innoSignCommand = '$q' + $signTool + '$q ' + ($signArguments -join ' ') + ' $f'
$exeLayout = Join-Path $repoRoot "work/exe-layout-$stamp"
New-Item -ItemType Directory -Path $exeLayout | Out-Null
Get-ChildItem -LiteralPath $bundleRoot | Copy-Item -Destination $exeLayout -Recurse
$engineLicenseRoot = Join-Path $exeLayout 'licenses/inno-setup'
New-Item -ItemType Directory -Path $engineLicenseRoot | Out-Null
Copy-Item -LiteralPath (Join-Path (Split-Path $iscc -Parent) 'license.txt') -Destination $engineLicenseRoot
$compilerOutput = & $iscc ("/DBundleDir=$exeLayout") ("/DInstallerVersion=$shortVersion") ("/O$outputRoot") ("/Sshelltrack=$innoSignCommand") (Join-Path $repoRoot 'packaging/ShellTrack.iss') 2>&1
$compilerExit = $LASTEXITCODE
$compilerOutput | Set-Content -LiteralPath (Join-Path $outputRoot 'inno.log')
if ($compilerExit -ne 0) { throw ('EXE installer compilation failed: ' + ($compilerOutput | Select-Object -Last 12 | Out-String)) }
$exe = Join-Path $outputRoot "ShellTrack-$shortVersion-win-x64-Setup.exe"
$details = [ordered]@{ version = $Version; publisher = $certificate.Subject; certificateThumbprint = $CertificateThumbprint; selfSigned = ($certificate.Subject -eq $certificate.Issuer); bundleDirectory = $bundleRoot; layoutDirectory = $layout; files = @() }
foreach ($file in @($exe, $msix, $publicCertificate)) {
    $details.files += @{ name = [IO.Path]::GetFileName($file); sha256 = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash }
}
$details | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $outputRoot 'installers.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging/INSTALL.md') -Destination $outputRoot
Copy-Item -LiteralPath (Join-Path (Split-Path $iscc -Parent) 'license.txt') -Destination (Join-Path $outputRoot 'INNO-LICENSE.txt')
Write-Host "Installers: $outputRoot"
Write-Host "Certificate: $CertificateThumbprint (private key remains in CurrentUser/My)"
