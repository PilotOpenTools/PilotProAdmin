#!/usr/bin/env bash
# Смена паролей контура Pilot (prod | test | stage): PostgreSQL, RustFS и служебная S3-учётка Pilot.
# Linux-аналог rotate-contour-secrets.ps1 (раскладка по папкам: prod, test, stage, proadmin, metrics рядом).
#
#   bash shared/rotate-contour-secrets.sh --env test
#   bash shared/rotate-contour-secrets.sh --env prod --confirm-prod prod
#   параметры: --env <prod|test|stage>, --confirm-prod prod (обязателен для prod), --data-root <каталог данных> (по умолчанию DATA_ROOT из <контур>/.env, иначе /opt/ascon-docker)
#
# Что делает (шаги те же, что в .ps1):
#   1. Генерирует новые пароли (32 символа [A-Za-z0-9] без похожих, из /dev/urandom) и пишет их в <контур>/.env (права 600).
#   2. RustFS: заводит/обновляет учётку MINIO_PILOT_USER (по умолчанию pilot) с доступом только к бакету pilot-ice-ru.
#   3. PostgreSQL: ALTER ROLE postgres и pilotuser; для prod ещё роли метрик metrics_writer и metrics_reader (после пересоздания контейнера).
#   4. Останавливает pilot-server, правит settings.xml (копия рядом: settings.xml.before-rotate-<время>): пароль в строке
#      подключения, AccessKeyId и EncryptedSecretAccessKey S3-архива (шифрование как у Pilot, Ascon.Pilot.Common.AesEx).
#   5. docker compose up -d контура; сервис миграции (proadmin/migration/data/config.json) и proadmin/.env получают S3-ключи
#      учётки pilot; для prod ещё metrics/.env (PG_USER, PG_PASSWORD и METRICS_READER_PASSWORD).
#   6. Если в proadmin/.env задан MODULES_DEPLOY_KEY_<КОНТУР>, выдаёт новый и пересоздаёт админку. Новый ключ нужно сразу
#      прописать в переменной группы GitLab DEPLOY_KEY_<КОНТУР>: до этого выкладка из CI в этот контур возвращает 401.
#   7. Проверяет вход pilotuser по сети, доступ pilot к бакету и старт базы в pilot-server.
#
# Нужны: docker, openssl 3.x (openssl kdf), perl (с модулем JSON::PP), base64. Пароли не печатаются и не попадают в аргументы
# docker, кроме команды rc admin user add: rc принимает пароль пользователя только аргументом, и на время команды он виден
# в списке процессов хоста (ps). Ключ root и пароль проверки передаются окружением (RC_HOST_<алиас>), в аргументы не попадают.
# Для установки из папки Install (общий .env, проект cicd) скрипт не подходит: там пароли меняют вручную, см. Install/README.md.
# Снимите бэкап перед запуском. Повторный запуск выдаёт новые пароли.
set -eu
umask 077

STACK="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_NAME=""; CONFIRM_PROD=""; DATA_ROOT=""
SECRETS=()   # значения для вычёркивания из вывода ошибок

fail() { echo "ОШИБКА: $*" >&2; exit 1; }
info() { printf '[%s] %s\n' "$ENV_NAME" "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    -e|--env) [ $# -ge 2 ] || fail "после $1 нужно значение"; ENV_NAME="$2"; shift 2 ;;
    --confirm-prod) [ $# -ge 2 ] || fail "после $1 нужно значение"; CONFIRM_PROD="$2"; shift 2 ;;
    --data-root) [ $# -ge 2 ] || fail "после $1 нужно значение"; DATA_ROOT="$2"; shift 2 ;;
    -h|--help) sed -n '2,24p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) fail "неизвестный параметр: $1" ;;
  esac
done
case "$ENV_NAME" in prod|test|stage) ;; *) fail "укажите --env prod|test|stage" ;; esac
if [ "$ENV_NAME" = prod ] && [ "$CONFIRM_PROD" != prod ]; then fail "Для прода добавьте --confirm-prod prod"; fi
for tool in docker openssl perl base64; do command -v "$tool" >/dev/null || fail "не найден $tool"; done

