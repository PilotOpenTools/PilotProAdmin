# Кластерный Pilot-Server для контура комплекта

Комплект сам ставит Pilot-Server только в монолитном режиме (один контейнер `pilot-server-<контур>` с `settings.xml`).
Этот документ описывает, как перевести один контур комплекта в кластер: несколько узлов Pilot-Server на общей конфигурации
в PostgreSQL, Redis и балансировщик перед ними. Команды и параметры ниже взяты из проверенного стенда: контур `test` из двух узлов
на одной машине с Linux, Docker 29, Traefik 3.6.

Для запуска узлов и балансировщика на разных серверах используйте [комплект Compose для кластера](cluster-installation.md).

Пароли и адреса в примерах условные: `10.0.0.x`, `change-me`. Подставляйте значения из своего `.env`.

## 0. Что проверено и что нет

Проверено на хранилище RustFS 1.0.5: одна машина с Windows 11 и Docker Desktop, контур `test` (установщик `install.ps1 -Roles test`, затем разделы 1-5 этого документа),
PostgreSQL 16.16, Redis 7.4, Traefik 3.6, два узла, ServerHost 1.3.0. Файловая база Pilot была переведена в PostgreSQL утилитой `PostgresMigration`,
файловый архив S3 - `http://rustfs-test:9000`:

- запуск двух узлов с одним `--pgConnectionString`, вход через балансировщик и напрямую в каждый узел, чтение и запись конфигурации кластера командами `Ascon.Pilot.Daemon -rc` и `-wc` (по 1 с, значение параметра менялось и возвращалось);
- запись файлов в RustFS через балансировщик и через каждый узел (4 КиБ, 300 КиБ, 20 МиБ, 100 МиБ), чтение через каждый узел и через балансировщик: содержимое совпало побайтно, объекты лежат в бакете, а не в локальном файловом архиве;
- перезапуск одного узла, обоих узлов и всего контура (`docker compose down` без `-v`, затем установщик и запуск узлов): файлы на месте;
- остановка RustFS во время чтения и записи: сервер возвращает ошибку `Name or service not known (rustfs-test:9000)`, после запуска RustFS узлы работают без перезапуска;
- ServerHost 1.3.0 записан в базу (объект `$PilotServerExtension` в папке расширений) и после перезапуска загрузился на обоих узлах;
- серверный модуль (пример `TestServerAction`): загрузка и проверка через одни узлы, установка через другие, появление на втором узле за секунды, вызов автоматизацией (`ItemCreated`) на обоих узлах, откат, отключение и включение, удаление, возврат после перезапуска узлов;
- клиентский модуль (`Test-module`, zip `.ext2`): объект типа `Extension` в папке расширений, чтение через оба узла, файл лежит в RustFS.

Модули и конфигурация проверялись прямыми вызовами API Pilot и ServerHost (через админку, клиент Pilot и GitLab CI не ходили).

Проверялось только на прежнем хранилище, до перехода на RustFS, и на RustFS не повторялось: вход в админку и чтение разделов, выкладка модулей из CI, перенос базы из другого контура сервисом миграции и схема на двух машинах с NFS (раздел 9).

Не проверялось: RustFS на Linux, SMB под общие каталоги, узлы под Windows, прод, отказ Redis или балансировщика, обновление версии Pilot-Server на работающем кластере.
Если вам это нужно, проверьте на копии контура.

Известные ограничения:

- Каталог индексов `--indexPath` у каждого узла свой (раздел 4): общий каталог на двух узлах на проверке с RustFS давал ошибки `CorruptIndexException` и `FileNotFoundException` в журнале каждые 5 с.
- Временная папка файлового архива S3 должна существовать заранее (раздел 4): иначе запись файла падает с `DirectoryNotFoundException`.
- Если изменение отклонено или оборвалось (например, RustFS остановлен, модуль, нужный сценарию автоматизации, отключён), в бакете могут остаться объекты `temp/…` и неиспользуемые файлы.
- Пока серверный модуль с активностью из сценария автоматизации отключён или не загружен, изменения объектов, запускающие этот сценарий, отклоняются.
- Настройки раздела «Серверы» вступают в силу только после перезапуска **всех** узлов (раздел 6).
- После переноса базы между контурами на одном из узлов серверные расширения однажды не загрузились (сбой внутри кода Pilot-Server на перегруженной ВМ);
  повторить его не удалось. Если расширение не загрузилось, перезапустите этот узел. В журнале миграции для кластера-приёмника есть такое напоминание.
