<#
  Регистрирует ежедневную резервную копию стенда в Планировщике заданий Windows (права администратора не нужны).
  Копию снимает backup.ps1 из этой же папки, поэтому держите распакованный комплект в постоянном месте.

    .\register-task.ps1 -InstallDirectory 'D:\PilotProAdmin\PilotProAdmin-install'     каждый день в 03:00 в D:\Backups
    .\register-task.ps1 -InstallDirectory '...' -At 22:30 -Root E:\Backups -KeepDays 30
    .\register-task.ps1 -StackDirectory 'C:\CICD\stack'                                раздельная раскладка (prod, test, stage, metrics)
    .\register-task.ps1 -Remove

  Задание «PilotProAdmin Backup» работает от текущего пользователя (ему нужен Docker Desktop) и только при входе в систему:
  без сеанса Docker Desktop не запущен. Пропущенный запуск догоняется. Прежнее задание «CICD Backup» (stack\backup\register-task.ps1
  старых версий) скрипт заменяет: оно вызывало скрипт, которого в релизе 1.1.1 больше нет.
  Проверка: Get-ScheduledTask -TaskName 'PilotProAdmin Backup' | Get-ScheduledTaskInfo, итог последнего запуска - <Root>\last-backup.json.
#>
[CmdletBinding(DefaultParameterSetName = 'Installed')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Installed')][string]$InstallDirectory,
    [Parameter(Mandatory, ParameterSetName = 'Split')][string]$StackDirectory,
    [Parameter(ParameterSetName = 'Installed')][Parameter(ParameterSetName = 'Split')][string]$At = '03:00',
    [Parameter(ParameterSetName = 'Installed')][Parameter(ParameterSetName = 'Split')][string]$Root = 'D:\Backups',
    [Parameter(ParameterSetName = 'Installed')][Parameter(ParameterSetName = 'Split')][int]$KeepDays = 14,
    [Parameter(Mandatory, ParameterSetName = 'Remove')][switch]$Remove
)
$ErrorActionPreference = 'Stop'
$name = 'PilotProAdmin Backup'
$legacy = 'CICD Backup'

function Remove-Task([string]$taskName) {
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
        Write-Host "Задание '$taskName' удалено"
    }
}

if ($Remove) { Remove-Task $name; return }

$script = Join-Path $PSScriptRoot 'backup.ps1'
if (-not (Test-Path -LiteralPath $script -PathType Leaf)) { throw "Рядом нет backup.ps1: $script" }
if ($InstallDirectory) {
    $dir = (Resolve-Path -LiteralPath $InstallDirectory).Path
    if (-not (Test-Path -LiteralPath (Join-Path $dir '.env') -PathType Leaf)) { throw "В $dir нет .env: укажите папку установки, где лежат .env и docker-compose.yml." }
    $target = "-InstallDirectory `"$dir`""
} else {
    $dir = (Resolve-Path -LiteralPath $StackDirectory).Path
    $target = "-StackDirectory `"$dir`""
}
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`" $target -Root `"$Root`" -KeepDays $KeepDays" `
    -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Remove-Task $legacy
Write-Host "Задание '$name': ежедневно в $At -> $Root (хранить $KeepDays дн.), стенд: $dir"
Write-Host "Запустить сейчас: Start-ScheduledTask -TaskName '$name'"
