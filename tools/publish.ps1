<#
.SYNOPSIS
  Собирает Universal Meeting Recorder: один самодостаточный exe (рантайм .NET внутри).
.EXAMPLE
  pwsh tools/publish.ps1                 # Release, win-x64 → dist/win-x64
  pwsh tools/publish.ps1 -Runtime win-arm64
#>
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "dist/$Runtime"
dotnet publish (Join-Path $root "src/UniversalMeetingRecorder.App") `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish завершился с кодом $LASTEXITCODE" }
Write-Host "Готово: $out\UniversalMeetingRecorder.exe"