- Нужен ServerHost **1.2.0 или новее** (общий каталог модулей, синхронизация между узлами).

## 1. Перед началом

1. Остановите монолит контура: `docker stop -t 60 pilot-server-test`. Не удаляйте его контейнер и не затирайте `settings.xml`:
   сохраните копию (`cp -a <корень данных>/test/pilot-server/settings <корень данных>/test/pilot-server/settings.before-cluster`).
2. Снимите дамп базы Pilot: `docker exec postgres-test sh -c 'pg_dump -U postgres -Fc pilot-ice_ru > /tmp/pilot-ice_ru.before-cluster.dump'`
   (имя базы - своё). Если база Pilot ещё файловая и `pg_dump` отвечает, что такой базы нет, сохраните копию каталога базы
   (`<корень данных>/test/databases/<база>`, файлы `.dbp`): `PostgresMigration` в шаге 3 перенесёт её в PostgreSQL. Ежедневный бэкап комплекта по коду `stack/backup/backup.ps1` снимает все базы PostgreSQL, кроме `postgres`, то есть и `configurationdb`;
   на живом контуре это не проверялось.
3. Имя `pilot-server-<контур>` в сети контура должно принадлежать балансировщику (раздел 4), а не монолиту. Переименование контейнера
   монолита не помогает: Compose находит его по меткам и при следующем `docker compose up` (в том числе повторном `install`) запускает снова,
   рядом с узлами кластера на той же базе. Снимать профиль контура из `COMPOSE_PROFILES` тоже нельзя: вместе с монолитом остановятся
   PostgreSQL, RustFS и сборщик автоматизаций контура. Положите рядом с `docker-compose.yml` файл `docker-compose.override.yml`
   (Compose и установщик подхватывают его сами):

   ```yaml
   # Контур test работает в кластерном режиме: монолит не запускается, узлы описаны в cluster-test.yml.
   services:
     pilot-server-test:
       profiles: !override [monolith-disabled]
     automation-metrics-test:
       depends_on: !reset {}
       environment:
         LOG_PATH: /App/logs/*/automation.log   # логи узлов в logs/<узел>/
   ```

   Теги `!override` и `!reset` нужны: без `!override` Compose объединяет списки профилей, и монолит остаётся в профиле `test`. Нужен Docker Compose не ниже 2.24.4 (проверено на Docker 29.9).
   Проверено вживую: после этого `docker compose up -d --remove-orphans` (так запускает сервисы установщик) монолит не поднимает,
   а узлы кластера, запущенные отдельным проектом (раздел 4), не трогает. Контейнер монолита после этого можно удалить.

## 2. Redis

Кластеру нужен Redis, его адрес записывается в конфигурацию. На проверенном стенде он запущен с параметром `--notify-keyspace-events KEA` и паролем:

```bash
docker run -d --name redis-test --network pilot-test --restart unless-stopped \
  redis:7.4-alpine redis-server --requirepass "$REDIS_PASSWORD" --notify-keyspace-events KEA
```

Пароль сохраните в защищённом месте (например, файл `600` на хосте): он понадобится в шаге 3. Порты Redis наружу не публикуйте.

## 3. Конфигурационная база: PostgresMigration

Утилита `/App/PostgresMigration` есть в образе Pilot-Server. Она создаёт базу `configurationdb`, переносит в неё конфигурацию из `settings.xml`
и прописывает Redis. Аргументы по порядку:

1. путь к `settings.xml` монолита;
2. строка подключения к PostgreSQL **к базе `postgres`** (не к базе Pilot);
3. строка Redis: `redis-test:6379, password=<пароль Redis>`;
4. имя конфигурационной базы: `configurationdb`;
5. логин администратора Pilot;
6. пароль администратора Pilot.

