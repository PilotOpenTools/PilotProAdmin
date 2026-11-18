#!/usr/bin/env bash
# Проверка обязательных переменных compose на настоящем `docker compose config` (демон не нужен, запускать там, где есть docker compose):
#   bash build/selftest/compose-env-selftest.sh
# 1. .env, который создаёт install.sh, проходит `compose config` для каждой роли и во всех путях установки: чистая, повторный запуск,
#    несколько машин с заданными паролями, восстановление из бэкапа, данные без метки .installed. docker внутри install.sh подменён заглушкой.
# 2. Без обязательного значения compose отказывает с понятным текстом (во всём файле, а не только у активных ролей).
# 3. Контурные compose (prod, test, stage, metrics) и proadmin: то же на .env, заполненном вручную.
# 4. Хранилище (данные - именованный том rustfs_<контур> / rustfs_data): в конфиге каждой роли (контур prod/test/stage, отдельные compose) есть сервис rustfs-<контур> с алиасом rustfs-<контур>, образом rustfs/rustfs, ключом доступа из MINIO_USER/MINIO_PASSWORD и без образа minio.
set -u
STACK="$(cd "$(dirname "$0")/../.." && pwd)"
REAL_DOCKER="$(command -v docker)" || { echo "docker не найден"; exit 1; }
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
FAILED=0
ok() { if eval "$1"; then echo "  ok: $2"; else echo "  НЕ ПРОЙДЕНО: $2"; FAILED=$((FAILED + 1)); fi; }

mkdir -p "$T/bin"
printf '#!/bin/sh\necho "docker $*" >> "$STUB_LOG"\nexit 0\n' > "$T/bin/docker"; chmod +x "$T/bin/docker"
export STUB_LOG="$T/docker.log"
cfg_ok() { (cd "$1" && "$REAL_DOCKER" compose config -q 2>&1); }   # выводит ошибку compose, код - как у compose
val() { sed -n "s/^$1=//p" .env | tail -1; }
# Блок сервиса $1 из вывода `compose config` (на stdin): от «  сервис:» до следующего ключа того же уровня.
svc() { awk -v s="  $1:" '$0==s{f=1;next} f&&/^  [^ ]/{exit} f'; }
# Проверка хранилища контура $2 в выводе `compose config` $1: сервис, образ, алиасы, ключи доступа (user $3, password $4), нет образа minio.
check_rustfs() {
  local out="$1" c="$2" u="$3" p="$4" v="${5:-rustfs_$2}" b
  b="$(echo "$out" | svc "rustfs-$c")"
  ok "[ -n \"\$b\" ] && echo \"\$b\" | grep -q 'image: rustfs/rustfs:1.0.5\$' && echo \"\$b\" | grep -q 'container_name: rustfs-$c\$'" "$c: сервис rustfs-$c, образ rustfs/rustfs:1.0.5"
  ok "echo \"\$b\" | grep -q -- '- rustfs-$c\$'" "$c: в сети алиас rustfs-$c"
  ok "echo \"\$b\" | grep -q 'RUSTFS_ACCESS_KEY: $u\$' && echo \"\$b\" | grep -q 'RUSTFS_SECRET_KEY: $p\$'" "$c: ключ доступа берётся из MINIO_USER / MINIO_PASSWORD"
  ok "echo \"\$b\" | grep -q 'source: $v\$' && echo \"\$b\" | grep -q 'target: /data\$' && ! echo \"\$b\" | grep -q 'type: bind' && ! echo \"\$b\" | grep -q 'source: .*/\(rustfs\|minio\)'" "$c: данные в именованном томе $v (не бинд-маунт, не DATA_ROOT)"
  ok "echo \"\$out\" | awk '/^volumes:/{f=1;next} f&&/^[^ ]/{exit} f' | grep -q '^  $v:\$'" "$c: том $v объявлен в volumes"
  ok "echo \"\$out\" | svc \"pilot-server-$c\" | grep -A1 '^ *rustfs-$c:\$' | grep -q 'condition: service_healthy\$'" "$c: pilot-server-$c ждёт rustfs-$c по service_healthy (не service_started)"
  ok "! echo \"\$out\" | grep -qi 'image: minio/' && ! echo \"\$out\" | grep -q 'minio-$c:\$'" "$c: образа minio и сервиса minio-$c в конфигурации нет"
  ok "echo \"\$out\" | grep -q 'S3ServerUrl: http://rustfs-$c:9000' || ! echo \"\$out\" | grep -q 'S3ServerUrl'" "$c: адрес S3 у миграции - rustfs-$c, если миграция в конфигурации есть"
}
PW='PG_SUPERUSER_PASSWORD PILOT_PG_PASSWORD METRICS_WRITER_PASSWORD METRICS_READER_PASSWORD MINIO_PASSWORD GRAFANA_ADMIN_PASSWORD PILOT_ADMIN_PASSWORD'

