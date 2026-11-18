# Мониторинг и статистика

Один compose-проект `metrics`: нагрузка и логи всех контейнеров плюс статистика автоматизаций, отчётов и модулей Pilot.

| Сервис | Что делает | Порт на хосте |
|---|---|---|
| `prometheus` | хранит метрики, 30 дней | 9090 (по умолчанию только этот хост, `INTERNAL_BIND`) |
| `node-exporter` | метрики хоста | - |
| `cadvisor` | метрики контейнеров | - |
| `loki` | хранит логи, 30 дней | - |
| `promtail` | собирает логи всех контейнеров через `docker.sock` и журнал входов хоста | - |
| `grafana` | дашборды | 3000 |
| `automation-metrics-test`, `automation-metrics-stage` | читают лог автоматизаций, [README](automation-collector/README.md) | - |
| `report-metrics-collector` | принимает статистику отчётов и модулей, [README](report-collector/README.md) | 5310 |

Статистика лежит в базе `pilot_metrics` на `postgres-prod` и хранится без ограничения срока. Подключения к ней заданы в `.env`.

## Запуск

Контуры Pilot должны работать раньше: сборщики автоматизаций берут лог из контейнеров `pilot-server-test` и `pilot-server-stage`, а база находится на проде. Если контур работает кластером, логи узлов лежат в подкаталогах `logs/<узел>/`: задайте сервису сборщика `LOG_PATH: /App/logs/*/automation.log` (метка узла берётся из имени каталога, подробности в [README сборщика](automation-collector/README.md), §4.4).

```powershell
copy .env.example .env     # обязательно: GRAFANA_ADMIN_PASSWORD, PG_PASSWORD = пароль metrics_writer, METRICS_READER_PASSWORD = пароль metrics_reader (те же значения, что в prod/.env)
docker compose up -d --build
```

Обычно стек запускают целиком: `.\shared\stack.ps1 up` из папки `stack`.

Grafana открывается на `http://<хост>:3000`, логин и пароль берутся из `.env` при первом запуске. Потом пароль меняют в интерфейсе.

## Дашборды

Источники данных `Prometheus`, `Loki` и `Pilot metrics (prod)` с uid `pilot-metrics-prod` подключаются сами. Дашборды из `config/grafana/dashboards/` тоже подключаются сами, в папку **CI/CD Pilot**:

| Дашборд | Файл |
|---|---|
| Нагрузка хоста (Node Exporter) | `host-load.json` |
| Нагрузка контейнеров (cAdvisor) | `docker-containers.json` |
| Логи и авторизация (Loki) | `logs-auth.json` |
| Pilot - клиентские модули | `dashboard-pilot-modules.json` |
| Pilot - серверные модули | `dashboard-pilot-server-modules.json` |

Дашборды автоматизаций и отчётов сюда не входят. На этом стенде они импортированы вручную из `automation-collector/grafana_dashboard_pilot_automation.json` и `report-collector/grafana/dashboard-pilot-reports.json`. В установочном комплекте их подключает сборка: `build/build.ps1` копирует оба файла в `Install/monitoring`.

## Логи и авторизация

Promtail помечает `category="auth"` строки логов контейнеров, похожие на вход: `Failed password`, `authentication failure`, `401`, `sign_in`, `invalid user`. Это разбор по шаблону, а не журнал аудита. Правило работает по метке `job="docker"`, которую ставит `relabel_configs` (без неё не срабатывали и панели «Объём логов по сервисам» и «Все логи контейнеров»). На Linux к этому добавляются `/var/log/auth.log` и `/var/log/secure` (`job="host_auth"`).

Loki отвергает записи старше 7 дней (`reject_old_samples_max_age: 168h`), хотя хранит логи 30 дней: при задержке доставки старые строки будут потеряны.

Логи новых контейнеров, в том числе GitLab, подхватываются сами. Метрики GitLab не снимаются: в нём отключён встроенный мониторинг, а задание `gitlab` в `prometheus.yml` закомментировано.

## Windows

На Docker Desktop `node-exporter` и `cadvisor` показывают виртуальную машину WSL, а не сам компьютер. Журнал входов в систему не собирается. Настоящая нагрузка машины видна только на Linux.

## cAdvisor и хранилище containerd

cAdvisor 0.49.1 на Docker с хранилищем образов containerd (по умолчанию в Docker 29 при чистой установке) не ставил метку `name`, и панели дашборда «Нагрузка контейнеров» были пусты. Поддержка этого хранилища появилась в cAdvisor 0.54 (образ в комплекте берётся из `ghcr.io/google/cadvisor`). В комплекте 0.60.6: на Docker 29.9 с containerd метки `name` есть у всех контейнеров, дашборд заполнен. Отдельных флагов не нужно, монтирования прежние.

## Безопасность

- `GRAFANA_ADMIN_PASSWORD`, `PG_PASSWORD` и `METRICS_READER_PASSWORD` обязательны: без них compose не запустится (значений по умолчанию нет).
- У Prometheus нет аутентификации; порт 9090 слушает только этот хост (`INTERNAL_BIND` в `.env` меняет адрес - тогда закройте порт брандмауэром). Управление Prometheus по HTTP выключено: перечитать конфигурацию можно командой `docker kill -s SIGHUP prometheus`.
- Источник `Pilot metrics (prod)` подключается под ролью `metrics_reader`: у неё только `CONNECT` к `pilot_metrics`, `USAGE` на схемы и `SELECT` на таблицы (включая будущие); записать данные и зайти в базы Pilot она не может. Источник помечен только для чтения и не редактируется из интерфейса Grafana. Сборщики пишут под `metrics_writer` (владелец `pilot_metrics`, без прав на базы Pilot). Обе роли создаёт `prod/init/postgres/02-metrics-roles.sh`. Для контура, созданного раньше: `docker compose up -d` в `prod` с новыми `METRICS_WRITER_PASSWORD` и `METRICS_READER_PASSWORD` в `prod/.env`, затем `docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh`, затем `PG_USER=metrics_writer` и те же пароли в `metrics/.env`. Пароли ролей меняет `shared/rotate-contour-secrets` с `-Env prod`.
- Сборщики автоматизаций получают только каталог логов контура (`${DATA_ROOT}/<контур>/logs`, только чтение), а не все тома Pilot-Server.
- Порт 5310 должен быть доступен с рабочих мест Pilot. Сборщик рассчитан на внутреннюю сеть. `COLLECTOR_API_KEY` нужен и для приёма событий, и для сводок (`GET …/summary`); `/health` открыт. Без ключа сборщик принимает запросы от кого угодно и пишет об этом предупреждение в журнал при старте (установщик генерирует ключ при чистой установке на одну машину).
- Статистика с логином, ФИО и именем машины хранится 30 дней: сборщики сами удаляют старшие события (`STATS_RETENTION_DAYS` в `.env`, `0` - не удалять). Loki хранит логи 720 часов (30 дней).

## Структура

```
docker-compose.yml, .env.example
config/prometheus/prometheus.yml
config/loki/loki-config.yml
config/promtail/promtail-config.yml
config/grafana/provisioning/   источники данных и подключение дашбордов
config/grafana/dashboards/     дашборды папки «CI/CD Pilot»
automation-collector/          сборщик статистики автоматизаций
report-collector/              сборщик статистики отчётов и модулей
```

Тома `grafana_grafana_data`, `grafana_prometheus_data` и `grafana_loki_data` названы явно, в них лежат данные прежнего проекта `grafana`. При изменении состава сервисов обновите `stack/Install` и `backup/backup.ps1`.