```bash
docker run --rm --network pilot-test \
  -v <корень данных>/test/databases:/usr/share/ascon/databases \
  -v <корень данных>/test/pilot-server/settings:/usr/share/ascon/pilot-server/settings \
  --entrypoint /App/PostgresMigration \
  "$PILOT_SERVER_IMAGE" \
  /usr/share/ascon/pilot-server/settings/settings.xml \
  "Host=postgres-test:5432;Username=pilotuser;Password=$PILOT_PG_PASSWORD;Include Error Detail=true;DataBase=postgres" \
  "redis-test:6379, password=$REDIS_PASSWORD" \
  configurationdb admin "$PILOT_ADMIN_PASSWORD"
```

`PILOT_PG_PASSWORD` и `PILOT_ADMIN_PASSWORD` - из `.env` комплекта (`<КОНТУР>_PILOT_PG_PASSWORD` важнее общего). Образ - тот же, что у монолита
(`docker inspect -f '{{.Config.Image}}' pilot-server-test`).

Что наблюдалось на стенде: утилита печатает `Created configuration base.`, после чего может упасть с необработанным исключением
`System.ArgumentException: The value cannot be an empty string. (Parameter 'path')` (метод `IsFileInUse`), код возврата 139. Конфигурационная база к этому моменту
была создана, и узлы стартовали. Не опирайтесь на код возврата: проверьте результат -

```bash
docker exec postgres-test sh -c 'psql -U postgres -d configurationdb -Atc "select id from config.settings order by id"'
```

В списке должна быть строка `1` (и `2`, резервирования). Строка `id = 1` - XML того же формата, что `settings.xml`, пароль PostgreSQL в нём не открытый (атрибут `EncryptedPassword`).

## 4. Узлы и балансировщик

Узел запускается нашей обёрткой `wait-for-postgres.sh` как entrypoint: она ждёт PostgreSQL по хосту и порту из `--pgConnectionString`,
применяет настройки из админки (раздел 6) и запускает `Ascon.Pilot.Daemon` с теми же аргументами. Что монтировать в каждый узел:

| Что | Путь в контейнере | Замечание |
|---|---|---|
| `wait-for-postgres.sh` | `/wait-for-postgres.sh` (`:ro`) | копия из `Install` |
| `<корень>/test/databases` | `/usr/share/ascon/databases` | общий для всех узлов: файловые архивы, индексы, каталог модулей ServerHost |
| `<корень>/test/aspnet` | `/root/.aspnet` | общий |
| `<корень>/test/license` | `/usr/share/ASCON` | общий |
| `<корень>/test/logs/<узел>` | `/App/logs` | **свой у каждого узла**, чтобы логи не перемешивались; сборщик автоматизаций читает их маской `LOG_PATH=/App/logs/*/automation.log` (см. README сборщика) |
| `<корень>/test/server-settings` | `/server-settings` | общий: тот же каталог, что видит админка |

Пример фрагмента compose для двух узлов и Traefik (версия не ниже 3.6: 3.0 не работает с Docker 29). Узлы и балансировщик в одной сети контура;
порт 5545 внутри сети, наружу публикует балансировщик:

