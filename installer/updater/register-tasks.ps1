# Run by setup, as administrator. Creates the two scheduled tasks automatic updates need, under "Dhwarsh HR":
#   Updater   runs update.ps1 as SYSTEM five minutes after Windows starts and then every hour, so updates
#             install without an administrator prompt.
#   Open app  opens the app on the signed-in user's desktop; the updater starts it after an update, because
#             a program SYSTEM starts itself would be invisible.
# Run with -Remove on uninstall.
param([string]$AppDir, [switch]$Remove)

$ErrorActionPreference = 'Stop'
$path = '\Dhwarsh HR\'

if ($Remove) {
    Get-ScheduledTask -TaskPath $path -ErrorAction SilentlyContinue | Unregister-ScheduledTask -Confirm:$false
    exit 0
}

$script = Join-Path $AppDir 'updater\update.ps1'

# During an automatic update this very task is running setup: leave it as it is rather than redefine it mid-run.
$existing = Get-ScheduledTask -TaskPath $path -TaskName 'Updater' -ErrorAction SilentlyContinue
$skipUpdater = $existing -and $existing.State -eq 'Running'
$updaterAction = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$script`""
$atStartup = New-ScheduledTaskTrigger -AtStartup
$atStartup.Delay = 'PT5M'
$hourly = New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddMinutes(7) -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Days 3650)
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 45) -MultipleInstances IgnoreNew
if (-not $skipUpdater) {
    Register-ScheduledTask -TaskPath $path -TaskName 'Updater' -Force -Action $updaterAction -Trigger @($atStartup, $hourly) -Settings $settings `
        -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) `
        -Description 'Installs new versions of Dhwarsh HR Attendance, signed with the release key.' | Out-Null
}

# BUILTIN\Users: runs on the desktop of whoever is signed in to Windows.
$openAction = New-ScheduledTaskAction -Execute (Join-Path $AppDir 'HrpayrollDesktop.exe') -WorkingDirectory $AppDir
Register-ScheduledTask -TaskPath $path -TaskName 'Open app' -Force -Action $openAction `
    -Principal (New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Limited) `
    -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Seconds 0)) `
    -Description 'Opens Dhwarsh HR Attendance again after an automatic update.' | Out-Null
