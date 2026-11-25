# Builds the OptLife installer: publishes the three programs self-contained (no .NET to install), then Inno Setup,
# then SHA256SUMS.txt beside the installer. Output in artifacts\.
#   powershell -File installer\build-installer.ps1 -Version 0.1.0 [-Repository owner/name]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    # GitHub "owner/name" where the program looks for its own updates; empty = no update check.
    [string]$Repository = '',
    [string]$Iscc = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$app = Join-Path $artifacts 'app'
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

foreach ($project in 'OptLife.Cli', 'OptLife.Agent', 'OptLife.Panel') {
    dotnet publish (Join-Path $root "src\$project") -c Release -r win-x64 --self-contained true -o $app --nologo -v quiet `
        "-p:Version=$Version" "-p:OptLifeRepository=$Repository"
    if ($LASTEXITCODE -ne 0) { throw "Build of $project failed." }
}
# Symbols are not needed to run.
Get-ChildItem $app -Filter *.pdb | Remove-Item

if (-not $Iscc) {
    $Iscc = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $Iscc) { throw 'Inno Setup 6 not found: winget install JRSoftware.InnoSetup' }

# The licence: at the root of the public repository, in public\ of a private one.
$license = @((Join-Path $root 'LICENSE'), (Join-Path $root 'public\LICENSE')) | Where-Object { Test-Path $_ } | Select-Object -First 1
$url = if ($Repository) { "https://github.com/$Repository" } else { '' }

& $Iscc /Q "/DAppVersion=$Version" "/DSourceDir=$app" "/DOutputDir=$artifacts" "/DLicenseFile=$license" "/DAppUrl=$url" (Join-Path $PSScriptRoot 'OptLife.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

$setup = Join-Path $artifacts "OptLife-Setup-$Version.exe"
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
# The usual format of sha256sum: "<hash>  <file name>".
Set-Content -Path (Join-Path $artifacts 'SHA256SUMS.txt') -Value "$hash  $(Split-Path $setup -Leaf)" -Encoding ascii
Write-Host "$setup`n$hash"
