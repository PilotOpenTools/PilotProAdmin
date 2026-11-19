# Быстрый старт на Windows

Для запуска без Docker используйте [Windows Portable](https://pilotopentools.github.io/PilotProAdmin/windows-portable.html).

1. Установите и запустите [Docker Desktop для Windows](https://docs.docker.com/desktop/setup/install/windows-install/) на компьютере x64. Используйте режим Linux containers. В PowerShell команда `docker info` должна показывать сведения о сервере Docker.
2. Скачайте и распакуйте `PilotProAdmin-vX.Y.Z-lite.zip` со [страницы релизов](https://github.com/PilotOpenTools/PilotProAdmin/releases). Внутри два файла: `README.md` и `install.ps1`. Сверьте SHA-256 архива с `SHA256SUMS.txt` через `Get-FileHash`.
3. Откройте PowerShell в распакованной папке и выполните:

   ```powershell
   .\install.ps1
   ```

   Если Windows заблокировала загруженный скрипт, после проверки архива выполните `Unblock-File -LiteralPath .\install.ps1`.
4. Ответьте на вопросы установщика. Если порт занят, нажмите Enter, чтобы принять предложенный свободный порт, или укажите свой. Установщик запускает PilotProAdmin и PilotMigration одной версии и выводит адреса сервисов.
5. Откройте адрес админки из вывода скрипта. В мастере первого входа проверьте адрес GitLab и введите установочный ключ `ADMIN_SETUP_KEY` из `PilotProAdmin-install\.env`. Войдите с Personal Access Token GitLab с правом `api`. Затем проверьте адреса контуров Pilot, задайте служебные учётные записи для выкладки из GitLab CI, проверьте адрес Grafana и укажите группы и проекты GitLab.

## Откуда взять образы

Lite скачивает PilotProAdmin и PilotMigration из GHCR. Если на целевой машине GHCR недоступен, скачайте также `PilotProAdmin-vX.Y.Z-linux-amd64-images.tar.gz` и проверьте его по `SHA256SUMS-CONTAINERS.txt`. Положите архив рядом с `install.ps1` или передайте полный путь:

```powershell
.\install.ps1 -ImageArchive 'D:\Downloads\PilotProAdmin-vX.Y.Z-linux-amd64-images.tar.gz'
```

Архив содержит оба приложения. Для локального GitLab, статистики и серверов Pilot нужны доступ к их реестрам и зависимости сборщиков либо заранее подготовленные образы. Для полностью офлайн установки полного стенда используйте [отдельный комплект стека](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/installation.html).

## Чистая установка

- Для GitLab выберите существующий экземпляр или установку на этой машине. Для нового GitLab начальный пароль `root` находится в `GITLAB_ROOT_PASSWORD` в `.env`. Создайте токен с правом `api` и группы для нужных типов проектов. Если вход выполняет пользователь без прав администратора GitLab, у него должен быть доступ хотя бы к одной из групп, указанных в мастере.
- Для статистики можно подключить Grafana, развернуть локальную Grafana со сборщиками или настроить позже. Локальные сборщики в этой схеме используют PostgreSQL рабочего контура Pilot.
- Для каждого контура Pilot (тест, стейдж, прод) можно подключить существующий сервер, развернуть локальный Pilot-Server с PostgreSQL и RustFS или отложить настройку. Для скачивания образа Pilot может потребоваться доступ к реестру поставщика. Свой образ можно указать через `-PilotImage`. Для нового сервера установите лицензию и подключите базу в Pilot-myAdmin. После этого добавьте имя базы к адресу контура в админке. [Установка Pilot-Server](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/installation.html).
- Учётные записи для выкладки модулей из CI можно изменить или отключить в разделе «Настройки». Для работы CI также нужны ключи выкладки в переменных групп GitLab. При подключении внешнего Pilot доступ к PostgreSQL и архивам для PilotMigration настраивается отдельно. [Настройка стека и интеграций](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/installation.html).

## Установка из резервной копии

На исходной машине откройте «Миграция → Подготовить перенос данных» в админке. Скачайте комплект, распакуйте его рядом с доступным Docker Desktop и запустите `prepare-transfer.ps1` по инструкции из комплекта. Скрипт напечатает путь к папке с `manifest.json`.

Выберите восстановление и укажите каталог полной резервной копии с `manifest.json`, либо передайте его при запуске:

```powershell
.\install.ps1 -Backup 'D:\Backup\2026-11-18_0300' -InstallDirectory 'D:\PilotProAdmin'
```

Для восстановления выберите новый каталог установки. Скрипт поднимает локальные GitLab, Grafana со сборщиками и три контура Pilot вместе с PostgreSQL и RustFS. Он берёт версию и редакцию GitLab из имени штатного архива резервной копии. [Требования GitLab к восстановлению](https://docs.gitlab.com/administration/backup_restore/restore_gitlab/).

После установки сохраните каталог `PilotProAdmin-install` или выбранный через `-InstallDirectory`. В нём остаются Compose, `.env` с реквизитами и папки данных. Часть данных хранится в томах Docker. При обновлении распакуйте новый lite и запустите установщик с тем же `-InstallDirectory`. Для переноса кластера используйте [инструкцию по кластерной установке](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/cluster-installation.html).
