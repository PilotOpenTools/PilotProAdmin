#!/usr/bin/env bash
# Первичная инициализация контура stage (один раз). Запускать из Git Bash в папке stage.
# 1) PostgreSQL: роль pilotuser и база pilot-ice_ru со схемой Pilot (из дампа прода);
# 2) RustFS: бакет pilot-ice-ru;
# 3) запуск pilot-server-stage.
# Данные затем приводятся к проду сервисом миграции (полная копия прод / стейдж).
#   ./init-stage.sh <дамп базы прода> <globals.sql прода>
# Оба файла лежат в бэкапе: <копия>/postgres/prod/pilot-ice_ru.dump и <копия>/postgres/prod/globals.sql
# (второй путь можно передать переменной GLOBALS).
set -euo pipefail
export MSYS_NO_PATHCONV=1
# .env не исполняем через «. ./.env»: в значениях бывают «;» и другие символы shell.
# Пароли контура - из stage/.env (docker compose читает его сам, скрипту они нужны для psql и rc); значение из .env важнее окружения.
for k in DATA_ROOT PILOT_PG_PASSWORD MINIO_USER MINIO_PASSWORD; do
  v=""; [ -f .env ] && v="$(sed -n "s/^$k=//p" .env | tail -1 | tr -d '\r')"
  if [ -n "$v" ]; then export "$k=$v"; fi
done
: "${PILOT_PG_PASSWORD:?Задайте PILOT_PG_PASSWORD в stage/.env}" "${MINIO_PASSWORD:?Задайте MINIO_PASSWORD в stage/.env}"
DUMP="${1:?Укажите дамп базы прода и globals.sql: ./init-stage.sh <pilot-ice_ru.dump> <globals.sql>}"
GLOBALS="${2:-${GLOBALS:-}}"
[ -n "$GLOBALS" ] || { echo "Укажите globals.sql прода вторым аргументом или переменной GLOBALS (pg_dumpall --globals-only; есть в бэкапе: postgres/prod/globals.sql)"; exit 1; }

# Данные RustFS - в именованном томе (создаётся compose, владелец 10001 наследуется от образа): chown/mkdir не нужны.
RUSTFS_IMAGE=rustfs/rustfs:1.0.5
docker compose up -d postgres-stage rustfs-stage
until docker exec postgres-stage pg_isready -U postgres >/dev/null 2>&1; do sleep 2; done

if docker exec postgres-stage psql -U postgres -tAc "select 1 from pg_database where datname='pilot-ice_ru'" | grep -q 1; then
  echo "База pilot-ice_ru уже есть - PostgreSQL пропускаю."
else
  docker cp "$GLOBALS" postgres-stage:/tmp/globals.sql
  docker exec postgres-stage psql -U postgres -q -f /tmp/globals.sql 2>&1 | grep -v "already exists" || true
  # globals прода переносят и пароль pilotuser прода - вернуть пароль стейджа (он же в settings.xml).
  # Пароль - переменной psql (:'pw'), а не вставкой в SQL; значение не попадает в аргументы команды.
  printf '%s\n' "ALTER ROLE pilotuser PASSWORD :'pw';" | PW="$PILOT_PG_PASSWORD" docker exec -i -e PW postgres-stage sh -c 'psql -U postgres -q -v ON_ERROR_STOP=1 -v "pw=$PW"'
  docker exec postgres-stage createdb -U postgres -O pilotuser pilot-ice_ru
  docker cp "$DUMP" postgres-stage:/tmp/pilot.dump
  docker exec postgres-stage pg_restore -U postgres -d pilot-ice_ru --exit-on-error --section=pre-data --section=data /tmp/pilot.dump
  # Схема Pilot называется dbp<версия> и меняется вместе с версией сервера: берём ту, что пришла с дампом.
  SCHEMA="$(docker exec postgres-stage psql -U postgres -d pilot-ice_ru -tAc "select nspname from pg_namespace where nspname ~ '^dbp[0-9.]+\$' order by nspname desc limit 1")"
  [ -n "$SCHEMA" ] || { echo "В дампе нет схемы Pilot (dbp<версия>)"; exit 1; }
  echo "DELETE FROM \"$SCHEMA\".databaseid a USING \"$SCHEMA\".databaseid b WHERE a.id = b.id AND a.ctid > b.ctid;" | docker exec -i postgres-stage psql -U postgres -d pilot-ice_ru -v ON_ERROR_STOP=1
  docker exec postgres-stage pg_restore -U postgres -d pilot-ice_ru --exit-on-error --section=post-data /tmp/pilot.dump
  # Свой идентификатор базы: клиент кеширует данные по нему (Databases\<id>), с id прода
  # кеши прода и стейджа смешиваются / «отменены изменения из-за конфликта» и зависание клиента.
  OLD_ID=$(docker exec postgres-stage psql -U postgres -d pilot-ice_ru -tAc "select data from \"$SCHEMA\".databaseid")
  NEW_ID=$(docker exec postgres-stage psql -U postgres -d pilot-ice_ru -qtAc "update \"$SCHEMA\".databaseid set data = gen_random_uuid()::text returning data")
  SETTINGS="${DATA_ROOT:-C:/Ascon-Docker}/stage/pilot-server/settings/settings.xml"
  [ -f "$SETTINGS" ] && sed -i "s/$OLD_ID/$NEW_ID/g" "$SETTINGS"
  echo "Идентификатор базы: $OLD_ID / $NEW_ID"
fi

# Бакет создаёт клиент rc из образа RustFS (отдельного образа клиента нет). Ждём готовности хранилища.
for i in $(seq 1 60); do
  docker exec rustfs-stage curl -fsS http://127.0.0.1:9000/health >/dev/null 2>&1 && break
  [ "$i" -lt 60 ] || { echo "rustfs-stage не стал готовым за 2 минуты: docker logs rustfs-stage (ключи, том rustfs_data)"; exit 1; }
  sleep 2
done
# Учётные данные не должны попадать в аргументы docker run (их видно в списке процессов): простые значения идут
# в RC_HOST_<алиас> (значения в нём URL-кодируются, простые символы кодировать не нужно), остальные - через переменные окружения.
# HOME=/tmp: rc хранит алиас в домашней папке, у пользователя rustfs она может быть недоступна на запись.
RCUSER="${MINIO_USER:?Задайте MINIO_USER в stage/.env}"
case "$RCUSER$MINIO_PASSWORD" in
  *[!A-Za-z0-9._~-]*) RCU="$RCUSER" RCP="$MINIO_PASSWORD" docker run --rm --network pilot-stage -e RCU -e RCP -e HOME=/tmp --entrypoint sh "$RUSTFS_IMAGE" -c "rc alias set m http://rustfs-stage:9000 \"\$RCU\" \"\$RCP\" >/dev/null && rc mb --ignore-existing m/pilot-ice-ru" ;;
  *) RC_HOST_m="http://$RCUSER:$MINIO_PASSWORD@rustfs-stage:9000" docker run --rm --network pilot-stage -e RC_HOST_m --entrypoint rc "$RUSTFS_IMAGE" mb --ignore-existing m/pilot-ice-ru ;;
esac

docker compose up -d pilot-server-stage
echo "Готово: http://localhost:5549 (база pilot-ice_ru). Дальше - полная копия прод / стейдж в админке."
