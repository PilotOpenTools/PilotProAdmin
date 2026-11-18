# Быстрый старт

PilotProAdmin подключается к действующему Pilot-Server. Админка и сервис миграции поставляются вместе. Сервис миграции можно запускать отдельно, если нужны импорт, экспорт или копирование баз.

## Windows x64

Скачайте архив на странице [релизов](https://github.com/PilotOpenTools/PilotProAdmin/releases/latest) и распакуйте.

| Архив | Требования |
| --- | --- |
| `PilotProAdmin-vX.Y.Z-win-x64-lite.zip` | Установлен ASP.NET Core Runtime 10 x64 |
| `PilotProAdmin-vX.Y.Z-win-x64-portable.zip` | Среда .NET включена в архив |

Сверьте SHA-256 ZIP с `SHA256SUMS.txt`. В PowerShell 7 откройте каталог архива. Для подключения первого Pilot задайте свой установочный ключ и запустите приложение.

```powershell
$env:App__SetupKey = Read-Host 'Установочный ключ PilotProAdmin' -MaskInput
Set-Location PilotProAdmin
.\PilotProAdmin.exe --urls http://127.0.0.1:5300
```

Откройте <http://127.0.0.1:5300> и войдите учётной записью администратора первой базы Pilot. Для миграции запустите `PilotMigration/PilotMigration.exe` с `--urls http://127.0.0.1:5320` и настройте общий ключ [по инструкции](configuration.md#миграция).

## Docker Linux amd64

Скачайте публичный образ из GitHub Container Registry. Вход в GitHub не требуется.

```bash
docker pull ghcr.io/pilotopentools/pilotproadmin:1.0.1
```

Задайте установочный ключ и запустите админку.

```bash
read -rsp 'Установочный ключ PilotProAdmin: ' App__SetupKey
printf '\n'
export App__SetupKey
docker run --detach --name pilotproadmin \
  --publish 127.0.0.1:5300:8080 \
  --volume pilotproadmin-data:/data \
  --env App__SetupKey \
  ghcr.io/pilotopentools/pilotproadmin:1.0.1
unset App__SetupKey
```

Ввод ключа после `read` скрыт. Откройте <http://127.0.0.1:5300>. Укажите адрес Pilot, доступный из контейнера. Если Pilot запущен на той же машине, адрес зависит от настроек сети Docker.

### Сервис миграции

Если нужен импорт, экспорт или перенос базы, скачайте образ сервиса.

```bash
docker pull ghcr.io/pilotopentools/pilotproadmin-migration:1.0.1
```

Выделите сервису отдельный том, подключите его к сети админки и задайте общий ключ API. При обновлении используйте одинаковый тег для обоих образов. [Настройка миграции](configuration.md#миграция).

### Офлайн-установка

На машине с интернетом скачайте `PilotProAdmin-v1.0.1-linux-amd64-images.tar.gz` и `SHA256SUMS-CONTAINERS.txt` со [страницы релиза](https://github.com/PilotOpenTools/PilotProAdmin/releases/tag/v1.0.1). Перенесите оба файла на сервер, проверьте контрольную сумму и загрузите образы.

```bash
sha256sum --check SHA256SUMS-CONTAINERS.txt
docker load --input PilotProAdmin-v1.0.1-linux-amd64-images.tar.gz
```

Запустите админку той же командой `docker run`. Архив содержит оба образа с тегом `1.0.1`.

## Кластер Pilot

Скачайте `PilotProAdmin-v1.0.1-cluster.tar.gz` и `SHA256SUMS-CLUSTER.txt` со [страницы релиза](https://github.com/PilotOpenTools/PilotProAdmin/releases/tag/v1.0.1). Сверьте контрольную сумму и распакуйте архив на узлах Linux.

```bash
sha256sum --check SHA256SUMS-CLUSTER.txt
tar -xzf PilotProAdmin-v1.0.1-cluster.tar.gz
```

Комплект содержит скрипты и Compose для узлов и балансировщика. Подготовьте образ Pilot-Server из своего дистрибутива, PostgreSQL, Redis и общий файловый архив. [Установка кластера](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/cluster-installation.html)

## Полный стенд

Шаблоны Compose для контуров Pilot, GitLab и мониторинга, варианты ролей и сборка офлайн-комплекта описаны в [инструкции по установке](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/installation.html). Для сборки комплекта нужны исходные образы и файлы вашего дистрибутива Pilot. [Варианты развёртывания](https://pilotopentools.github.io/PilotProAdmin/deployment.html).
