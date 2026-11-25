# Установочный комплект и мониторинг

Отсюда собирается установщик релиза. Ставить стенд по этой папке не нужно: установка идёт из файлов релиза, см. [«Установка и устройство пакета»](../docs/installation-package.md).

| Папка | Что в ней | Куда попадает в релизе |
|---|---|---|
| `Install/` | `install.ps1` (в релизе `install-stack.ps1`), `docker-compose.yml` всех ролей, `docker-compose.tls.yml`, `.env.example`, init-скрипты PostgreSQL, шаг GitLab после установки, файлы кластера (`cluster/`), руководства (`docs/`) | Внутрь `install.ps1` из lite; `cluster/` и руководства по кластеру — в `cluster.tar.gz` |
| `metrics/` | Сборщики статистики автоматизаций (Python) и отчётов (.NET), конфигурация Prometheus, Loki, Promtail, Grafana и дашборды | Внутрь `install.ps1`: сборщики собираются на месте, конфигурация кладётся в `monitoring/` папки установки |
| `build/` | `pilot-metrics.yml` (источник данных Grafana) и самопроверки установщика | `pilot-metrics.yml` — внутрь `install.ps1`; самопроверки запускает workflow Release |

## Роли и контейнеры

Все роли описаны в одном `Install/docker-compose.yml`, проект compose — `COMPOSE_PROJECT_NAME` из `.env` папки установки.

| Роль | Контейнеры |
|---|---|
| `admin` | `pilot-proadmin` (:5300), `pilot-migration` (:5320, только 127.0.0.1) |
| `gitlab` | `gitlab`, `gitlab-runner` |
| `metrics` | `prometheus`, `node-exporter`, `cadvisor`, `loki`, `promtail`, `grafana`, `automation-metrics-test`, `automation-metrics-stage`, `report-metrics-collector` |
| `prod`, `test`, `stage` | `postgres-<контур>`, `rustfs-<контур>`, `pilot-server-<контур>`; у прода ещё `pilot-web-myadmin-prod` |

Базы PostgreSQL, базы и лицензии Pilot, данные админки лежат в `DATA_ROOT` (по умолчанию `data` в папке установки). Файлы Pilot (S3) RustFS хранит в именованном томе Docker `<проект>_rustfs_<контур>`, а не в папке хоста: на Windows папка хоста подключается к контейнеру через 9p, и на ней RustFS не завершает составную загрузку больших файлов (`NoSuchKey`). Подробности — в `Install/README.md`.

Резервную копию и перенос стенда делает комплект «Миграция → Подготовить перенос данных» (`proadmin/transfer`): разовая копия — `prepare-transfer.ps1`, ежедневная — задание `register-task.ps1`. Восстановление — `install.ps1 -Backup` из релиза. `backup/backup.ps1` — временная обёртка для заданий «CICD Backup» прежних версий, удалится в следующем релизе (см. «Резервные копии» в `Install/docs/installation.md`).

## Версии образов

Сторонние образы закреплены версией и digest (`name:tag@sha256:…`) в `Install/docker-compose.yml`, в `FROM` наших Dockerfile и в шаблонах CI. Новая версия приходит только правкой этих строк. Исключение — RustFS (`rustfs/rustfs:1.0.5`): у образа, загруженного через `docker load`, `RepoDigest` может не быть, поэтому он закреплён тегом. Дайджест индекса для сверки указан в комментарии к сервису rustfs в `Install/docker-compose.yml`.

**Обновление Pilot-Server.** Сначала снимите резервную копию и поменяйте образ на тесте:

```powershell
docker pull registry.ascon.ru/project/pilotdev/pilot/pilot-server:release
docker image inspect registry.ascon.ru/project/pilotdev/pilot/pilot-server:release --format '{{index .RepoDigests 0}}'
```

Запишите полученный образ в `PILOT_SERVER_IMAGE` в `.env` папки установки (или передайте установщику `-PilotImage`). Переменная общая для всех контуров этой машины, поэтому пересоздавайте их по одному из папки установки: `docker compose up -d pilot-server-test`, после проверки — `pilot-server-stage`, затем `pilot-server-prod`. Общий `docker compose up -d` или повторный запуск установщика переведут на новый образ все контуры сразу. GitLab обновляют только по его пути обновления, без пропуска обязательных версий.

Образы админки и миграции берутся из релиза (`ghcr.io/pilotopentools/pilotproadmin:X.Y.Z`, `…-migration:X.Y.Z`), образы сборщиков статистики собираются при установке с тегом версии релиза.

## Самопроверки

Описаны в `build/README.md`. Работу с настоящими RustFS, Pilot-Server и GitLab они не заменяют.
