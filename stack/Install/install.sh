#!/usr/bin/env bash
# Установка (и обновление) стека на Linux-хосте. Аналог install.ps1:
#   ./install.sh [--from-backup <каталог>] [--gitlab-host <ip>] [--roles prod,admin] [--no-data] [--with-gitlab] [--with-myadmin] [--skip-load]
# Наши образы - из images/, остальные docker скачивает из интернета. Повторный запуск безопасен.
# С данными: если в папке есть data/ или указан бэкап, всё поднимается из него; пароли берутся из бэкапа,
# данные восстанавливаются один раз и только в пустой DATA_ROOT (метка DATA_ROOT/.installed). --no-data - чистая установка.
# DATA_ROOT на Linux по умолчанию /opt/ascon-docker, другой путь задаётся в .env.
# .env и PASSWORDS.md получают права 600, каталоги с секретами в DATA_ROOT закрываются от остальных пользователей.
# --roles - только эти сервисы: prod, test, stage, admin, metrics, gitlab; адреса других машин - в .env.
# Хранилище файлов - RustFS, его данные лежат в именованном томе Docker (<проект>_rustfs_<контур>), не в DATA_ROOT.
set -euo pipefail
export MSYS_NO_PATHCONV=1

SNAP=""; GITLAB_NEW=""; ROLES=""; NO_DATA=0; SKIP_LOAD=0; GITLAB=0; MYADMIN=0
while [ $# -gt 0 ]; do case "$1" in
  --from-backup)  SNAP="$(cd "$2" && pwd)"; shift ;;
  --gitlab-host)  GITLAB_NEW="$2"; shift ;;
  --roles)        ROLES="$2"; shift ;;
  --no-data)      NO_DATA=1 ;;
  --with-gitlab)  GITLAB=1 ;;
  --with-myadmin) MYADMIN=1 ;;
  --skip-load)    SKIP_LOAD=1 ;;
  *) echo "Неизвестный параметр: $1"; exit 1 ;;
esac; shift; done
# Только после разбора ключей: путь к бэкапу может быть относительным.
cd "$(dirname "$0")"
step() { printf '\n==> %s\n' "$*"; }
fail() { printf '%s\n' "$*" >&2; exit 1; }
# Сеть бывает нестабильной: скачивание образов - три попытки с паузой.
retry() {
  n=1
  until "$@"; do
    [ "$n" -lt 3 ] || return 1
    echo "  ! попытка $n из 3 не удалась, повтор через 15 с"; n=$((n + 1)); sleep 15
  done
}
gen_key() { head -c "${1:-20}" /dev/urandom | od -An -tx1 | tr -d ' \n'; }
# Бакет RustFS клиентом rc из образа RustFS. Учётные данные не должны попадать в аргументы docker run (их видно в списке процессов):
# RC_HOST_<алиас> читается из окружения, поэтому так передаются только простые значения (в URL они кодируются); остальные идут
# через переменные окружения внутри sh -c (в аргументах rc их видно только на время запуска).
# Флаг --ignore-existing у rc mb описан в документации клиента, на живом RustFS не проверен.
rc_bucket() {  # $1 - сеть, $2 - контейнер RustFS, $3 - логин, $4 - пароль
  case "$3$4" in
    *[!A-Za-z0-9._~+=-]*) RCU="$3" RCP="$4" docker run --rm --network "$1" -e RCU -e RCP -e HOME=/tmp --entrypoint sh "$RUSTFS_IMG" -c \
      "rc alias set m http://$2:9000 \"\$RCU\" \"\$RCP\" >/dev/null && rc mb --ignore-existing m/pilot-ice-ru" ;;
    *) RC_HOST_m="http://$3:$4@$2:9000" docker run --rm --network "$1" -e RC_HOST_m --entrypoint rc "$RUSTFS_IMG" mb --ignore-existing m/pilot-ice-ru ;;
  esac
}
# Учётка Pilot с доступом только к бакету pilot-ice-ru (как в shared/rotate-contour-secrets.ps1). Значения - через окружение внутри sh -c.
# Повтор для существующих учётки и политики проверен на RustFS 1.0.5; «политика уже привязана» у attach - не ошибка.
rc_pilot_user() {  # $1 - сеть, $2 - контейнер RustFS, $3 - логин root, $4 - пароль root, $5 - учётка Pilot, $6 - её секрет
  RCU="$3" RCP="$4" PILOT_U="$5" PILOT_S="$6" \
  PILOT_POLICY='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:ListAllMyBuckets","s3:GetBucketLocation"],"Resource":["arn:aws:s3:::*"]},{"Effect":"Allow","Action":["s3:*"],"Resource":["arn:aws:s3:::pilot-ice-ru","arn:aws:s3:::pilot-ice-ru/*"]}]}' \
  docker run --rm --network "$1" -e RCU -e RCP -e PILOT_U -e PILOT_S -e PILOT_POLICY -e HOME=/tmp --entrypoint sh "$RUSTFS_IMG" -c \
    "rc alias set m http://$2:9000 \"\$RCU\" \"\$RCP\" >/dev/null && printf %s \"\$PILOT_POLICY\" > /tmp/p.json && rc admin policy create m/ pilot-bucket /tmp/p.json >/dev/null && rc admin user add m/ \"\$PILOT_U\" \"\$PILOT_S\" >/dev/null && { rc admin policy attach m/ pilot-bucket --user \"\$PILOT_U\" >/dev/null 2>&1; true; }"
}
# Контейнер RustFS отвечает (curl есть в образе); до этого rc mb получил бы отказ в соединении.
wait_rustfs() {  # $1 - контур
  n=0
  until docker exec "rustfs-$1" curl -fsS http://127.0.0.1:9000/health >/dev/null 2>&1; do n=$((n + 1)); [ "$n" -le 60 ] || return 1; sleep 2; done
}

# .env не исполняем через «. ./.env»: в значениях бывает «;» (ENV_MAP), shell принял бы это за команды.
cfg_from() { [ -f "$2" ] && sed -n "s/^$1=//p" "$2" | tail -1 | tr -d '\r' || true; }
cfg() { cfg_from "$1" .env; }
# Новый файл создаётся с правами 600 (umask 077): mv подменяет .env и не должен сбрасывать права.
env_set() {
  (umask 077; K="$1" V="$2" awk 'BEGIN{k=ENVIRON["K"];v=ENVIRON["V"]} index($0,k"=")==1{print k"="v;f=1;next} {print} END{if(!f)print k"="v}' .env > .env.tmp)
  mv .env.tmp .env
}
# Все KEY=VALUE файла бэкапа - в .env (с префиксом $2), кроме путей исходной машины и настроек сборщика.
env_merge() {
  [ -f "$1" ] || return 0
  tr -d '\r' < "$1" | grep -E '^[A-Z0-9_]+=' | grep -vE '^(DATA_ROOT|[A-Z]+_ROOT|PG_HOST|PG_PORT|PG_DB|PG_USER|PG_PASSWORD|MIGRATION_URL)=' | while IFS= read -r line; do
    env_set "$2${line%%=*}" "${line#*=}"
  done
}
replace_in() { [ -f "$1" ] && OLD="$2" NEW="$3" awk 'BEGIN{o=ENVIRON["OLD"];n=ENVIRON["NEW"]} {while((i=index($0,o))>0){printf "%s%s",substr($0,1,i-1),n;$0=substr($0,i+length(o))} print}' "$1" > "$1.tmp" && mv "$1.tmp" "$1" || true; }

if [ -z "$SNAP" ] && [ "$NO_DATA" = 0 ] && [ -f data/manifest.json ]; then SNAP="$(pwd)/data"; fi
if [ -n "$SNAP" ]; then
  [ -f "$SNAP/manifest.json" ] || { echo "В $SNAP нет manifest.json - это не каталог запуска бэкапа"; exit 1; }
  if ! tr -d ' \r\n' < "$SNAP/manifest.json" | grep -q '"ok":true'; then echo "Бэкап $SNAP снят с ошибками, восстанавливать его нельзя. Подробности - в его backup.log"; exit 1; fi
fi

if [ ! -f .env ]; then
  # Стек уже стоит, а .env потерян: новый .env из шаблона вернул бы заглушки вместо рабочих паролей.
  [ ! -f /opt/ascon-docker/.installed ] || fail "Найден /opt/ascon-docker/.installed, но .env рядом со скриптом нет. Верните .env прежней установки: пароли в нём должны совпасть с рабочими."
  (umask 077; cp .env.example .env)
  [ -n "$SNAP" ] || echo "Создан .env из .env.example: пароли-заглушки установка заменит сама (одна машина) или остановится (несколько машин)."
fi
chmod 600 .env
ROOT="$(cfg DATA_ROOT)"; ROOT="${ROOT:-/opt/ascon-docker}"
# В шаблоне путь для Windows: на Linux он создал бы каталог с именем «C:».
case "$ROOT" in [A-Za-z]:*) if [ "$(uname -s)" = Linux ]; then ROOT=/opt/ascon-docker; env_set DATA_ROOT "$ROOT"; fi ;; esac
# Путь по умолчанию записываем в .env: иначе compose подставит свой, путь для Windows.
[ -n "$(cfg DATA_ROOT)" ] || env_set DATA_ROOT "$ROOT"

# Роли этой машины: ключ, затем запись в .env, иначе всё, кроме GitLab.
ROLES_CHOSEN=1
[ -n "$ROLES" ] || ROLES="$(cfg ROLES)"
ROLES="$(printf '%s' "$ROLES" | tr -d ' \t')"
if [ -z "$ROLES" ]; then ROLES=prod,test,stage,admin,metrics; ROLES_CHOSEN=0; fi
for r in $(printf '%s' "$ROLES" | tr ',' ' '); do case "$r" in prod|test|stage|admin|metrics|gitlab) ;; *) echo "Неизвестная роль: $r. Допустимо: prod, test, stage, admin, metrics, gitlab"; exit 1 ;; esac; done
has_role() { case ",$ROLES," in *,"$1",*) return 0 ;; *) return 1 ;; esac; }
if [ "$GITLAB" = 1 ] && ! has_role gitlab; then ROLES="$ROLES,gitlab"; fi
MY_CONTOURS=""; for c in prod test stage; do if has_role "$c"; then MY_CONTOURS="$MY_CONTOURS $c"; fi; done

