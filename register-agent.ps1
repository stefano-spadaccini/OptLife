# Makes the OptLife agent start by itself at every logon of the current user (or removes that).
param(
    # Where OptLife lives. When this script sits next to the program (the package given to friends,
    # unzipped anywhere) that folder is used; otherwise the place publish.ps1 installs to.
    [string]$Target = $(if (Test-Path (Join-Path $PSScriptRoot 'optlife-agent.exe')) { $PSScriptRoot } else { 'C:\Software\OptLife' }),
    [switch]$Remove,
    # The agent runs as administrator: no permission windows for the scripts and actions that need it.
    # Creating such a task needs administrator rights once: the script asks for them by itself.
    [switch]$Admin
)

$ErrorActionPreference = 'Stop'
$name = 'OptLife'

$elevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($Admin -and -not $elevated) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Admin -Target `"$Target`""
    Start-Process powershell.exe -Verb RunAs -ArgumentList $arguments -Wait
    return
}

if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false
    Write-Host "Avvio automatico di OptLife rimosso."
    return
}

$exe = Join-Path $Target 'optlife-agent.exe'
if (-not (Test-Path $exe)) { throw "Non trovo ${exe}: esegui prima publish.ps1." }

$user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $exe -WorkingDirectory $Target
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
# Watchdog: every 5 minutes the task is started again. While the agent runs this does nothing (only one
# instance is allowed); if it died (on 20/11/2026 a driver install crashed it) it comes back. A repetition
# hung on the logon trigger would only begin at the next logon, so it is a trigger of its own, from now on.
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$trigger = @($atLogon, $watchdog)
# No time limit (it is meant to run all day), keep running on battery, and come back if it ever dies.
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -MultipleInstances IgnoreNew -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel $(if ($Admin) { 'Highest' } else { 'Limited' })

# A running agent keeps its old rights: stop it, so the task starts it again with the new ones.
$cli = Join-Path $Target 'optlife.exe'
if ((Get-Process -Name 'optlife-agent' -ErrorAction SilentlyContinue) -and (Test-Path $cli)) {
    & $cli stop | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Name 'optlife-agent' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
}

Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
    -Description 'OptLife: raccoglie lo stato del PC in background.' -Force | Out-Null
Start-ScheduledTask -TaskName $name
Write-Host "OptLife parte ora a ogni accesso di $user$(if ($Admin) { ', come amministratore' })."
if ($Admin) { Start-Sleep -Seconds 3 }