```yaml
x-pilot-node: &pilot-node
  image: ${PILOT_SERVER_IMAGE}
  restart: unless-stopped
  working_dir: /App
  entrypoint: ["/usr/bin/bash", "/wait-for-postgres.sh"]
  extra_hosts: ["host.docker.internal:host-gateway"]
  networks: [pilot-test]
  labels:
    traefik.enable: "true"
    traefik.http.routers.pilot.rule: PathPrefix(`/`)
    traefik.http.services.pilot.loadbalancer.server.port: "5545"
    # Сессия клиента Pilot живёт на узле, поэтому запросы одного клиента держим на одном узле.
    traefik.http.services.pilot.loadbalancer.sticky.cookie: "true"
    traefik.http.services.pilot.loadbalancer.sticky.cookie.name: pilot_session

services:
  pilot-test-1:
    <<: *pilot-node
    container_name: pilot-test-1
    hostname: pilot-test-1
    command:
      - "--pgConnectionString=Host=postgres-test:5432;Username=pilotuser;Password=${TEST_PILOT_PG_PASSWORD:-${PILOT_PG_PASSWORD}};Include Error Detail=true;Pooling=true;Connection Lifetime=0;Keepalive=3;DataBase=configurationdb"
      - "--indexPath=/usr/share/ascon/databases/cluster-indexes/pilot-test-1"
      - "--httpPort=5545"
    volumes:
      - ./wait-for-postgres.sh:/wait-for-postgres.sh:ro
      - ${DATA_ROOT}/test/databases:/usr/share/ascon/databases
      - ${DATA_ROOT}/test/aspnet:/root/.aspnet
      - ${DATA_ROOT}/test/license:/usr/share/ASCON
      - ${DATA_ROOT}/test/logs/pilot-test-1:/App/logs
      - ${DATA_ROOT}/test/server-settings:/server-settings

  pilot-test-2:
    <<: *pilot-node
    container_name: pilot-test-2
    hostname: pilot-test-2
    command:
      - "--pgConnectionString=Host=postgres-test:5432;Username=pilotuser;Password=${TEST_PILOT_PG_PASSWORD:-${PILOT_PG_PASSWORD}};Include Error Detail=true;Pooling=true;Connection Lifetime=0;Keepalive=3;DataBase=configurationdb"
      - "--indexPath=/usr/share/ascon/databases/cluster-indexes/pilot-test-2"
      - "--httpPort=5545"
    volumes:
      - ./wait-for-postgres.sh:/wait-for-postgres.sh:ro
      - ${DATA_ROOT}/test/databases:/usr/share/ascon/databases
      - ${DATA_ROOT}/test/aspnet:/root/.aspnet
      - ${DATA_ROOT}/test/license:/usr/share/ASCON
      - ${DATA_ROOT}/test/logs/pilot-test-2:/App/logs
      - ${DATA_ROOT}/test/server-settings:/server-settings

  pilot-test-lb:
    image: traefik:v3.6
    container_name: pilot-test-lb
    restart: unless-stopped
    command:
      - --providers.docker=true
      - --providers.docker.exposedByDefault=false
      - --providers.docker.network=pilot-test
      - --entrypoints.web.address=:5545
    ports: ["5547:5545"]
    volumes: ["/var/run/docker.sock:/var/run/docker.sock:ro"]
    networks:
      pilot-test:
        aliases: [pilot-server-test]   # админка и миграция ходят на это имя

networks:
  pilot-test:
    external: true
```

Замечания:

- Каталог индексов `--indexPath` лежит внутри `databases`, но **у каждого узла свой** (индекс можно перестроить, каталог узла создаётся сам). Общий каталог на двух узлах
  на проверке с RustFS (Windows, Docker Desktop) давал в журнале обоих узлов `CorruptIndexException` и `FileNotFoundException` каждые 5 с; с отдельными каталогами ошибок не было.
  Поэтому у уже работающего кластера со старым общим `cluster-indexes` смените пути и перезапустите узлы.
- Файловый архив S3 добавляется так же, как у монолита (Pilot Admin или админский API; адрес `http://rustfs-test:9000`). Его временная папка (например,
  `/usr/share/ascon/databases/s3temp`) должна лежать на общем томе `databases` и существовать до первой записи: `docker exec pilot-test-1 mkdir -p /usr/share/ascon/databases/s3temp`.
- Балансировщик читает Docker-сокет: он даёт полный доступ к Docker. Если это недопустимо, задайте узлы в статической конфигурации Traefik.
- Балансировщику нужен тот же адрес, что был у монолита (`5547` в примере): клиенты Pilot, админка и сервис миграции не меняют настройки.
- Строка `--pgConnectionString=...` содержит пароль PostgreSQL: он виден в `docker inspect` и в списке процессов контейнера. Ограничьте доступ к Docker на хосте.

Сохраните фрагмент рядом с `docker-compose.yml` как `cluster-test.yml` и запустите его отдельным проектом Compose, чтобы установщик
(он вызывает `docker compose up -d --remove-orphans` для основного проекта) не считал узлы лишними. Образ - тот же, что у монолита (из `.env`, иначе значение по умолчанию из `docker-compose.yml`):

