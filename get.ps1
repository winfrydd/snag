<#
  Snag installer / updater.

  Install or update (PowerShell):
    irm https://raw.githubusercontent.com/winfrydd/snag/main/get.ps1 | iex

  What it does:
    1. Makes sure the .NET 10 Desktop Runtime, yt-dlp and ffmpeg are installed (via winget, with your OK).
    2. Downloads the latest Snag release from GitHub.
    3. Installs it to %LOCALAPPDATA%\Programs\Snag (no admin rights needed).
       Running it again updates Snag and keeps your settings.
    4. Adds Snag to the Start menu and opens it.

  Uninstall:
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/winfrydd/snag/main/get.ps1))) -Uninstall
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\Snag'),
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$repo = 'winfrydd/snag'
$exe = Join-Path $InstallDir 'Snag.exe'
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'Snag.lnk'

function Stop-Running {
    $procs = Get-Process Snag -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, 'OrdinalIgnoreCase') }
    if ($procs) {
        Write-Host 'Closing Snag...'
        $procs | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        Start-Sleep -Seconds 2
        $procs | Where-Object { -not $_.HasExited } | Stop-Process -Force
    }
}

if ($Uninstall) {
    if (-not (Test-Path $exe)) { Write-Host "Snag isn't installed in $InstallDir"; return }
    Stop-Running
    Remove-Item $InstallDir -Recurse -Force
    if (Test-Path $lnk) { Remove-Item $lnk -Force }
    Write-Host "Removed $InstallDir. yt-dlp and ffmpeg were left installed (winget uninstall yt-dlp.yt-dlp Gyan.FFmpeg)."
    return
}

$hasWinget = [bool](Get-Command winget -ErrorAction SilentlyContinue)

# 1a. .NET Desktop Runtime 10+
function Test-Runtime {
    $roots = @("$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App",
               "$env:LOCALAPPDATA\Microsoft\dotnet\shared\Microsoft.WindowsDesktop.App")
    foreach ($r in $roots) {
        if (Test-Path $r) {
            foreach ($d in Get-ChildItem $r -Directory) {
                $v = $null
                if ([version]::TryParse(($d.Name -replace '-.*$', ''), [ref]$v) -and $v.Major -ge 10) { return $true }
            }
        }
    }
    return $false
}

if (-not (Test-Runtime)) {
    Write-Host ''
    Write-Host 'Snag needs the free Microsoft .NET 10 Desktop Runtime (about 55 MB).'
    if ($hasWinget) {
        $ok = Read-Host 'Install it now with winget? [Y/n]'
        if ($ok -notmatch '^[Nn]') {
            winget install --id Microsoft.DotNet.DesktopRuntime.10 -e --accept-package-agreements --accept-source-agreements
        }
    }
    if (-not (Test-Runtime)) {
        Write-Host "Get it here (choose '.NET Desktop Runtime', x64): https://dotnet.microsoft.com/download/dotnet/10.0"
        Write-Host 'Then run this installer again.'
        return
    }
}

# 1b. yt-dlp and ffmpeg
function Test-Tool($name) {
    if (Get-Command $name -ErrorAction SilentlyContinue) { return $true }
    $paths = [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + [Environment]::GetEnvironmentVariable('Path', 'Machine')
    foreach ($p in $paths.Split(';', [StringSplitOptions]::RemoveEmptyEntries)) {
        if (Test-Path (Join-Path $p "$name.exe")) { return $true }
    }
    return $false
}

$missing = @()
if (-not (Test-Tool 'yt-dlp')) { $missing += 'yt-dlp.yt-dlp' }
if (-not (Test-Tool 'ffmpeg')) { $missing += 'Gyan.FFmpeg' }
if ($missing.Count -gt 0) {
    Write-Host ''
    Write-Host "Snag uses yt-dlp (downloads) and ffmpeg (merging/converting). Missing: $($missing -join ', ')"
    if ($hasWinget) {
        $ok = Read-Host 'Install them now with winget? [Y/n]'
        if ($ok -notmatch '^[Nn]') {
            foreach ($id in $missing) { winget install --id $id -e --accept-package-agreements --accept-source-agreements }
        }
    }
    else {
        Write-Host 'Get them from https://github.com/yt-dlp/yt-dlp and https://ffmpeg.org, or point Snag to them after it opens.'
    }
}

# 2. Latest release
Write-Host 'Finding the latest Snag release...'
$headers = @{ 'User-Agent' = 'Snag-installer'; 'Accept' = 'application/vnd.github+json' }
$rel = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers $headers
$asset = $rel.assets | Where-Object { $_.name -like 'Snag-*.zip' } | Select-Object -First 1
if (-not $asset) { throw "The latest release ($($rel.tag_name)) has no Snag zip attached." }

$tmp = Join-Path ([IO.Path]::GetTempPath()) ('Snag-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $tmp | Out-Null
try {
    $zip = Join-Path $tmp $asset.name
    Write-Host "Downloading $($asset.name)..."
    Invoke-WebRequest $asset.browser_download_url -OutFile $zip -Headers $headers -UseBasicParsing
    $files = Join-Path $tmp 'files'
    Expand-Archive $zip $files

    # 3. Install (program files only; data\ with settings is left alone)
    $updating = Test-Path $exe
    Stop-Running
    New-Item -ItemType Directory -Force $InstallDir | Out-Null
    Copy-Item (Join-Path $files '*') $InstallDir -Recurse -Force
    if ($updating) { Write-Host "Updated to $($rel.tag_name) in $InstallDir" }
    else { Write-Host "Installed $($rel.tag_name) to $InstallDir" }
}
finally { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue }

# 4. Start menu + open
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.WorkingDirectory = $InstallDir
$s.Description = 'Download video and audio'
$s.Save()
Start-Process $exe
Write-Host "Done. Find 'Snag' in the Start menu."