RESTORE=0; OLD_HOST=""
if [ -n "$SNAP" ] && [ -f "$ROOT/.installed" ]; then echo "Стек в $ROOT уже установлен (метка .installed): данные из $SNAP не восстанавливаю."; fi
if [ -n "$SNAP" ] && [ ! -f "$ROOT/.installed" ]; then
  [ ! -f "$ROOT/.restore-started" ] || fail "Прошлое восстановление GitLab в $ROOT не завершилось (метка .restore-started): данные могли измениться, повторное восстановление их затрёт. Журнал: $ROOT/gitlab/logs/restore.log. Очистите $ROOT и запустите установку снова."
  for c in $MY_CONTOURS; do
    if [ -n "$(ls -A "$ROOT/$c/postgres" 2>/dev/null)" ]; then
      echo "В $ROOT/$c/postgres уже есть данные. Укажите пустой DATA_ROOT в .env. Если прошлое восстановление оборвалось, очистите эту папку и запустите снова."; exit 1
    fi
  done
  RESTORE=1
  echo "Восстановление из бэкапа $SNAP"
  # Пароли должны совпасть с бэкапом: они записаны в settings.xml, в ролях PostgreSQL и в служебных данных RustFS.
  # Бэкапы до 2026-11-05 хранят настройки админки и GitLab на уровень глубже.
  for f in gitlab proadmin metrics; do
    if [ -f "$SNAP/admin/env/$f/.env" ]; then env_merge "$SNAP/admin/env/$f/.env" ""; else env_merge "$SNAP/admin/env/admin/$f/.env" ""; fi
  done
  env_merge "$SNAP/admin/env/prod/.env" PROD_
  env_merge "$SNAP/admin/env/test/.env" TEST_
  env_merge "$SNAP/admin/env/stage/.env" STAGE_
  if [ -n "$(cfg PROD_PILOT_ADMIN)" ]; then env_set PILOT_ADMIN_LOGIN "$(cfg PROD_PILOT_ADMIN)"; env_set PILOT_ADMIN_PASSWORD "$(cfg PROD_PILOT_PASSWORD)"; fi
  OLD_HOST="$(cfg GITLAB_HOST_IP)"
  if [ -n "$GITLAB_NEW" ]; then
    if [ -n "$OLD_HOST" ] && [ "$OLD_HOST" != localhost ]; then replace_in .env "$OLD_HOST" "$GITLAB_NEW"; fi
    env_set GITLAB_HOST_IP "$GITLAB_NEW"
  fi
  # Роли выбраны явно: GitLab только там, где он указан.
  if [ "$ROLES_CHOSEN" = 0 ] && ls "$SNAP"/gitlab/*_gitlab_backup.tar >/dev/null 2>&1; then ROLES="$ROLES,gitlab"; fi
fi

# Профили запоминаем в .env: так их видят и повторный install, и ручной docker compose.
PREVIOUS="$(cfg COMPOSE_PROFILES)"
if [ "$ROLES_CHOSEN" = 0 ]; then case ",$PREVIOUS," in *,gitlab,*) has_role gitlab || ROLES="$ROLES,gitlab" ;; esac; fi
GITLAB=0; if has_role gitlab; then GITLAB=1; fi
if [ "$GITLAB" = 1 ] && [ -n "$(cfg GITHUB_TYPES_REPOSITORY)" ]; then fail 'Для GitHub выберите роли без gitlab и уберите --with-gitlab.'; fi
LOCAL_PG=""; LOCAL_S3=""
for c in $MY_CONTOURS; do
  U="$(printf '%s' "$c" | tr 'a-z' 'A-Z')"
  if [ -z "$(cfg "${U}_EXTERNAL_PG_HOST")" ]; then LOCAL_PG="$LOCAL_PG $c"
  elif [ "$RESTORE" = 1 ]; then fail 'Для внешнего PostgreSQL используйте чистую установку (--no-data). Данные внешнего сервиса восстанавливает его владелец.'; fi
  if [ -z "$(cfg "${U}_EXTERNAL_S3_URL")" ]; then LOCAL_S3="$LOCAL_S3 $c"
  elif [ "$RESTORE" = 1 ]; then fail 'Для внешнего S3 используйте чистую установку (--no-data). Данные внешнего сервиса восстанавливает его владелец.'; fi
done
case ",$PREVIOUS," in *,myadmin*) MYADMIN=1 ;; esac
PROFILES="$ROLES"
if [ "$MYADMIN" = 1 ]; then for c in test stage; do if has_role "$c"; then PROFILES="$PROFILES,myadmin-$c"; fi; done; fi

# Мосты: контур или мониторинг на другой машине нужен тем, кто к нему обращается.
link_contour() {  # $1 - контур
  if has_role "$1"; then return 0; fi
  remote="$(cfg "$(printf '%s' "$1" | tr 'a-z' 'A-Z')_HOST")"
  if [ -n "$remote" ]; then PROFILES="$PROFILES,link-$1"
  else echo "! Контура $1 на этой машине нет, а его адрес в .env не задан: сервисы до него не достучатся."; fi
}
if has_role admin || has_role metrics || has_role test || has_role stage; then link_contour prod; fi
if has_role admin; then link_contour test; link_contour stage; fi
if has_role metrics; then PROFILES="$PROFILES,agents"
elif [ -n "$(cfg METRICS_HOST)" ]; then PROFILES="$PROFILES,agents,link-metrics"
elif [ -n "$MY_CONTOURS" ]; then echo "! METRICS_HOST в .env не задан: нагрузка и логи этой машины в Grafana не попадут."; fi

env_set ROLES "$ROLES"
PROFILES="$(printf '%s' "$PROFILES" | tr ',' '\n' | grep . | sort -u | paste -sd, - || true)"
env_set COMPOSE_PROFILES "$PROFILES"
echo "Роли машины: $ROLES"

# --- секреты: пустые ключи генерируем, пароли-заглушки при первой установке заменяем ---
GENERATED=""
fill_secret() { if [ -z "$(cfg "$1")" ]; then env_set "$1" "$(gen_key "${2:-20}")"; GENERATED="$GENERATED $1"; return 0; fi; return 1; }
MULTI=0; for k in INTERNAL_BIND PROD_HOST TEST_HOST STAGE_HOST METRICS_HOST; do if [ -n "$(cfg "$k")" ]; then MULTI=1; fi; done
FIRST=0; if [ "$RESTORE" = 0 ] && [ ! -f "$ROOT/.installed" ]; then FIRST=1; fi
# Postgres уже инициализированы (оборванная установка): новые пароли в .env не совпали бы с теми, что внутри данных.
if [ "$FIRST" = 1 ]; then
  for c in $MY_CONTOURS; do for d in postgres; do
    if [ -n "$(ls -A "$ROOT/$c/$d" 2>/dev/null)" ]; then
      FIRST=0; echo "! В $ROOT/$c/$d уже есть данные без метки .installed: пароли-заглушки в .env не заменяю, чтобы они совпали с паролями внутри данных. Смените их после установки через shared/rotate-contour-secrets.ps1."
    fi
  done; done
fi
if [ "$FIRST" = 1 ]; then
  BAD=""
  for k in PILOT_ADMIN_PASSWORD PG_SUPERUSER_PASSWORD PILOT_PG_PASSWORD MINIO_PASSWORD GRAFANA_ADMIN_PASSWORD $(if has_role gitlab; then echo GITLAB_ROOT_PASSWORD; fi); do
    v="$(cfg "$k")"
    if [ -z "$v" ] || [ "$v" = "$(cfg_from "$k" .env.example)" ]; then
      # Несколько машин делят один .env: пароль, придуманный на одной из них, не совпал бы с остальными.
      if [ "$MULTI" = 1 ]; then BAD="$BAD $k"; else env_set "$k" "$(gen_key 24)"; GENERATED="$GENERATED $k"; fi
    fi
  done
  [ -z "$BAD" ] || fail "В .env остались пароли-заглушки:$BAD. Задайте их (одинаково на всех машинах) и запустите установку снова."
  if [ "$MULTI" = 0 ]; then fill_secret COLLECTOR_API_KEY || true; fi
fi
# Роль gitlab добавлена к уже установленному стеку: заглушка root осталась бы известным паролем.
if [ "$RESTORE" = 0 ] && has_role gitlab; then
  v="$(cfg GITLAB_ROOT_PASSWORD)"
  if [ -z "$v" ] || [ "$v" = "$(cfg_from GITLAB_ROOT_PASSWORD .env.example)" ]; then
    if [ -n "$(ls -A "$ROOT/gitlab/data" 2>/dev/null)" ]; then
      echo "! Пароль root GitLab - заглушка из .env.example (в данных GitLab уже есть база): смените его в GitLab (Preferences / Password) и запишите новый в .env."
    elif [ "$MULTI" = 1 ]; then
      fail "В .env осталась заглушка GITLAB_ROOT_PASSWORD. Задайте пароль (одинаково на всех машинах) и запустите установку снова."
    else
      env_set GITLAB_ROOT_PASSWORD "$(gen_key 24)"; GENERATED="$GENERATED GITLAB_ROOT_PASSWORD"
    fi
  fi
fi
if [ -z "$(cfg COLLECTOR_API_KEY)" ]; then echo "! COLLECTOR_API_KEY пуст: сборщик статистики принимает данные без ключа. Задайте ключ в .env и пропишите тот же в клиентских модулях."; fi
fill_secret MIGRATION_API_KEY || true
# Ключи выкладки из CI - отдельные на контур. Старый общий ключ становится ключом test, для stage и prod создаются новые.
if has_role admin; then
  if [ -n "$(cfg MODULES_DEPLOY_KEY)" ] && [ -z "$(cfg MODULES_DEPLOY_KEY_TEST)" ]; then
    env_set MODULES_DEPLOY_KEY_TEST "$(cfg MODULES_DEPLOY_KEY)"; env_set MODULES_DEPLOY_KEY ""
  fi
  for s in TEST STAGE PROD; do fill_secret "MODULES_DEPLOY_KEY_$s" || true; done
  # Новый GitLab на этой же машине: OAuth-приложение для входа в админку через браузер создаёт шаг GitLab ниже
  # с этими ID и секретом. Админка стартует уже с ними, кнопка «Войти через GitLab» есть сразу.
  if [ "$RESTORE" = 0 ] && has_role gitlab && [ -z "$(cfg GITLAB_OAUTH_CLIENT_ID)" ]; then
    env_set GITLAB_OAUTH_CLIENT_ID "$(gen_key 32)"; env_set GITLAB_OAUTH_CLIENT_SECRET "$(gen_key 32)"
    GENERATED="$GENERATED GITLAB_OAUTH_CLIENT_ID GITLAB_OAUTH_CLIENT_SECRET"
  fi
  # Служебная учётка Pilot контура на этой же машине: администратор, которого заводит установка (при восстановлении - из бэкапа).
  # С ней админка работает с Pilot сразу после запуска, без входа в Pilot; учётка из «Настроек» админки важнее.
  for c in $MY_CONTOURS; do
    s="$(printf '%s' "$c" | tr 'a-z' 'A-Z')"
    [ -z "$(cfg "MODULES_DEPLOY_${s}_LOGIN")" ] || continue
    if [ -n "$(cfg "${s}_PILOT_ADMIN")" ]; then dl="$(cfg "${s}_PILOT_ADMIN")"; dp="$(cfg "${s}_PILOT_PASSWORD")"
    else dl="$(cfg PILOT_ADMIN_LOGIN)"; dl="${dl:-admin}"; dp="$(cfg PILOT_ADMIN_PASSWORD)"; fi
    if [ -z "$dp" ] || [ "$dp" = "$(cfg_from PILOT_ADMIN_PASSWORD .env.example)" ]; then continue; fi
    env_set "MODULES_DEPLOY_${s}_LOGIN" "$dl"; env_set "MODULES_DEPLOY_${s}_PASSWORD" "$dp"
  done
fi
# compose проверяет обязательные переменные во всём файле, в том числе у сервисов ролей, которых на этой машине нет.
# Значение для роли этой машины придумывать нельзя (оно должно совпасть с рабочим), для остальных нужно любое.
MISSING=""
require_env() {  # $1 - ключ, $2 - роли, которым он нужен, $3 - 1: контур может задать его своим префиксом PROD_/TEST_/STAGE_
  if [ -n "$(cfg "$1")" ]; then return 0; fi
  if [ "${3:-0}" = 1 ]; then
    all=1; for c in PROD TEST STAGE; do if [ -z "$(cfg "${c}_$1")" ]; then all=0; fi; done
    if [ "$all" = 1 ]; then return 0; fi
  fi
  for r in $2; do if has_role "$r"; then MISSING="$MISSING $1"; return 0; fi; done
  fill_secret "$1" || true
}
require_env PG_SUPERUSER_PASSWORD "$LOCAL_PG" 1
require_env PILOT_PG_PASSWORD "prod test stage admin metrics" 1
S3_ROLES="$LOCAL_S3"
if has_role admin; then
  for U in PROD TEST STAGE; do
    if [ -z "$(cfg "${U}_EXTERNAL_S3_URL")" ]; then S3_ROLES="$S3_ROLES admin"; break; fi
  done
fi
require_env MINIO_USER "$S3_ROLES" 1
require_env MINIO_PASSWORD "$S3_ROLES" 1
require_env GRAFANA_ADMIN_PASSWORD "metrics"
# Пароли ролей метрик в данных не записаны: установка задаёт их ролям PostgreSQL при каждом запуске, поэтому пустые можно придумать.
# Несколько машин делят один набор: пароль, придуманный на одной из них, не совпал бы с остальными.
METRICS_BAD=""
for k in METRICS_WRITER_PASSWORD METRICS_READER_PASSWORD; do
  [ -z "$(cfg "$k")" ] || continue
  if [ "$MULTI" = 1 ] && { has_role prod || has_role test || has_role stage || has_role metrics; }; then METRICS_BAD="$METRICS_BAD $k"; else fill_secret "$k" || true; fi
done
[ -z "$METRICS_BAD" ] || fail "В .env не заданы пароли ролей метрик:$METRICS_BAD. Задайте их (одинаково на всех машинах) и запустите установку снова."
[ -z "$MISSING" ] || fail "В .env не заданы обязательные значения:$MISSING. Эти пароли записаны в данных контуров, поэтому установка их не придумывает: верните прежние значения или задайте новые (одинаково на всех машинах)."
chmod 600 .env
# Образ RustFS даёт клиент rc для создания бакета.
RUSTFS_IMG="$(cfg RUSTFS_IMAGE)"; RUSTFS_IMG="${RUSTFS_IMG:-rustfs/rustfs:1.0.5}"
PROJECT="$(cfg COMPOSE_PROJECT_NAME)"; PROJECT="${PROJECT:-cicd}"

# --- HTTPS (необязательно): сертификат и ключ в certs/ включают его для админки и Grafana на тех же портах ---
TLS_OVERLAY=docker-compose.tls.yml
env_unset() {
  grep -q "^$1=" .env || return 0
  (umask 077; grep -v "^$1=" .env > .env.tmp || true)
  mv .env.tmp .env
}
# Сертификат и ключ копируются в DATA_ROOT/tls/<сервис> с правами пользователя контейнера: общий ключ в папке комплекта пришлось бы открыть всем.
tls_copy() {  # $1 - сервис, $2 - uid в контейнере, $3 - сертификат, $4 - ключ
  local d="$ROOT/tls/$1"
  mkdir -p "$d"; rm -f "$d/tls.crt" "$d/tls.key"
  (umask 077; cp "$3" "$d/tls.crt"; cp "$4" "$d/tls.key")
  chmod 644 "$d/tls.crt"
  # Ключ не открываем всем: без root передать его контейнеру нельзя - останавливаемся.
  chown "$2:$2" "$d" "$d/tls.crt" "$d/tls.key" 2>/dev/null || fail "HTTPS: не удалось передать ключ $d/tls.key пользователю контейнера (uid $2). Запустите install.sh от root (sudo) или уберите certs/, чтобы остаться на HTTP."
  chmod 700 "$d"; chmod 600 "$d/tls.key"
  env_set "TLS_$(printf '%s' "$1" | tr 'a-z' 'A-Z')_CRT" "$d/tls.crt"; env_set "TLS_$(printf '%s' "$1" | tr 'a-z' 'A-Z')_KEY" "$d/tls.key"
}
# Включает HTTPS, если есть оба файла; без них убирает надстройку из COMPOSE_FILE. TLS_ON=1, если HTTPS включён.
TLS_ON=0; TLS_CRT=
init_tls() {
  local crt key others="" f sep oldifs="$IFS"
  crt="$(cfg TLS_CERT_FILE)"; crt="${crt:-certs/tls.crt}"; key="$(cfg TLS_KEY_FILE)"; key="${key:-certs/tls.key}"
  case "$crt" in /*) ;; *) crt="$(pwd)/$crt" ;; esac
  case "$key" in /*) ;; *) key="$(pwd)/$key" ;; esac
  # Разделитель в COMPOSE_FILE: свой, если задан; у чужого списка файлов - стандартный для Linux; у нашего - запятая (одинаково на любой ОС).
  # В нашем списке (разделитель «,») основной файл подразумевается: свои файлы пользователя - это всё остальное.
  sep="$(cfg COMPOSE_PATH_SEPARATOR)"
  if [ -z "$sep" ]; then if [ -n "$(cfg COMPOSE_FILE)" ]; then sep=:; else sep=,; fi; fi
  IFS="$sep"; for f in $(cfg COMPOSE_FILE); do if [ -n "$f" ] && [ "$f" != "$TLS_OVERLAY" ] && { [ "$sep" != , ] || [ "$f" != docker-compose.yml ]; }; then others="${others:+$others$sep}$f"; fi; done; IFS="$oldifs"
  if { [ -f "$crt" ] || [ -f "$key" ]; } && { has_role admin || has_role metrics; }; then
    [ -f "$crt" ] && [ -f "$key" ] || fail "HTTPS: найден только один из файлов ($crt, $key). Положите оба или уберите оставшийся: иначе админка и Grafana остались бы без HTTPS незаметно для вас."
    [ -f "$TLS_OVERLAY" ] || fail "HTTPS: нет файла $TLS_OVERLAY рядом с install.sh (комплект неполный)."
    grep -q -e '-----BEGIN CERTIFICATE-----' "$crt" || fail "HTTPS: $crt не похож на сертификат в формате PEM (нет строки BEGIN CERTIFICATE)."
    if grep -q -e '-----BEGIN ENCRYPTED PRIVATE KEY-----' "$key"; then fail "HTTPS: ключ $key зашифрован паролем, а админка и Grafana читают только ключ без пароля. Снимите пароль: openssl pkey -in <ключ> -out tls.key"; fi
    grep -qE -e '-----BEGIN (RSA |EC )?PRIVATE KEY-----' "$key" || fail "HTTPS: $key не похож на закрытый ключ в формате PEM."
    tls_copy admin 1654 "$crt" "$key"; tls_copy grafana 472 "$crt" "$key"
    TLS_CRT="$crt"
    env_set COMPOSE_FILE "${others:-docker-compose.yml}$sep$TLS_OVERLAY"
    if [ "$sep" = , ]; then env_set COMPOSE_PATH_SEPARATOR ,; fi
    TLS_ON=1
  else
    for f in TLS_ADMIN_CRT TLS_ADMIN_KEY TLS_GRAFANA_CRT TLS_GRAFANA_KEY; do env_unset "$f"; done
    if [ -n "$others" ]; then env_set COMPOSE_FILE "$others"
    else env_unset COMPOSE_FILE; if [ "$(cfg COMPOSE_PATH_SEPARATOR)" = , ]; then env_unset COMPOSE_PATH_SEPARATOR; fi; fi
  fi
}
init_tls
if [ "$TLS_ON" = 1 ]; then
  echo "HTTPS включён для админки и Grafana (сертификат $TLS_CRT)."
  case "$(cfg ADMIN_PUBLIC_URL)" in http://*) echo "! ADMIN_PUBLIC_URL в .env начинается с http://: укажите https://, а в Redirect URI OAuth-приложения GitLab и в переменной группы ADMIN_URL (CI модулей) - адрес админки по https." ;; esac
fi
SCHEME=http; if [ "$TLS_ON" = 1 ]; then SCHEME=https; fi

# Сверка загруженного образа с IMAGES.txt (строка: имя, полный идентификатор, дата, отпечаток слоёв). Идентификатор зависит от
# хранилища образов (classic или containerd), поэтому при расхождении сверяются ещё и слои: они от хранилища не зависят.
check_image() {  # $1 - имя образа из вывода docker load, $2 - файл комплекта
  local name="$1" aid alay id lay found=0
  aid="$(docker image inspect "$name" --format '{{.Id}}')" || fail "Не удалось прочитать образ $name после загрузки из $2."
  alay="$(docker image inspect "$name" --format '{{json .RootFS.Layers}}')" || fail "Не удалось прочитать слои образа $name после загрузки из $2."
  alay="$(printf '%s' "$alay" | sha256sum | cut -d' ' -f1)"
  while read -r id lay; do
    found=1
    if [ "$id" = "$aid" ] || [ "$lay" = "$alay" ]; then return 0; fi
  # tr: build.ps1 на Windows пишет IMAGES.txt с CRLF, а mawk (Debian) не отбрасывает \r и длина последнего поля выходит 65
  done < <(tr -d '\r' < IMAGES.txt | awk -v n="$name" '{b=$1; sub(/@.*/,"",b)} ($1==n || b==n) && substr($2,1,7)=="sha256:" && length($2)==71 && length($4)==64 {print $2, $4}')
  if [ "$found" = 0 ]; then echo "  ! Для образа $name в IMAGES.txt нет полной записи (комплект старого формата?): целостность не проверена."; return 0; fi
  fail "Образ $name из $2 не совпал с IMAGES.txt (ожидался другой идентификатор, загружен $aid). Файл повреждён при копировании или не от этого комплекта: скопируйте папку комплекта заново."
}
if [ "$SKIP_LOAD" = 0 ]; then
  step "Загрузка образов из комплекта"
  [ -f IMAGES.txt ] || echo "! Нет IMAGES.txt: целостность образов не проверяется (комплект старого формата?)."
  for f in images/*.tar.gz; do
    echo "  $(basename "$f")"
    loaded="$(docker load -q -i "$f")"
    [ -f IMAGES.txt ] || continue
    while IFS= read -r line; do
      case "$line" in
        "Loaded image: "*) check_image "${line#Loaded image: }" "$(basename "$f")" ;;
        "Loaded image ID: "*) echo "  ! Образ из $(basename "$f") загружен без имени: целостность не проверена." ;;
      esac
    done <<< "$loaded"
  done
fi

copy_tree() { if [ -d "$1" ]; then mkdir -p "$2"; cp -a "$1/." "$2/"; else echo "  в бэкапе нет $1 - пропуск"; fi; }

if [ "$RESTORE" = 1 ]; then
  step "Файлы из бэкапа / $ROOT"
  for c in $MY_CONTOURS; do
    for sub in pilot-server server-settings license databases; do copy_tree "$SNAP/pilot/$c/$sub" "$ROOT/$c/$sub"; done
    # Администратор Pilot уже есть в восстановленной базе.
    mkdir -p "$ROOT/$c/pilot-server/settings"; touch "$ROOT/$c/pilot-server/settings/.admin-created"
    # Бэкап времён MinIO: S3-архив в settings.xml указывает на minio-<контур>, такого контейнера больше нет -> база Broken.
    case " $LOCAL_S3 " in *" $c "*) replace_in "$ROOT/$c/pilot-server/settings/settings.xml" "http://minio-$c:9000" "http://rustfs-$c:9000" ;; esac
  done
  if has_role admin; then
    copy_tree "$SNAP/admin/proadmin-data" "$ROOT/services/proadmin"
    copy_tree "$SNAP/admin/migration-data" "$ROOT/services/migration"
    # Данные комплекта, а не бэкап этой же машины: ключи защиты данных админки чужие, админка создаст свои. Свой бэкап (--from-backup) ключи сохраняет.
    if [ "$SNAP" = "$(pwd)/data" ]; then rm -rf "$ROOT/services/proadmin/keys"; fi
  fi
  if [ "$GITLAB" = 1 ]; then
    # gitlab-secrets.json должен лежать на месте до первого старта - им зашифрованы данные в бэкапе.
    copy_tree "$SNAP/gitlab/config" "$ROOT/gitlab/config"
    copy_tree "$SNAP/gitlab/runner-config" "$ROOT/gitlab/runner-config"
    mkdir -p "$ROOT/gitlab/data/backups"
    cp "$SNAP"/gitlab/*_gitlab_backup.tar "$ROOT/gitlab/data/backups/"
    # Бэкап мог быть снят на Windows и потерять права; sshd и GitLab требуют закрытые ключи.
    chmod 600 "$ROOT"/gitlab/config/ssh_host_*_key "$ROOT/gitlab/config/gitlab-secrets.json" 2>/dev/null || true
  fi
  if [ -n "$GITLAB_NEW" ] && [ -n "$OLD_HOST" ] && [ "$OLD_HOST" != localhost ]; then
    replace_in "$ROOT/services/proadmin/settings.json" "$OLD_HOST" "$GITLAB_NEW"
    replace_in "$ROOT/gitlab/runner-config/config.toml" "$OLD_HOST" "$GITLAB_NEW"
    # Адрес в nuget.config правит шаг после установки GitLab; OLD_HOST живёт только в этом запуске, поэтому его запоминаем.
    if [ "$GITLAB" = 1 ] && [ "$OLD_HOST" != "$GITLAB_NEW" ]; then mkdir -p "$ROOT"; printf '%s\n' "$OLD_HOST" > "$ROOT/.gitlab-old-host"; fi
  fi
fi

step "Папки данных в $ROOT"
for c in $MY_CONTOURS; do
  mkdir -p "$ROOT/$c/pilot-server/settings" "$ROOT/$c/license" "$ROOT/$c/databases"
  [ -f "$ROOT/$c/pilot-server/settings/settings.xml" ] || echo '<Configuration></Configuration>' > "$ROOT/$c/pilot-server/settings/settings.xml"
  # Временные папки S3-архивов (TempFolderPath): Pilot-Server их не создаёт, без них загрузка файлов падает
  # с «Could not find a part of the path .../s3temp/<guid>». Пустые папки в бэкап не попадают.
  { grep -o 'TempFolderPath="/usr/share/ascon/databases/[^"]*"' "$ROOT/$c/pilot-server/settings/settings.xml" 2>/dev/null || true; } \
    | sed 's|^TempFolderPath="/usr/share/ascon/databases/||; s|"$||' | { grep -v '\.\.' || true; } \
    | while IFS= read -r rel; do mkdir -p "$ROOT/$c/databases/$rel"; done
  # Настройки сервера из админки («Серверы»); по умолчанию логирование автоматизаций - везде, кроме прода.
  mkdir -p "$ROOT/$c/server-settings"
  if [ ! -f "$ROOT/$c/server-settings/server-settings.conf" ]; then
    [ "$c" = prod ] && logging=false || logging=true
    printf '# Настройки сервера Pilot (контур %s). Правятся в админке / «Серверы»; применяются при перезапуске pilot-server-%s.
IsAutomationLoggingEnabled=%s
' "$c" "$c" "$logging" > "$ROOT/$c/server-settings/server-settings.conf"
  fi
  # Админка и миграция работают не от root (APP_UID 1654): настройки контура читает миграция.
  chown -R 1654:1654 "$ROOT/$c/server-settings" "$ROOT/$c/pilot-server/settings" 2>/dev/null || true
done
# Миграция пишет в databases контуров test и stage; прод ей смонтирован только для чтения.
for c in test stage; do
  if has_role "$c" && [ "$(stat -c %u "$ROOT/$c/databases" 2>/dev/null)" != 1654 ]; then chown -R 1654:1654 "$ROOT/$c/databases" 2>/dev/null || true; fi
done
if has_role admin; then
  mkdir -p "$ROOT/services/proadmin" "$ROOT/services/migration" "$ROOT/import" "$ROOT/export"
  # Админка и миграция работают не от root (APP_UID 1654).
  chown -R 1654:1654 "$ROOT/services/proadmin" "$ROOT/services/migration" "$ROOT/export" 2>/dev/null || true
  # Адреса контуров на других машинах (при восстановлении - и в настройках админки из бэкапа).
  for pair in prod:5546 test:5547 stage:5549; do
    c="${pair%%:*}"; port="${pair#*:}"; U="$(printf '%s' "$c" | tr 'a-z' 'A-Z')"; remote="$(cfg "${U}_HOST")"
    if has_role "$c" || [ -z "$remote" ]; then continue; fi
    env_set "PILOT_${U}_URL" "http://$remote:$port/pilot-ice_ru"
    replace_in "$ROOT/services/proadmin/settings.json" "localhost:$port" "$remote:$port"
  done
fi

# Секреты в DATA_ROOT и .env закрываем от остальных пользователей хоста.
secure_data() {
  for d in "$ROOT/services/migration" "$ROOT/services/proadmin" "$ROOT/services/proadmin/keys" "$ROOT/gitlab/config" "$ROOT/gitlab/runner-config"; do if [ -d "$d" ]; then chmod 700 "$d"; fi; done
  for c in $MY_CONTOURS; do if [ -d "$ROOT/$c/pilot-server/settings" ]; then chmod 700 "$ROOT/$c/pilot-server/settings"; fi; done
  chmod 600 .env
  if [ -f PASSWORDS.md ]; then chmod 600 PASSWORDS.md; fi
}
secure_data

step "Сети"
for n in pilot-prod pilot-test pilot-stage; do docker network inspect "$n" >/dev/null 2>&1 || docker network create "$n" >/dev/null; done

for c in $MY_CONTOURS; do
  U="$(printf '%s' "$c" | tr 'a-z' 'A-Z')"
  pg_host="$(cfg "${U}_EXTERNAL_PG_HOST")"
  if [ -n "$pg_host" ]; then
    pg_port="$(cfg "${U}_EXTERNAL_PG_PORT")"; pg_port="${pg_port:-5432}"
    pg_db="$(cfg "${U}_EXTERNAL_PG_DATABASE")"; pg_db="${pg_db:-pilot-ice_ru}"
    [[ "$pg_host" =~ ^[A-Za-z0-9.-]+$ && "$pg_port" =~ ^[0-9]{1,5}$ && "$pg_db" =~ ^[A-Za-z0-9_.-]+$ ]] &&
      (( 10#$pg_port >= 1 && 10#$pg_port <= 65535 )) || fail "Некорректное подключение внешнего PostgreSQL для $c."
    pg_password="$(cfg "${U}_PILOT_PG_PASSWORD")"; pg_password="${pg_password:-$(cfg PILOT_PG_PASSWORD)}"
    PGPASSWORD="$pg_password" docker run --rm --network "pilot-$c" -e PGPASSWORD -e PGCONNECT_TIMEOUT=10 --entrypoint psql \
      postgres:16.16@sha256:30551eca978d79ccf49d037bc297b5670baea61a04ceca42ff7fd3100163d3ce \
      -X -h "$pg_host" -p "$pg_port" -U pilotuser -d "$pg_db" -v ON_ERROR_STOP=1 -c 'SELECT 1' >/dev/null 2>&1 ||
      fail "PostgreSQL контура $c: проверьте доступ pilotuser к базе $pg_db по адресу $pg_host:$pg_port."
    echo "  PostgreSQL $c доступен"
  fi
  endpoint="$(cfg "${U}_EXTERNAL_S3_URL")"
  if [ -n "$endpoint" ]; then
    bucket="$(cfg "${U}_EXTERNAL_S3_BUCKET")"
    [[ "$endpoint" =~ ^https?://[A-Za-z0-9.-]+(:[0-9]{1,5})?/?$ && "$bucket" =~ ^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$ ]] ||
      fail "Некорректные адрес или бакет внешнего S3 для $c."
    s3_user="$(cfg "${U}_S3_ACCESS_KEY")"; s3_password="$(cfg "${U}_S3_SECRET_KEY")"
    [ -n "$s3_user" ] && [ -n "$s3_password" ] || fail "Укажите ${U}_S3_ACCESS_KEY и ${U}_S3_SECRET_KEY для внешнего S3."
    RCU="$s3_user" RCP="$s3_password" RC_ENDPOINT="$endpoint" RC_BUCKET="$bucket" docker run --rm --network "pilot-$c" \
      -e RCU -e RCP -e RC_ENDPOINT -e RC_BUCKET -e HOME=/tmp --entrypoint sh "$RUSTFS_IMG" \
      -c 'rc alias set m "$RC_ENDPOINT" "$RCU" "$RCP" >/dev/null && rc ls "m/$RC_BUCKET" >/dev/null' >/dev/null 2>&1 ||
      fail "S3 контура $c: проверьте доступ к бакету $bucket."
    echo "  S3 $c доступен"
  fi
done

# Не «compose pull --policy missing»: он всё равно лезет в реестр за нашими образами и падает.
if [ "$RESTORE" = 1 ]; then
  # Контейнеры и тома создаём, не запуская: том надо наполнить до старта Grafana, а базы - до старта Pilot-Server.
  step "Скачивание недостающих образов (Pilot-Server, PostgreSQL, Grafana…)"
  retry docker compose up --no-start --pull missing || fail "Не удалось скачать образы за 3 попытки: проверьте сеть, прокси и доступ к реестрам."

  if has_role metrics && [ -f "$SNAP/grafana/grafana_grafana_data.tgz" ]; then
    step "Grafana из бэкапа"
    GRAFANA="$(docker compose config --images | grep 'grafana/grafana' | head -1)"
    GVOL="${PROJECT}_grafana_data"
    # Распаковка поверх существующих данных Grafana смешала бы две базы дашбордов и пользователей.
    # Признак данных - grafana.db: пустую папку plugins в новый том кладёт сам образ.
    if [ -n "$(docker run --rm --user 0 --entrypoint sh -v "$GVOL:/v:ro" "$GRAFANA" -c 'ls /v/grafana.db 2>/dev/null || true')" ]; then
      fail "В томе $GVOL уже есть данные Grafana, восстановление поверх них не выполняется. Если они не нужны, удалите том (docker compose down; docker volume rm $GVOL) и запустите установку снова."
    fi
    docker run --rm --user 0 --entrypoint sh -v "$GVOL:/v" -v "$SNAP/grafana:/b:ro" "$GRAFANA" -c "tar xzf /b/grafana_grafana_data.tgz -C /v"
  fi

  # Файлы S3: архив тома rustfs/rustfs_<контур>.tgz (тот же формат у бэкапа и у data/ комплекта). До старта pilot-server и до создания бакета.
  # Бэкап старого формата (до перехода на RustFS) хранит вместо него папку minio/<контур>/pilot-ice-ru - данные MinIO (xl.meta).
  # RustFS читает этот формат как есть (проверено на 1.0.5: содержимое и ETag совпали, в том числе у объектов с part.1),
  # поэтому папка бакета копируется в том целиком. Служебные данные MinIO (.minio.sys) не переносятся - учётку Pilot заводит шаг «Бакеты RustFS».
  for c in $MY_CONTOURS; do
    rfile="rustfs_$c.tgz"
    if [ ! -f "$SNAP/rustfs/$rfile" ]; then
      [ -d "$SNAP/minio/$c/pilot-ice-ru" ] || continue
      step "Файлы MinIO ($c) из бэкапа старого формата -> RustFS"
      RVOL="${PROJECT}_rustfs_$c"
      rnames="$(docker run --rm --user 0 --entrypoint sh -v "$RVOL:/v:ro" "$RUSTFS_IMG" -c 'ls -A /v')" || fail "Не удалось прочитать том $RVOL"
      if [ -n "$(printf '%s
' "$rnames" | grep -v -x -e '' -e '.rustfs' -e '.rustfs.sys')" ]; then
        fail "В томе $RVOL уже есть данные RustFS, восстановление поверх них установщик не выполняет. Если они не нужны, удалите том (docker compose down; docker volume rm $RVOL) и запустите установку снова."
      fi
      if [ -n "$(docker ps -q --filter "name=^rustfs-$c\$")" ]; then docker stop "rustfs-$c" >/dev/null; fi   # compose up запустит снова
      # Владелец - пользователь RustFS (10001), включая корень тома: иначе RustFS не стартует («File access denied»).
      docker run --rm --user 0 --entrypoint sh -v "$RVOL:/v" -v "$SNAP/minio/$c:/b:ro" "$RUSTFS_IMG" -c \
        "mkdir -p /v/pilot-ice-ru && cp -r /b/pilot-ice-ru/. /v/pilot-ice-ru/ && chown 10001:10001 /v && chown -R 10001:10001 /v/pilot-ice-ru" || fail "Не удалось перенести файлы MinIO в том $RVOL."
      continue
    fi
    step "Файлы RustFS ($c) из бэкапа"
    RVOL="${PROJECT}_rustfs_$c"
    # Образ RustFS уже скачан предыдущим шагом; в нём есть tar (busybox). Пустым считается том только со служебными .rustfs и .rustfs.sys (как в restore.ps1).
    rnames="$(docker run --rm --user 0 --entrypoint sh -v "$RVOL:/v:ro" "$RUSTFS_IMG" -c 'ls -A /v')" || fail "Не удалось прочитать том $RVOL"
    if [ -n "$(printf '%s
' "$rnames" | grep -v -x -e '' -e '.rustfs' -e '.rustfs.sys')" ]; then
      fail "В томе $RVOL уже есть данные RustFS, восстановление поверх них установщик не выполняет. Если они не нужны, удалите том (docker compose down; docker volume rm $RVOL) и запустите установку снова; либо восстановите вручную: backup/restore.ps1 -Env $c -Parts rustfs -ConfirmOverwrite rustfs"
    fi
    if [ -n "$(docker ps -q --filter "name=^rustfs-$c\$")" ]; then docker stop "rustfs-$c" >/dev/null; fi   # compose up запустит снова
    # Архив проверяется до распаковки; владелец файлов (10001) берётся из архива.
    docker run --rm --user 0 --entrypoint sh -v "$RVOL:/v" -v "$SNAP/rustfs:/b:ro" "$RUSTFS_IMG" -c "tar tzf /b/$rfile > /dev/null && tar xzf /b/$rfile -C /v" || fail "Не удалось развернуть $rfile в том $RVOL."
  done

  step "Базы PostgreSQL из бэкапа"
  for c in $MY_CONTOURS; do docker compose up -d "postgres-$c"; done
  for c in $MY_CONTOURS; do
    pg="postgres-$c"
    # По TCP: во время initdb сервер слушает только сокет, pg_isready по сокету соврёт.
    until docker exec "$pg" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; do sleep 2; done
    docker exec "$pg" rm -rf /tmp/restore
    docker cp "$SNAP/postgres/$c/." "$pg:/tmp/restore"
    # Роли: «already exists» ожидаемо (pilotuser заводит init-скрипт), ALTER ROLE вернёт исходные пароли.
    docker exec "$pg" sh -c "psql -U postgres -q -f /tmp/restore/globals.sql 2>&1 | grep -v 'already exists' || true"
    for dump in "$SNAP/postgres/$c"/*.dump; do
      name="$(basename "$dump")"
      docker exec "$pg" pg_restore -U postgres --clean --if-exists --create --exit-on-error -d postgres "/tmp/restore/$name"
      echo "  $c: ${name%.dump}"
    done
    docker exec "$pg" rm -rf /tmp/restore
  done
fi

# Роли metrics_writer и metrics_reader и права на pilot_metrics: при первом старте их создаёт init-скрипт; на существующих данных
# (база создана по-старому, под pilotuser) и после восстановления из бэкапа скрипт приводит владельца и права. Идемпотентно.
# Пароли берутся из окружения контейнера (их задаёт compose из .env), в аргументы docker они не попадают.
metrics_roles() {
  has_role prod || return 0
  [ -z "$(cfg PROD_EXTERNAL_PG_HOST)" ] || return 0
  retry docker compose up -d --pull missing postgres-prod || fail "Не удалось запустить postgres-prod."
  n=0
  # По TCP: во время initdb сервер слушает только сокет, pg_isready по сокету соврёт.
  until docker exec postgres-prod pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; do n=$((n + 1)); [ "$n" -le 90 ] || fail "postgres-prod не поднялся за 3 минуты."; sleep 2; done
  docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh >/dev/null || fail "Не удалось создать роли метрик в postgres-prod (подробности выше). Сборщики и Grafana без них работать не будут."
}
step "Роли PostgreSQL для метрик"
metrics_roles

step "Скачивание недостающих образов (Pilot-Server, PostgreSQL, Grafana…) и запуск"
retry docker compose up -d --pull missing --remove-orphans || fail "Не удалось скачать образы или запустить сервисы за 3 попытки: проверьте сеть, прокси и доступ к реестрам."
if [ -n "$LOCAL_S3" ]; then docker image inspect "$RUSTFS_IMG" >/dev/null 2>&1 || retry docker pull -q "$RUSTFS_IMG" >/dev/null || fail "Не удалось скачать образ $RUSTFS_IMG."; fi

# GitLab готов, когда запущен puma и отвечает /-/readiness; healthy у образа этого не гарантирует.
gitlab_ready() { docker exec gitlab sh -c 'gitlab-ctl status puma 2>/dev/null | grep -q "^run: puma" && curl -fs http://localhost/-/readiness >/dev/null' >/dev/null 2>&1; }
wait_gitlab() {
  waited=0
  until gitlab_ready; do
    waited=$((waited + 10)); [ "$waited" -le 1800 ] || return 1
    sleep 10
  done
}
# Адрес группы: настройки админки на этой машине, затем снимок бэкапа, затем .env. Ключи settings.json - как в C#, по одному свойству на строку.
json_val() { [ -f "$1" ] && sed -n "s/^ *\"$2\": *\"\([^\"]*\)\".*/\1/p" "$1" | head -1 || true; }
group_url() {
  v="$(json_val "$ROOT/services/proadmin/settings.json" "$1")"
  [ -n "$v" ] || [ -z "$SNAP" ] || v="$(json_val "$SNAP/admin/proadmin-data/settings.json" "$1")"
  [ -n "$v" ] || v="$(cfg "$2")"
  printf '%s' "$v"
}
# Адрес возврата OAuth админки: GITLAB_OAUTH_REDIRECT_URI, иначе ADMIN_PUBLIC_URL/GitLab/Callback, иначе пусто.
admin_callback() {
  r="$(cfg GITLAB_OAUTH_REDIRECT_URI)"
  if [ -z "$r" ]; then p="$(cfg ADMIN_PUBLIC_URL)"; if [ -n "$p" ]; then r="${p%/}/GitLab/Callback"; fi; fi
  printf '%s' "$r"
}
# Для шага GitLab: без заданного адреса админка на этой же машине открывается по localhost или 127.0.0.1.
post_callback() {
  r="$(admin_callback)"; ap="$(cfg ADMIN_PORT)"
  if [ -z "$r" ] && has_role admin && [ -n "$ap" ]; then r="$SCHEME://localhost:$ap/GitLab/Callback $SCHEME://127.0.0.1:$ap/GitLab/Callback"; fi
  printf '%s' "$r"
}
# Шаг после восстановления или чистой установки GitLab: gitlab-post-restore.rb (регистрация, переменные групп, защита веток, nuget.config).
gitlab_post_restore() {  # $1 - группы модулей, $2 - группа отчётов, $3 - старый адрес GitLab, $4 - новый (http://хост:порт), $5 - 1: групп ещё может не быть
  docker cp ./gitlab-post-restore.rb gitlab:/tmp/post.rb || return 1
  # docker cp хранит права файла с хоста, а читает его пользователь git.
  docker exec gitlab chmod 644 /tmp/post.rb || return 1
  prc=0
  # Значения ключей идут только через окружение: в аргументы docker (и в список процессов) они не попадают.
  # Секрет OAuth нужен только чтобы создать приложение при чистой установке.
  osec=""; if [ "$RESTORE" = 0 ]; then osec="$(cfg GITLAB_OAUTH_CLIENT_SECRET)"; fi
  # Приложение модуля отчётов (вход через браузер из клиента Pilot): Application ID - в .env, шаг создаёт приложение в GitLab.
  if [ -z "$(cfg REPORTS_GITLAB_OAUTH_CLIENT_ID)" ]; then
    env_set REPORTS_GITLAB_OAUTH_CLIENT_ID "$(gen_key 32)"; GENERATED="$GENERATED REPORTS_GITLAB_OAUTH_CLIENT_ID"
  fi
  # Общие проверки CI (проект pilot-ci/checks): файлы из комплекта (ci-checks/) или из репозитория (gitlab/ci-checks).
  checks_in=""; checks_dir=""
  for d in ./ci-checks ../../gitlab/ci-checks; do if [ -f "$d/automation.yml" ]; then checks_dir="$d"; break; fi; done
  if [ -n "$checks_dir" ]; then
    docker exec gitlab rm -rf /tmp/ci-checks >/dev/null 2>&1 || true
    docker cp "$checks_dir/." gitlab:/tmp/ci-checks && docker exec gitlab chmod -R a+rX /tmp/ci-checks && checks_in=/tmp/ci-checks
  else
    echo "  ! Нет папки ci-checks в комплекте: проект общих проверок CI в GitLab не создаётся."
  fi
  # Runner без config.toml (чистая установка) - шаг создаёт runner в GitLab, токен забираем из файла и регистрируем.
  runner_token=""; [ -f "$ROOT/gitlab/runner-config/config.toml" ] || runner_token=/tmp/runner.token
  DEPLOY_KEY_TEST="$(cfg MODULES_DEPLOY_KEY_TEST)" DEPLOY_KEY_STAGE="$(cfg MODULES_DEPLOY_KEY_STAGE)" DEPLOY_KEY_PROD="$(cfg MODULES_DEPLOY_KEY_PROD)" \
  OAUTH_CLIENT_SECRET="$osec" \
    docker exec -e DEPLOY_KEY_TEST -e DEPLOY_KEY_STAGE -e DEPLOY_KEY_PROD -e OAUTH_CLIENT_SECRET \
      -e "GROUP_URLS=$1" -e "REPORTS_GROUP_URL=$2" -e "OLD_HOST=$3" -e "NEW_URL=$4" -e "ALLOW_MISSING_GROUPS=$5" \
      -e "OAUTH_CLIENT_ID=$(cfg GITLAB_OAUTH_CLIENT_ID)" -e "ADMIN_CALLBACK=$(post_callback)" \
      -e "REPORTS_OAUTH_CLIENT_ID=$(cfg REPORTS_GITLAB_OAUTH_CLIENT_ID)" -e "REPORTS_OAUTH_REDIRECT_URI=$(cfg REPORTS_GITLAB_OAUTH_REDIRECT_URI)" \
      -e "CHECKS_DIR=$checks_in" -e "CHECKS_PROJECT=$(group_url AutomationsCiConfigPath AUTOMATIONS_CI_CONFIG_PATH)" -e "RUNNER_TOKEN_FILE=$runner_token" \
      gitlab gitlab-rails runner /tmp/post.rb || prc=$?
  docker exec gitlab rm -rf /tmp/post.rb /tmp/ci-checks >/dev/null 2>&1 || true
  if [ -n "$runner_token" ]; then
    tok="$(docker exec gitlab sh -c "cat $runner_token 2>/dev/null; rm -f $runner_token" | tr -d '\r\n')"
    if [ -n "$tok" ]; then register_runner "$tok" || prc=1
    else echo "  ! GitLab не выдал токен runner'а: runner не зарегистрирован, pipeline будут ждать исполнителя."; prc=1; fi
  fi
  return "$prc"
}
# Регистрация runner'а (docker executor в сети GitLab). Токен - через окружение, не в аргументах.
# RUNNER_DNS в .env (через запятую) - DNS для заданий, если Docker на машине отдаёт контейнерам негодные адреса.
register_runner() {
  set -- "$1" $(cfg RUNNER_DNS | tr ',' ' ' | awk '{for(i=1;i<=NF;i++) printf "--docker-dns %s ", $i}')
  tok="$1"; shift
  if CI_SERVER_TOKEN="$tok" docker exec -e CI_SERVER_TOKEN gitlab-runner gitlab-runner register --non-interactive \
      --url http://gitlab --clone-url http://gitlab --executor docker --name local-docker-runner --docker-image alpine:3.20 \
      --docker-network-mode gitlab_gitlab-net --docker-pull-policy if-not-present \
      --docker-extra-hosts host.docker.internal:host-gateway --docker-volumes /cache "$@"; then
    docker exec gitlab-runner sed -i 's/^concurrent = .*/concurrent = 3/' /etc/gitlab-runner/config.toml >/dev/null 2>&1 || true
    echo "  runner local-docker-runner зарегистрирован"
  else
    echo "  ! Не удалось зарегистрировать runner (подробности выше): pipeline будут ждать исполнителя."; return 1
  fi
}
# На медленной машине «gitlab-ctl stop» не дожидается остановки (timeout: run: puma … want down): повторяем, затем kill.
gitlab_stop_service() {
  local i
  for i in 1 2 3; do
    docker exec gitlab gitlab-ctl stop "$1" >/dev/null 2>&1 && return 0
    docker exec gitlab sh -c "gitlab-ctl status $1 2>/dev/null | grep -q '^down:'" && return 0
    sleep 10
  done
  docker exec gitlab gitlab-ctl kill "$1" >/dev/null 2>&1 || true
  sleep 5
  docker exec gitlab sh -c "gitlab-ctl status $1 2>/dev/null | grep -q '^down:'"     || fail "GitLab: сервис $1 не останавливается (gitlab-ctl stop $1). Данные GitLab ещё не менялись, но базы Pilot уже восстановлены: смотрите docker logs gitlab, очистите DATA_ROOT и запустите установку снова."
}
GITLAB_WARN=""
if [ "$RESTORE" = 1 ]; then
  TAR="$(ls "$ROOT"/gitlab/data/backups/*_gitlab_backup.tar 2>/dev/null | head -1 || true)"
  if [ -n "$TAR" ]; then
    step "GitLab: ожидание готовности (до 30 минут)"
    wait_gitlab || fail "GitLab не стал готов за 30 минут: смотрите docker logs gitlab."
    step "GitLab: восстановление бэкапа (5-15 минут)"
    b="$(basename "$TAR" _gitlab_backup.tar)"
    docker stop gitlab-runner >/dev/null
    docker exec gitlab chown git:git "/var/opt/gitlab/backups/${b}_gitlab_backup.tar"
    gitlab_stop_service puma
    gitlab_stop_service sidekiq
    # Метка до «force=yes»: после прерванного восстановления повторное затёрло бы изменившиеся данные.
    # Ставится после остановки сервисов: сбой остановки данные GitLab ещё не трогает.
    : > "$ROOT/.restore-started"
    # Полный вывод - в журнал, на экран без тысяч строк ERROR от psql; код возврата берём из файла (в sh нет PIPESTATUS).
    RESTORE_CMD='{ gitlab-backup restore BACKUP=$BACKUP_NAME force=yes; echo $? > /tmp/restore.rc; } 2>&1 | tee /var/log/gitlab/restore.log | grep -v -E '\''^(ERROR|STATEMENT|DETAIL|HINT|CONTEXT):'\''; exit $(cat /tmp/restore.rc)'
    rc=0; docker exec -e GITLAB_ASSUME_YES=1 -e "BACKUP_NAME=$b" gitlab sh -c "$RESTORE_CMD" || rc=$?
    [ "$rc" = 0 ] || fail "gitlab-backup restore завершился с кодом $rc. Журнал: $ROOT/gitlab/logs/restore.log. Очистите $ROOT и запустите установку снова."
    if docker exec gitlab grep -q 'There were errors in restoring the schema' /var/log/gitlab/restore.log; then
      GITLAB_WARN="GitLab сообщил об ошибках схемы при восстановлении (см. $ROOT/gitlab/logs/restore.log): проверьте docker exec gitlab gitlab-rake gitlab:check SANITIZE=true."
    fi
    projects="$(docker exec gitlab gitlab-psql -d gitlabhq_production -tAc 'select count(*) from projects' 2>/dev/null | tr -d '[:space:]' || true)"
    case "$projects" in ''|*[!0-9]*) fail "Не удалось прочитать число проектов GitLab после восстановления. Журнал: $ROOT/gitlab/logs/restore.log. Очистите $ROOT и запустите установку снова." ;; esac
    [ "$projects" -gt 0 ] || fail "После восстановления в GitLab нет проектов: данные из бэкапа не загрузились (GitLab ещё достраивал схему или бэкап пуст). Журнал: $ROOT/gitlab/logs/restore.log. Очистите $ROOT и запустите установку снова."
    echo "  проектов в GitLab: $projects"
    # После переноса репозитории могут оказаться у root, и Gitaly отклонит push («dubious ownership»).
    docker exec gitlab chown -R git:git /var/opt/gitlab/git-data/repositories
    # Нечистая остановка PostgreSQL посреди восстановления уводит GitLab в цикл падений: даём 10 минут.
    step "GitLab: перезапуск и ожидание готовности (до 30 минут)"
    docker stop -t 600 gitlab >/dev/null
    docker start gitlab >/dev/null
    wait_gitlab || fail "GitLab не стал готов за 30 минут после перезапуска: смотрите docker logs gitlab."
    docker start gitlab-runner >/dev/null
    rm -f "$TAR" "$ROOT/.restore-started"
  fi
  printf '%s\n' "$SNAP" > "$ROOT/.installed"
