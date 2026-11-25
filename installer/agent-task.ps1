# The scheduled task that starts the OptLife agent at sign-in, for whoever signs in (the installer runs this).
# It runs with the highest rights the person has: as administrator for an administrator (no Windows prompt for each
# action they confirm), as a normal user otherwise. Every 5 minutes it is started again: with the agent running this
# does nothing (one instance only); if the agent died, it comes back.
param(
    [Parameter(Mandatory = $true)][string]$Target,
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$name = 'OptLife'

if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
    return
}

$exe = Join-Path $Target 'optlife-agent.exe'
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $Target
$atLogon = New-ScheduledTaskTrigger -AtLogOn
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5)
# BUILTIN\Users, by its SID (the name changes with the language of Windows).
$principal = New-ScheduledTaskPrincipal -GroupId 'S-1-5-32-545' -RunLevel Highest

Register-ScheduledTask -TaskName $name -Action $action -Trigger @($atLogon, $watchdog) -Settings $settings -Principal $principal `
    -Description 'OptLife: keeps an eye on the PC in the background.' -Force | Out-Null
Start-ScheduledTask -TaskName $name