fresh() {  # новая «папка комплекта» с DATA_ROOT внутри временного каталога
  rm -rf "$T/work" "$T/data"; : > "$STUB_LOG"; mkdir -p "$T/work"
  cp "$STACK/Install/install.sh" "$STACK/Install/gitlab-post-restore.rb" "$STACK/Install/.env.example" "$STACK/Install/docker-compose.yml" "$STACK/Install/docker-compose.tls.yml" "$T/work/"
  sed -i "s|^DATA_ROOT=.*|DATA_ROOT=$T/data|" "$T/work/.env.example"
  cd "$T/work" || exit 1
}
install() { PATH="$T/bin:$PATH" bash install.sh "$@" > "$T/install.out" 2>&1; }

echo "== .env установщика, чистая установка: каждая роль и набор по умолчанию"
for roles in prod test stage admin metrics gitlab prod,test,stage,admin,metrics; do
  fresh; install --no-data --skip-load --roles "$roles"; rc=$?
  err="$(cfg_ok "$T/work")"; crc=$?
  ok "[ $rc = 0 ] && [ $crc = 0 ]" "роли $roles: установка и compose config проходят ${err:+($err)}"
done

echo "== хранилище RustFS в compose установщика: роли prod, test, stage"
for c in prod test stage; do
  fresh; install --no-data --skip-load --roles "$c"
  C="$(echo "$c" | tr a-z A-Z)"
  printf '%s
' "${C}_MINIO_USER=ukey$c" "${C}_MINIO_PASSWORD=upass${c}12345" >> .env
  check_rustfs "$(cd "$T/work" && "$REAL_DOCKER" compose config 2>&1)" "$c" "ukey$c" "upass${c}12345"
done
fresh; install --no-data --skip-load --roles prod,test,stage,admin
out="$(cd "$T/work" && "$REAL_DOCKER" compose config 2>&1)"
for c in prod test stage; do
  ok "echo \"\$out\" | grep -q 'S3ServerUrl: http://rustfs-$c:9000'" "роль admin: адрес S3 контура $c - http://rustfs-$c:9000"
done
fresh; install --no-data --skip-load --roles prod,test,stage
ok "(cd \"$T/work\" && \"$REAL_DOCKER\" compose config 2>&1) | awk '/^services:/{f=1;next} /^[^ ]/{f=0} f' | grep -c 'image: rustfs/rustfs:1.0.5' | grep -q '^3\$'" "роли prod,test,stage: три сервиса RustFS на одном образе"

echo "== независимый выбор внешних сервисов"
fresh; cp .env.example .env
printf '%s\n' 'PROD_EXTERNAL_PG_HOST=pg.example' 'PROD_EXTERNAL_PG_PORT=5440' 'PROD_EXTERNAL_PG_DATABASE=pilot-prod' 'PROD_PILOT_PG_PASSWORD=external-pilot-fixture' \
  'TEST_EXTERNAL_S3_URL=https://objects.example' 'TEST_EXTERNAL_S3_BUCKET=pilot-test' 'TEST_S3_ACCESS_KEY=fixture-access' 'TEST_S3_SECRET_KEY=fixture-secret' \
  'GITHUB_TYPES_REPOSITORY=https://github.com/example/pilot-types' >> .env