ENV_DIR="$STACK/$ENV_NAME"
PG="postgres-$ENV_NAME"; RUSTFS="rustfs-$ENV_NAME"; SERVER="pilot-server-$ENV_NAME"
KEY_NAME="$(printf '%s' "$ENV_NAME" | tr 'a-z' 'A-Z')"
if [ -z "$DATA_ROOT" ] && [ -f "$ENV_DIR/.env" ]; then DATA_ROOT="$(sed -n 's/^DATA_ROOT=//p' "$ENV_DIR/.env" | head -n 1 | tr -d '\r"')"; fi
DATA_ROOT="${DATA_ROOT:-/opt/ascon-docker}"
SETTINGS="$DATA_ROOT/$ENV_NAME/pilot-server/settings/settings.xml"
PROADMIN_ENV="$STACK/../proadmin/.env"
MIGRATION_CFG="$STACK/../proadmin/migration/data/config.json"
T="$(mktemp -d)"; trap 'rm -rf "$T"' EXIT
STAMP="$(date +%Y%m%d-%H%M%S)"

# Команда без вывода; при ошибке печатается её вывод (без значений секретов) и код. Аргументы не печатаются: в них бывают пароли.
quiet() {
  local out rc=0 s
  out="$("$@" 2>&1)" || rc=$?
  if [ "$rc" != 0 ]; then
    for s in ${SECRETS[@]+"${SECRETS[@]}"}; do out="${out//"$s"/***}"; done
    printf '%s\n' "$out" >&2
    fail "$1 ${2:-} ... -> код $rc"
  fi
}

# Секрет кладётся в переменную с именем $1 (не печатается; без подоболочки, чтобы он попал в SECRETS).
new_secret() {
  local len="${2:-32}" s
  s="$(LC_ALL=C tr -dc 'A-HJ-NP-Za-km-z2-9' < /dev/urandom | head -c "$len")"
  [ "${#s}" = "$len" ] || fail "не удалось получить случайные байты из /dev/urandom"
  SECRETS+=("$s")
  printf -v "$1" '%s' "$s"
}

# Копия Ascon.Pilot.Common.AesEx (Pilot-Server 26.x): PBKDF2-SHA1 из фиксированных пароля '123456' и соли
# '{5AF2954F-02FC-445A-BC27-976DD11A6258}' (обе в UTF-16LE, hex), 1000 итераций; ключ AES-256 - первые 32 байта, IV - следующие 16.
PILOT_KEY_IV=""
pilot_key_iv() {
  if [ -z "$PILOT_KEY_IV" ]; then
    PILOT_KEY_IV="$(openssl kdf -keylen 48 -kdfopt digest:SHA1 -kdfopt hexpass:310032003300340035003600 \
      -kdfopt hexsalt:7b00350041004600320039003500340046002d0030003200460043002d0034003400350041002d0042004300320037002d003900370036004400440031003100410036003200350038007d00 \
      -kdfopt iter:1000 PBKDF2 | tr -d ':\n' | tr 'A-F' 'a-f')"
    [ "${#PILOT_KEY_IV}" = 96 ] || fail "openssl kdf не вернул ключ (нужен OpenSSL 3.0 или новее)"
  fi
}
pilot_aes_enc() { pilot_key_iv; printf '%s' "$1" | openssl enc -aes-256-cbc -K "${PILOT_KEY_IV:0:64}" -iv "${PILOT_KEY_IV:64:32}" -a -A; }
pilot_aes_dec() { pilot_key_iv; printf '%s' "$1" | openssl enc -d -aes-256-cbc -K "${PILOT_KEY_IV:0:64}" -iv "${PILOT_KEY_IV:64:32}" -a -A; }

# KEY=value в .env: заменить первую строку или дописать, остальное не трогать. Запись в тот же файл (владелец и права сохраняются).
set_env() {
  local f="$1" k="$2" v="$3" tmp
  if grep -q "^$k=" "$f" 2>/dev/null; then
    tmp="$T/env.$$"
    K="$k" V="$v" perl -pe 'BEGIN { $k = $ENV{K}; $v = $ENV{V} } if (!$d && /^\Q$k\E=/) { $_ = "$k=$v\n"; $d = 1 }' "$f" > "$tmp"
    cat "$tmp" > "$f"; rm -f "$tmp"
  else
    if [ -s "$f" ] && [ -n "$(tail -c1 "$f")" ]; then echo >> "$f"; fi
    printf '%s=%s\n' "$k" "$v" >> "$f"
  fi
}

