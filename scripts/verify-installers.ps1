#Requires -Version 7.0
param([Parameter(Mandatory)][string]$InstallerDirectory, [switch]$TestMsixLayout)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'packaging-tools.ps1')
$outputRoot = [IO.Path]::GetFullPath($InstallerDirectory)
$details = Get-Content -LiteralPath (Join-Path $outputRoot 'installers.json') -Raw | ConvertFrom-Json
foreach ($file in $details.files) {
    $path = Join-Path $outputRoot $file.name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw "Installer checksum mismatch: $($file.name)" }
}
$exe = Join-Path $outputRoot ($details.files | Where-Object name -Like '*-Setup.exe').name
$msix = Join-Path $outputRoot ($details.files | Where-Object name -Like '*.msix').name
$expectedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new((Join-Path $outputRoot 'ShellTrack.cer'))
if ($expectedCertificate.Thumbprint -ne $details.certificateThumbprint) { throw 'Public certificate mismatch.' }
$exeSignature = Get-AuthenticodeSignature -LiteralPath $exe
if ($exeSignature.SignerCertificate.Thumbprint -ne $expectedCertificate.Thumbprint) { throw 'EXE installer signer mismatch.' }

# Verify cryptographic signature without adding a certificate to a trust store.
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$archive = [IO.Compression.ZipFile]::OpenRead($msix)
try {
    $signatureStream = $archive.GetEntry('AppxSignature.p7x').Open()
    $signatureBytes = [IO.MemoryStream]::new()
    try { $signatureStream.CopyTo($signatureBytes); $bytes = $signatureBytes.ToArray() }
    finally { $signatureStream.Dispose(); $signatureBytes.Dispose() }
    if ([Text.Encoding]::ASCII.GetString($bytes, 0, 4) -ne 'PKCX') { throw 'Invalid MSIX signature header.' }
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([byte[]]$bytes[4..($bytes.Length - 1)])
    $cms.CheckSignature($true)
    if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $expectedCertificate.Thumbprint) { throw 'MSIX signer mismatch.' }
    $mapReader = [IO.StreamReader]::new($archive.GetEntry('AppxBlockMap.xml').Open())
    try { [xml]$blockMap = $mapReader.ReadToEnd() } finally { $mapReader.Dispose() }
    foreach ($file in $blockMap.BlockMap.File) {
        $stream = $archive.GetEntry($file.Name.Replace('\', '/')).Open()
        try {
            [long]$remaining = $file.Size
            foreach ($block in $file.Block) {
                $buffer = [byte[]]::new([int][Math]::Min(65536, $remaining))
                $stream.ReadExactly($buffer)
                if ([Convert]::ToBase64String([Security.Cryptography.SHA256]::HashData($buffer)) -ne $block.Hash) { throw "MSIX block hash mismatch: $($file.Name)" }
                $remaining -= $buffer.Length
            }
            if ($remaining -ne 0) { throw "Incomplete MSIX block map: $($file.Name)" }
        } finally { $stream.Dispose() }
    }
} finally { $archive.Dispose(); $expectedCertificate.Dispose() }
Write-Host 'PASS: EXE signer, MSIX cryptographic signature, certificate and all payload block hashes.'

$testRoot = Join-Path $repoRoot ('work/installer-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$installRoot = Join-Path $testRoot 'installed app'
$dataRoot = Join-Path $testRoot 'records'
$cli = Join-Path $installRoot 'shelltrack.exe'
$uninstaller = Join-Path $installRoot 'unins000.exe'
$uninstallKey = 'HKCU:/Software/Microsoft/Windows/CurrentVersion/Uninstall/{A795A862-EA04-4A12-A129-98698D24C9B0}_is1'
if (Test-Path -LiteralPath $uninstallKey) { throw 'Existing EXE installation found; refusing to replace it during tests.' }
$ownershipKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software/ShellTrack/Installer')
if ($ownershipKey) {
    try { if ($ownershipKey.GetValue('AddedPath')) { throw 'Existing installer PATH ownership found.' } }
    finally { $ownershipKey.Dispose() }
}
function Read-UserPath {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment')
    try { return $key.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { $key.Dispose() }
}
$originalPath = Read-UserPath
$group = 'Shell Track Packaging Test ' + [Guid]::NewGuid().ToString('N')
try {
    $install = Start-Process -FilePath $exe -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/TASKS=userpath', ('/DIR="' + $installRoot + '"'), ('/GROUP="' + $group + '"'), ('/LOG="' + (Join-Path $testRoot 'install.log') + '"')) -Wait -PassThru -WindowStyle Hidden
    if ($install.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $cli)) { throw "EXE installation failed: $($install.ExitCode)" }
    if ((Read-UserPath) -notlike ('*' + $installRoot + '*')) { throw 'EXE installer did not add user PATH.' }
    $output = & $cli -d $dataRoot -s cmd -c 'echo EXE_INSTALLED_OK & exit /b 7'
    if ($LASTEXITCODE -ne 7 -or ($output -join '') -notmatch 'EXE_INSTALLED_OK') { throw 'Installed EXE invocation failed.' }
    Write-Host 'PASS: EXE installation, user PATH and installed CLI/Host output and exit code.'
} finally {
    if (Test-Path -LiteralPath $cli) { & $cli -d $dataRoot shutdown }
    if (Test-Path -LiteralPath $uninstaller) {
        $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"')) -Wait -PassThru -WindowStyle Hidden
        if ($uninstall.ExitCode -ne 0) { throw 'EXE uninstall failed.' }
    }
}
$restoredPath = Read-UserPath
if ($restoredPath -ne $originalPath) { throw "EXE uninstall changed user PATH (expected length $($originalPath.Length), actual $($restoredPath.Length); expected trailing separator $($originalPath.EndsWith(';')), actual $($restoredPath.EndsWith(';')))." }
if (Test-Path -LiteralPath $cli) { throw 'EXE uninstall left the CLI executable installed.' }
if (Test-Path -LiteralPath $uninstallKey) { throw 'EXE uninstall left its registry entry.' }
if (-not (Test-Path -LiteralPath (Join-Path $dataRoot 'sessions'))) { throw 'Uninstall deleted task records.' }
Write-Host 'PASS: EXE uninstall restores user PATH and retains task records.'

if ($TestMsixLayout) {
    if (Get-AppxPackage -Name ShellTrack.Desktop) { throw 'Existing MSIX installation found; refusing to replace it during tests.' }
    $development = Get-ItemPropertyValue 'HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/AppModelUnlock' 'AllowDevelopmentWithoutDevLicense' -ErrorAction SilentlyContinue
    if ($development -ne 1) { throw 'MSIX layout tests require Windows Developer Mode; no system setting was changed.' }
    $layout = Join-Path $testRoot 'msix-layout'
    $makeAppx = Find-WindowsSdkTool 'makeappx.exe'
    $unpackOutput = & $makeAppx unpack /p $msix /d $layout /o 2>&1
    if ($LASTEXITCODE -ne 0) { throw ('MSIX unpack failed: ' + ($unpackOutput | Select-Object -Last 5 | Out-String)) }
    Remove-Item -LiteralPath (Join-Path $layout 'AppxSignature.p7x')
    $package = $null
    $oldDiagnostics = $env:SHELLTRACK_DIAGNOSTICS
    $env:SHELLTRACK_DIAGNOSTICS = '1'
    $msixRoot = Join-Path $testRoot 'msix-records'
    try {
        Add-AppxPackage -Register (Join-Path $layout 'AppxManifest.xml')
        $package = Get-AppxPackage -Name ShellTrack.Desktop
        $aliasRoot = Join-Path $env:LOCALAPPDATA ('Microsoft/WindowsApps/' + $package.PackageFamilyName)
        $msixCli = Join-Path $aliasRoot 'shelltrack.exe'
        $output = & $msixCli -d $msixRoot -s cmd -c 'echo MSIX_INSTALLED_OK & exit /b 9'
        if ($LASTEXITCODE -ne 9 -or ($output -join '') -notmatch 'MSIX_INSTALLED_OK') { throw 'MSIX execution alias failed.' }
        foreach ($alias in @('shelltrack.exe', 'shelltrack-cmd.exe', 'shelltrack-pwsh.exe', 'shelltrack-powershell.exe', 'shelltrack-viewer.exe')) {
            if (-not (Test-Path -LiteralPath (Join-Path $aliasRoot $alias))) { throw "MSIX alias missing: $alias" }
        }
        & $msixCli -d $msixRoot ui
        $diagnosticsFile = Join-Path $msixRoot 'ui-diagnostics.json'
        $limit = [DateTime]::UtcNow.AddSeconds(20)
        while (-not (Test-Path -LiteralPath $diagnosticsFile) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 200 }
        if (-not (Test-Path -LiteralPath $diagnosticsFile)) { throw 'MSIX window failed to load.' }
        $diagnostics = Get-Content -LiteralPath $diagnosticsFile -Raw | ConvertFrom-Json
        if (-not $diagnostics.perMonitorV2 -or $diagnostics.packageFamily -ne $package.PackageFamilyName) { throw 'MSIX window identity/DPI check failed.' }
        if (Test-Path -LiteralPath (Join-Path $msixRoot 'desktop-errors.log')) { throw 'MSIX window reported registration errors; inspect desktop-errors.log.' }
        Write-Host 'PASS: MSIX development registration, five execution aliases, CLI/Host, packaged window and DPI.'
    } finally {
        $env:SHELLTRACK_DIAGNOSTICS = $oldDiagnostics
        if ($package) {
            Get-CimInstance Win32_Process -Filter "Name='ShellTrack.Desktop.exe'" | Where-Object { $_.ExecutablePath -eq (Join-Path $layout 'desktop/ShellTrack.Desktop.exe') } | ForEach-Object { Stop-Process -Id $_.ProcessId }
            if (Test-Path -LiteralPath (Join-Path $msixRoot 'connection.json')) { & $msixCli -d $msixRoot shutdown }
            Remove-AppxPackage -Package $package.PackageFullName
        }
    }
    if (Get-AppxPackage -Name ShellTrack.Desktop) { throw 'MSIX test registration was not removed.' }
    if (-not (Test-Path -LiteralPath (Join-Path $msixRoot 'sessions'))) { throw 'MSIX unregister deleted task records.' }
    Write-Host 'PASS: MSIX unregister retains task data; no signing certificate was added to a trust store.'
}
Write-Host "Installer diagnostics: $testRoot"