```bash
export PILOT_SERVER_IMAGE=$(sed -n 's/^PILOT_SERVER_IMAGE=//p' .env)
[ -n "$PILOT_SERVER_IMAGE" ] || export PILOT_SERVER_IMAGE=$(sed -n 's/.*PILOT_SERVER_IMAGE:-\([^}]*\)}.*/\1/p' docker-compose.yml | head -1)
mkdir -p <корень данных>/test/logs/pilot-test-1 <корень данных>/test/logs/pilot-test-2
docker compose --env-file .env -p test-cluster -f cluster-test.yml up -d
```

После запуска проверьте, что оба узла ответили (`docker ps`, `docker logs pilot-test-1`) и что балансировщик отдаёт код 200 на `http://127.0.0.1:5547/`.

## 5. ServerHost

Серверные модули (ServerHost не ниже 1.2.0) кладут версии в общий каталог, поэтому все узлы должны смотреть в одну и ту же папку.
Если ничего не задавать, хост использует `/usr/share/ascon/databases` (общий том `databases` из таблицы выше). Свой путь задаётся переменной окружения
`SERVER_MODULES_DIR` одинаково во всех узлах. Остальные переменные хоста (`STATS_URL`, `STATS_API_KEY`) задавайте так же, как у монолита.

Сам ServerHost хранится в базе. Первый раз его кладут как серверное расширение (zip `ServerHost.zip`, объект типа `$PilotServerExtension` в папке расширений)
и перезапускают все узлы: в журнале каждого появится `Extension ServerHost version … - loaded`. Его новую версию записывает админка (страница «Серверные модули»), узлы читают хост только при старте,
поэтому после обновления хоста перезапустите все узлы. Остальные модули подхватываются на лету, перезапуск не нужен.

## 6. Настройки раздела «Серверы»

Раздел «Серверы» админки пишет `server-settings.conf` в каталог `server-settings` контура. В кластере его применяет `wait-for-postgres.sh` **каждого узла** при старте:

1. читает конфигурацию кластера командой `Daemon -rc` во временный каталог в `/tmp` контейнера (права 700, не в смонтированных каталогах);
2. применяет к ней `server-settings.conf` тем же кодом, что для `settings.xml`;
3. если что-то изменилось, ещё раз читает конфигурацию и сверяет с первым чтением (если её изменили между чтениями, повторяет до трёх раз), записывает командой `Daemon -wc`;
4. читает конфигурацию ещё раз, проверяет, что значения применились, и записывает `applied.conf` со строкой `Mode=cluster` - по ней админка узнаёт, что контур кластерный;
5. удаляет временные файлы (в них секреты) и запускает узел.

Каждая из команд идёт под `timeout` (120 с, меняется переменной `CLUSTER_CONFIG_TIMEOUT`). Любой сбой - предупреждение в журнале контейнера и запуск
узла с текущей конфигурацией: настройки не мешают старту. Если `server-settings.conf` нет, конфигурация кластера не трогается. Вывод `Daemon` в журнал не попадает
(в нём может быть строка подключения с паролем), только код возврата.

Что важно знать:

- Узлы читают конфигурацию только при старте и не перезаписывают её при остановке. Но административная операция на работающем узле (остановка и запуск базы через API,
  в том числе из сервиса миграции) записывает в конфигурацию **его** значения из памяти. Поэтому после изменения настроек перезапустите все узлы контура:
  остановите все, затем запустите. Пока не перезапущены все узлы, остановка или запуск базы на ещё не перезапущенном узле может вернуть прежние значения.
- Страница «Серверы» для кластера показывает именно это. Колонка «В конфигурации кластера» отражает значения при последнем старте узла, а не то, что держит в памяти каждый работающий узел.
- Одновременный старт двух узлов сужает риск, но не исключает его: оба правят одну конфигурацию. Блокировки в базе нет. Запускайте узлы по одному, если меняете настройки.
- Режим определяется только после первого старта узла через обёртку (нужен `applied.conf` с `Mode=cluster`; узел пишет его при каждом старте, даже если `server-settings.conf` ещё нет). До этого страница показывает тексты для монолита.

## 7. Сервис миграции и кластер

