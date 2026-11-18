#!/bin/bash
# Роли метрик и база pilot_metrics; только там, где задано CREATE_METRICS_DB=1 (postgres прода).
#   metrics_writer - владелец базы pilot_metrics, под ней пишут сборщики (создают и меняют свои таблицы);
#   metrics_reader - только чтение базы pilot_metrics, под ней работает Grafana.
# Ни та ни другая не имеют прав на базы Pilot; у pilotuser прав на pilot_metrics нет.
# Образ postgres запускает файл при первом старте на пустой папке данных; установка с уже существующими данными
# (и после восстановления дампа pilot_metrics) запускает его повторно, он идемпотентен:
#   docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh
# Пароли берутся из окружения контейнера (psql \getenv): в аргументы процессов они не попадают.
# Файл одинаковый в Install/, prod/, test/ и stage/ - правьте все.
if [ "${CREATE_METRICS_DB:-}" = "1" ]; then
  # Пустой пароль завершает оболочку с ошибкой до psql (при source из entrypoint остановится и initdb: сбой виден сразу).
  : "${METRICS_WRITER_PASSWORD:?METRICS_WRITER_PASSWORD не задан}" "${METRICS_READER_PASSWORD:?METRICS_READER_PASSWORD не задан}"
  psql -v ON_ERROR_STOP=1 -U "${POSTGRES_USER:-postgres}" -d postgres <<'SQL'
-- Долгая транзакция сборщика не должна подвешивать установку: ALTER ... OWNER берёт ACCESS EXCLUSIVE.
SET lock_timeout = '30s';
\getenv wpw METRICS_WRITER_PASSWORD
\getenv rpw METRICS_READER_PASSWORD

SELECT format('CREATE ROLE %I NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT LOGIN NOREPLICATION NOBYPASSRLS', r)
  FROM (VALUES ('metrics_writer'), ('metrics_reader')) AS v(r)
  WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = r) \gexec
ALTER ROLE metrics_writer PASSWORD :'wpw';
ALTER ROLE metrics_reader PASSWORD :'rpw';

SELECT 'CREATE DATABASE pilot_metrics OWNER metrics_writer'
  WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'pilot_metrics') \gexec
ALTER DATABASE pilot_metrics OWNER TO metrics_writer;
REVOKE ALL ON DATABASE pilot_metrics FROM PUBLIC, pilotuser;
GRANT CONNECT ON DATABASE pilot_metrics TO metrics_reader;
-- В базы Pilot (их создаёт pilotuser) роли метрик не заходят; владелец сохраняет доступ как владелец.
-- Базы, которые Pilot-Server создаст позже, закрываются при следующем запуске скрипта.
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', datname)
  FROM pg_database WHERE datdba = 'pilotuser'::regrole AND datname <> 'pilot_metrics' \gexec

\connect pilot_metrics
SET lock_timeout = '30s';
-- REASSIGN OWNED не годится: он забрал бы у pilotuser и базы Pilot. Объекты переводятся по одному.
SELECT format('ALTER SCHEMA %I OWNER TO metrics_writer', nspname)
  FROM pg_namespace WHERE nspowner = 'pilotuser'::regrole \gexec
-- Последовательности, привязанные к столбцам, переходят вместе с таблицей.
SELECT format('ALTER %s %I.%I OWNER TO metrics_writer',
              CASE c.relkind WHEN 'v' THEN 'VIEW' WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'S' THEN 'SEQUENCE' WHEN 'f' THEN 'FOREIGN TABLE' ELSE 'TABLE' END,
              n.nspname, c.relname)
  FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
  WHERE c.relowner = 'pilotuser'::regrole AND c.relkind IN ('r', 'p', 'v', 'm', 'S', 'f')
    AND NOT (c.relkind = 'S' AND EXISTS (SELECT 1 FROM pg_depend d WHERE d.classid = 'pg_class'::regclass AND d.objid = c.oid
                                         AND d.refclassid = 'pg_class'::regclass AND d.deptype IN ('a', 'i'))) \gexec
SELECT format('ALTER %s %s OWNER TO metrics_writer', CASE p.prokind WHEN 'p' THEN 'PROCEDURE' WHEN 'a' THEN 'AGGREGATE' ELSE 'FUNCTION' END, p.oid::regprocedure)
  FROM pg_proc p WHERE p.proowner = 'pilotuser'::regrole \gexec
SELECT format('ALTER %s %s OWNER TO metrics_writer', CASE t.typtype WHEN 'd' THEN 'DOMAIN' ELSE 'TYPE' END, t.oid::regtype)
  FROM pg_type t WHERE t.typowner = 'pilotuser'::regrole AND t.typcategory <> 'A'
    AND (t.typtype IN ('e', 'd', 'r', 'm') OR (t.typtype = 'c' AND EXISTS (SELECT 1 FROM pg_class k WHERE k.oid = t.typrelid AND k.relkind = 'c'))) \gexec

REVOKE CREATE ON SCHEMA public FROM PUBLIC;
SELECT format('GRANT USAGE ON SCHEMA %I TO metrics_reader', nspname)
  FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname <> 'information_schema' \gexec
SELECT format('GRANT SELECT ON ALL TABLES IN SCHEMA %I TO metrics_reader', nspname)
  FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname <> 'information_schema' \gexec
ALTER DEFAULT PRIVILEGES FOR ROLE metrics_writer GRANT USAGE ON SCHEMAS TO metrics_reader;
ALTER DEFAULT PRIVILEGES FOR ROLE metrics_writer GRANT SELECT ON TABLES TO metrics_reader;
-- Проверка в конце: при остатке права уже выданы, повторный запуск после ручной передачи объектов довершит остальное.
SELECT (count(*) > 0) AS left_objects FROM pg_shdepend
  WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
    AND refclassid = 'pg_authid'::regclass AND refobjid = 'pilotuser'::regrole AND deptype = 'o' \gset
\if :left_objects
  DO $$ BEGIN RAISE EXCEPTION 'в pilot_metrics остались объекты pilotuser: передайте их metrics_writer вручную'; END $$;
\endif
SQL
fi
