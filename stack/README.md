# CI/CD-стенд Pilot

Стек лежит в папке `stack` монорепозитория; рядом, в корне репозитория, находятся `proadmin` (веб-админка и миграция), `gitlab`, `server-extensions`, `client-modules` и `Reports` ([описание проекта](../docs/index.md)). Скрипты `build/build.ps1`, `backup/backup.ps1` и `shared/stack.ps1` ищут эти папки рядом со стеком. Пути `C:\CICD` и `C:\Ascon-Docker` в документах - раскладка автора: каталог данных задаётся переменной `DATA_ROOT`. Контейнеры сгруппированы по папкам, в каждой свой `docker-compose.yml` и `.env.example`. Свой README есть у `stage`, `metrics` и `backup`; `prod` и `test` устроены так же, как `stage`.
Запуск и остановка: `.\shared\stack.ps1 up|down|ps|build [prod|test|stage|admin|metrics]`. Цель `admin` здесь - `gitlab` и `proadmin` (с миграцией); в `Install` роль `admin` - только админка и миграция, GitLab - отдельная роль `gitlab`.

| Папка | Compose-проект | Контейнеры |
|---|---|---|
| `prod/` | `pilot-prod` | `postgres-prod`, `rustfs-prod`, `pilot-server-prod`, `pilot-web-myadmin-prod` |
| `test/` | `pilot-test` | `postgres-test`, `rustfs-test`, `pilot-server-test` |
| `stage/` | `pilot-stage` | `postgres-stage`, `rustfs-stage`, `pilot-server-stage` |
| `../proadmin` | `pilot-proadmin` | веб-админка `pilot-proadmin` (:5300) и сервис миграции `pilot-migration` (:5320, исходники в `proadmin/migration`) |
| `../gitlab` | `gitlab` | `gitlab`, `gitlab-runner` |
| `metrics/` | `metrics` | `prometheus`, `node-exporter`, `cadvisor`, `loki`, `promtail`, `grafana`, `automation-metrics-test/stage`, `report-metrics-collector` |

Данные контуров - в `C:\Ascon-Docker\<контур>` (переменная `DATA_ROOT`), а файлы Pilot (S3) хранит RustFS в именованном томе Docker (`rustfs_data` в compose каждой папки, Docker-имя `pilot-<контур>_rustfs_data`; `docker volume ls`). Папка на диске для него не нужна и права (`chown`) настраивать не надо. Почему том: на Windows папка хоста подключается к контейнеру через 9p, и на ней RustFS не завершает составную загрузку больших файлов (`NoSuchKey`); на томе Docker всё работает (подробности - в `Install/README.md`). Копию тома снимает `backup/backup.ps1` (часть `rustfs`); на новой машине его разворачивает установка с `-FromBackup` (`--from-backup`), ручной `backup/restore.ps1` после установки потребует `-ConfirmOverwrite rustfs`. Имена переменных `MINIO_*` исторические: значение - ключ доступа RustFS. Сети `pilot-prod`, `pilot-test`,
`pilot-stage` создаются один раз (`shared/stack.ps1` делает это сам). Порядок запуска: контуры / admin / metrics.

Смена паролей контура (PostgreSQL, RustFS, `settings.xml`, зависимые `.env`): `.\shared\rotate-contour-secrets.ps1 -Env test`, для прода `-Env prod -ConfirmProd prod`; на Linux то же делает `bash shared/rotate-contour-secrets.sh --env test` (для прода `--env prod --confirm-prod prod`, каталог данных - `--data-root`, по умолчанию `DATA_ROOT` из `<контур>/.env`, иначе `/opt/ascon-docker`; нужны docker, openssl 3, perl). Что именно меняет скрипт, описано в его заголовке. Если в `proadmin\.env` задан ключ выкладки модулей контура (`MODULES_DEPLOY_KEY_<КОНТУР>`), скрипт выдаёт новый, и его нужно сразу внести в переменную группы GitLab `DEPLOY_KEY_<КОНТУР>`.

Оба скрипта рассчитаны на раскладку по папкам (`prod`, `test`, `stage`, `proadmin`, `metrics` рядом). Для установки из `Install` (общий `.env`, проект `cicd`) они не подходят: там пароли меняют вручную (новое значение в `.env`, `ALTER ROLE` в PostgreSQL, учётная запись RustFS и `settings.xml` контура). Ротация работает через `rc` в контейнере `rustfs-<контур>` (адрес S3 - `rustfs-<контур>`). Пароли Grafana, GitLab и Pilot меняют в их интерфейсах. Копии прежних секретов, которые оставляет скрипт (`.env.before-rotate-*`, `settings.xml.before-rotate-*`, `config.json.before-rotate-*`), удаляйте после проверки: в git они не попадают.