install --no-data --skip-load --roles prod,test,admin,metrics; rc=$?
out="$("$REAL_DOCKER" compose config 2>&1)"; crc=$?
ok "[ $rc = 0 ] && [ $crc = 0 ]" "установка с внешними сервисами и compose config проходят"
ok "echo \"\$out\" | svc pilot-server-prod | grep -q 'PG_WAIT_HOST: pg.example' && echo \"\$out\" | svc pilot-server-prod | grep -q 'PG_WAIT_PORT: \"5440\"'" "Pilot использует внешний адрес PostgreSQL"
ok "! echo \"\$out\" | grep -Eq '^  (postgres-prod|rustfs-test|gitlab|gitlab-runner):$' && echo \"\$out\" | grep -q '^  postgres-test:$' && echo \"\$out\" | grep -q '^  rustfs-prod:$'" "PostgreSQL и S3 выбираются независимо; локальные дубли и GitLab пропущены"
ok "echo \"\$out\" | grep -q 'Migration__Defaults__1__S3ServerUrl: https://objects.example' && echo \"\$out\" | grep -q 'Migration__Defaults__0__PilotUrl: http://pilot-server-prod:5545/pilot-prod'" "миграция получает внешний S3 и имя базы Pilot"
ok "grep -q -- '-U pilotuser -d pilot-prod' \"\$STUB_LOG\" && grep -q 'rc ls' \"\$STUB_LOG\" && ! grep -q 'docker exec postgres-prod' \"\$STUB_LOG\"" "установщик проверяет внешние подключения и не выполняет локальную настройку PostgreSQL"
ok "install --no-data --skip-load --roles test --with-gitlab; [ \$? -ne 0 ]" "режим GitHub и локальный GitLab нельзя включить одновременно"

echo "== повторный запуск, данные уже есть"
fresh; install --no-data --skip-load --roles prod,admin
install --no-data --skip-load --roles prod,admin; rc=$?
ok "[ $rc = 0 ] && cfg_ok $T/work >/dev/null" "повторная установка: .env по-прежнему проходит compose config"

echo "== данные PostgreSQL без метки .installed (пароли-заглушки сохраняются)"
fresh; mkdir -p "$T/data/prod/postgres"; echo x > "$T/data/prod/postgres/PG_VERSION"
install --no-data --skip-load --roles prod; rc=$?
ok "[ $rc = 0 ] && cfg_ok $T/work >/dev/null" "compose config проходит"

echo "== несколько машин: пароли заданы вручную, роль admin"
fresh; cp .env.example .env
for k in $PW; do sed -i "s/^$k=.*/$k=manual-$k/" .env; done
printf 'INTERNAL_BIND=10.0.0.1\nPROD_HOST=10.0.0.2\nTEST_HOST=10.0.0.3\nSTAGE_HOST=10.0.0.3\nMETRICS_HOST=10.0.0.4\n' >> .env
install --no-data --skip-load --roles admin; rc=$?
ok "[ $rc = 0 ] && cfg_ok $T/work >/dev/null" "установка и compose config проходят"
ok "[ \"\$(val PG_SUPERUSER_PASSWORD)\" = manual-PG_SUPERUSER_PASSWORD ]" "заданные вручную пароли установка не трогает"

