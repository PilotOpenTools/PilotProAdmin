# Сборка установочного комплекта

Заполняет папку `Install` перед передачей заказчику. Запускается только на исходной машине, в комплект эта папка не входит.

Заказчику передаётся `Install` целиком. В git попадают только её скрипты и настройки: `images`, `data`, `monitoring`, `modules`,
`IMAGES.txt` и `PASSWORDS.md` с паролями заполняет сборка. Руководства в `Install/docs` входят в комплект и хранятся в Markdown.

Команды запускаются из корня стека:

```powershell
.\build\build.ps1                     # пересобрать наши образы, снять свежий бэкап (стек должен работать)
.\build\build.ps1 -NoBuild            # образы взять те, что стоят локально
.\build\build.ps1 -DataFrom D:\Backups\2026-11-03_0300   # данные из готового бэкапа
.\build\build.ps1 -NoData             # без данных: останется только чистая установка
.\build\build.ps1 -Offline            # в images все образы, в том числе для CI: установка без интернета
.\build\make-passwords.ps1            # собрать заново только Install\PASSWORDS.md
```

Нужен git: им собираются бандлы модулей в `Install\modules\sources`. Папки модулей могут быть и без `.git` (например, скачанный архив): тогда бандл - один коммит из файлов папки с учётом её `.gitignore`.

Свежий бэкап (без `-DataFrom` и `-NoData`) снимается только с работающего стека, запущенного из этой же папки: `.env` контуров, `gitlab\config` и данные админки бэкап берёт из соседних папок. Если стек запущен из другой папки, сборка останавливается сразу: снимите бэкап там (`backup\backup.ps1`) и передайте его через `-DataFrom`. Бэкап без `.env` какого-либо контура считается неудачным.

| Файл | Что это |
|---|---|
| `build.ps1` | Сборка комплекта |
| `make-passwords.ps1` | Собирает `Install\PASSWORDS.md`: логины и пароли из `data` |
| `kit-secrets.ps1` | `Remove-KitSecrets`: убирает из `data` то, что каждая установка создаёт себе сама. Сейчас это папка ключей защиты данных админки `admin\proadmin-data\keys`: иначе все установки из комплекта получили бы один ключ. Бэкап своей машины ключи хранит, из комплекта их убирает `build.ps1` |
| `pilot-metrics.yml` | Источник данных Grafana для комплекта |
| `selftest/install-selftest.sh`, `selftest/install-selftest.ps1` | Самопроверки `install.sh` и `install.ps1` без стенда: docker подменён заглушкой |
| `selftest/compose-env-selftest.sh` | `docker compose config` на `.env`, который создаёт `install.sh` (каждая роль, повторный запуск, несколько машин, бэкап), и на `.env` контурных compose: обязательные пароли и понятный текст отказа |
| `selftest/firewall-live-selftest.sh` | Файл правил брандмауэра `firewall-linux.sh` на настоящем `iptables` в одноразовом контейнере (`--privileged`, нужен интернет): разбор, применение, повторное применение, трафик через подмену адреса, снятие |
| `selftest/post-restore-flow.rb` | Поток `Install/gitlab-post-restore.rb` на подмене REST API GitLab: защита веток, коммит `nuget.config`, переменные, токен |

`modules` нужна для чистой установки: в пустом GitLab и базах Pilot модулей нет. Сборка кладёт туда `sources/*.bundle`
для Pilot.ClientSdk и Reports: если модуль - отдельный репозиторий, это ветка `main` с тегами из GitLab (без веток
задач), если папка монорепозитория - один корневой коммит с содержимым папки на ветке `main` (история монорепозитория не нужна), `ServerHost.zip`
(без `-NoBuild` собирается заново в Release) и `packages/*.nupkg`: пакеты `Pilot.*`, на которые ссылаются эти модули,
из локального кэша NuGet. Как ими пользоваться, описано в документе установки, раздел «Модули при чистой установке».

