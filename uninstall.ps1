# Midnight Eclipse uninstaller. Run from a normal PowerShell:
#
#   powershell -NoProfile -ExecutionPolicy Bypass -File .\uninstall.ps1
#
# Stops Zebar, puts back any windows the bar had taken off the taskbar,
# removes the pack, the helpers and the startup entries, then restarts Zebar
# if other widgets are left to run. Keep this file ASCII-only.
$ErrorActionPreference = 'Stop'

$ZebarDir  = Join-Path $env:USERPROFILE '.glzr\zebar'
$PackDir   = Join-Path $ZebarDir 'midnight-eclipse'
$DataDir   = Join-Path $env:LOCALAPPDATA 'midnight-eclipse'
$Mirror    = Join-Path $DataDir 'bin\taskbar-mirror.exe'
$Utf8      = New-Object System.Text.UTF8Encoding $false

$zebarExe = (Get-Process zebar -ErrorAction SilentlyContinue | Select-Object -First 1).Path
Get-Process zebar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 1
Get-Process taskbar-mirror -ErrorAction SilentlyContinue | Stop-Process -Force

# Windows on hidden workspaces may still be off the taskbar if a helper was
# killed; its state file says which. Put them back before deleting it.
if (Test-Path $Mirror) { & $Mirror restore; Write-Host 'restored taskbar entries' }

$settingsPath = Join-Path $ZebarDir 'settings.json'
$remaining = @()
if (Test-Path $settingsPath) {
    Copy-Item $settingsPath "$settingsPath.bak" -Force
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $remaining = @($settings.startupConfigs | Where-Object { $_.pack -ne 'midnight-eclipse' })
    $settings.startupConfigs = $remaining
    [System.IO.File]::WriteAllText($settingsPath, ($settings | ConvertTo-Json -Depth 5), $Utf8)
    Write-Host 'removed Midnight Eclipse from Zebar startup widgets (backup: settings.json.bak)'
}

foreach ($dir in $PackDir, $DataDir) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force; Write-Host "removed $dir" }
}

if ($zebarExe -and $remaining.Count -gt 0) { Start-Process explorer.exe "`"$zebarExe`""; Write-Host 'restarted Zebar for your other widgets' }
Write-Host 'Done.'
