# Publishes the built installer as a GitHub release. Within about an hour every branch PC installs it by itself.
#
#   powershell -ExecutionPolicy Bypass -File installer\publish-release.ps1
#
# Before running: raise <Version> in HrpayrollDesktop.csproj, run build-installer.ps1, and test the setup.
# Needs the GitHub CLI signed in as an account that can create releases on the repository (gh auth login).

param([string]$Notes = 'Bug fixes and improvements.')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repo = 'jasper-nyambichu/HrpayrollDesktop'

[xml]$csproj = Get-Content (Join-Path $here '..\HrpayrollDesktop\HrpayrollDesktop.csproj')
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$setup = Join-Path $here "Output\DhwarshHR-Setup-$version.exe"
$sig = "$setup.sig"
if (-not (Test-Path $setup) -or -not (Test-Path $sig)) { throw "Build and sign $version first: run build-installer.ps1." }

# Check it here exactly as the branch PCs will, so a bad signature is caught before anyone downloads it.
$sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
$rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
$rsa.PersistKeyInCsp = $false
$rsa.FromXmlString((Get-Content (Join-Path $here 'updater\update-public-key.xml') -Raw))
$ok = $rsa.VerifyData([Text.Encoding]::UTF8.GetBytes("DhwarshHR-Setup|$version|$sha"), 'SHA256', [Convert]::FromBase64String((Get-Content $sig -Raw).Trim()))
if (-not $ok) { throw 'The signature does not match this installer. Run build-installer.ps1 again.' }

gh release view "v$version" --repo $repo *> $null
if ($LASTEXITCODE -eq 0) { throw "Release v$version already exists. Raise <Version> in HrpayrollDesktop.csproj and build again." }

gh release create "v$version" $setup $sig --repo $repo --title "Dhwarsh HR Attendance $version" --notes $Notes --latest
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
Write-Host "Published v$version. Branch PCs install it within the hour, once nobody is signed in." -ForegroundColor Green
