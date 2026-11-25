<#
  Совместимость: прежняя точка входа ежедневного бэкапа.

  Задание «CICD Backup», зарегистрированное старым stack\backup\register-task.ps1, вызывает этот файл по абсолютному пути.
  Сам бэкап живёт в proadmin\transfer\backup.ps1 (тот же скрипт входит в комплект «Миграция → Подготовить перенос данных»),
  этот файл передаёт ему свои параметры, в том числе позиционные (backup.ps1 'E:\Backups' 30). Перерегистрируйте задание
  новым скриптом; обёртка будет удалена в следующем релизе:

    ..\..\proadmin\transfer\register-task.ps1 -StackDirectory <папка stack>      (раздельная раскладка)
    ..\..\proadmin\transfer\register-task.ps1 -InstallDirectory <папка установки>   (установка из релиза)
#>
param(
    [string]$Root,
    [int]$KeepDays,
    [string[]]$Skip,
    [string]$DataRoot,
    [string]$Project,
    [string]$TarImage
)
$script = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) 'proadmin/transfer/backup.ps1'
Write-Warning 'stack\backup\backup.ps1 устарел: бэкап выполняет proadmin\transfer\backup.ps1. Перерегистрируйте задание: proadmin\transfer\register-task.ps1.'
$PSBoundParameters['StackDirectory'] = Split-Path $PSScriptRoot -Parent
& $script @PSBoundParameters
exit $LASTEXITCODE
