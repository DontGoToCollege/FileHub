# Publishes FileHub as a self-contained, single-file x64 executable.
# Usage:  .\publish.ps1            -> dist\FileHub.exe
#         .\publish.ps1 -Run       -> publish and launch
param([switch]$Run)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$proj = Join-Path $root "src\FileHub\FileHub.csproj"
$out  = Join-Path $root "dist"

Write-Host "Publishing FileHub (Release, win-x64, self-contained, single file)..." -ForegroundColor Cyan
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $out
if ($LASTEXITCODE -ne 0) { throw "publish failed" }

$exe = Join-Path $out "FileHub.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host "Done: $exe ($size MB)" -ForegroundColor Green
if ($Run) { Start-Process $exe }
