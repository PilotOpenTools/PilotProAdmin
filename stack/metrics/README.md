# Мониторинг и статистика

Роль `metrics` установочного комплекта (`Install/docker-compose.yml`): нагрузка и логи всех контейнеров плюс статистика автоматизаций, отчётов и модулей Pilot.

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

Роль ставит установщик релиза, когда на вопрос о статистике выбрано «установить здесь». Пароли (`GRAFANA_ADMIN_PASSWORD`, `METRICS_WRITER_PASSWORD`, `METRICS_READER_PASSWORD`) он создаёт сам в `.env` папки установки, конфигурацию из `config/` кладёт в `monitoring/`, сборщики собирает на месте.

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

Дашборды автоматизаций и отчётов сюда не входят. Их подключает релиз: `scripts/package-release.ps1` кладёт `automation-collector/grafana_dashboard_pilot_automation.json` и `report-collector/grafana/dashboard-pilot-reports.json` в `monitoring/grafana/dashboards/` установщика.

## Логи и авторизация

Promtail помечает `category="auth"` строки логов контейнеров, похожие на вход: `Failed password`, `authentication failure`, `401`, `sign_in`, `invalid user`. Это разбор по шаблону, а не журнал аудита. Правило работает по метке `job="docker"`, которую ставит `relabel_configs` (без неё не срабатывали и панели «Объём логов по сервисам» и «Все логи контейнеров»). На Linux к этому добавляются `/var/log/auth.log` и `/var/log/secure` (`job="host_auth"`).

Loki отвергает записи старше 7 дней (`reject_old_samples_max_age: 168h`), хотя хранит логи 30 дней: при задержке доставки старые строки будут потеряны.

Логи новых контейнеров, в том числе GitLab, подхватываются сами. Метрики GitLab не снимаются: в нём отключён встроенный мониторинг, а задание `gitlab` в `prometheus.yml` закомментировано.

## Windows

На Docker Desktop `node-exporter` и `cadvisor` показывают виртуальную машину WSL, а не сам компьютер. Журнал входов в систему не собирается. Настоящая нагрузка машины видна только на Linux.

## cAdvisor и хранилище containerd

cAdvisor 0.49.1 на Docker с хранилищем образов containerd (по умолчанию в Docker 29 при чистой установке) не ставил метку `name`, и панели дашборда «Нагрузка контейнеров» были пусты. Поддержка этого хранилища появилась в cAdvisor 0.54 (образ в комплекте берётся из `ghcr.io/google/cadvisor`). В комплекте 0.60.6: на Docker 29.9 с containerd метки `name` есть у всех контейнеров, дашборд заполнен. Отдельных флагов не нужно, монтирования прежние.

## Безопасность

- `GRAFANA_ADMIN_PASSWORD`, `METRICS_WRITER_PASSWORD` и `METRICS_READER_PASSWORD` обязательны: без них compose не запустится (значений по умолчанию нет). Установщик создаёт их сам.
- У Prometheus нет аутентификации; порт 9090 слушает только этот хост (`INTERNAL_BIND` в `.env` меняет адрес - тогда закройте порт брандмауэром). Управление Prometheus по HTTP выключено: перечитать конфигурацию можно командой `docker kill -s SIGHUP prometheus`.
- Источник `Pilot metrics (prod)` подключается под ролью `metrics_reader`: у неё только `CONNECT` к `pilot_metrics`, `USAGE` на схемы и `SELECT` на таблицы (включая будущие); записать данные и зайти в базы Pilot она не может. Источник помечен только для чтения и не редактируется из интерфейса Grafana. Сборщики пишут под `metrics_writer` (владелец `pilot_metrics`, без прав на базы Pilot). Обе роли создаёт `Install/init/postgres/02-metrics-roles.sh`; установщик запускает его при каждом запуске, и он же выставляет ролям пароли `METRICS_WRITER_PASSWORD` и `METRICS_READER_PASSWORD` из `.env` папки установки. Чтобы сменить пароли, задайте новые значения в `.env` и запустите установщик ещё раз: роли получат новые пароли, сборщики и Grafana пересоздадутся с ними (раздел «Смена паролей» в `Install/docs/installation.md`).
- Сборщики автоматизаций получают только каталог логов контура (`${DATA_ROOT}/<контур>/logs`, только чтение), а не все тома Pilot-Server.
- Порт 5310 должен быть доступен с рабочих мест Pilot. Сборщик рассчитан на внутреннюю сеть. `COLLECTOR_API_KEY` нужен и для приёма событий, и для сводок (`GET …/summary`); `/health` открыт. Без ключа сборщик принимает запросы от кого угодно и пишет об этом предупреждение в журнал при старте (установщик генерирует ключ при чистой установке на одну машину).
- Статистика с логином, ФИО и именем машины хранится 30 дней: сборщики сами удаляют старшие события (`STATS_RETENTION_DAYS` в `.env`, `0` - не удалять). Loki хранит логи 720 часов (30 дней).

## Структура

```
config/prometheus/prometheus.yml
config/loki/loki-config.yml
config/promtail/promtail-config.yml
config/grafana/provisioning/   источники данных и подключение дашбордов
config/grafana/dashboards/     дашборды папки «CI/CD Pilot»
automation-collector/          сборщик статистики автоматизаций
report-collector/              сборщик статистики отчётов и модулей
```

При изменении состава сервисов обновите `Install/docker-compose.yml` и комплект переноса данных `proadmin/transfer/backup.ps1`.