container_env() { docker inspect "$1" --format '{{range .Config.Env}}{{println .}}{{end}}' | sed -n "s/^$2=//p" | head -n 1; }

# Элемент S3-архива этого хранилища из settings.xml: атрибуты в любом порядке, элемент может занимать несколько строк.
# Адрес в settings.xml: rustfs-<контур>.
s3_element() {
  ENVN="$ENV_NAME" perl -0777 -e '$m = quotemeta $ENV{ENVN}; $t = <STDIN>;
    if ($t =~ m{(<FileArchiveParametersS3\b[^>]*\bUrl="http://rustfs-${m}:9000"[^>]*/>)}) { print $1; exit 0 } exit 1' < "$SETTINGS"
}

# Значение окружения RC_HOST_<алиас> для rc: http://КЛЮЧ:СЕКРЕТ@хост:порт, ключ и секрет в URL-кодировке.
urlenc() { printf '%s' "$1" | perl -pe 's/([^A-Za-z0-9._~-])/sprintf("%%%02X", ord($1))/ge'; }
rc_host() { printf 'http://%s:%s@127.0.0.1:9000' "$(urlenc "$1")" "$(urlenc "$2")"; }
xml_attr() { printf '%s' "$1" | A="$2" perl -0777 -ne 'print $1 if /\b$ENV{A}="([^"]*)"/'; }

# --- 0. Проверки и текущие значения
# config.json разбирается до любых изменений (иначе отказ на шаге 5, когда пароли уже сменены).
if [ -f "$MIGRATION_CFG" ]; then perl -MJSON::PP -0777 -e 'JSON::PP->new->utf8->decode(<STDIN>)' < "$MIGRATION_CFG" 2>/dev/null || fail "не удалось разобрать $MIGRATION_CFG"; fi
pilot_key_iv   # кэшируется в основной оболочке; нет openssl kdf - остановка до любых изменений
for c in "$PG" "$RUSTFS" "$SERVER"; do
  [ -n "$(docker ps --filter "name=^$c\$" --format '{{.Names}}')" ] || fail "$c не запущен"
done
[ -f "$SETTINGS" ] || fail "нет $SETTINGS"
S3_EL="$(s3_element)" || fail "В settings.xml нет S3-архива на $RUSTFS (адрес rustfs-$ENV_NAME)"
grep -q "Host=$PG:5432;Username=pilotuser;Password=" "$SETTINGS" || fail "В settings.xml нет строки подключения к $PG"
OLD_ENC="$(xml_attr "$S3_EL" EncryptedSecretAccessKey)"
OLD_KEY_ID="$(xml_attr "$S3_EL" AccessKeyId)"
ROOT_USER="$(container_env "$RUSTFS" RUSTFS_ACCESS_KEY)"
ROOT_PASS="$(container_env "$RUSTFS" RUSTFS_SECRET_KEY)"
[ -n "$ROOT_USER" ] && [ -n "$ROOT_PASS" ] || fail "не удалось прочитать RUSTFS_ACCESS_KEY и RUSTFS_SECRET_KEY из $RUSTFS"
SECRETS+=("$ROOT_PASS")
# Самопроверка шифрования: текущий секрет из settings.xml должен расшифровываться в действующий ключ.
if [ "$OLD_KEY_ID" = "$ROOT_USER" ]; then
  OLD_PLAIN="$(pilot_aes_dec "$OLD_ENC" 2>/dev/null)" || OLD_PLAIN=""
  [ "$OLD_PLAIN" = "$ROOT_PASS" ] || fail "Шифрование Pilot не совпало (EncryptedSecretAccessKey не равен паролю root RustFS) - версия Pilot изменилась?"
fi
info "шифрование Pilot проверено"

