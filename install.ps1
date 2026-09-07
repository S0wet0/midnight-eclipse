# Midnight Eclipse installer. Run from a normal (not elevated) PowerShell in
# this folder:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
#
# Options:
#   -KeepOtherWidgets   add Midnight Eclipse to Zebar's startup widgets
#                       instead of replacing them (by default it replaces
#                       them, so two bars don't stack on top of each other)
#   -KomorebicPath      path to komorebic.exe, if it isn't on PATH or in
#                       C:\Program Files\komorebi\bin
#
# What it does:
#  1. Builds the taskbar-mirror helper from source with the C# compiler
#     built into Windows, into %LOCALAPPDATA%\midnight-eclipse\bin. No
#     prebuilt .exe is shipped.
#  2. Copies the Zebar pack to %USERPROFILE%\.glzr\zebar\midnight-eclipse,
#     filling in this machine's helper and komorebic paths. Zebar only lets
#     a widget run a program whose path matches its zpack.json entry
#     exactly, so the paths can't be generic.
#  3. Sets Zebar to start the bar and its tooltip widget (backing up
#     settings.json first), then restarts Zebar.
# Re-running is safe and is how you update after pulling a new version.
# Keep this file ASCII-only (Windows PowerShell 5.1 misreads non-ASCII).

param(
    [switch]$KeepOtherWidgets,
    [string]$KomorebicPath
)
$ErrorActionPreference = 'Stop'

$Here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$ZebarDir  = Join-Path $env:USERPROFILE '.glzr\zebar'
$PackDir   = Join-Path $ZebarDir 'midnight-eclipse'
$HelperDir = Join-Path $env:LOCALAPPDATA 'midnight-eclipse\bin'
$Utf8      = New-Object System.Text.UTF8Encoding $false

# --- prerequisites -----------------------------------------------------------
$zebarExe = Join-Path $env:ProgramFiles 'glzr.io\Zebar\zebar.exe'
if (-not (Test-Path $zebarExe)) {
    $cmd = Get-Command zebar.exe -ErrorAction SilentlyContinue
    if ($cmd) { $zebarExe = $cmd.Source } else { $zebarExe = $null }
}
if (-not $zebarExe) { Write-Warning 'Zebar not found. Install it (https://github.com/glzr-io/zebar, v3.3 or later) and re-run.' }

if (-not $KomorebicPath) {
    $cmd = Get-Command komorebic.exe -ErrorAction SilentlyContinue
    if ($cmd) { $KomorebicPath = $cmd.Source }
    elseif (Test-Path (Join-Path $env:ProgramFiles 'komorebi\bin\komorebic.exe')) { $KomorebicPath = Join-Path $env:ProgramFiles 'komorebi\bin\komorebic.exe' }
}
if (-not $KomorebicPath -or -not (Test-Path $KomorebicPath)) {
    throw 'komorebic.exe not found. Install komorebi (winget install LGUG2Z.komorebi) or pass -KomorebicPath.'
}
$KomorebicPath = (Resolve-Path $KomorebicPath).Path

$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "The .NET Framework 4 C# compiler was not found at $csc (it ships with Windows 10/11)." }

# --- stop Zebar (it holds the helpers open) -----------------------------------
$zebarWasRunning = [bool](Get-Process zebar -ErrorAction SilentlyContinue)
if ($zebarWasRunning) {
    Write-Host 'stopping Zebar...'
    Get-Process zebar -ErrorAction SilentlyContinue | Stop-Process -Force
    # The helpers exit when Zebar closes their input; give them a moment.
    $deadline = (Get-Date).AddSeconds(5)
    while ((Get-Process taskbar-mirror -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
}

# --- 1. helpers ----------------------------------------------------------------
Write-Host "building helpers into $HelperDir"
& (Join-Path $Here 'helpers\taskbar-mirror\build.ps1') -OutDir $HelperDir

# --- 2. pack -------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $PackDir | Out-Null
# Paths go into a JS string (index.html) and a JSON string (zpack.json):
# both need backslashes doubled.
$helpersEscaped   = $HelperDir.Replace('\', '\\')
$komorebicEscaped = $KomorebicPath.Replace('\', '\\')
foreach ($file in Get-ChildItem (Join-Path $Here 'pack') -File) {
    $target = Join-Path $PackDir $file.Name
    if ($file.Name -in 'index.html', 'zpack.json') {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
        $text = $text.Replace('__HELPERS__', $helpersEscaped).Replace('__KOMOREBIC__', $komorebicEscaped)
        [System.IO.File]::WriteAllText($target, $text, $Utf8)
    } else {
        Copy-Item $file.FullName $target -Force
    }
}
Write-Host "installed the pack to $PackDir"

# --- 3. Zebar startup widgets ---------------------------------------------------
$settingsPath = Join-Path $ZebarDir 'settings.json'
$ours = @(
    [ordered]@{ pack = 'midnight-eclipse'; widget = 'bar'; preset = 'default' },
    [ordered]@{ pack = 'midnight-eclipse'; widget = 'tooltip'; preset = 'default' }
)
$startup = @()
if (Test-Path $settingsPath) {
    Copy-Item $settingsPath "$settingsPath.bak" -Force
    Write-Host "backed up settings.json to $settingsPath.bak"
    if ($KeepOtherWidgets) {
        $existing = (Get-Content $settingsPath -Raw | ConvertFrom-Json).startupConfigs
        $startup = @($existing | Where-Object { $_.pack -ne 'midnight-eclipse' })
    }
}
$settings = [ordered]@{
    '$schema'      = 'https://github.com/glzr-io/zebar/raw/v3.3.1/resources/settings-schema.json'
    startupConfigs = @($startup) + $ours
}
[System.IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5), $Utf8)
Write-Host 'Zebar will start the Midnight Eclipse bar and tooltip widgets'

# --- restart Zebar -------------------------------------------------------------
if ($zebarExe) {
    # Launched through Explorer so Zebar isn't tied to this console window.
    Start-Process explorer.exe "`"$zebarExe`""
    Write-Host 'started Zebar'
}

Write-Host ''
Write-Host 'Done. For the bar to sit above your windows instead of under them, komorebi needs'
Write-Host '  "global_work_area_offset": { "left": 0, "top": 44, "right": 0, "bottom": 44 }'
Write-Host 'and an ignore rule for zebar.exe in komorebi.json (see README.md).'
