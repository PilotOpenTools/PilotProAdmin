<#
  Регистрирует ежедневный бэкап в Планировщике заданий Windows (права администратора не нужны).

    .\backup\register-task.ps1                     каждый день в 03:00
    .\backup\register-task.ps1 -At 22:30 -Root E:\Backups
    .\backup\register-task.ps1 -Remove

  Задание «CICD Backup» работает от текущего пользователя (ему нужен Docker Desktop),
  только при входе в систему - Docker Desktop без сеанса не запущен. Пропущенный запуск догоняется.
#>
param(
    [string]$At = '03:00',
    [string]$Root = 'D:\Backups',
    [int]$KeepDays = 14,
    [switch]$Remove
)
$ErrorActionPreference = 'Stop'
$name = 'CICD Backup'

if ($Remove) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; Write-Host "Задание '$name' удалено"; return }

$script = Join-Path $PSScriptRoot 'backup.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`" -Root `"$Root`" -KeepDays $KeepDays" `
    -WorkingDirectory (Split-Path $PSScriptRoot -Parent)
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Hours 2) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
Write-Host "Задание '$name': ежедневно в $At -> $Root (хранить $KeepDays дн.)"
Write-Host "Запустить сейчас: Start-ScheduledTask -TaskName '$name'"