`IMAGES.txt` начинается со строки о дате сборки (имя машины не пишется), вторая строка называет тип комплекта
(«для установки без интернета» или «для установки с интернетом»), по ней администратор отличает комплекты.
В строке образа четыре колонки: имя, полный идентификатор `sha256:…`, дата, отпечаток слоёв (SHA-256 списка `RootFS.Layers`). По ним `install` после `docker load` сверяет каждый образ; подробности - в `Install/README.md`.
Наши образы собираются с метками коммита и времени (`org.opencontainers.image.revision`, `.created`). В комплект входит один
сторонний образ без digest в compose: `rustfs/rustfs:1.0.5` (тег берётся из `Install\docker-compose.yml`; если его нет локально, он скачивается
по дайджесту индекса `sha256:b81d16a90ee25bd9511280127ca1471788c6e874cf0f0da759645f46b2dc1d74` и получает тег; для другого тега
обычный `docker pull` без сверки).

С `-DataFrom` копия кладётся в комплект как есть, без разбора частей: в новой копии хранилище лежит в части `rustfs` (архивы томов `rustfs_<контур>.tgz`; на целевой машине том из них сам разворачивает установщик: `install.ps1` или `install.sh` распаковывают архив до старта Pilot-Server; ручной `backup/restore.ps1` после установки потребует `-ConfirmOverwrite rustfs`).

Ключ `-Offline` есть только у сборки. У заказчика её нет, поэтому комплект для закрытого контура собирается здесь.

Запускать `install` прямо в `stack\Install` на этой машине нельзя: совпадут имена контейнеров и порты с рабочим стеком.

Версии образов закреплены в `Install\docker-compose.yml`. Чтобы обновить версию, поменяйте там образ. RustFS закреплён
только тегом: его образ едет в `images`, а `docker load` теряет digest. `Install\wait-for-postgres.sh` должен совпадать с
`shared\wait-for-postgres.sh` (сверяет RepoCopiesTests).

Для `-Offline` на машине сборки нужно хранилище образов containerd: на обычном `docker load` теряет digest, и compose не найдёт образы.

## Самопроверка установщиков

Без стенда и без запуска контейнеров проекта (docker подменён заглушкой, команды GitLab выполняются локально):

```powershell
# install.sh в одноразовом контейнере Linux
docker run --rm -v "${PWD}:/stack:ro" postgres:16 bash /stack/build/selftest/install-selftest.sh
# install.ps1 в Windows PowerShell 5.1 (нужен образ alpine:3)
powershell -NoProfile -ExecutionPolicy Bypass -File build\selftest\install-selftest.ps1
```

Проверяются права `.env`, замена паролей-заглушек, генерация и перенос ключей выкладки, отказ при нескольких машинах и при потерянном `.env`, порядок и условия восстановления GitLab (код возврата, число проектов, метка `.restore-started`, таймаут), имя тома Grafana, обязательные пароли в `.env`, сверка загруженных образов с `IMAGES.txt` (совпадение, расхождение, нет файла или строки), отказ init-скрипта PostgreSQL без пароля. Живой GitLab, Docker Desktop и сеть они не заменяют.

Обязательные пароли compose проверяются настоящим `docker compose config` (демон не нужен; `install.sh` в этом скрипте идёт с заглушкой docker):

```powershell
bash build/selftest/compose-env-selftest.sh
```

Файл правил брандмауэра для Linux проверяется на настоящем `iptables` (в контейнере создаются сетевые пространства имён, правила хоста не затрагиваются):

```powershell
docker run --rm --privileged -v "${PWD}:/stack:ro" alpine:3 sh -c 'apk add -q --no-cache bash && bash /stack/build/selftest/firewall-live-selftest.sh'
```

Генерацию правил (Linux и Windows), HTTPS-надстройку (включение, отключение, ошибки в файлах, свой `COMPOSE_FILE`) и ключи админки при восстановлении из `data` и из своего бэкапа проверяют `install-selftest.sh` и `install-selftest.ps1`, а `compose-env-selftest.sh` пропускает через `docker compose config` `.env` с включённым HTTPS. Файл Windows проверяется разбором PowerShell и самотестом генерации; действие правил Windows Firewall на живой машине не проверялось.

Поток шага GitLab после установки проверяется отдельно, без GitLab (нужен образ ruby:3.3-alpine):

```powershell
docker run --rm -v "${PWD}:/stack:ro" ruby:3.3-alpine ruby /stack/build/selftest/post-restore-flow.rb /stack/Install/gitlab-post-restore.rb
```

Подмена API не воспроизводит GitLab: передачу окружения через обёртку `gitlab-rails` и реальную работу `[skip ci]` она не проверяет.