ENV_FILE="$ENV_DIR/.env"
new_secret NEW_PG_SUPERUSER; new_secret NEW_PILOT_PG; new_secret NEW_MINIO_PASSWORD; new_secret NEW_MINIO_PILOT_SECRET
if [ "$ENV_NAME" = prod ]; then new_secret NEW_METRICS_WRITER; new_secret NEW_METRICS_READER; fi
NEW_MINIO_PILOT_USER=pilot
if [ ! -f "$ENV_FILE" ]; then
  [ -f "$ENV_DIR/.env.example" ] || fail "нет $ENV_FILE и $ENV_DIR/.env.example"
  cp "$ENV_DIR/.env.example" "$ENV_FILE"
fi
ENV_BACKUP="$ENV_FILE.before-rotate-$STAMP"
cp -p "$ENV_FILE" "$ENV_BACKUP"

# --- 1. RustFS: учётка pilot с доступом только к бакету pilot-ice-ru (пока под старым root)
# Клиент rc из контейнера хранилища; алиас rot задаётся окружением RC_HOST_rot (ключ не оседает в файле настроек rc).
# Проверено живьём на RustFS 1.0.5 (Windows, Docker Desktop): rc admin policy create / user add / policy attach, в том числе повтор для существующих. RC_HOST_<алиас> по проверке не работает со «спецсимволами» в ключе или секрете (у новых паролей ротации только [A-Za-z0-9]); на Linux-хосте не проверялось.
POLICY='{"Version":"2012-10-17","Statement":[{"Effect":"Allow","Action":["s3:ListAllMyBuckets","s3:GetBucketLocation"],"Resource":["arn:aws:s3:::*"]},{"Effect":"Allow","Action":["s3:*"],"Resource":["arn:aws:s3:::pilot-ice-ru","arn:aws:s3:::pilot-ice-ru/*"]}]}'
printf '%s' "$POLICY" | quiet docker exec -i "$RUSTFS" sh -c 'cat > /tmp/pilot-policy.json'
export RC_HOST_rot; RC_HOST_rot="$(rc_host "$ROOT_USER" "$ROOT_PASS")"
quiet docker exec -e RC_HOST_rot "$RUSTFS" rc admin policy create rot/ pilot-bucket /tmp/pilot-policy.json
quiet docker exec -e RC_HOST_rot "$RUSTFS" rc admin user add rot/ "$NEW_MINIO_PILOT_USER" "$NEW_MINIO_PILOT_SECRET"
docker exec -e RC_HOST_rot "$RUSTFS" rc admin policy attach rot/ pilot-bucket --user "$NEW_MINIO_PILOT_USER" >/dev/null 2>&1 || true   # «уже привязана» - не ошибка; доступ проверяет шаг 7
unset RC_HOST_rot
quiet docker exec "$RUSTFS" rm -f /tmp/pilot-policy.json
info "RustFS: учётка $NEW_MINIO_PILOT_USER с доступом к pilot-ice-ru"

# --- 2. PostgreSQL: пароли ролей (SQL - через stdin, не через аргументы)
printf "ALTER ROLE postgres PASSWORD '%s'; ALTER ROLE pilotuser PASSWORD '%s';\n" "$NEW_PG_SUPERUSER" "$NEW_PILOT_PG" \
  | quiet docker exec -i "$PG" psql -q -U postgres -v ON_ERROR_STOP=1
info "PostgreSQL: пароли postgres и pilotuser сменены"