fi

step "Администраторы Pilot"
LOGIN="$(cfg PILOT_ADMIN_LOGIN)"; LOGIN="${LOGIN:-admin}"
for c in $MY_CONTOURS; do
  m="$ROOT/$c/pilot-server/settings/.admin-created"; [ -f "$m" ] && continue
  if docker exec "pilot-server-$c" ./Ascon.Pilot.Daemon --admin /usr/share/ascon/pilot-server/settings/settings.xml "$LOGIN" "$(cfg PILOT_ADMIN_PASSWORD)" >/dev/null; then
    touch "$m"; docker restart "pilot-server-$c" >/dev/null; echo "  $c: $LOGIN"
  else echo "  ! не удалось создать администратора на pilot-server-$c"; fi
done

# Базы Pilot к этому моменту уже созданы: роли метрик закрываются и от них.
metrics_roles

step "Бакеты RustFS"
for c in $LOCAL_S3; do
  U="$(printf '%s' "$c" | tr 'a-z' 'A-Z')"
  user="$(cfg "${U}_MINIO_USER")";     user="${user:-$(cfg MINIO_USER)}"
  pass="$(cfg "${U}_MINIO_PASSWORD")"; pass="${pass:-$(cfg MINIO_PASSWORD)}"
  if ! { wait_rustfs "$c" && rc_bucket "pilot-$c" "rustfs-$c" "$user" "$pass" >/dev/null; }; then
    echo "  ! не удалось создать бакет в rustfs-$c (смотрите docker logs rustfs-$c)"; continue
  fi
  # Служебная учётка Pilot (AccessKeyId в settings.xml из бэкапа). В бэкапе RustFS она уже есть (повтор безвреден),
  # в бэкапе старого формата (MinIO) её нет: без неё Pilot получает отказ доступа к S3 и база уходит в Broken.
  pu="$(cfg "${U}_MINIO_PILOT_USER")";   pu="${pu:-$(cfg MINIO_PILOT_USER)}"
  ps_="$(cfg "${U}_MINIO_PILOT_SECRET")"; ps_="${ps_:-$(cfg MINIO_PILOT_SECRET)}"
  if [ -n "$pu" ] && [ -n "$ps_" ] && [ "$pu" != "$user" ]; then
    if rc_pilot_user "pilot-$c" "rustfs-$c" "$user" "$pass" "$pu" "$ps_" >/dev/null; then echo "  rustfs-$c: учётка $pu (доступ к pilot-ice-ru)"
    else echo "  ! не удалось завести учётку $pu в rustfs-$c: файловый архив Pilot будет недоступен (смотрите docker logs rustfs-$c)"; fi
  fi
