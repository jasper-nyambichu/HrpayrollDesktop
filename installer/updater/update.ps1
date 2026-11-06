# Dhwarsh HR Attendance: automatic updates.
#
# Windows runs this every hour as SYSTEM (the "Dhwarsh HR\Updater" scheduled task the installer creates), so an
# update needs nobody to approve an administrator prompt at the branch. It:
#   1. asks GitHub for the newest release of the desktop app;
#   2. stops if this PC already has it, or while someone is signed in to the app (it tries again next hour);
#   3. downloads the setup .exe and its signature into the app folder, which only administrators can change;
#   4. installs it only if the signature was made with the release key, so nobody who gets into the GitHub
#      account, or between the PC and GitHub, can push their own program onto the branch PCs;
#   5. installs silently, then opens the app again if it was open.
# What it did goes to C:\ProgramData\Dhwarsh HR\update.log.

# The parameters are for testing only; the scheduled task passes none.
param([string]$ReleaseUrl = 'https://api.github.com/repos/jasper-nyambichu/HrpayrollDesktop/releases/latest', [string]$AppDir = '')

$ErrorActionPreference = 'Stop'
if (-not $AppDir) { $AppDir = Split-Path $PSScriptRoot -Parent }
$AppExe = Join-Path $AppDir 'HrpayrollDesktop.exe'
$DataDir = Join-Path $env:ProgramData 'Dhwarsh HR'
$LogFile = Join-Path $DataDir 'update.log'
$Downloads = Join-Path $AppDir 'updates'
$Headers = @{ 'User-Agent' = 'DhwarshHR-Updater'; 'Accept' = 'application/vnd.github+json' }

function Write-Log([string]$message) {
    try {
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 1MB) { Move-Item $LogFile "$LogFile.old" -Force }
        Add-Content -Path $LogFile -Value ('{0:yyyy-MM-dd HH:mm:ss}  {1}' -f (Get-Date), $message)
    } catch { }
}

# Someone is signing in or signed in: the app keeps this file fresh while they are.
function Test-AppBusy {
    $flag = Join-Path $DataDir 'app-busy'
    return (Test-Path $flag) -and (((Get-Date) - (Get-Item $flag).LastWriteTime).TotalMinutes -lt 10)
}

$mutex = New-Object System.Threading.Mutex($false, 'Global\DhwarshHR.Updater')
if (-not $mutex.WaitOne(0)) { exit 0 }

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue'

    if (-not (Test-Path $AppExe)) { exit 0 }
    $installed = [version](Get-Item $AppExe).VersionInfo.FileVersion

    try {
        $release = Invoke-RestMethod $ReleaseUrl -Headers $Headers -TimeoutSec 30
    } catch {
        # No internet, or no release yet. Normal; try again next hour.
        exit 0
    }

    $tag = ([string]$release.tag_name).TrimStart('v', 'V')
    $latest = $null
    if (-not [version]::TryParse($tag, [ref]$latest)) { Write-Log "Release tag '$($release.tag_name)' is not a version; ignored."; exit 0 }
    if ($latest.Build -lt 0) { $latest = New-Object Version($latest.Major, $latest.Minor, 0) }
    $installedShort = New-Object Version($installed.Major, $installed.Minor, [Math]::Max(0, $installed.Build))
    if ($latest -le $installedShort) { exit 0 }

    if (Test-AppBusy) { Write-Log "Version $tag is ready but someone is signed in. Trying again within the hour."; exit 0 }

    $setupName = "DhwarshHR-Setup-$tag.exe"
    $asset = $release.assets | Where-Object { $_.name -eq $setupName } | Select-Object -First 1
    $sigAsset = $release.assets | Where-Object { $_.name -eq "$setupName.sig" } | Select-Object -First 1
    if (-not $asset -or -not $sigAsset) { Write-Log "Release $tag has no $setupName with its .sig; not installed."; exit 0 }

    Write-Log "Updating from $installed to $tag."
    if (Test-Path $Downloads) { Remove-Item "$Downloads\*" -Recurse -Force -ErrorAction SilentlyContinue }
    New-Item -ItemType Directory -Force $Downloads | Out-Null
    $setup = Join-Path $Downloads $setupName
    $sigFile = "$setup.sig"
    Invoke-WebRequest $asset.browser_download_url -OutFile $setup -Headers @{ 'User-Agent' = 'DhwarshHR-Updater' } -UseBasicParsing -TimeoutSec 900
    Invoke-WebRequest $sigAsset.browser_download_url -OutFile $sigFile -Headers @{ 'User-Agent' = 'DhwarshHR-Updater' } -UseBasicParsing -TimeoutSec 60

    # The signature covers the version and the file's SHA-256 together, so neither can be swapped.
    $sha = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
    $message = [Text.Encoding]::UTF8.GetBytes("DhwarshHR-Setup|$tag|$sha")
    $rsa = New-Object System.Security.Cryptography.RSACryptoServiceProvider
    $rsa.PersistKeyInCsp = $false
    $rsa.FromXmlString((Get-Content (Join-Path $PSScriptRoot 'update-public-key.xml') -Raw))
    $valid = $false
    try { $valid = $rsa.VerifyData($message, 'SHA256', [Convert]::FromBase64String((Get-Content $sigFile -Raw).Trim())) } catch { $valid = $false }
    if (-not $valid) {
        Write-Log "REFUSED version ${tag}: its signature does not match the release key. Nothing was installed."
        Remove-Item $setup, $sigFile -Force -ErrorAction SilentlyContinue
        exit 1
    }

    # Someone may have signed in while it downloaded.
    if (Test-AppBusy) { Write-Log "Version $tag downloaded, but someone signed in. Installing at the next check."; exit 0 }

    $wasRunning = [bool](Get-Process -Name 'HrpayrollDesktop' -ErrorAction SilentlyContinue)
    $installLog = Join-Path $DataDir 'update-install.log'
    $process = Start-Process -FilePath $setup -Wait -PassThru -ArgumentList @(
        '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOCANCEL', '/SP-', "/LOG=`"$installLog`"")
    if ($process.ExitCode -ne 0) {
        Write-Log "Setup for $tag failed with code $($process.ExitCode). See update-install.log. The app is opened again."
    } else {
        Write-Log "Installed $tag."
    }

    if ($wasRunning -and -not (Get-Process -Name 'HrpayrollDesktop' -ErrorAction SilentlyContinue)) {
        try { Start-ScheduledTask -TaskPath '\Dhwarsh HR\' -TaskName 'Open app' } catch { Write-Log "Could not reopen the app: $($_.Exception.Message)" }
    }
    Remove-Item $setup, $sigFile -Force -ErrorAction SilentlyContinue
} catch {
    Write-Log "Update check failed: $($_.Exception.Message)"
    exit 1
} finally {
    $mutex.ReleaseMutex()
}
