<#
  Совместимость: прежняя точка входа ежедневного бэкапа.

  Задание «CICD Backup», зарегистрированное старым stack\backup\register-task.ps1, вызывает этот файл по абсолютному пути.
  Сам бэкап теперь живёт в proadmin\transfer\backup.ps1 (тот же скрипт входит в комплект «Миграция → Подготовить перенос данных»),
  этот файл передаёт ему все параметры. Перерегистрируйте задание новым скриптом и удалите эту обёртку в следующем релизе:

    ..\..\proadmin\transfer\register-task.ps1 -StackDirectory <папка stack>      (раздельная раскладка)
    ..\..\proadmin\transfer\register-task.ps1 -InstallDirectory <папка установки>   (установка из релиза)
#>
$ErrorActionPreference = 'Stop'
$target = Join-Path $PSScriptRoot '..\..\proadmin\transfer\backup.ps1'
if (-not (Test-Path -LiteralPath $target -PathType Leaf)) { throw "Нет ${target}: обновите checkout целиком или перерегистрируйте задание (proadmin\transfer\register-task.ps1)." }
Write-Warning 'stack\backup\backup.ps1 устарел: бэкап выполняет proadmin\transfer\backup.ps1. Перерегистрируйте задание: proadmin\transfer\register-task.ps1.'
$forward = @{ StackDirectory = (Split-Path $PSScriptRoot -Parent) }
for ($i = 0; $i -lt $args.Count; $i++) {
    if ("$($args[$i])" -match '^-(\w+)$') {
        $key = $Matches[1]
        if ($i + 1 -lt $args.Count -and "$($args[$i + 1])" -notmatch '^-\w+$') { $forward[$key] = $args[$i + 1]; $i++ } else { $forward[$key] = $true }
    }
}
if ($forward.Skip -is [string]) { $forward.Skip = @($forward.Skip -split ',' | Where-Object { $_ }) }
& $target @forward
exit $LASTEXITCODE
