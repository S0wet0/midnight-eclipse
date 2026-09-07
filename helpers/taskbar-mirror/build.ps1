# Builds taskbar-mirror.exe with the C# compiler that ships with Windows
# (.NET Framework 4.x), so no SDK install is needed. /target:winexe gives a
# windowless program, so nothing flashes when Zebar runs it.
#   build.ps1 [-OutDir <folder>]   (default: next to this script)
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $fw 'csc.exe'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = $here }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
# Note: the in-box compiler only supports C# 5.
& $csc /nologo /optimize+ /target:winexe `
  /r:System.Drawing.dll "/r:$fw\WPF\UIAutomationClient.dll" "/r:$fw\WPF\UIAutomationTypes.dll" "/r:$fw\WPF\WindowsBase.dll" `
  "/out:$OutDir\taskbar-mirror.exe" "$here\taskbar-mirror.cs"
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }
"built $OutDir\taskbar-mirror.exe"
