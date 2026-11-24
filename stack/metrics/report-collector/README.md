# report-metrics-collector - статистика отчётов и модулей Pilot в Grafana

> С 2026-09-27 сервис принимает и события клиентских модулей (Pilot.ClientSdk, до 2.0.0 - Modules): `POST /api/module-events` / `metrics.module_events`,
> дашборды **«Pilot - клиентские модули»** и **«Pilot - серверные модули»**. См. раздел [«События модулей»](#события-модулей-pilotclientsdk).

Небольшой сервис (ASP.NET 10, Docker). Принимает от клиентского модуля [Reports](https://github.com/PilotOpenTools/PilotReports)
события «отчёт построен / ошибка / остановлен проверкой / отменён» и пишет их в PostgreSQL.
Grafana строит по ним дашборд **«Pilot - отчёты»**.

```
Клиенты Pilot (Reports) ──HTTP POST /api/report-events──report-metrics-collector :5310
                                                                   │
                                   postgres-prod / pilot_metrics.metrics.report_events
                                                                   │
                                          Grafana (источник pilot-metrics-prod)
```

Сервис пишет в ту же базу `pilot_metrics`, что и [сборщик метрик автоматизаций](../automation-collector/README.md),
и использует тот же источник данных в Grafana.

---

## Запуск

Сервис входит в роль `metrics` установочного комплекта: установщик собирает образ на месте и запускает его рядом с `postgres-prod`. Порт сервиса (5310) должен быть **доступен с рабочих мест пользователей Pilot**.

```powershell
# в папке установки
docker logs -f report-metrics-collector
```

База `pilot_metrics` и роли для неё создаются при первом старте `postgres-prod` на пустой папке данных (`stack/Install/init/postgres/02-metrics-roles.sh`): сервис пишет под ролью `metrics_writer` (владелец базы, создаёт и меняет свои таблицы), Grafana читает под `metrics_reader` (только `SELECT`). У обеих нет прав на базы Pilot, у `pilotuser` нет прав на `pilot_metrics`. Если контур создан раньше (база принадлежит `pilotuser`) или базы нет, приведите её командой (идемпотентна; пароли ролей берёт из окружения контейнера, то есть из `METRICS_WRITER_PASSWORD` и `METRICS_READER_PASSWORD` в `.env` папки установки):

```powershell
docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh
```

Установщик делает это сам при каждом запуске.

Таблица и индексы создаются сервисом при старте.

Проверка:

```powershell
curl http://localhost:5310/health                       # {"status":"ok"}
curl "http://localhost:5310/api/report-events/summary?hours=24"   # при заданном ключе: -H "X-Api-Key: <ключ>"
```

После запуска задайте адрес `http://<docker-хост>:5310/api/module-events` в общих настройках Pilot, раздел «PilotOpenTools отправка статистики», на каждом контуре. По нему отправляют статистику Pilot.ClientSdk и версия Reports, читающая общие настройки (в [CHANGELOG Reports](https://github.com/PilotOpenTools/PilotReports/blob/main/CHANGELOG.md) она значится как «Не выпущено»). Остальные версии Reports читают адрес `…/api/report-events` из своего `Reports.config.json`.

### Переменные `.env`

| Переменная | По умолчанию | Назначение |
|---|---|---|
| `COLLECTOR_PORT` | `5310` | внешний порт сервиса |
| `COLLECTOR_API_KEY` | пусто | если задан - клиенты обязаны слать заголовок `X-Api-Key` (тот же ключ в поле «API-ключ» общих настроек Pilot). **Пусто - ключ не проверяется вообще**: любой, кто достанет до порта, может писать события; при старте сервис пишет об этом предупреждение в журнал. Ключ нужен для приёма (`POST`) и для сводок (`GET …/summary`); `/health` открыт всегда |
| `PG_HOST`, `PG_PORT`, `PG_DB`, `PG_USER`, `PG_PASSWORD` | `postgres-prod`, `5432`, `pilot_metrics`, `metrics_writer`, `password` | подключение к PostgreSQL (внутри docker-сети порт всегда `5432`) |
| `ENV_MAP` | `:5549=stage;:5547=test;stage=stage;test=test;*=prod` | контур по адресу сервера Pilot: `подстрока=контур;…;*=по_умолчанию`, первое совпадение без учёта регистра |

> Ключ API защищает от случайного мусора, а не от злоумышленника: он доступен клиенту Pilot у всех пользователей. Ключ сравнивается за постоянное время.
> Сервис рассчитан на внутреннюю сеть - не публикуйте порт наружу. Лимитов частоты нет. Срок хранения событий - 30 дней (см. ниже), старше этого срока данных в таблицах и сводках нет.
>
> **Срок хранения.** В событиях есть логин, ФИО и имя машины, поэтому сервис сам удаляет из `metrics.report_events` и `metrics.module_events` строки со временем `ts` старше `STATS_RETENTION_DAYS` дней (в приложении `Collector__RetentionDays`, по умолчанию 30). Удаление идёт при старте и затем раз в час, пачками по 5000 строк по индексу `ts`. Ошибка удаления пишется в журнал предупреждением, приём событий продолжается. `0` - не удалять (при старте предупреждение в журнале), отрицательное или нечисловое значение - сервис не стартует. Сводки за `hours` больше срока хранения вернут только оставшиеся данные. Копии базы, сделанные до удаления, хранят старые строки до собственной ротации.
>
> Дополнительные настройки приложения (переменные `Collector__…`): `Collector__MaxBatch` - событий в пачке (500), `Collector__MaxBodyBytes` - размер тела запроса (10 МБ, больше - отказ `413`), `Collector__RetentionDays` - срок хранения (в `.env` стека `STATS_RETENTION_DAYS`).

---

## API

### `POST /api/report-events`

Тело - одно событие или массив, до 500 штук. Поля в snake_case:

```json
[{
  "run_id": "5f0c…",                 // обязательно, уникально для построения
  "ts": "2026-09-26T10:00:00.000Z",  // начало построения (UTC)
  "report": "Реестр НОПРИЗ",         // обязательно
  "report_id": null,
  "status": "success",               // success | error | validation | cancelled
  "duration_ms": 1830,
  "pages": 4,
  "error_type": null,
  "error_message": null,
  "login": "ivanov",
  "person": "Иванов Иван Иванович",
  "server": "https://pilot-stage.example.com/PilotDb",
  "client_type": "PilotBim",
  "client_version": "26.13.0.62219",
  "module_version": "1.0.0",
  "machine": "PC-123"
}]
```

Заголовки:

- `X-Api-Key` - если задан `COLLECTOR_API_KEY`;
- `X-Client-Time` - время отправки по часам клиента (ISO 8601). Если часы клиента сбиты больше чем на 2 минуты, сервис сдвигает `ts` событий на эту разницу. Событие, время которого после этого сдвига оказалось в будущем (больше 10 минут) или старше 60 дней, получает время приёма - и с заголовком, и без него.

Ответ `200`: `{"accepted": 3, "duplicates": 0, "rejected": []}`.

- Дубли по `run_id` пропускаются, поэтому повторная отправка безопасна.
- Событие с неверным `status`, пустым `run_id` или `report`, а также `null` в массиве попадает в `rejected`, остальные пачки принимаются.
- `400` - битый JSON или слишком большая пачка. `401` - неверный ключ. `413` - тело больше `Collector__MaxBodyBytes`.

### `GET /health`

`200 {"status":"ok"}` или `503`, если база недоступна. Ключ не требуется.

### `GET /api/report-events/summary?hours=24`

Сводка по отчётам за последние N часов: построений, успешных, ошибок. Нужна, чтобы проверить сбор без Grafana. Требует `X-Api-Key`, если ключ задан. `hours` - от 1 до 8784 (366 суток), без параметра или при значении не больше 0 - 24; больше - `400`.

---

## Таблица `metrics.report_events`

| Колонка | Смысл |
|---|---|
| `run_id` | id построения (PK, защита от дублей) |
| `env` | контур: prod / test / stage (по `server` и `ENV_MAP`) |
| `ts` | начало построения |
| `received_at` | когда событие пришло в сервис |
| `report`, `report_id` | название отчёта (из `Report.Begin`), GUID отчёта, если передан |
| `status`, `is_success` | `success` / `error` / `validation` / `cancelled`; `is_success` вычисляется |
| `duration_ms`, `pages` | длительность и число страниц |
| `error_type`, `error_message` | тип и текст исключения (для `error` и `validation`) |
| `login`, `person` | кто строил |
| `server`, `client_type`, `client_version`, `module_version`, `machine` | откуда |

Полезные запросы:

```sql
-- Топ отчётов за неделю
SELECT report, count(*) AS builds, count(*) FILTER (WHERE is_success) AS ok
FROM metrics.report_events WHERE ts > now() - interval '7 days'
GROUP BY report ORDER BY builds DESC LIMIT 20;

-- Отчёты, которыми никто не пользовался 90 дней (кандидаты в архив)
SELECT report, max(ts) AS last_used FROM metrics.report_events
GROUP BY report HAVING max(ts) < now() - interval '90 days' ORDER BY last_used;

-- Частые ошибки
SELECT report, error_message, count(*) FROM metrics.report_events
WHERE status = 'error' AND ts > now() - interval '30 days'
GROUP BY 1, 2 ORDER BY 3 DESC;
```

---

## Grafana

Источник данных `pilot-metrics-prod` (`postgres-prod:5432`, база `pilot_metrics`) подключается автоматически.

Дашборд «Pilot - отчёты» в `metrics/config/grafana/dashboards/` не входит. Импорт: *Dashboards / New / Import / Upload dashboard JSON file* / `grafana/dashboard-pilot-reports.json`. В установочном комплекте он подключается сам.

Что на дашборде:

| Панель | Содержание |
|---|---|
| Плитки | построений, успешных, доля успешных, ошибок, остановлено проверкой, пользователей |
| Сводка по отчётам | по каждому отчёту: построений, успешно, ошибок, проверка, отменено, % успешных, среднее время и p95, пользователей, последнее построение |
| Построения по статусам | столбцы по интервалам, цвет - статус |
| Построения по отчётам | топ-10 отчётов за период |
| Сравнение с предыдущим периодом | сейчас и раньше, дельта и прирост в процентах; сдвиг - переменная «Сравнить с периодом назад» |
| Кто строит отчёты | по пользователям |
| Самые долгие отчёты | p95 времени построения |
| Последние неудачные построения | время, отчёт, статус, текст ошибки, пользователь, версии |

Переменные: **Контур** (prod/test/stage), **Отчёт** (мультивыбор), **Сравнить с периодом назад** (1/7/14/30 дней).

SQL панелей задаётся в `grafana/gen_dashboard.py`. После правки пересоберите JSON:

```bash
python grafana/gen_dashboard.py grafana/dashboard-pilot-reports.json
```

---

## События модулей (Pilot.ClientSdk)

Клиентский модуль [Pilot.ClientSdk](https://github.com/PilotOpenTools/PilotClientSdk) (`113Pilot.ClientSdk.ext2`) шлёт пачки событий модулей:
действия пользователей (`ModuleStats.Run/Use`) и «модуль загружен» с версией (через минуту после старта клиента).

### `POST /api/module-events`

Одно событие или массив (до `Collector__MaxBatch`), тот же `X-Api-Key` и `X-Client-Time`, что у отчётов.

```json
[{
  "event_id": "4b0f…", "ts": "2026-09-27T10:00:00.000Z",
  "kind": "action", "module": "087OpenStoradge.ext2",
  "module_title": "01.087 - Открыть Storadge - (OpenStoradge) - (CICD)", "module_version": "1.0.1",
  "action": "Открыть Storadge", "status": "success", "duration_ms": 850, "items": 12,
  "login": "ivanov", "person": "Иванов Иван", "server": "http://pilot:5547/pilot-ice_ru",
  "client_type": "PilotBim", "client_version": "26.13.0.0", "machine": "PC-01", "lib_version": "1.0.0"
}]
```

`kind` - `action` (нужен `action`) или `load`; `status` - `success` / `error` / `cancelled` (по умолчанию `success`).
Контур - по `server`, как у отчётов (`ENV_MAP`). Ответ: `{"accepted": n, "duplicates": n, "rejected": [...]}`; повтор по `event_id` пропускается.

`GET /api/module-events/summary?hours=24` - по модулям: действий, ошибок, пользователей, последняя версия (версия самого позднего по времени события). Ключ и ограничение `hours` - как у сводки отчётов.

### Таблица `metrics.module_events`

`event_id` (PK), `env`, `ts`, `received_at`, `kind`, `module`, `module_title`, `module_version`, `action`, `status`,
`is_success` (вычисляемая), `duration_ms`, `items`, `error_type`, `error_message`, `login`, `person`, `server`,
`client_type`, `client_version`, `machine`, `lib_version`. Индексы: `ts`, `(module_title, ts)`, `(env, kind, ts)`.
Создаётся при старте сервиса.

### Дашборды «Pilot - клиентские модули» и «Pilot - серверные модули»

Обе панели строятся по `metrics.module_events` и различаются полем `client_type`:

| Дашборд | Файл, UID | Чьи события |
|---|---|---|
| Pilot - клиентские модули | `grafana/dashboard-pilot-modules.json`, `pilot-modules` | Pilot.ClientSdk из клиентов Pilot (`client_type` ≠ `pilot-server`) |
| Pilot - серверные модули | `grafana/dashboard-pilot-server-modules.json`, `pilot-server-modules` | ServerHost: загрузка модуля и каждый вызов активности (`client_type = pilot-server`) |

Источник `pilot-metrics-prod`. Оба JSON лежат и в `metrics/config/grafana/dashboards/` - Grafana подгружает их сама в папку «CI/CD Pilot».
Панели: плитки, сводка по модулям, действия/вызовы по статусам и модулям, действия (активности) внутри модулей, **версии модулей
на рабочих местах / на серверах**, пользователи (у серверных - контуры и серверы), самые долгие действия, последние ошибки.
Переменные: **Контур**, **Модуль**. SQL - в `grafana/gen_dashboard_modules.py`; после правки пересоберите оба JSON и скопируйте в `metrics/config/grafana/dashboards/`:

```bash
python grafana/gen_dashboard_modules.py grafana/dashboard-pilot-modules.json client
python grafana/gen_dashboard_modules.py grafana/dashboard-pilot-server-modules.json server
```

Контур серверных событий определяется по полю `server`: ServerHost пишет туда `pilot-server <контейнер> (<PG_WAIT_HOST>)`,
например `(postgres-test)` / `test` по `ENV_MAP`.

---

## Разработка

```bash
dotnet test tests/ReportMetrics.Collector.Tests -c Release   # или dotnet test -c Release в этом каталоге (есть .sln)
# локальный запуск (нужен PostgreSQL)
set ConnectionStrings__Metrics=Host=localhost;Port=5433;Database=pilot_metrics;Username=metrics_writer;Password=<пароль metrics_writer>
dotnet run --project src/ReportMetrics.Collector
```

Модульные тесты (без PostgreSQL) покрывают нормализацию событий и модулей, определение контура, исправление времени, проверку ключа и ограничение `hours`. Приём запросов целиком, SQL хранилищ и дашборды тестами не покрыты: их проверяют запуском сервиса с PostgreSQL.

## Обслуживание

Старые события удаляет сам сервис (см. «Срок хранения»). Разовая ручная очистка нужна только при `STATS_RETENTION_DAYS=0`:

```powershell
docker exec -ti postgres-prod psql -U metrics_writer -d pilot_metrics -c "DELETE FROM metrics.report_events WHERE ts < now() - INTERVAL '365 days';"
```

| Симптом | Причина | Решение |
|---|---|---|
| `База недоступна (n/30)` в логах | контейнер не в сети `pilot-prod` или база не создана | `docker network connect pilot-prod report-metrics-collector`, `createdb …` |
| Клиенты не достучались | порт закрыт файрволом или в адресе статистики указан `localhost` | открыть `5310` для сети рабочих мест, поправить адрес в общих настройках Pilot |
| Много `unknown` в `env` | в `ENV_MAP` нет правила по умолчанию | добавить `*=prod` |
| В Grafana пусто | нет источника `pilot-metrics-prod` или период не тот | проверить источник; `SELECT min(ts), max(ts) FROM metrics.report_events;` |
