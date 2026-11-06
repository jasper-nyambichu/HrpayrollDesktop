# Run ONCE, on the PC that builds releases. Creates the key that signs every update:
#   private half: %USERPROFILE%\.dhwarsh\update-signing-key.xml   (never commit or share it; back it up)
#   public half:  installer\updater\update-public-key.xml          (goes into the installer; commit it)
# Branch PCs install an update only if it was signed with this key. If the private half is lost, branch PCs
# can't be updated automatically any more until someone runs a setup built with a new key on each of them.

$ErrorActionPreference = 'Stop'
$keyDir = Join-Path $env:USERPROFILE '.dhwarsh'
$private = Join-Path $keyDir 'update-signing-key.xml'
$public = Join-Path $PSScriptRoot 'updater\update-public-key.xml'

if (Test-Path $private) { throw "A signing key already exists at $private. Delete it first only if you mean to replace it." }

New-Item -ItemType Directory -Force $keyDir | Out-Null
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider(3072)
$rsa.PersistKeyInCsp = $false
Set-Content -Path $private -Value $rsa.ToXmlString($true) -Encoding ASCII
Set-Content -Path $public -Value $rsa.ToXmlString($false) -Encoding ASCII

# Only this Windows user may read the private half.
icacls $keyDir /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null

Write-Host "Private key: $private  <- back this up somewhere safe (not in git)" -ForegroundColor Yellow
Write-Host "Public key:  $public  <- commit this" -ForegroundColor Green
