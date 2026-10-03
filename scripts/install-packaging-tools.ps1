#Requires -Version 7.0
$ErrorActionPreference = 'Stop'
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../work/tools'))
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
$installer = Join-Path $toolsRoot 'innosetup-6.7.3.exe'
Invoke-WebRequest 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe' -OutFile $installer
if ((Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash -ne '9C73C3BAE7ED48D44112A0F48E66742C00090BDB5BEF71D9D3C056C66E97B732') { throw 'Inno Setup download checksum validation failed.' }
$signature = Get-AuthenticodeSignature -LiteralPath $installer
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') { throw 'Inno Setup download signature validation failed.' }
$installRoot = Join-Path $toolsRoot 'InnoSetup'
$process = Start-Process -FilePath $installer -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', '/CURRENTUSER', ('/DIR="' + $installRoot + '"')) -Wait -PassThru -WindowStyle Hidden
if ($process.ExitCode -ne 0 -or -not (Test-Path -LiteralPath (Join-Path $installRoot 'ISCC.exe'))) { throw 'Inno Setup installation failed.' }
Write-Host "Inno Setup compiler: $installRoot/ISCC.exe"