echo "== восстановление из бэкапа"
fresh
S="$T/work/data"; mkdir -p "$S/admin/env/prod" "$S/admin/env/proadmin" "$S/admin/proadmin-data" "$S/postgres/prod" "$S/pilot/prod/pilot-server/settings"
echo '{"ok": true, "created": "2026-01-01"}' > "$S/manifest.json"
printf 'PG_SUPERUSER_PASSWORD=pgpass123456\nPILOT_PG_PASSWORD=pilotpass12345\nMINIO_PASSWORD=miniopass12345\n' > "$S/admin/env/prod/.env"
printf 'MIGRATION_API_KEY=migkey123456\n' > "$S/admin/env/proadmin/.env"
echo 'select 1;' > "$S/postgres/prod/globals.sql"; echo dump > "$S/postgres/prod/pilot-ice_ru.dump"; echo '<Configuration/>' > "$S/pilot/prod/pilot-server/settings/settings.xml"
install --roles prod,admin --skip-load; rc=$?
err="$(cfg_ok "$T/work")"; crc=$?
ok "[ $rc = 0 ] && [ $crc = 0 ] && [ \"\$(val PROD_PG_SUPERUSER_PASSWORD)\" = pgpass123456 ]" "пароли из бэкапа записаны, compose config проходит ${err:+($err)}"

echo "== без обязательного значения compose отказывает, даже если сервис в неактивном профиле"
fresh; install --no-data --skip-load --roles prod
sed -i '/^GRAFANA_ADMIN_PASSWORD=/d' .env; echo COMPOSE_PROFILES=prod >> .env
err="$(cfg_ok "$T/work")"; crc=$?
ok "[ $crc != 0 ] && echo \"\$err\" | grep -q 'GRAFANA_ADMIN_PASSWORD'" "GRAFANA_ADMIN_PASSWORD: ошибка называет переменную"
for k in PG_SUPERUSER_PASSWORD PILOT_PG_PASSWORD METRICS_WRITER_PASSWORD METRICS_READER_PASSWORD MINIO_PASSWORD MINIO_USER MIGRATION_API_KEY; do
  fresh; install --no-data --skip-load --roles prod,admin
  sed -i "/^$k=/d" .env
  err="$(cfg_ok "$T/work")"; crc=$?
  ok "[ $crc != 0 ] && echo \"\$err\" | grep -q '$k'" "$k: ошибка называет переменную"
done
fresh; install --no-data --skip-load --roles prod,admin
for c in PROD TEST STAGE; do printf '%s_PG_SUPERUSER_PASSWORD=a\n%s_PILOT_PG_PASSWORD=a\n' $c $c >> .env; done
sed -i '/^PG_SUPERUSER_PASSWORD=/d;/^PILOT_PG_PASSWORD=/d' .env
ok "cfg_ok $T/work >/dev/null" "пароли заданы всеми тремя контурами: общие не нужны"

echo "== HTTPS: надстройка docker-compose.tls.yml подключается через COMPOSE_FILE из .env"
fresh; mkdir certs
printf -- '-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n' > certs/tls.crt
printf -- '-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----\n' > certs/tls.key
install --no-data --skip-load --roles admin,metrics; rc=$?
err="$(cfg_ok "$T/work")"; crc=$?
out="$(cd "$T/work" && "$REAL_DOCKER" compose config 2>&1)"
ok "[ $rc = 0 ] && [ $crc = 0 ]" "с certs/: установка и compose config проходят ${err:+($err)}"
ok "echo \"\$out\" | grep -q 'GF_SERVER_PROTOCOL: https' && echo \"\$out\" | grep -q 'Kestrel__Certificates__Default__KeyPath: /certs/tls.key' && echo \"\$out\" | grep -q 'ASPNETCORE_URLS: https://+:8080;http://127.0.0.1:8081'" "в конфигурации Grafana и админка включают HTTPS"
ok "[ \"\$(echo \"\$out\" | grep -c 'target: /certs/tls\\.')\" = 4 ]" "сертификат и ключ примонтированы обоим сервисам (4 монтирования)"
ok "echo \"\$out\" | grep -A8 'healthcheck:' | grep -q '8081'" "проверка состояния админки идёт на loopback-порт внутри контейнера"
sed -i '/^TLS_ADMIN_CRT=/d' .env
err="$(cfg_ok "$T/work")"; crc=$?
ok "[ $crc != 0 ] && echo \"\$err\" | grep -q 'TLS_ADMIN_CRT'" "надстройка без TLS_ADMIN_CRT: ошибка называет переменную"
fresh; install --no-data --skip-load --roles admin,metrics
out="$(cd "$T/work" && "$REAL_DOCKER" compose config 2>&1)"
ok "! echo \"\$out\" | grep -q 'GF_SERVER_PROTOCOL\\|/certs/tls'" "без certs/: конфигурация без HTTPS, как раньше"

