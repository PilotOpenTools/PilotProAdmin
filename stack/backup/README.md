# Резервные копии стека

Скрипты рассчитаны на Windows и раскладку по папкам (рядом со стеком лежат `gitlab` и `proadmin`, данные контуров - `C:\Ascon-Docker`). Для установки из `Install` (данные GitLab и админки в `DATA_ROOT`, Docker на Linux) они не подходят: там копируют `DATA_ROOT` и `.env` при остановленных сервисах, а том RustFS (`<проект>_rustfs_<контур>`) вне `DATA_ROOT` копируют отдельно, например `docker run --rm -v <том>:/v:ro -v <каталог>:/b alpine tar czf /b/rustfs.tgz -C /v .`.

Каждую ночь в 03:00 задание Планировщика «CICD Backup» запускает `backup.ps1`. Копия ложится в `D:\Backups\<yyyy-MM-dd_HHmm>\`; это отдельный физический диск, не тот, где `C:\Ascon-Docker`. Копии хранятся 14 дней. Старые удаляются, только если текущий запуск прошёл без ошибок.

| Каталог | Что внутри | Чем снимается |
|---|---|---|
| `postgres\<контур>\` | `pilot-ice_ru.dump`, на prod ещё `pilot_metrics.dump`, `globals.sql` (роли, в том числе `metrics_writer` и `metrics_reader`) | `pg_dump -Fc`, `pg_dumpall --globals-only` |
| `pilot\<контур>\` | `pilot-server\settings` (settings.xml), `server-settings`, `license`, `databases` (вместе с `.server-modules`) | robocopy |
| `rustfs\` | файловый архив Pilot (S3): файл `rustfs_<контур>.tgz` на каждый контур; в `manifest.json` `volumes.rustfs.<контур>` | tar тома Docker RustFS в одноразовом контейнере |
| `gitlab\` | `<дата>_gitlab_backup.tar`, `config\` (**gitlab-secrets.json**, gitlab.rb, ssh-ключи), `runner-config\` | `gitlab-backup create STRATEGY=copy` |
| `admin\` | `data` админки и миграции (настройки, ключи cookie, журналы), все `.env` | копирование |
| `grafana\` | том `grafana_data` (без `plugins`), файл `grafana_grafana_data.tgz` | tar в одноразовом контейнере |
| `loki\` | том `loki_data`, файл `grafana_loki_data.tgz` | tar в одноразовом контейнере |
| `prometheus\` | том `prometheus_data`, файл `grafana_prometheus_data.tgz` | tar в одноразовом контейнере |
| `D:\Backups\images\` (отдельно от запусков) | закреплённые digest образы Pilot и myAdmin (и RustFS, если в compose указан его digest): в реестрах их может уже не быть. Один файл на digest, по сроку не удаляются | `docker save` |

Если источник части отсутствует (кроме необязательных `license`, `databases` и `server-settings`), часть завершается ошибкой, а в `manifest.json` будет `ok: false`: копия без данных не объявляется удачной.

Тома мониторинга упаковываются командой `docker run --rm -v <том>:/v:ro -v <каталог копии>:/b alpine tar czf ...` (образ `alpine:3.20` закреплён digest, параметр `-TarImage`; на машине без интернета он должен быть загружен, `build.ps1 -Offline` кладёт его в комплект). Том монтируется только для чтения, готовый архив перечитывается `tar tzf`; сервисы при этом работают, поэтому копия согласована на уровне файлов, а не на момент времени: Prometheus и Loki восстанавливаются после неё как после аварийной остановки. Если часть упала из-за файлов, изменившихся во время упаковки, повторите запуск. Имя тома - `<проект>_grafana_data`, `<проект>_loki_data`, `<проект>_prometheus_data`. Проект берётся из `-Project`, иначе из `COMPOSE_PROJECT_NAME` в `.env` (рядом со стеком или в `Install\.env`), иначе `cicd`, как в установщике. Если проект не задан нигде, а томов `*` нет, используются тома `grafana_*`: их имена закреплены в `metrics\docker-compose.yml`. Нет тома - часть завершается ошибкой; если мониторинга на этой машине нет, пропустите части: `-Skip grafana,loki,prometheus`. Имена файлов в копии от проекта не зависят, архив `grafana_grafana_data.tgz` читает установщик.

`manifest.json` содержит статус каждой части, размер и имена томов мониторинга (`volumes`). `D:\Backups\last-backup.json` хранит итог последнего **завершившегося** запуска. Если запуск оборвался, файл остаётся прежним, а в каталоге запуска есть только `backup.log`, поэтому проверяйте дату в `last-backup.json`, а не только `ok`.

Данные RustFS лежат не в папке, а в именованном томе Docker, поэтому часть `rustfs` снимает том так же, как тома мониторинга: `docker run --rm -v <том>:/v:ro -v <каталог копии>:/b alpine tar czf ...`, затем проверка `tar tzf` (тот же `-TarImage`). Имя тома - `<проект>_rustfs_<контур>` (проект из `-Project`, иначе из `COMPOSE_PROJECT_NAME` в `.env`, иначе `cicd`). Если проект не задан нигде, пробуются ещё имена раскладки по папкам: `pilot-<контур>_rustfs_data` и `<контур>_rustfs_data`. Контур пропускается, если нет ни тома, ни контейнера `rustfs-<контур>`; есть контейнер, а тома нет - ошибка (проверьте имя проекта). Если нет ни одного тома, часть завершается ошибкой: пустая копия выглядела бы удачной (если хранилища на машине нет, `-Skip rustfs`). Копия снимается при работающем RustFS, как у мониторинга: согласована на уровне файлов, не на момент времени (нужна живая проверка на большом томе).

Недоступный Docker для части `rustfs` - ошибка, а не «пусто». Остальные источники (`gitlab\config`, `runner-config`, `proadmin\data`, `proadmin\migration\data`) обязательны: если части нет на машине, снимайте копию с `-Skip gitlab,admin`, иначе `manifest.json` получит `ok: false`.

Задание работает от текущего пользователя и только пока он вошёл в Windows: Docker Desktop без входа не запускается. Если компьютер в 03:00 был выключен, задание стартует после включения и ждёт Docker до 10 минут.

> В копии лежат секреты: `.env`, `gitlab-secrets.json`, ключи админки (`admin\proadmin-data\keys`: они нужны, чтобы после восстановления на своей машине вошедшие пользователи не были разлогинены; `build.ps1` убирает их из копии, которая идёт в комплект для других машин). Доступ к `D:\Backups` должен быть только у администратора. Чтобы копии пережили потерю машины, `D:\Backups` нужно периодически переносить за её пределы: на внешний диск или в сетевое хранилище.

## Команды

```powershell
.\backup\backup.ps1                          # снять копию сейчас
.\backup\backup.ps1 -Skip gitlab,grafana     # без частей
.\backup\backup.ps1 -Project lab             # тома lab_grafana_data и т. д. (по умолчанию из .env, иначе cicd)
.\backup\restore.ps1 -Check                  # проверить последнюю копию (живое не трогает)
.\backup\register-task.ps1 [-At 03:00]       # (пере)создать задание; -Remove - удалить
Get-Content D:\Backups\last-backup.json
```

`restore.ps1 -Check` восстанавливает дампы во временный контейнер `postgres:16.16` (закреплён digest) без сети. Проблемой считаются: `ok: false` в манифесте, пустая восстановленная база, нехватка в архиве GitLab `backup_information.yml`, `db/`, бандлов репозиториев или `gitlab-secrets.json`, архив тома RustFS (`rustfs\rustfs_<контур>.tgz`) или мониторинга, который не читается, пуст или отсутствует при `ok` в манифесте (запись `volumes.rustfs` без файла тоже проблема; части, которых в манифесте нет, не проверяются; нужен тот же образ `alpine`). Расхождение числа таблиц с живой базой только предупреждение (схема могла смениться после обновления Pilot), число строк только печатается: живая база меняется. Итог `ПРОВЕРКА: ok` означает отсутствие этих проблем, а не побайтное сравнение данных. Без `-From` берётся последняя копия с `ok: true`; неудачные копии пропускаются, восстановление из копии с `ok: false` скрипт отклоняет. Проверку стоит запускать хотя бы раз в неделю.

## Восстановление контура Pilot

```powershell
.\backup\restore.ps1 -Env test -Parts postgres,pilot,rustfs           # последняя копия
.\backup\restore.ps1 -Env stage -Parts postgres -From D:\Backups\2026-11-02_0300
.\backup\restore.ps1 -Env prod -Parts postgres,rustfs -ConfirmProd prod
.\backup\restore.ps1 -Env prod -Parts rustfs -ConfirmProd prod -ConfirmOverwrite rustfs   # том RustFS уже с данными
```

Скрипт останавливает `pilot-server-<контур>` (а для `rustfs` ещё и `rustfs-<контур>`, запускает его снова, только если он был запущен). Архив тома RustFS разворачивается в том `<проект>_rustfs_<контур>`: том должен существовать (создаёт стек), порядок такой: проверка архива `tar tzf`, очистка тома, `tar xzf`; нечитаемый архив том не стирает, а `pilot-server` останавливается только после проверок. Том, где кроме `.rustfs` и `.rustfs.sys` ничего нет, считается пустым. Если в нём есть данные бакетов, скрипт отказывает: содержимое будет удалено, перезапись только с `-ConfirmOverwrite rustfs`. После обычной установки том уже не пуст (установщик создал бакет, RustFS записал служебные каталоги), поэтому ручной `restore.ps1 -Parts rustfs` на только что установленную машину тоже требует `-ConfirmOverwrite rustfs`. Для новой машины рекомендуем другой путь: установку с `-FromBackup` (на Linux `--from-backup`). Она сама разворачивает `rustfs\rustfs_<контур>.tgz` в том до старта Pilot-Server и до создания бакета, подробности в `Install/README.md`. Права на файлы выставлять не нужно: владельца 10001 несёт сам том, а `tar` от root берёт его из архива (проверено на образе RustFS: после распаковки файлы остались у 10001; запуск RustFS на восстановленных данных отдельно не проверялся). Базы пересоздаются через `pg_restore --clean --create`: база сначала удаляется, поэтому перед восстановлением снимите свежую копию. После восстановления всех баз (если среди дампов есть `pilot_metrics`) скрипт один раз запускает `02-metrics-roles.sh` внутри `postgres-prod`: владелец и права приводятся к ролям `metrics_writer` и `metrics_reader` (так же для копий, снятых до их появления; контейнер должен быть создан с `METRICS_WRITER_PASSWORD` и `METRICS_READER_PASSWORD`). Папки `pilot` зеркалируются из копии (`robocopy /MIR`, лишние файлы в живой папке удаляются), после этого контейнеры запускаются снова. Части `postgres` и `pilot` восстанавливайте вместе: база без своего файлового архива (и наоборот) рассогласуется. Копию одного контура в другой этим скриптом не переносят: для этого есть сервис миграции, он следит за разными DatabaseId.

## Восстановление томов Grafana, Loki и Prometheus

```powershell
.\backup\restore.ps1 -Parts grafana,loki,prometheus                       # в пустые тома, -Env не нужен
.\backup\restore.ps1 -Parts grafana -ConfirmOverwrite grafana -From D:\Backups\2026-11-02_0300
```

Том должен существовать (его создаёт стек: `docker compose up -d --no-start` или первый запуск). Запущенный сервис (`grafana`, `loki`, `prometheus`) останавливается на время записи и запускается снова. Непустой том скрипт не трогает: перезапись только с `-ConfirmOverwrite <части>`, содержимое тома перед распаковкой удаляется (перед этим архив проверяется `tar tzf`, нечитаемый архив том не стирает). Имя тома строится так же, как у `backup.ps1` (`-Project`, `COMPOSE_PROJECT_NAME`, `cicd`). Части томов можно указывать вместе с частями контура, но тогда нужен `-Env`.

## Восстановление GitLab (вручную)

Версия образа `gitlab/gitlab-ce` должна совпадать с той, на которой снята копия. Версия указана в `backup_information.yml` внутри архива; имя архива `<дата>_gitlab_backup.tar` версии не содержит.

```powershell
docker stop gitlab-runner
Copy-Item <копия>\gitlab\config\* C:\CICD\gitlab\config\ -Force          # секреты - до restore
Copy-Item <копия>\gitlab\*_gitlab_backup.tar C:\CICD\gitlab\data\backups\
docker stop -t 600 gitlab; docker start gitlab                       # подхватить config; не docker restart: PostgreSQL нужно время на остановку
# Дождаться готовности: puma запущен и отвечает readiness (иначе восстановление идёт поверх недостроенной схемы)
docker exec gitlab sh -c 'gitlab-ctl status puma; curl -fs http://localhost/-/readiness'
docker exec gitlab gitlab-ctl stop puma
docker exec gitlab gitlab-ctl stop sidekiq
docker exec -it gitlab gitlab-backup restore BACKUP=<дата>     # имя без _gitlab_backup.tar
docker exec gitlab gitlab-psql -d gitlabhq_production -tAc "select count(*) from projects"   # должно быть больше нуля
docker stop -t 600 gitlab; docker start gitlab
docker exec gitlab gitlab-rake gitlab:check SANITIZE=true
docker exec gitlab chown -R git:git /var/opt/gitlab/git-data/repositories
Copy-Item <копия>\gitlab\runner-config\* C:\CICD\gitlab\runner-config\ -Force
docker start gitlab-runner
```

`chown` нужен потому, что после переноса данных репозитории могут оказаться у root, и Gitaly тогда отклоняет push («dubious ownership»).

## Админка и миграция

Остановить контейнер, вернуть `admin\proadmin-data` в `proadmin\data` (или `admin\migration-data` в `proadmin\migration\data`) и `.env` из `admin\env\...` на их места, затем запустить контейнер.

## Проверка самих скриптов

Без стенда (docker подменён заглушкой): `powershell -NoProfile -ExecutionPolicy Bypass -File build\selftest\backup-selftest.ps1` из папки `stack`. Проверяются имена томов (в том числе RustFS), монтирование только для чтения, манифест, сбои, `-Check` и восстановление томов. Настоящий docker для этого не нужен; снятие и восстановление на живых томах проверяйте вручную после изменений в скриптах.