done

mkdir -p "$ROOT"
[ -f "$ROOT/.installed" ] || echo "чистая установка" > "$ROOT/.installed"
secure_data

# Ключи выкладки вручную: на машине admin без GitLab - всегда; с GitLab - только если шаг ниже пропущен или не удался.
POST_WARN=""; POST_FAIL=0; MANUAL_KEYS=0; OAUTH_DONE=0
if [ "$GITLAB" = 1 ]; then
  step "GitLab: регистрация, переменные групп, защита веток, адрес в nuget.config"
  MODULE_GROUPS=""
  for u in "$(group_url ModulesGitLabGroupUrl MODULES_GITLAB_GROUP_URL)" "$(group_url ServerModulesGitLabGroupUrl SERVER_MODULES_GITLAB_GROUP_URL)"; do
    if [ -n "$u" ]; then MODULE_GROUPS="${MODULE_GROUPS:+$MODULE_GROUPS,}$u"; fi
  done
  REPORTS_GROUP="$(group_url ReportsGitLabGroupUrl REPORTS_GITLAB_GROUP_URL)"
  OLD_LABEL=""; if [ -f "$ROOT/.gitlab-old-host" ]; then OLD_LABEL="$(tr -d '\r\n' < "$ROOT/.gitlab-old-host")"; fi
  GL_PORT="$(cfg GITLAB_HTTP_PORT)"; GL_URL="http://$(cfg GITLAB_HOST_IP):${GL_PORT:-8080}"
  if [ -z "$MODULE_GROUPS" ] || [ -z "$(cfg MODULES_DEPLOY_KEY_TEST)" ] || [ -z "$(cfg MODULES_DEPLOY_KEY_STAGE)" ] || [ -z "$(cfg MODULES_DEPLOY_KEY_PROD)" ]; then
    MANUAL_KEYS=1; echo "  ! Нет ключей MODULES_DEPLOY_KEY_* в .env или адресов групп модулей в настройках админки: переменные групп и защита веток не заданы."
  fi
  # Чистая установка: группы из настроек админки в новом GitLab ещё могут не существовать.
  ALLOW_MISSING=0; if [ "$RESTORE" != 1 ] && [ -z "$OLD_LABEL" ]; then ALLOW_MISSING=1; fi
  prc=0
  if wait_gitlab; then gitlab_post_restore "$MODULE_GROUPS" "$REPORTS_GROUP" "$OLD_LABEL" "$GL_URL" "$ALLOW_MISSING" || prc=$?; else prc=1; fi
  # Адрес возврата OAuth шаг добавляет сам, если знает Application ID и адрес админки.
  if [ "$prc" != 1 ] && [ -n "$(cfg GITLAB_OAUTH_CLIENT_ID)" ] && [ -n "$(admin_callback)" ]; then OAUTH_DONE=1; fi
  if [ "$prc" = 2 ]; then
    # Код 2 - групп модулей ещё нет: не сбой, но переменные не заданы.
    MANUAL_KEYS=1; echo "  ! Групп модулей в GitLab ещё нет: переменные групп и защита веток не заданы (регистрация закрыта)."
  elif [ "$prc" = 0 ]; then
    if [ -n "$OLD_LABEL" ] && [ -n "$MODULE_GROUPS$REPORTS_GROUP" ]; then rm -f "$ROOT/.gitlab-old-host"; fi
  else
    POST_FAIL=1; MANUAL_KEYS=1
    POST_WARN="Шаг GitLab после установки не выполнен или завершился с ошибкой (код $prc, подробности выше). Саморегистрация, переменные групп, защита веток и адрес в nuget.config могли не примениться; шаг идемпотентен, повторный запуск установки повторит его."
  fi
