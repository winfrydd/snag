<#
  Builds Snag and installs it into a folder, plus a Start menu shortcut.
  Settings in <Target>\data are kept on reinstall.

  Usage:  powershell -ExecutionPolicy Bypass -File install.ps1 [-Target "C:\Tools\Snag"] [-NoShortcut]
#>
param(
    [string]$Target = (Join-Path $env:LOCALAPPDATA 'Programs\Snag'),
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$out = Join-Path $repo 'publish'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
& $dotnet publish (Join-Path $repo 'src\Snag') -c Release -o $out --nologo -v q -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Get-Process Snag -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$Target*" } | ForEach-Object {
    $_.CloseMainWindow() | Out-Null; $_.WaitForExit(5000) | Out-Null
    if (-not $_.HasExited) { throw 'Snag is still running (downloads active?). Close it and run this again.' }
}

New-Item -ItemType Directory -Force $Target | Out-Null
# Replace program files, keep data\ (settings) and tools\ (optional bundled yt-dlp/ffmpeg)
Get-ChildItem $Target | Where-Object { $_.Name -notin 'data', 'tools' } | Remove-Item -Recurse -Force
Copy-Item (Join-Path $out '*') $Target -Recurse -Force
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $Target 'LICENSE.txt') -Force
Remove-Item $out -Recurse -Force

if (-not $NoShortcut) {
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Snag.lnk'
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = Join-Path $Target 'Snag.exe'
    $s.WorkingDirectory = $Target
    $s.Description = 'Download video and audio'
    $s.Save()
    Write-Host "Start menu shortcut: $lnk"
}
Write-Host "Installed to $Target"