# --- 3. settings.xml при остановленном pilot-server
SINCE="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
quiet docker stop "$SERVER"
cp -p "$SETTINGS" "$SETTINGS.before-rotate-$STAMP"
NEW_ENC="$(pilot_aes_enc "$NEW_MINIO_PILOT_SECRET")"
PG="$PG" ENVN="$ENV_NAME" NEW_PG="$NEW_PILOT_PG" NEW_USER="$NEW_MINIO_PILOT_USER" NEW_ENC="$NEW_ENC" perl -0777 -e '
  $t = <STDIN>; $pg = quotemeta $ENV{PG}; $m = quotemeta $ENV{ENVN};
  $t =~ s{(Host=${pg}:5432;Username=pilotuser;Password=)[^;"]*}{$1$ENV{NEW_PG}}g or die "нет строки подключения\n";
  $t =~ s{(<FileArchiveParametersS3\b[^>]*\bUrl="http://rustfs-${m}:9000"[^>]*/>)}{
    $e = $1; $e =~ s/\bAccessKeyId="[^"]*"/AccessKeyId="$ENV{NEW_USER}"/; $e =~ s/\bEncryptedSecretAccessKey="[^"]*"/EncryptedSecretAccessKey="$ENV{NEW_ENC}"/; $e
  }e or die "нет S3-архива\n";
  print $t;' < "$SETTINGS" > "$T/settings.xml" || fail "не удалось подготовить settings.xml"
cat "$T/settings.xml" > "$SETTINGS"

# --- 4. .env и пересоздание контейнеров контура (RustFS с новым root-паролем)
set_env "$ENV_FILE" PG_SUPERUSER_PASSWORD "$NEW_PG_SUPERUSER"
set_env "$ENV_FILE" PILOT_PG_PASSWORD "$NEW_PILOT_PG"
set_env "$ENV_FILE" MINIO_USER "$ROOT_USER"
set_env "$ENV_FILE" MINIO_PASSWORD "$NEW_MINIO_PASSWORD"
set_env "$ENV_FILE" MINIO_PILOT_USER "$NEW_MINIO_PILOT_USER"
set_env "$ENV_FILE" MINIO_PILOT_SECRET "$NEW_MINIO_PILOT_SECRET"
if [ "$ENV_NAME" = prod ]; then
  set_env "$ENV_FILE" METRICS_WRITER_PASSWORD "$NEW_METRICS_WRITER"
  set_env "$ENV_FILE" METRICS_READER_PASSWORD "$NEW_METRICS_READER"
fi
chmod 600 "$ENV_FILE"
(cd "$ENV_DIR" && quiet docker compose up -d)
info "контур пересоздан с новыми паролями"
if [ "$ENV_NAME" = prod ]; then
  # Контейнер PostgreSQL пересоздан с новыми паролями ролей метрик в окружении: скрипт задаёт их ролям (и создаёт роли, если их ещё нет).
  n=0; until docker exec "$PG" pg_isready -h 127.0.0.1 -U postgres >/dev/null 2>&1; do n=$((n + 1)); [ "$n" -le 90 ] || fail "$PG не поднялся после пересоздания"; sleep 2; done
  quiet docker exec "$PG" bash /docker-entrypoint-initdb.d/02-metrics-roles.sh
  info "PostgreSQL: пароли metrics_writer и metrics_reader сменены"
fi

# --- 5. Потребители: миграция, админка, (prod) метрики
set_env "$PROADMIN_ENV" "${KEY_NAME}_S3_ACCESS_KEY" "$NEW_MINIO_PILOT_USER"
set_env "$PROADMIN_ENV" "${KEY_NAME}_S3_SECRET_KEY" "$NEW_MINIO_PILOT_SECRET"
chmod 600 "$PROADMIN_ENV"
if [ -f "$MIGRATION_CFG" ]; then
  # Ключи S3 этого контура в config.json; формат как у сервиса миграции (отступ 2, порядок ключей не сохраняется).
  KEY="$ENV_NAME" NEW_USER="$NEW_MINIO_PILOT_USER" NEW_SECRET="$NEW_MINIO_PILOT_SECRET" perl -MJSON::PP -0777 -e '
    $j = JSON::PP->new->utf8->indent->indent_length(2)->space_after->canonical;
    $c = $j->decode(<STDIN>);
    for $e (@{ $c->{Environments} || [] }) {
      next unless lc($e->{Key} // "") eq lc($ENV{KEY});
      for $s (@{ $e->{S3} || [] }) {
        next unless ($s->{ServerUrl} // "") =~ /rustfs-\Q$ENV{KEY}\E\b/;
        $s->{AccessKey} = $ENV{NEW_USER}; $s->{SecretKey} = $ENV{NEW_SECRET};
      }
    }
    print $j->encode($c);' < "$MIGRATION_CFG" > "$T/config.json" || fail "не удалось разобрать $MIGRATION_CFG"
  cp -p "$MIGRATION_CFG" "$MIGRATION_CFG.before-rotate-$STAMP"
  cat "$T/config.json" > "$MIGRATION_CFG"
  quiet docker restart pilot-migration
  info "сервис миграции: S3-ключи обновлены"
fi
if [ "$ENV_NAME" = prod ]; then
  set_env "$STACK/metrics/.env" PG_USER metrics_writer
  set_env "$STACK/metrics/.env" PG_PASSWORD "$NEW_METRICS_WRITER"
  set_env "$STACK/metrics/.env" METRICS_READER_PASSWORD "$NEW_METRICS_READER"
  chmod 600 "$STACK/metrics/.env"
  (cd "$STACK/metrics" && quiet docker compose up -d)
  info "метрики: пароли ролей обновлены, сборщики и Grafana пересозданы"
fi

# --- 5a. Ключ выкладки модулей из CI: у каждого контура свой
DEPLOY_KEY_NAME="MODULES_DEPLOY_KEY_$KEY_NAME"
NEW_DEPLOY_KEY=""
if [ -f "$PROADMIN_ENV" ] && grep -q "^$DEPLOY_KEY_NAME=." "$PROADMIN_ENV"; then
  new_secret NEW_DEPLOY_KEY 40
  set_env "$PROADMIN_ENV" "$DEPLOY_KEY_NAME" "$NEW_DEPLOY_KEY"
  (cd "$STACK/../proadmin" && quiet docker compose up -d)
  info "ключ выкладки модулей обновлён, админка пересоздана"
  # Сразу, а не в конце: если проверка шага 6 упадёт, ключ уже сменён и CI получает 401, пока его не обновят.
  echo "ВАЖНО: ключ выкладки уже сменён. Задайте в переменных группы GitLab DEPLOY_KEY_$KEY_NAME новое значение ($DEPLOY_KEY_NAME из $PROADMIN_ENV), иначе выкладка из CI в $ENV_NAME вернёт 401."
fi

# --- 6. Проверка
PG_IMAGE="$(docker inspect "$PG" --format '{{.Config.Image}}')"
export PGPASSWORD="$NEW_PILOT_PG"
quiet docker run --rm --network "pilot-$ENV_NAME" -e PGPASSWORD "$PG_IMAGE" psql -h "$PG" -U pilotuser -d pilot-ice_ru -Atc 'select 1'
unset PGPASSWORD
info "проверка: pilotuser входит в Postgres по сети"
export RC_HOST_chk; RC_HOST_chk="$(rc_host "$NEW_MINIO_PILOT_USER" "$NEW_MINIO_PILOT_SECRET")"
LISTING="$(docker exec -e RC_HOST_chk "$RUSTFS" rc ls --recursive chk/pilot-ice-ru 2>&1)" || { unset RC_HOST_chk; fail "учётка $NEW_MINIO_PILOT_USER не видит бакет pilot-ice-ru"; }
unset RC_HOST_chk
info "проверка: учётка pilot видит $(printf '%s\n' "$LISTING" | grep -c . || true) объектов в pilot-ice-ru"
# Pilot пишет «UpdateDatabases: pilot-ice_ru is launched» или «pilot-ice_ru to state Broken».
STATE=""
for _ in $(seq 60); do
  LOGS="$(docker logs --since "$SINCE" "$SERVER" 2>&1 || true)"
  if printf '%s' "$LOGS" | grep -q 'pilot-ice_ru to state Broken'; then STATE=Broken; break; fi
  if printf '%s' "$LOGS" | grep -q 'pilot-ice_ru is launched'; then STATE=Launched; break; fi
  sleep 3
done
[ "$STATE" = Launched ] || fail "[$ENV_NAME] база в $SERVER не запустилась (состояние: ${STATE:-нет данных}). Действующие пароли уже в .env и в Postgres; сверьте settings.xml с .env (прежний - settings.xml.before-rotate-*)"
info "pilot-server: база pilot-ice_ru запущена"
if [ -n "$NEW_DEPLOY_KEY" ]; then echo "ВАЖНО: задайте в переменных группы GitLab DEPLOY_KEY_$KEY_NAME новое значение ($DEPLOY_KEY_NAME из $PROADMIN_ENV), иначе выкладка из CI в $ENV_NAME вернёт 401."; fi
echo "Готово. Копия прежнего .env: $ENV_BACKUP"
