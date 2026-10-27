# Elevated one-time setup for the widget's network-action task. The widget prepends
# $PayloadB64 / $ScriptPath / $TargetPath / $TaskName / $RequestFile before running this body.
#
# Installs AnemoNetTask.ps1 into an admin-only location and registers an on-demand
# (trigger-less) HIGHEST-privilege task that runs exactly that script. It also removes the
# older per-action tasks, which ran a user-writable .bat out of %TEMP%.
$ErrorActionPreference = 'Stop'

$dir = Split-Path -Parent $ScriptPath
New-Item -ItemType Directory -Force -Path $dir | Out-Null
[IO.File]::WriteAllBytes($ScriptPath, [Convert]::FromBase64String($PayloadB64))

foreach ($legacy in 'Anemo_ReleaseRenew', 'Anemo_SetStaticIp') {
    Unregister-ScheduledTask -TaskName $legacy -Confirm:$false -ErrorAction SilentlyContinue
}

$psExe = Join-Path ([Environment]::GetFolderPath('System')) 'WindowsPowerShell\v1.0\powershell.exe'
$taskArgs = '-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $ScriptPath + '" -RequestFile "' + $RequestFile + '"'

$action = New-ScheduledTaskAction -Execute $psExe -Argument $taskArgs
$principal = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Highest
# Unlike schtasks' defaults, don't refuse to start / kill the task on battery power (laptops).
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 2)

Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings -Force | Out-Null

# Records which request file the task was registered for, so the widget can tell when the
# task needs re-registering (e.g. a different user profile) without parsing task XML.
[IO.File]::WriteAllText($TargetPath, $RequestFile)
