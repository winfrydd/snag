<#
  Builds the release zip: dist\Snag-<version>.zip
  Used by the GitHub release workflow; also works locally.

  Usage:  powershell -ExecutionPolicy Bypass -File build-release.ps1 -Version 1.0.0
#>
param([Parameter(Mandatory = $true)][string]$Version)
$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$out = Join-Path $repo 'publish'
$dist = Join-Path $repo 'dist'
foreach ($d in $out, $dist) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

& $dotnet publish (Join-Path $repo 'src\Snag') -c Release -o $out --nologo -v q `
    "-p:Version=$Version" -p:ContinuousIntegrationBuild=true
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $out 'LICENSE.txt')
New-Item -ItemType Directory $dist | Out-Null
$zip = Join-Path $dist "Snag-$Version.zip"
Compress-Archive (Join-Path $out '*') $zip
Write-Host "Built $zip"