elif has_role admin && [ -n "$(cfg MODULES_DEPLOY_KEY_PROD)" ]; then
  MANUAL_KEYS=1
fi

ADMIN_PORT="$(cfg ADMIN_PORT)"; GRAFANA_PORT="$(cfg GRAFANA_PORT)"
echo "Готово. Роли машины: $ROLES."
for pair in prod:5546 test:5547 stage:5549; do if has_role "${pair%%:*}"; then echo "Pilot ${pair%%:*}: порт ${pair#*:}"; fi; done
if has_role admin; then echo "Админка: $SCHEME, порт ${ADMIN_PORT:-5300}"; fi
if has_role metrics; then echo "Grafana: $SCHEME, порт ${GRAFANA_PORT:-3000}"; fi
if [ "$GITLAB" = 1 ]; then echo "GitLab: http://$(cfg GITLAB_HOST_IP):$(cfg GITLAB_HTTP_PORT)"; fi
if [ -n "$GENERATED" ]; then echo "Сгенерированы значения в $(pwd)/.env:$GENERATED"; fi
# Общие настройки Pilot хранятся в базе каждого сервера Pilot: установка их не задаёт, их вписывают вручную
# (клиент Pilot под администратором: Сервис / Настройки / Общие настройки) на каждом сервере - prod, test, stage.
if [ "$GITLAB" = 1 ] || [ -n "$MY_CONTOURS" ]; then
  echo
  echo "Общие настройки Pilot - вручную на КАЖДОМ сервере Pilot (Сервис / Настройки / Общие настройки, под администратором):"
  if [ "$GITLAB" = 1 ]; then
    rep_path="$(group_url ReportsGitLabGroupUrl REPORTS_GITLAB_GROUP_URL | sed -E 's#^https?://[^/]+/##; s#/+$##')"
    echo "  «PilotOpenTools отчёты (GitLab)»: сервер http://$(cfg GITLAB_HOST_IP):$(cfg GITLAB_HTTP_PORT), группа ${rep_path:-<группа отчётов>}, Application ID $(cfg REPORTS_GITLAB_OAUTH_CLIENT_ID)"
    echo "     (если GITLAB_HOST_IP = localhost, укажите адрес GitLab, который видят рабочие места)"
  else
    echo "  «PilotOpenTools отчёты (GitLab)»: сервер и группа GitLab с отчётами, Application ID - REPORTS_GITLAB_OAUTH_CLIENT_ID из .env машины с GitLab"
  fi
  echo "  «PilotOpenTools отправка статистики»: адрес сервиса статистики http://<машина metrics>:5310/api/module-events"
  echo "  Подробно - инструкция по установке, раздел «Общие настройки Pilot на каждом сервере»."
