# Builds OptLife and installs it into one folder (program + background agent side by side).
# The "dati" folder inside the install folder holds history and settings and is never touched.
param(
    [string]$Target = 'C:\Software\OptLife'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# The panel first: one opened from the agent's icon runs as administrator, and a script that is not
# cannot close its window. Checked before the agent is touched, so a refusal here leaves the agent running.
Get-Process -Name 'optlife-panel' -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null }
Start-Sleep -Milliseconds 1500
if (Get-Process -Name 'optlife-panel' -ErrorAction SilentlyContinue) {
    throw "Il pannello di OptLife e' aperto e non si chiude da qui: chiudilo e rilancia l'installazione."
}

# While the files are copied, the agent must not come back by itself (its scheduled task tries every 5 minutes):
# a mark beside the program tells a starting agent to wait; removed at the end whatever happens.
New-Item -ItemType Directory -Force $Target | Out-Null
$lock = Join-Path $Target 'installing.lock'
Set-Content -Path $lock -Value (Get-Date -Format o) -Encoding utf8

# The agent locks its own files: ask it to stop.
$wasRunning = $false
$cli = Join-Path $Target 'optlife.exe'
if (Get-Process -Name 'optlife-agent' -ErrorAction SilentlyContinue) {
    $wasRunning = $true
    if (Test-Path $cli) { & $cli stop | Out-Null }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Process -Name 'optlife-agent' -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 300
    }
    if (Get-Process -Name 'optlife-agent' -ErrorAction SilentlyContinue) {
        Remove-Item $lock -ErrorAction SilentlyContinue
        throw "L'agente non si e' fermato entro 15 secondi: installazione annullata."
    }
}

try {
    foreach ($project in 'OptLife.Cli', 'OptLife.Agent', 'OptLife.Panel') {
        dotnet publish (Join-Path $root "src\$project") -c Release -o $Target --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw "Compilazione di $project fallita." }
    }
}
finally {
    Remove-Item $lock -ErrorAction SilentlyContinue
    # The agent is meant to run all the time: it comes back whatever happened above, and also when it
    # was not running before. Through its own scheduled task when there is one: started that way it belongs
    # to Windows, not to whatever ran this script, and outlives the terminal (or the Claude session).
    $task = Get-ScheduledTask -TaskName 'OptLife' -ErrorAction SilentlyContinue
    if ($task) {
        # Right after the agent exits, the task can still read "running" for a moment, and a start would be ignored.
        $deadline = (Get-Date).AddSeconds(10)
        while ((Get-ScheduledTask -TaskName 'OptLife').State -eq 'Running' -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300 }
        Start-ScheduledTask -TaskName 'OptLife'
    }
    elseif ($wasRunning) { Start-Process (Join-Path $Target 'optlife-agent.exe') }
}

Start-Sleep -Seconds 2
$running = [bool](Get-Process -Name 'optlife-agent' -ErrorAction SilentlyContinue)
Write-Host "OptLife installato in $Target$(if ($running) { ' (agente attivo)' } else { ' (ATTENZIONE: agente non partito)' })"
