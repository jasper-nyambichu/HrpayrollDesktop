# Builds the branch PC installer: installer\Output\DhwarshHR-Setup-<version>.exe
#
#   powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
#
# Needs the .NET 10 SDK and Inno Setup 6 (winget install JRSoftware.InnoSetup).
# Always a Release build: Debug builds carry a test hook that fakes fingerprint scans.

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$project = Join-Path $here '..\HrpayrollDesktop\HrpayrollDesktop.csproj'
$out = Join-Path $here 'out\app'
$redist = Join-Path $here 'redist'

# The version comes from the project file, so the installer and the app always agree.
[xml]$csproj = Get-Content $project
$version = ($csproj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in HrpayrollDesktop.csproj' }
Write-Host "Building Dhwarsh HR Attendance $version" -ForegroundColor Cyan

# 1. The app, with its own .NET runtime, so the branch PC needs nothing else.
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

foreach ($file in 'HrpayrollDesktop.exe', 'appsettings.json', 'SecuGen.FDxSDKPro.Windows.dll', 'sgfplib.dll', 'sgfpamx.dll', 'sgwsqlib.dll') {
    if (-not (Test-Path (Join-Path $out $file))) { throw "The published app is missing $file" }
}
Write-Host "Server address in this build: $((Get-Content (Join-Path $out 'appsettings.json') -Raw | ConvertFrom-Json).Agent.BackendBaseUrl)"

# 2. The SecuGen reader drivers (signed by Microsoft for SecuGen). Kept in installer\redist once copied, so any
#    PC can build the installer. They come from a PC with the SecuGen driver package installed.
$drivers = Join-Path $redist 'SecuGen\Drivers'
if (-not (Test-Path (Join-Path $drivers '*\*.inf'))) {
    $source = 'C:\Program Files\SecuGen\Drivers'
    if (-not (Test-Path $source)) {
        throw "No SecuGen drivers. Install the SecuGen driver package on this PC (or copy its Drivers folder to $drivers)."
    }
    New-Item -ItemType Directory -Force $drivers | Out-Null
    Copy-Item "$source\*" $drivers -Recurse -Force
}

# 3. The Visual C++ runtime the SecuGen SDK needs, straight from Microsoft.
$vc = Join-Path $redist 'vc_redist.x64.exe'
if (-not (Test-Path $vc)) {
    New-Item -ItemType Directory -Force $redist | Out-Null
    Write-Host 'Downloading the Microsoft Visual C++ runtime...'
    Invoke-WebRequest 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $vc -UseBasicParsing
}
$signature = Get-AuthenticodeSignature $vc
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
    Remove-Item $vc -Force
    throw 'vc_redist.x64.exe is not signed by Microsoft; deleted it. Run the script again.'
}

# Automatic updates check releases against this key, so it must be in the installer.
$publicKey = Join-Path $here 'updater\update-public-key.xml'
$privateKey = Join-Path $env:USERPROFILE '.dhwarsh\update-signing-key.xml'
if (-not (Test-Path $publicKey)) { throw 'No release key yet. Run installer\new-signing-key.ps1 once on this PC.' }

# 4. The setup .exe.
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

& $iscc "/DAppVersion=$version" (Join-Path $here 'DhwarshHR.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

$setup = Join-Path $here "Output\DhwarshHR-Setup-$version.exe"

# 5. The signature branch PCs check before installing it as an update: over the version and the file's SHA-256.
if (Test-Path $privateKey) {
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString((Get-Content $privateKey -Raw))
    if ($rsa.ToXmlString($false).Trim() -ne (Get-Content $publicKey -Raw).Trim()) {
        throw "The private key in $privateKey does not match updater\update-public-key.xml. Updates signed with it would be refused."
    }
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $signature = $rsa.SignData([Text.Encoding]::UTF8.GetBytes("DhwarshHR-Setup|$version|$sha"), 'SHA256')
    Set-Content -Path "$setup.sig" -Value ([Convert]::ToBase64String($signature)) -Encoding ASCII
    Write-Host "Signed: $setup.sig"
} else {
    Write-Warning "No private key at $privateKey, so this build is not signed and can't be published as an update."
}

Write-Host ''
Write-Host "Done: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
