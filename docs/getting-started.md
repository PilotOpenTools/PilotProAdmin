# Быстрый старт

PilotProAdmin подключается к действующему Pilot-Server. Админка и сервис миграции поставляются вместе. Сервис миграции можно запускать отдельно, если нужны импорт, экспорт или копирование баз.

## Windows x64

Скачайте архив на странице [релизов](https://github.com/PilotOpenTools/PilotProAdmin/releases/latest) и распакуйте.

| Архив | Требования |
| --- | --- |
| `PilotProAdmin-vX.Y.Z-win-x64-lite.zip` | Установлен ASP.NET Core Runtime 10 x64 |
| `PilotProAdmin-vX.Y.Z-win-x64-portable.zip` | Среда .NET включена в архив |

Сверьте SHA-256 ZIP с `SHA256SUMS.txt`. В PowerShell откройте каталог архива. Для подключения первого Pilot задайте свой установочный ключ и запустите приложение.

```powershell
$env:App__SetupKey = Read-Host 'Установочный ключ PilotProAdmin' -MaskInput
Set-Location PilotProAdmin
.\PilotProAdmin.exe --urls http://127.0.0.1:5300
```

Откройте <http://127.0.0.1:5300> и войдите учётной записью администратора первой базы Pilot. Для миграции запустите `PilotMigration/PilotMigration.exe` с `--urls http://127.0.0.1:5320` и настройте общий ключ [по инструкции](configuration.md#миграция).

## Docker Linux amd64

```bash
read -rs App__SetupKey
export App__SetupKey
docker run --detach --name pilotproadmin \
  --publish 127.0.0.1:5300:8080 \
  --volume pilotproadmin-data:/data \
  --env App__SetupKey \
  ghcr.io/pilotopentools/pilotproadmin:1.0.0
unset App__SetupKey
```

Ввод ключа после `read` скрыт. Откройте <http://127.0.0.1:5300>. Укажите адрес Pilot, доступный из контейнера. Если Pilot запущен на той же машине, адрес зависит от настроек сети Docker.

Образ сервиса миграции: `ghcr.io/pilotopentools/pilotproadmin-migration:1.0.0`. Для него выделите отдельный том и подключите его к сети админки. При обновлении укажите одинаковую версию тега для обоих образов.

## Полный стенд

Шаблоны Compose для контуров Pilot, GitLab и мониторинга, варианты ролей и сборка офлайн-комплекта описаны в [инструкции по установке](https://github.com/PilotOpenTools/PilotProAdmin/tree/main/stack/Install). Для сборки комплекта нужны исходные образы и файлы вашего дистрибутива Pilot.
