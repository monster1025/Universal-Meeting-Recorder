<#
.SYNOPSIS
  Устанавливает (или обновляет) Universal Meeting Recorder в %LOCALAPPDATA%\Programs\UniversalMeetingRecorder
  и запускает его. Работающий экземпляр сначала завершается: заменять exe под живым процессом нельзя.
  Путь установки стабилен — от него зависят автозапуск (HKCU\...\Run) и исключение собственного
  процесса из детекции микрофона, поэтому запускать приложение из папки сборки не стоит.
.EXAMPLE
  pwsh tools/install.ps1                    # взять dist/win-x64/UniversalMeetingRecorder.exe
  pwsh tools/install.ps1 -Source D:\UniversalMeetingRecorder.exe
#>
param(
    [string]$Source = (Join-Path (Split-Path -Parent $PSScriptRoot) "dist/win-x64/UniversalMeetingRecorder.exe"),
    [switch]$NoLaunch
)
$ErrorActionPreference = "Stop"
if (-not (Test-Path $Source)) { throw "Нет файла $Source — сначала tools/publish.ps1" }

$target = Join-Path $env:LOCALAPPDATA "Programs\UniversalMeetingRecorder"
New-Item -ItemType Directory -Force -Path $target | Out-Null

$running = Get-Process -Name UniversalMeetingRecorder -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    Write-Host "Завершаю работающий экземпляр…"
    # CloseMainWindow у трей-приложения нет окна — мягкого способа нет; Stop-Process даёт
    # приложению шанс не успеть свести запись, но восстановление подхватит её на следующем старте.
    $running | Stop-Process -Force
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

Copy-Item -Force $Source (Join-Path $target "UniversalMeetingRecorder.exe")
Write-Host "Установлено в $target"

# Ярлык в меню «Пуск» — по нему Windows находит имя и иконку для уведомлений.
$shortcut = Join-Path ([Environment]::GetFolderPath("Programs")) "UniversalMeetingRecorder.lnk"
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut($shortcut)
$lnk.TargetPath = Join-Path $target "UniversalMeetingRecorder.exe"
$lnk.WorkingDirectory = $target
$lnk.Save()

if (-not $NoLaunch) { Start-Process (Join-Path $target "UniversalMeetingRecorder.exe") }
