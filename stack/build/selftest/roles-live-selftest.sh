#!/usr/bin/env bash
# Проверка 02-metrics-roles.sh на настоящем PostgreSQL (одноразовый сервер внутри контейнера, без стенда):
#   docker run --rm -v "<путь к stack>:/stack:ro" postgres:16 bash /stack/build/selftest/roles-live-selftest.sh
# Права проверяются поведением: reader читает, но не пишет; writer выполняет DDL; метрики не заходят в базы Pilot;
# pilotuser не заходит в pilot_metrics; повторный запуск идемпотентен; старая раскладка (владелец pilotuser) приводится.
set -u
SCRIPT=/stack/Install/init/postgres/02-metrics-roles.sh
BIN="$(ls -d /usr/lib/postgresql/*/bin | head -1)"
D=/tmp/pgdata; FAILED=0
ok() { if eval "$1"; then echo "  ok: $2"; else echo "  НЕ ПРОЙДЕНО: $2"; FAILED=$((FAILED + 1)); fi; }
rm -rf "$D"; mkdir -p "$D"; chown postgres "$D"
su postgres -c "$BIN/initdb -D $D -A trust >/dev/null && $BIN/pg_ctl -D $D -o '-c listen_addresses= -c unix_socket_directories=/tmp' -w -l /tmp/pg.log start >/dev/null" || { echo "сервер не запустился"; exit 1; }
export PGHOST=/tmp PGUSER=postgres
q() { psql -v ON_ERROR_STOP=1 -qAt "$@"; }
as() { local u="$1" db="$2"; shift 2; PGPASSWORD="${PW[$u]}" psql -h 127.0.0.1 -v ON_ERROR_STOP=1 -qAt -U "$u" -d "$db" -c "$*" 2>&1; }
# trust по сокету не проверяет пароли: переключаем TCP на scram, чтобы роли заходили по паролю
declare -A PW=([metrics_writer]='w"p$1' [metrics_reader]="r'p\$2" [pilotuser]=pu)
echo "host all all 127.0.0.1/32 scram-sha-256" > "$D/pg_hba.new"; cat "$D/pg_hba.conf" >> "$D/pg_hba.new"; cp "$D/pg_hba.new" "$D/pg_hba.conf"
su postgres -c "$BIN/pg_ctl -D $D -o '-c listen_addresses=127.0.0.1 -c unix_socket_directories=/tmp' -w restart >/dev/null"
runroles() { CREATE_METRICS_DB=1 METRICS_WRITER_PASSWORD="${PW[metrics_writer]}" METRICS_READER_PASSWORD="${PW[metrics_reader]}" bash "$SCRIPT" 2>&1; }

# Старая раскладка: pilotuser владеет базой метрик, таблицами, схемой, типами; база Pilot создана им же.
q -c "CREATE ROLE pilotuser LOGIN CREATEDB PASSWORD 'pu'"
q -c "CREATE DATABASE pilot_metrics OWNER pilotuser" -c "CREATE DATABASE \"pilot-ice_ru\" OWNER pilotuser"
as pilotuser pilot_metrics "CREATE SCHEMA metrics; CREATE TABLE metrics.ev (id bigserial PRIMARY KEY, who text); INSERT INTO metrics.ev(who) VALUES ('a'); CREATE TYPE metrics.mood AS ENUM ('x'); CREATE DOMAIN metrics.pos AS int CHECK (VALUE > 0); CREATE VIEW metrics.v AS SELECT * FROM metrics.ev" >/dev/null
out="$(runroles)"; rc=$?
ok "[ $rc = 0 ]" "скрипт на старой раскладке (таблицы, enum, domain, представление у pilotuser) завершился без ошибок"
ok "[ -z \"\$(q -d pilot_metrics -c \"SELECT 1 FROM pg_shdepend WHERE refobjid = 'pilotuser'::regrole AND deptype = 'o' AND dbid = (SELECT oid FROM pg_database WHERE datname = 'pilot_metrics')\")\" ]" "у pilotuser в pilot_metrics не осталось объектов"
ok "[ \"\$(as metrics_reader pilot_metrics 'SELECT who FROM metrics.ev')\" = a ]" "reader читает таблицу"
ok "as metrics_reader pilot_metrics 'INSERT INTO metrics.ev(who) VALUES (1)' | grep -q 'permission denied'" "reader не пишет"
ok "as metrics_reader pilot_metrics 'CREATE TABLE metrics.x(i int)' | grep -q 'permission denied'" "reader не создаёт таблицы"
ok "as metrics_reader pilot_metrics 'CREATE TEMP TABLE t(i int)' | grep -q 'permission denied'" "reader не создаёт временные таблицы"
ok "! as metrics_writer pilot_metrics 'CREATE TABLE metrics.w(i int); INSERT INTO metrics.w VALUES (1); INSERT INTO metrics.ev(who) VALUES (2); CREATE INDEX ON metrics.w(i); ALTER TABLE metrics.ev ADD COLUMN z int' | grep -q 'ERROR'" "writer выполняет DDL и пишет (в т.ч. в таблицу, созданную pilotuser)"
ok "[ \"\$(as metrics_reader pilot_metrics 'SELECT count(*) FROM metrics.w')\" = 1 ]" "reader видит таблицу, созданную writer позже (права по умолчанию)"
ok "as metrics_writer pilot-ice_ru 'SELECT 1' | grep -q 'permission denied'" "writer не заходит в базу Pilot"
ok "as metrics_reader pilot-ice_ru 'SELECT 1' | grep -q 'permission denied'" "reader не заходит в базу Pilot"
ok "as pilotuser pilot_metrics 'SELECT 1' | grep -q 'permission denied'" "pilotuser не заходит в pilot_metrics"
ok "[ \"\$(as pilotuser pilot-ice_ru 'SELECT 1')\" = 1 ]" "pilotuser по-прежнему заходит в свою базу Pilot"
out2="$(runroles)"; rc2=$?
ok "[ $rc2 = 0 ]" "повторный запуск идемпотентен"
ok "[ \"\$(as metrics_reader pilot_metrics 'SELECT count(*) FROM metrics.w')\" = 1 ]" "после повторного запуска права reader прежние"

# База Pilot, созданная после запуска: закрывается следующим запуском
q -c "CREATE DATABASE \"pilot-late\" OWNER pilotuser"
ok "[ \"\$(as metrics_reader pilot-late 'SELECT 1')\" = 1 ]" "ограничение: новая база Pilot открыта для CONNECT до следующего запуска скрипта"
runroles >/dev/null
ok "as metrics_reader pilot-late 'SELECT 1' | grep -q 'permission denied'" "после запуска скрипта новая база Pilot закрыта"

# Неподдерживаемый объект pilotuser (расширение/иное): сбой не оставляет промежуточное состояние - права reader уже выданы
q -d pilot_metrics -c "CREATE COLLATION metrics.cc (provider = libc, locale = 'C')" -c "ALTER COLLATION metrics.cc OWNER TO pilotuser"
out3="$(runroles)"; rc3=$?
ok "[ $rc3 != 0 ] && echo \"\$out3\" | grep -q 'остались объекты pilotuser'" "объект, который скрипт не умеет передать: код ошибки и понятное сообщение"
ok "[ \"\$(as metrics_reader pilot_metrics 'SELECT count(*) FROM metrics.w')\" = 1 ]" "при таком сбое права reader всё равно выданы (проверка в конце скрипта)"

su postgres -c "$BIN/pg_ctl -D $D -m fast stop >/dev/null"
[ "$FAILED" = 0 ] && echo "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ" || { echo "Не пройдено проверок: $FAILED"; exit 1; }
