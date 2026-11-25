# Prepares the Windows Sandbox test: the installers in artifacts\ go in read-only, the results come back in
# artifacts\sandbox-results\. Double-click artifacts\OptLife-test.wsb afterwards: the test runs by itself at sign-in.
# Windows Sandbox must be on (Windows 10/11 Pro or higher, virtualization on in the BIOS):
#   dism /online /enable-feature /featurename:Containers-DisposableClientVM /all   (as administrator, then restart)
$ErrorActionPreference = 'Stop'
$root = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$artifacts = Join-Path $root 'artifacts'
$results = Join-Path $artifacts 'sandbox-results'
New-Item -ItemType Directory -Force $results | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'sandbox-test.ps1') $artifacts -Force
if (-not (Get-ChildItem $artifacts -Filter 'OptLife-Setup-*.exe')) { throw 'No installer in artifacts\: run installer\build-installer.ps1 first.' }

$wsb = @"
<Configuration>
  <Networking>Enable</Networking>
  <MappedFolders>
    <MappedFolder>
      <HostFolder>$artifacts</HostFolder>
      <SandboxFolder>C:\OptLife</SandboxFolder>
      <ReadOnly>true</ReadOnly>
    </MappedFolder>
    <MappedFolder>
      <HostFolder>$results</HostFolder>
      <SandboxFolder>C:\Results</SandboxFolder>
      <ReadOnly>false</ReadOnly>
    </MappedFolder>
  </MappedFolders>
  <LogonCommand>
    <Command>powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\OptLife\sandbox-test.ps1</Command>
  </LogonCommand>
</Configuration>
"@
Set-Content (Join-Path $artifacts 'OptLife-test.wsb') $wsb -Encoding utf8
Write-Host "Ready: $(Join-Path $artifacts 'OptLife-test.wsb')  (results in $results)"
