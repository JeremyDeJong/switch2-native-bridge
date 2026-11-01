# Builds Switch2Bridge, runs the self-test, and writes the single-file exe to
# artifacts\release\Switch2Bridge-v<version>.exe.
#
# Usage: powershell -NoProfile -File tools\build.ps1
#
# The versioned name means a bridge that is running (and so locks its exe) never
# blocks the next build.

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe" }
if (-not (Test-Path $dotnet)) { throw "The .NET 10 SDK was not found. Install it from https://dot.net." }

$version = ([xml](Get-Content Switch2Bridge.csproj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
Write-Host "==> Switch2Bridge $version" -ForegroundColor Cyan

Write-Host "==> Building and running the self-test..." -ForegroundColor Cyan
& $dotnet build -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
& $dotnet "artifacts\bin\Switch2Bridge\release_win-x64\Switch2Bridge.dll" --selftest
if ($LASTEXITCODE -ne 0) { throw "Self-test failed." }

Write-Host "==> Publishing the single-file exe..." -ForegroundColor Cyan
& $dotnet publish -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

$release = Join-Path $root "artifacts\release"
New-Item -ItemType Directory -Force $release | Out-Null
$exe = Join-Path $release "Switch2Bridge-v$version.exe"
Copy-Item "artifacts\publish\Switch2Bridge\release_win-x64\Switch2Bridge.exe" $exe -Force
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  Switch2Bridge-v$version.exe" | Set-Content "$exe.sha256" -Encoding ascii

$mb = (Get-Item $exe).Length / 1MB
Write-Host ("==> {0} ({1:N1} MB)" -f $exe, $mb) -ForegroundColor Green
Write-Host "    SHA-256 $hash"