fi
if [ "$RESTORE" = 1 ]; then
  echo "Данные восстановлены из $SNAP. Все пароли - в $(pwd)/.env."
  if [ -n "$OLD_HOST" ] && [ -z "$GITLAB_NEW" ]; then echo "! GitLab настроен на адрес исходной машины ($OLD_HOST). Если у этого сервера другой IP, см. инструкцию по установке, раздел об установке с данными."; fi
  if [ -n "$GITLAB_NEW" ] && [ "$OAUTH_DONE" != 1 ]; then echo "! Redirect URI OAuth-приложения админки хранится в GitLab (Admin / Applications): укажите там адрес админки с путём /GitLab/Callback. Адрес админки для браузеров задаёт ADMIN_PUBLIC_URL в .env (если он задан на машине с GitLab вместе с GITLAB_OAUTH_CLIENT_ID, установка добавляет адрес сама)."; fi
fi
if [ -n "$GITLAB_WARN" ]; then echo "! $GITLAB_WARN"; fi
if [ -n "$POST_WARN" ]; then echo "! $POST_WARN"; fi
if [ "$MANUAL_KEYS" = 1 ]; then echo "! Ключи выкладки из CI: задайте в переменных групп модулей GitLab DEPLOY_KEY_TEST (Masked), DEPLOY_KEY_STAGE и DEPLOY_KEY_PROD (Masked и Protected; ветки main и release/* защищены) значениями MODULES_DEPLOY_KEY_TEST, MODULES_DEPLOY_KEY_STAGE, MODULES_DEPLOY_KEY_PROD из .env. Старый общий ключ принимается только для test."; fi
# firewall:begin
# IPv4 в виде a.b.c.d (имена хостов не резолвим: ответ DNS на другой машине может отличаться).
is_ipv4() {
  local o oldifs="$IFS"
  case "$1" in ''|*[!0-9.]*|.*|*.|*..*) return 1 ;; esac
  [ "$(printf '%s' "$1" | tr -cd . | wc -c)" = 3 ] || return 1
  IFS=.; set -- $1; IFS="$oldifs"
  # Без ведущих нулей: «010» разные программы читают как 8 или как 10.
  for o in "$@"; do [ "${#o}" -le 3 ] && [ "$o" -le 255 ] || return 1; case "$o" in 0?*) return 1 ;; esac; done
}
# Файл правил для служебных портов. $1 - INTERNAL_BIND, $2 - адреса машин стенда, $3 - порты (всё через пробел). Печатает файл в stdout.
fw_linux_script() {
  local bind="$1" peers="$2" ports="$3" p a
  cat <<HEAD
#!/bin/sh
# Правила брандмауэра для служебных портов этой машины (INTERNAL_BIND=$bind). Создан install.sh из .env, автоматически не применяется.
# Порты (PostgreSQL, RustFS, миграция, Prometheus, Loki, агенты): $ports
# Разрешены адреса: ${peers:-нет} (машины стенда из .env и сама эта машина). Остальным эти порты закрыты.
# Порты админки, Grafana, Pilot и GitLab правила не затрагивают. Адрес каждой машины стенда должен быть в .env
# (PROD_HOST, TEST_HOST, STAGE_HOST, METRICS_HOST, ADMIN_HOST, GITLAB_HOST_IP), иначе она потеряет доступ к этим портам.
# Docker обходит ufw и цепочку INPUT, поэтому правила стоят в DOCKER-USER (своя цепочка INTERNAL). Файл рассчитан на Docker с iptables:
# без цепочки DOCKER-USER (nftables-бэкенд без неё или "iptables": false) он остановится, ничего не изменив.
# Любой интерфейс br-* считается мостом Docker и не режется: не называйте так посторонние мосты.
#   проверить без применения:   sh firewall-linux.sh --test
#   применить (от root, повтор безопасен):   sh firewall-linux.sh
#   снять:   sh firewall-linux.sh --remove
# После перезагрузки правила пропадают: запускайте файл при старте системы, после docker.
set -e
RULES='*filter
:INTERNAL - [0:0]
-A INTERNAL -i br-+ -j RETURN
-A INTERNAL -i docker0 -j RETURN
HEAD
  for p in $ports; do
    for a in $peers; do echo "-A INTERNAL -p tcp -m conntrack --ctstate DNAT --ctdir ORIGINAL --ctorigdstport $p -s $a/32 -j RETURN"; done
    echo "-A INTERNAL -p tcp -m conntrack --ctstate DNAT --ctdir ORIGINAL --ctorigdstport $p -j DROP"
  done
  cat <<'TAIL'
COMMIT'
case "${1:-}" in
  --test) printf '%s\n' "$RULES" | iptables-restore --test --noflush; exit 0 ;;
  --remove) iptables -D DOCKER-USER -j INTERNAL 2>/dev/null || true; iptables -F INTERNAL 2>/dev/null || true; iptables -X INTERNAL 2>/dev/null || true; exit 0 ;;
