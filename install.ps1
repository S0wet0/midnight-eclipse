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
#   -NoAutostart        don't start Zebar at logon (skipped automatically if
#                       the komorebi Midnight Eclipse preset's autostart is
#                       installed, since that already starts Zebar)
#
# What it does:
#  1. Builds the two helper programs (taskbar-mirror, power-status) from
#     source with the C# compiler built into Windows, into
#     %LOCALAPPDATA%\midnight-eclipse\bin. No prebuilt .exe is shipped.
#  2. Copies the Zebar pack to %USERPROFILE%\.glzr\zebar\midnight-eclipse,
#     filling in this machine's helper and komorebic paths. Zebar only lets
#     a widget run a program whose path matches its zpack.json entry
#     exactly, so the paths can't be generic.
#  3. Sets Zebar to open the bar and its tooltip widget (backing up
#     settings.json first), then restarts Zebar.
#  4. Starts Zebar at logon (a per-user Run entry). Zebar has no setting of
#     its own for this, so without it the bar would be gone after a reboot.
# Re-running is safe and is how you update after pulling a new version.
# Keep this file ASCII-only (Windows PowerShell 5.1 misreads non-ASCII).

param(
    [switch]$KeepOtherWidgets,
    [string]$KomorebicPath,
    [switch]$NoAutostart
)
$ErrorActionPreference = 'Stop'

$Here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$ZebarDir  = Join-Path $env:USERPROFILE '.glzr\zebar'
$PackDir   = Join-Path $ZebarDir 'midnight-eclipse'
$HelperDir = Join-Path $env:LOCALAPPDATA 'midnight-eclipse\bin'
$RunKey    = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$RunName   = 'Midnight Eclipse (Zebar)'
$Utf8     = New-Object System.Text.UTF8Encoding $false

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
    while ((Get-Process taskbar-mirror, power-status -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 200 }
}

# --- 1. helpers ----------------------------------------------------------------
Write-Host "building helpers into $HelperDir"
& (Join-Path $Here 'helpers\taskbar-mirror\build.ps1') -OutDir $HelperDir
& (Join-Path $Here 'helpers\power-status\build.ps1') -OutDir $HelperDir

# --- 2. pack -------------------------------------------------------------------
New-Item -ItemType Directory -Force -Path $PackDir | Out-Null
# The paths go into a single-quoted JS string (index.html) and a JSON string
# (zpack.json). Both need backslashes doubled; the JS string also needs
# apostrophes escaped (a profile like C:\Users\O'Brien), which JSON must not
# get. Windows paths can't contain double quotes.
function Escape-Js($path)   { $path.Replace('\', '\\').Replace("'", "\'") }
function Escape-Json($path) { $path.Replace('\', '\\') }
foreach ($file in Get-ChildItem (Join-Path $Here 'pack') -File) {
    $target = Join-Path $PackDir $file.Name
    if ($file.Name -in 'index.html', 'zpack.json') {
        $text = [System.IO.File]::ReadAllText($file.FullName, [System.Text.Encoding]::UTF8)
        if ($file.Name -eq 'index.html') { $helpers = Escape-Js $HelperDir; $komorebic = Escape-Js $KomorebicPath }
        else { $helpers = Escape-Json $HelperDir; $komorebic = Escape-Json $KomorebicPath }
        $text = $text.Replace('__HELPERS__', $helpers).Replace('__KOMOREBIC__', $komorebic)
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
Write-Host 'Zebar will open the Midnight Eclipse bar and tooltip widgets'

# --- 4. start Zebar at logon ---------------------------------------------------
# The preset's autostart (a scheduled task) already starts Zebar, after
# komorebi is ready; a second launcher would only start it twice.
$presetAutostart = $false
try { $presetAutostart = [bool](Get-ScheduledTask -TaskPath '\komorebi-desktop\' -TaskName 'User' -ErrorAction Stop) } catch { }
if ($NoAutostart -or $presetAutostart -or -not $zebarExe) {
    Remove-ItemProperty -Path $RunKey -Name $RunName -ErrorAction SilentlyContinue
    if ($presetAutostart) { Write-Host 'Zebar is started at logon by the komorebi preset autostart; no separate entry added' }
    elseif ($zebarExe) { Write-Host 'not starting Zebar at logon (-NoAutostart)' }
} else {
    # Never New-Item -Force here: on an existing registry key it recreates the
    # key empty, deleting every other program's startup entry.
    if (-not (Test-Path $RunKey)) { New-Item -Path $RunKey | Out-Null }
    Set-ItemProperty -Path $RunKey -Name $RunName -Value "`"$zebarExe`""
    Write-Host 'Zebar will start at logon'
}

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
