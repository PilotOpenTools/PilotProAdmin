# Контур stage

Копия устройства прода: Pilot-Server, PostgreSQL, RustFS, сборщик метрик автоматизаций. Pilot-myAdmin Web не ставится.

| Компонент | Контейнер | Порт на хосте | Данные |
|---|---|---|---|
| Pilot-Server | `pilot-server-stage` | **5549** | `C:\Ascon-Docker\stage\{databases,pilot-server\settings,server-settings,logs,license,aspnet}` |
| PostgreSQL | `postgres-stage` | 5435 | `C:\Ascon-Docker\stage\postgres` - **своя** папка данных |
| RustFS (S3) | `rustfs-stage` | 9020 (API), 9021 (консоль) | том Docker `pilot-stage_rustfs_data` (не папка `C:\Ascon-Docker`), бакет `pilot-ice-ru` |
| Метрики автоматизаций | `automation-metrics-stage` (`metrics/automation-collector`) | - | пишет в `pilot_metrics` на `postgres-prod`, `env = stage` |

Сеть - `pilot-stage`. Адрес для клиента и админки: `http://localhost:5549/pilot-ice_ru`. Пароли контура лежат в `stage/.env`.

> **Почему 5549, а не 5548 из таблицы портов.** На этой машине 5545 и 5548 заняты системой (http.sys, `netsh int ipv4 show excludedportrange protocol=tcp`) - похоже, установлен «родной» Pilot-Server для Windows (5545 HTTP, 5548 HTTPS). Порт меняется переменной `PILOT_PORT`.

## Как создан

1. `settings.xml` - копия продового с заменой `postgres-prod` / `postgres-stage`, `rustfs-prod` / `rustfs-stage` (Id архивов те же, логирование автоматизаций включено). В папке `FileArchive` - только файл-маркер `<Id>.pilotfa`. Id самой базы у стейджа свой: его выдаёт `init-stage.sh` и сам правит `settings.xml` (одинаковый Id смешал бы кеши клиента прода и стейджа).
2. `./init-stage.sh <дамп прода> <globals.sql прода>` - поднимает PostgreSQL и RustFS (данные RustFS лежат в томе Docker, права настраивать не нужно; бакет создаёт клиентом `rc` из образа RustFS), создаёт роль `pilotuser` и базу `pilot-ice_ru` со схемой Pilot из дампа, запускает сервер. Оба файла лежат в бэкапе: `postgres/prod/pilot-ice_ru.dump` и `postgres/prod/globals.sql` (второй путь можно передать переменной `GLOBALS`). Имя схемы Pilot (`dbp<версия>`) скрипт берёт из дампа.
3. **Данные** - полная копия прод / стейдж сервисом миграции (админка / «Миграция»). Файлы (папка и S3) докопированы им же.

```bash
docker compose up -d        # обычный запуск после первичной инициализации
```

## Связанные изменения

- Сервис миграции (`proadmin/migration`): сеть `pilot-stage`, тома `/mnt/stage/settings` (чтение) и `/mnt/stage/databases`, подключение «Стейдж».
- Сборщик отчётов (`metrics/report-collector`): `ENV_MAP` относит адреса с `:5549` к стейджу, с `:5547` - к тесту (раньше контур определялся только по слову `stage`/`test` в адресе, и локальные серверы попадали в прод).
- Grafana: отдельная настройка не нужна - дашборды «Pilot автоматизации» и «Pilot отчёты» берут список контуров из `pilot_metrics`, стейдж появляется в переменной «Контур».
- В админке (`Настройки / Серверы Pilot / Стейдж`) адрес `http://localhost:5549/pilot-ice_ru`. Ветка у стейджа не задаётся: на него записываются ветки `release/*` модулей и автоматизаций и ветка `release` типов.
