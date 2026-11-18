#!/bin/bash
# Выполняется образом postgres один раз - при первом старте на пустой папке данных.
# Роль pilotuser для Pilot-Server (--attachpg). Роли и база метрик (pilot_metrics) - в 02-metrics-roles.sh.
# Пароль передаётся переменной psql и подставляется как литерал (:'pw'): кавычка в пароле не ломает SQL.
# Файл одинаковый в Install/, prod/, test/ и stage/ - правьте все.
set -e
psql -v ON_ERROR_STOP=1 -v "pw=${PILOT_PG_PASSWORD:?PILOT_PG_PASSWORD не задан}" -U "$POSTGRES_USER" <<'SQL'
CREATE ROLE pilotuser WITH NOSUPERUSER CREATEDB NOCREATEROLE NOINHERIT LOGIN NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1 PASSWORD :'pw';
SQL
