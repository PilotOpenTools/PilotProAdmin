# Установка и устройство пакета

PilotProAdmin ставится из файлов [релиза на GitHub](https://github.com/PilotOpenTools/PilotProAdmin/releases/latest). Клонировать репозиторий и собирать что-то самому не нужно: всё, что нужно установщику, уже лежит в файлах релиза.

## Что есть в релизе

| Файл | Что это | Когда брать |
| --- | --- | --- |
| `PilotProAdmin-vX.Y.Z-lite.zip` | `install.ps1` и `README.md`. Образы админки и миграции установщик скачивает из GHCR | Обычная установка на Windows или Linux с Docker |
| `PilotProAdmin-vX.Y.Z-offline.zip` | Тот же lite плюс архив образов админки и миграции рядом с `install.ps1` | Машина без доступа к GHCR |
| `PilotProAdmin-vX.Y.Z-linux-amd64-images.tar.gz` | Только образы `pilotproadmin` и `pilotproadmin-migration` | Принести образы отдельно к уже скачанному lite (`-ImageArchive`) |
| `PilotProAdmin-vX.Y.Z-win-x64-portable.zip` | Админка одним exe для Windows, без Docker | Попробовать админку или подключить её к готовой инфраструктуре. [Windows Portable](windows-portable.md) |
| `PilotProAdmin-vX.Y.Z-cluster.tar.gz` | Compose узлов и балансировщика Pilot-Server, скрипт подготовки конфигурационной базы, инструкции | Кластер Pilot-Server на нескольких машинах. [Кластерный Pilot](../stack/Install/docs/cluster-installation.md) |
| `ServerHost-X.Y.Z.zip` | Загрузчик серверных модулей для Pilot-Server | Ставится из админки (страница «PilotOpenTools»), вручную нужен только без доступа к GitHub |
| `SHA256SUMS*.txt` | Контрольные суммы всех файлов | Сверьте архив перед установкой (`Get-FileHash`, `sha256sum`) |

Образы в GHCR: `ghcr.io/pilotopentools/pilotproadmin:X.Y.Z` и `ghcr.io/pilotopentools/pilotproadmin-migration:X.Y.Z`.

## Как устроен lite

`install.ps1` из lite — один файл. Внутри него, в виде zip-архива в base64, лежит установочный комплект стека:

| В комплекте | Откуда в репозитории |
| --- | --- |
| `install-stack.ps1`, `docker-compose.yml`, `docker-compose.tls.yml`, `.env.example`, `init/`, вспомогательные скрипты | `stack/Install` |
| Сборщики статистики автоматизаций и отчётов (`collectors/`) | `stack/metrics/automation-collector`, `stack/metrics/report-collector` |
| Конфигурация Prometheus, Loki, Grafana и дашборды (`monitoring/`) | `stack/metrics/config`, `stack/build/pilot-metrics.yml` |
| Общие проверки CI автоматизаций и отчётов (`ci-checks/`) | `gitlab/ci-checks` |

При запуске `install.ps1`:

1. Задаёт вопросы: что ставить на этой машине, что подключить существующим (GitLab, Grafana, контуры Pilot тест / стейдж / прод), загрузить ли базы Pilot из файлов `.dbp`.
2. Распаковывает комплект в **папку установки** `PilotProAdmin-install` рядом с собой (или в каталог `-InstallDirectory`). Распаковка повторяется при каждом запуске, поэтому править файлы комплекта в папке установки бесполезно: их перезапишет следующий запуск.
3. Создаёт `.env` с паролями и ключами стенда (при повторном запуске сохраняет прежний).
4. Загружает образы админки и миграции: из архива рядом с собой (offline) или из GHCR (lite). Сборщики статистики собирает на месте.
5. Запускает `install-stack.ps1` с выбранными ролями. Тот поднимает контейнеры, создаёт базы, бакеты RustFS, проекты и раннер GitLab, ставит модули.

### Роли

Установщик сам составляет список ролей по ответам на вопросы.

| Роль | Что поднимается |
| --- | --- |
| `admin` | PilotProAdmin (:5300) и PilotMigration (:5320) — всегда |
| `gitlab` | GitLab и GitLab Runner, проект общих проверок `pilot-ci/checks`, OAuth-приложения для входа в админку и для модуля отчётов |
| `metrics` | Prometheus, Loki, Grafana, сборщики статистики автоматизаций и отчётов |
| `prod`, `test`, `stage` | Pilot-Server контура с PostgreSQL и RustFS (S3); у прода ещё Pilot-myAdmin Web |

Внешние сервисы подключаются вместо ролей: свой GitLab, своя Grafana, существующий Pilot-Server (монолит или балансировщик кластера), внешний PostgreSQL или S3 для нового контура. [Варианты развёртывания](deployment.md).

### Папка установки

После установки стенд живёт в папке установки: compose, `.env`, базы PostgreSQL, базы и лицензии Pilot, настройки и журнал админки. Её нельзя удалять, переносить и переименовывать. Файлы Pilot (RustFS), вложения GitLab и данные мониторинга Docker хранит в своих томах. Подробно — в [быстром старте](getting-started.md#папка-установки).

## Установка

| Что нужно | Инструкция |
| --- | --- |
| Поставить стенд на Windows или Linux из lite / offline | [Быстрый старт](getting-started.md) |
| Запустить только админку без Docker | [Windows Portable](windows-portable.md) |
| Перенести стенд с другой машины | [Установка из резервной копии](getting-started.md#установка-из-резервной-копии) |
| Подробности о ролях, параметрах `.env`, хранении паролей и ручной настройке Pilot | [Установка стенда](../stack/Install/docs/installation.md) |
| Сетевые правила и секреты для службы ИБ | [Безопасность](../stack/Install/docs/security.md) |
| GitLab и CI без интернета | [GitLab без интернета](../stack/Install/docs/gitlab-offline.md) |
| Кластер Pilot-Server | [Кластерный Pilot](../stack/Install/docs/cluster-installation.md) |

## Обновление

Скачайте lite новой версии, распакуйте в любую папку и запустите установщик с прежним каталогом установки:

```powershell
.\install.ps1 -InstallDirectory 'D:\PilotProAdmin\PilotProAdmin-install'
```

Установщик сохранит `.env` и данные, распакует новый комплект и переключит админку и миграцию на образы новой версии. Старый распакованный lite после этого можно удалить, папку установки — нет. Перед обновлением снимите резервную копию: «Миграция → Подготовить перенос данных».

## Из чего собирается релиз

Релиз собирает GitHub Actions (`.github/workflows/release.yml`) по тегу `vX.Y.Z`. Версия берётся из `proadmin/src/ProAdmin/PilotProAdmin.csproj`.

| Папка репозитория | Что из неё попадает в релиз |
| --- | --- |
| `proadmin/` | Образ админки, образ миграции (`proadmin/migration`), portable-сборка, комплект переноса данных (`proadmin/transfer`) |
| `stack/Install/` | Установочный комплект внутри `install.ps1`, файлы кластера в `cluster.tar.gz` |
| `stack/metrics/` | Сборщики статистики и конфигурация мониторинга внутри `install.ps1` |
| `stack/build/` | `pilot-metrics.yml` (источник данных Grafana) и самопроверки установщика, которые запускает CI |
| `gitlab/ci-checks/` | Проект общих проверок CI внутри `install.ps1` |
| `server-extensions/ServerHost/` | `ServerHost-X.Y.Z.zip` |
| `scripts/` | `package-release.ps1` собирает lite и offline, `install-release.ps1` — шаблон `install.ps1`, `package-windows-portable.ps1` — portable |
| `client-modules/`, `server-extensions/` (кроме ServerHost) | В релиз не попадают: тестовый модуль, пример серверного модуля и шаблоны CI для репозиториев модулей |

Локальная сборка того же пакета (нужен PowerShell 7.4):

```powershell
.\scripts\package-release.ps1 -Version X.Y.Z -WithPortable          # lite и portable в artifacts\
.\scripts\package-release.ps1 -Version X.Y.Z -Offline               # плюс образы из рабочего дерева и offline.zip
```

Других способов собрать установку нет: прежняя раскладка по отдельным compose-папкам и сборка комплекта папкой удалены из репозитория.