Скрипты резервных копий и ротации проверяются без стенда (docker подменён заглушкой): `powershell -NoProfile -ExecutionPolicy Bypass -File build\selftest\backup-selftest.ps1` (части rustfs, grafana, loki, prometheus в `backup.ps1` и `restore.ps1`) и `docker run --rm -v "<путь к stack>:/stack:ro" postgres:16 bash /stack/build/selftest/rotate-selftest.sh` (`rotate-contour-secrets.sh`). Работа с настоящими RustFS и Pilot-Server этими проверками не покрыта.

Исходники расширений Pilot:

| Папка | Что внутри |
|---|---|
| `../client-modules/` | `Pilot.ClientSdk` (общий клиентский модуль, до 2.0.0 - Modules), `Test-module`, шаблон CI |
| `../Reports/` | модуль отчётов |
| `../server-extensions/` | Три решения: `ServerHost` (загрузчик серверных модулей и тесты), `Debug` (локальная отладка), `TestServerAction` (пример модуля); шаблон CI - см. README папки |
| `../gitlab/ci-checks/` | проверки автоматизаций и отчётов для GitLab CI, свой репозиторий `pilot-ci/checks` |
| `backup/` | ежедневные резервные копии (Postgres, файлы Pilot и RustFS, GitLab, админка, тома Grafana, Loki и Prometheus) в `D:\Backups`, проверка и восстановление - см. README папки |
| `Install/` | установочная папка для другого сервера, с текущими данными или чистая. Передаётся целиком, заполняет её `build/build.ps1` - см. `build/README.md`. Ручная настройка Pilot - `Install/docs/` |

## Версии образов

Сторонние образы закреплены версией и digest (`name:tag@sha256:…`): в `prod`, `test`, `stage`, `gitlab`, `metrics`, в `FROM` наших Dockerfile и в шаблонах CI. Новая версия приходит только правкой этих строк. Исключение - RustFS (`rustfs/rustfs:1.0.5`, во всех compose): образ приходит через `docker load` (из `Install/images`), а у загруженного образа `RepoDigest` может не быть (при хранилище classic его нет), поэтому он закреплён тегом. Дайджест индекса `1.0.5` для сверки указан в `build/README.md` и в комментарии к сервису rustfs в `Install/docker-compose.yml`. Без digest остаются также наши образы `pilot-*:latest` и плавающие теги в CI-файлах модулей.

**Обновление.** Сначала снять бэкап и поменять образ на test:

```powershell
# Узнать новый digest Pilot
docker pull registry.ascon.ru/project/pilotdev/pilot/pilot-server:release
docker image inspect registry.ascon.ru/project/pilotdev/pilot/pilot-server:release --format '{{index .RepoDigests 0}}'
# Записать его в .env контура: остальные контуры остаются на старой версии
# Перевод строки в начале нужен: Add-Content не добавляет его, и строка склеилась бы с последней переменной .env.
Add-Content test\.env "`nPILOT_SERVER_IMAGE=registry.ascon.ru/project/pilotdev/pilot/pilot-server:release@sha256:<новый>" -Encoding ASCII
```

Потом та же строка в `stage/.env`, и только после проверки - в `prod/.env`. Когда все контуры обновлены, новый digest записывается в compose, а строка из `.env` убирается. У остальных образов digest меняют сразу в compose: это общий образ контуров, поэтому правят по одному файлу, начиная с `test`. GitLab обновляют только по его пути обновления (без пропуска обязательных версий).

Образы Pilot и myAdmin, закреплённые digest, `backup.ps1` сохраняет в `D:\Backups\images`.

**Наши образы** (`pilot-proadmin`, `pilot-migration`, `pilot-report-metrics`, `pilot-automation-metrics`) собираются здесь и остаются `:latest`. `shared/stack.ps1 build` дополнительно ставит тег текущего коммита репозитория `stack`, а не того репозитория, из которого собран образ. Сборка комплекта (`build/build.ps1`) записывает коммит и время в метки образа (`org.opencontainers.image.revision`, `org.opencontainers.image.created`). Откат: `docker tag pilot-proadmin:<коммит> pilot-proadmin:latest`, затем `docker compose up -d` в папке сервиса.