echo "== контурные compose и proadmin на .env, заполненном вручную"
for c in prod test stage; do
  d="$T/$c"; rm -rf "$d"; mkdir -p "$d"; cp "$STACK/$c/docker-compose.yml" "$d/"
  # Роли метрик нужны только проду: на нём живёт база pilot_metrics.
  keys='PG_SUPERUSER_PASSWORD PILOT_PG_PASSWORD MINIO_USER MINIO_PASSWORD'
  printf '%s\n' "PG_SUPERUSER_PASSWORD=a" "PILOT_PG_PASSWORD=b" "MINIO_USER=u" "MINIO_PASSWORD=c" > "$d/.env"
  if [ "$c" = prod ]; then printf '%s\n' "METRICS_WRITER_PASSWORD=d" "METRICS_READER_PASSWORD=e" >> "$d/.env"; keys="$keys METRICS_WRITER_PASSWORD METRICS_READER_PASSWORD"; fi
  ok "cfg_ok $d >/dev/null" "$c: .env со всеми обязательными значениями проходит"
  check_rustfs "$(cd "$d" && "$REAL_DOCKER" compose config 2>&1)" "$c" u c rustfs_data
  for k in $keys; do
    sed "/^$k=/d" "$d/.env" > "$d/.env.cut"; mv "$d/.env.cut" "$d/.env.try"
    err="$(cd "$d" && "$REAL_DOCKER" compose --env-file .env.try config -q 2>&1)"; crc=$?
    ok "[ $crc != 0 ] && echo \"\$err\" | grep -q '$k'" "$c без $k: ошибка называет переменную"
  done
done
d="$T/metrics"; mkdir -p "$d"; cp "$STACK/metrics/docker-compose.yml" "$d/"
printf '%s\n' "GRAFANA_ADMIN_PASSWORD=a" "PG_PASSWORD=b" "METRICS_READER_PASSWORD=c" > "$d/.env"
ok "cfg_ok $d >/dev/null" "metrics: пароли Grafana, сборщиков и чтения PostgreSQL заданы - проходит"
for k in GRAFANA_ADMIN_PASSWORD PG_PASSWORD METRICS_READER_PASSWORD; do
  sed "/^$k=/d" "$d/.env" > "$d/.env.try"
  err="$(cd "$d" && "$REAL_DOCKER" compose --env-file .env.try config -q 2>&1)"; crc=$?
  ok "[ $crc != 0 ] && echo \"\$err\" | grep -q '$k'" "metrics без $k: ошибка называет переменную"
done
d="$T/proadmin"; mkdir -p "$d"; cp "$STACK/../proadmin/docker-compose.yml" "$d/"
printf 'MIGRATION_API_KEY=k\n' > "$d/.env"
ok "cfg_ok $d >/dev/null" "proadmin: ключ миграции задан - проходит"
: > "$d/.env"
err="$(cfg_ok "$d")"; crc=$?
ok "[ $crc != 0 ] && echo \"\$err\" | grep -q 'MIGRATION_API_KEY'" "proadmin без MIGRATION_API_KEY: ошибка называет переменную"

echo
if [ "$FAILED" = 0 ]; then echo "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ"; else echo "НЕ ПРОЙДЕНО: $FAILED"; exit 1; fi