Контур считается кластерным, если админский API отдаёт у его базы непустой `RedisConnectionString`. Для такого контура миграция читает настройки из
`config.settings` базы конфигурации, а не из `settings.xml`, и берёт пароль PostgreSQL из окружения сервиса (`Migration__PgPasswords__<контур>`).
Переменные, правила и ограничения описаны в README сервиса миграции (`proadmin/migration/README.md`, раздел «Кластерный Pilot-Server»); в комплекте они уже заданы в `docker-compose.yml`
из `.env` (`<КОНТУР>_PILOT_PG_PASSWORD`, `MIGRATION_CONFIG_DATABASE`). Путь к `settings.xml` для кластерного контура в подключениях очистите.

## 8. Возврат к монолиту

Не проверялся. Для него нужны копии из раздела 1: дамп базы Pilot и `settings.before-cluster`.

## 9. Узлы на разных машинах

Схема, проверенная на двух машинах только на прежнем хранилище (с RustFS она на двух машинах не запускалась; на одной машине с RustFS проверено, раздел 0):
машина 1 - PostgreSQL, S3-хранилище, Redis контура, узел 1, балансировщик и NFS-сервер с каталогом данных контура; машина 2 - только узел 2.
Каталог индексов задайте каждому узлу свой (раздел 4).

1. **Общие каталоги по NFS.** На машине 1 каталог `<корень данных>/test` экспортируется машине 2 (`/etc/exports.d/test.exports`):
   `<корень данных>/test 10.0.0.2(rw,sync,no_subtree_check,no_root_squash)`. `no_root_squash` нужен: узел работает от root. На машине 2:
   `mount -t nfs -o vers=4.2,hard 10.0.0.1:<корень данных>/test <корень данных>/test` (в `/etc/fstab` с `_netdev`). Экспортируйте только нужные узлу
   подкаталоги (`databases`, `aspnet`, `license`, `logs`, `server-settings`), а не весь каталог контура: в нём лежат и данные PostgreSQL (файлы RustFS хранятся в томе Docker `<проект>_rustfs_<контур>`, не в этом каталоге).
2. **PostgreSQL, Redis и RustFS для второй машины.** В конфигурации кластера адреса записаны именами сети контура (`postgres-test:5432`, `redis-test:6379`,
   `rustfs-test:9000`). Узлу на машине 2 они задаются через `extra_hosts` на адрес машины 1, а на машине 1 эти порты публикуются на её внутреннем адресе
   (на стенде - ретрансляторами `alpine/socat` в сети контура: `-p 10.0.0.1:5432:5432 … tcp-connect:postgres-test:5432`). Эти порты открыты в сеть:
   ограничьте их брандмауэром адресами узлов.
3. **Узел 2** - тот же фрагмент, что `pilot-test-2` в разделе 4, но без сети контура: `extra_hosts: ["postgres-test:10.0.0.1", "rustfs-test:10.0.0.1", "redis-test:10.0.0.1"]`,
   `ports: ["10.0.0.2:5545:5545"]`, тома - из смонтированного NFS-каталога.
4. **Балансировщик** провайдером Docker видит только контейнеры своей машины, поэтому узлы задаются статически (провайдер `file`), с той же sticky-cookie:

   ```yaml
   http:
     routers:
       pilot: {rule: "PathPrefix(`/`)", service: pilot, entryPoints: [web]}
     services:
       pilot:
         loadBalancer:
           sticky: {cookie: {name: pilot_session}}
           servers:
             - url: "http://pilot-test-1:5545"
             - url: "http://10.0.0.2:5545"
   ```

   Traefik запускается с `--providers.file.filename=/etc/traefik/dynamic.yml` и этим файлом на месте `dynamic.yml`; Docker-сокет ему тогда не нужен.

Что наблюдалось: при первом старте узла 1 после перевода в кластер база ушла в `Broken` (тайм-аут подключения Npgsql в `ServerExtensionManager.InitAsync` через 6 минут
при низкой нагрузке); после перезапуска узла запустилась и больше не повторялось. Если узел оставил базу в `Broken`, перезапустите его.
Машина 2 с монтированием `hard` переживает перезапуск NFS-сервера: узел ждёт и продолжает работу сам.