esac
iptables -n -L DOCKER-USER >/dev/null 2>&1 || { echo "Нет цепочки DOCKER-USER: Docker не управляет iptables на этой машине, правила не применены." >&2; exit 1; }
printf '%s\n' "$RULES" | iptables-restore --noflush
iptables -C DOCKER-USER -j INTERNAL 2>/dev/null || iptables -I DOCKER-USER 1 -j INTERNAL
TAIL
}
# firewall:end
BIND="$(cfg INTERNAL_BIND)"
if [ -n "$BIND" ]; then
  PORTS=""
  for c in $MY_CONTOURS; do case "$c" in prod) PORTS="$PORTS 5433 9000 9001" ;; test) PORTS="$PORTS 5434 9010 9011" ;; stage) PORTS="$PORTS 5435 9020 9021" ;; esac; done
  if has_role admin; then mp="$(cfg MIGRATION_PORT)"; PORTS="$PORTS ${mp:-5320}"; fi
  if has_role metrics; then pp="$(cfg PROMETHEUS_PORT)"; PORTS="$PORTS ${pp:-9090} 3100"; fi
  case ",$PROFILES," in *,agents,*) PORTS="$PORTS 9100 9101" ;; esac
  # Адреса других машин стенда (и этой): правила пускают на служебные порты только их.
  PEERS=""; SKIPPED=""
  for k in PROD_HOST TEST_HOST STAGE_HOST METRICS_HOST ADMIN_HOST GITLAB_HOST_IP; do
    v="$(cfg "$k")"
    if [ -z "$v" ] || { [ "$k" = GITLAB_HOST_IP ] && [ "$v" = localhost ]; }; then continue; fi
    if is_ipv4 "$v"; then case " $PEERS " in *" $v "*) ;; *) PEERS="$PEERS $v" ;; esac; else SKIPPED="$SKIPPED $k=$v"; fi
  done
  if is_ipv4 "$BIND" && [ "$BIND" != 0.0.0.0 ]; then case " $PEERS " in *" $BIND "*) ;; *) PEERS="$PEERS $BIND" ;; esac; fi
  FW=firewall-linux.sh
  fw_linux_script "$BIND" "${PEERS# }" "${PORTS# }" > "$FW"
  echo "! INTERNAL_BIND=$BIND: служебные порты слушают сеть без аутентификации:$PORTS. Правила брандмауэра, которые пускают на них только машины стенда, записаны в $(pwd)/$FW: просмотрите и примените от root (sh $FW). Автоматически они не применяются."
  if [ -n "$SKIPPED" ]; then echo "! В правила не вошли значения, которые не IP-адреса:$SKIPPED. Имена хостов не резолвятся: задайте IP, иначе эти машины не получат доступа."; fi
else
  # Правила относятся к заданному INTERNAL_BIND: устаревший файл потом применили бы по ошибке.
  rm -f firewall-linux.sh
fi
exit "$POST_FAIL"
