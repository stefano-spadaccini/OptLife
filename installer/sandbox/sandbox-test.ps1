# Runs inside Windows Sandbox (a clean Windows, no NVIDIA, no Afterburner): installs OptLife from scratch, checks it,
# updates it to the next version, uninstalls it. Writes what it found to C:\Results\report.txt (a folder of the host).
$ErrorActionPreference = 'Continue'
$results = 'C:\Results'
New-Item -ItemType Directory -Force $results | Out-Null
$report = Join-Path $results 'report.txt'
Set-Content $report "OptLife in Windows Sandbox - $(Get-Date -Format o)`n" -Encoding utf8
function Say([string]$text) { Add-Content $report $text -Encoding utf8 }
function Check([string]$what, [bool]$ok, [string]$detail = '') { Say ("{0}  {1}{2}" -f $(if ($ok) { 'OK  ' } else { 'FAIL' }), $what, $(if ($detail) { " - $detail" } else { '' })) }

$setups = Get-ChildItem 'C:\OptLife\OptLife-Setup-*.exe' | Sort-Object { [version]($_.BaseName -replace '^OptLife-Setup-', '') }
if ($setups.Count -lt 1) { Say 'No installer in C:\OptLife'; return }
$first = $setups[0]; $last = $setups[-1]
$app = Join-Path $env:ProgramFiles 'OptLife'
$data = Join-Path $env:ProgramData 'OptLife'
$cli = Join-Path $app 'optlife.exe'

# ── 1. Install from scratch ──
Say "`n== Install $($first.Name) =="
$p = Start-Process $first.FullName -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=C:\Results\install-1.log' -Wait -PassThru
Check 'installer exit code 0' ($p.ExitCode -eq 0) "code $($p.ExitCode)"
Check 'program in Program Files' (Test-Path (Join-Path $app 'optlife-agent.exe'))
Check 'Start menu entry' (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\OptLife\OptLife.lnk'))
$task = Get-ScheduledTask -TaskName 'OptLife' -ErrorAction SilentlyContinue
Check 'scheduled task' ($null -ne $task) $(if ($task) { "RunLevel $($task.Principal.RunLevel), group $($task.Principal.GroupId)" })
Start-Sleep -Seconds 20
$agent = Get-Process optlife-agent -ErrorAction SilentlyContinue
Check 'agent running' ($null -ne $agent)
Check 'data in ProgramData' (Test-Path (Join-Path $data 'optlife.db'))
Check 'no data folder beside the program' (-not (Test-Path (Join-Path $app 'dati')))
$acl = Get-Acl $data
$userWrite = $acl.Access | Where-Object { $_.IdentityReference -notmatch 'SYSTEM|Administrators|Administrat' -and $_.FileSystemRights -match 'Write|Modify|FullControl|Delete' }
Check 'only SYSTEM and administrators may change the data folder' ($null -eq $userWrite) (($userWrite | ForEach-Object { "$($_.IdentityReference): $($_.FileSystemRights)" }) -join '; ')
Check 'data folder not inheriting from ProgramData' ($acl.AreAccessRulesProtected)

# ── 2. Modules on a PC without NVIDIA or Afterburner: none may fail ──
Say "`n== Modules =="
& $cli run 2>&1 | Set-Content (Join-Path $results 'run-1.txt') -Encoding utf8
$status = & $cli status --json 2>$null | ConvertFrom-Json
foreach ($m in $status.modules) {
    $errors = @($m.tasks | Where-Object { $_.lastError }) | ForEach-Object { $_.lastError }
    Check "module $($m.id)" ($errors.Count -eq 0) ($errors -join '; ')
}
foreach ($e in $status.loadErrors) { Check 'module loaded' $false $e }
& $cli status --json > (Join-Path $results 'status-1.json') 2>&1
& $cli findings > (Join-Path $results 'findings-1.txt') 2>&1
& $cli live > (Join-Path $results 'live-1.txt') 2>&1

# The panel, as a picture (on a copy of the data: the snapshot writes preferences).
$copy = 'C:\Results\data-copy'
Copy-Item $data $copy -Recurse -Force
$env:OPTLIFE_DATA = $copy
foreach ($page in 'Monitor', 'Modules') {
    Start-Process (Join-Path $app 'optlife-panel.exe') -ArgumentList "--snapshot C:\Results\panel-$page.png --page $page" -Wait
}
Remove-Item Env:OPTLIFE_DATA
Check 'panel pictures' ((Test-Path 'C:\Results\panel-Monitor.png') -and (Test-Path 'C:\Results\panel-Modules.png'))

# A setting written as the person, to see it survive the update.
& $cli set optlife everyHours 48 2>&1 | Out-Null

# ── 3. Update to the next version ──
if ($last.FullName -ne $first.FullName) {
    Say "`n== Update to $($last.Name) =="
    $p = Start-Process $last.FullName -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /LOG=C:\Results\install-2.log' -Wait -PassThru
    Check 'update exit code 0' ($p.ExitCode -eq 0) "code $($p.ExitCode)"
    Start-Sleep -Seconds 20
    $version = (Get-Item (Join-Path $app 'optlife.exe')).VersionInfo.ProductVersion
    Check 'new version installed' ($version -like "$($last.BaseName -replace '^OptLife-Setup-', '')*") $version
    Check 'agent running again' ($null -ne (Get-Process optlife-agent -ErrorAction SilentlyContinue))
    $kept = ((& $cli modules --json 2>$null | ConvertFrom-Json) | Where-Object { $_.id -eq 'optlife' }).settings | Where-Object { $_.key -eq 'everyHours' }
    Check 'settings kept' ("$($kept.value)" -eq '48') "everyHours = $($kept.value)"
    Check 'no lock left' (-not (Test-Path (Join-Path $app 'installing.lock')))
}
else { Say "`n(only one installer: no update test)" }

# ── 4. Uninstall (silent: the data stays, as the installer promises) ──
Say "`n== Uninstall =="
$uninstaller = Get-ChildItem $app -Filter 'unins*.exe' | Select-Object -First 1
$p = Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART' -Wait -PassThru
Start-Sleep -Seconds 5
Check 'uninstall exit code 0' ($p.ExitCode -eq 0) "code $($p.ExitCode)"
Check 'program removed' (-not (Test-Path (Join-Path $app 'optlife-agent.exe')))
Check 'task removed' ($null -eq (Get-ScheduledTask -TaskName 'OptLife' -ErrorAction SilentlyContinue))
Check 'agent stopped' ($null -eq (Get-Process optlife-agent -ErrorAction SilentlyContinue))
Check 'data kept after a silent uninstall' (Test-Path (Join-Path $data 'optlife.db'))
Check 'Start menu entry removed' (-not (Test-Path (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\OptLife')))

# ── 5. For the eyes: install again with the wizard, and open the panel (first opening, guided tour) ──
Say "`nDone. Now the interactive check: the installer opens with its wizard."
Remove-Item $data -Recurse -Force
Start-Process $last.FullName
