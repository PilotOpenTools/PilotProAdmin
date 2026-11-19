# Windows Portable

Архив `PilotProAdmin-vX.Y.Z-win-x64-portable.zip` содержит PilotProAdmin и PilotMigration для Windows x64. Среда выполнения .NET включена. Сверьте SHA-256 архива с `SHA256SUMS.txt` и распакуйте его.

Для запуска админки откройте PowerShell в корне распакованной папки. Укажите адрес группы автоматизаций и задайте свой установочный ключ.

```powershell
$env:App__Defaults__GitLabGroupUrl = 'https://gitlab.example.com/automation'
$env:App__SetupKey = Read-Host 'Ключ установки' -MaskInput
Set-Location PilotProAdmin
.\PilotProAdmin.exe --urls http://127.0.0.1:5300
```

Откройте <http://127.0.0.1:5300>, войдите через GitLab и выберите «Подключить Pilot». Для скрытого ввода через `-MaskInput` нужен PowerShell 7.

Если нужен сервис миграции, запустите `PilotMigration.exe --urls http://127.0.0.1:5320` из папки `PilotMigration` в другом окне PowerShell. Параметры адреса и общего ключа описаны в [конфигурации](configuration.md#миграция).
