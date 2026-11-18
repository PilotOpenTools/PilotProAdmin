#!/usr/bin/bash
# Обёртка запуска pilot-server:
#  1) применяет настройки сервера из админки (server-settings.conf) - до старта сервера, пока он не держит
#     конфигурацию в памяти и не перезапишет правку. Монолит: правит settings.xml. Узел кластера (среди аргументов
#     есть --pgConnectionString=...): конфигурация лежит в БД, её читает и пишет только Daemon -rc / -wc;
#  2) ждёт, пока PostgreSQL реально примет подключение, и только потом запускает pilot-server.
#     Нужен, потому что при перезапуске Docker/Windows контейнеры поднимаются по restart-policy без учёта
#     depends_on/healthcheck: сервер стартует раньше базы, получает "Connection is not open" и помечает БД Broken.
#     Проверка: шлём StartupMessage протокола PG; ответ 'R' (запрос аутентификации) = сервер готов,
#     'E' (FATAL "starting up") или отказ в соединении = ещё нет.
# Файл одинаковый в shared/ и Install/ (сверяет RepoCopiesTests) - правьте обе копии; prod/, test/, stage/ монтируют shared/.
# Проверка поведения: stack/build/selftest/wait-for-postgres-selftest.sh.
DAEMON="${PILOT_DAEMON:-/App/Ascon.Pilot.Daemon}"
CLUSTER_TIMEOUT="${CLUSTER_CONFIG_TIMEOUT:-120}"

# Узел кластера: строка подключения к PostgreSQL - аргумент --pgConnectionString=... (у монолита её нет).
CLUSTER_CONN=""
for arg in "$@"; do
  case "$arg" in --pgConnectionString=*) CLUSTER_CONN="${arg#--pgConnectionString=}" ;; esac
done

# Хост и порт PostgreSQL из строки подключения (Host=имя[:порт];Port=порт;...). shortcut: IPv6 и список хостов не разбираются.
conn_host=""; conn_port=""
parse_cluster_conn() {
  local part key value
  local -a parts
  IFS=';' read -ra parts <<< "$CLUSTER_CONN"
  for part in "${parts[@]}"; do
    key="${part%%=*}"; key="${key//[[:space:]]/}"; key="${key,,}"
    value="${part#*=}"
    case "$key" in
      host|server) conn_host="${value%%,*}" ;;
      port) conn_port="$value" ;;
    esac
  done
  case "$conn_host" in *:*) conn_port="${conn_host##*:}"; conn_host="${conn_host%:*}" ;; esac
  [[ "$conn_port" =~ ^[0-9]+$ ]] || conn_port=""
}
[ -n "$CLUSTER_CONN" ] && parse_cluster_conn

if [ -n "$conn_host" ] && [ -z "${PG_WAIT_HOST:-}" ]; then
  HOST="$conn_host"
  PORT="${PG_WAIT_PORT:-${conn_port:-5432}}"
else
  HOST="${PG_WAIT_HOST:-postgres}"
  PORT="${PG_WAIT_PORT:-5432}"
fi
MAX="${PG_WAIT_TIMEOUT:-300}"

# ---------------------------------------------------------------- настройки сервера
# Каталог контура C:/Ascon-Docker/<контур>/server-settings, смонтирован в /server-settings.
#   server-settings.conf - пишет админка («Серверы») и миграция: строки Параметр=true|false;
#   applied.conf         - пишет этот скрипт: фактические значения после применения (в кластере ещё строка Mode=cluster).
# Управляются только параметры из списка ниже, значения - только true/false (произвольный XML не вставить).
# Нет каталога или файла - settings.xml (и конфигурация кластера) не трогается.
SETTINGS_XML="${1:-/usr/share/ascon/pilot-server/settings/settings.xml}"
SERVER_SETTINGS_DIR="${SERVER_SETTINGS_DIR:-/server-settings}"
SERVER_SETTINGS_KEYS="IsAutomationLoggingEnabled IsExtendedLoggingEnabled IsFileReadJournalingEnabled EnableCSharpScriptDebug IsSnapshotEnabled CheckChangeCreator AreParallelChangesDisabled"

# Общее ядро: применяет server-settings.conf к XML-файлу $1 (правит на месте). $2=quiet - без предупреждений о строках conf.
apply_conf_to_xml() {
  local file="$1" conf="$SERVER_SETTINGS_DIR/server-settings.conf" line key value
  while IFS= read -r line || [ -n "$line" ]; do
    line="${line%%#*}"
    line="$(printf '%s' "$line" | tr -d ' \t\r')"
    [ -z "$line" ] && continue
    key="${line%%=*}"
    value="${line#*=}"
    case " $SERVER_SETTINGS_KEYS " in
      *" $key "*) ;;
      *) [ "${2:-}" = quiet ] || echo "[server-settings] неизвестный параметр «$key» - пропущен" >&2; continue ;;
    esac
    case "$value" in
      true|false) ;;
      *) [ "${2:-}" = quiet ] || echo "[server-settings] $key=$value: допустимо только true/false - пропущен" >&2; continue ;;
    esac
    if grep -q "<$key>" "$file"; then
      sed -i "s#<$key>[^<]*</$key>#<$key>$value</$key>#" "$file"
    else
      sed -i "s#</Configuration>#  <$key>$value</$key>\n</Configuration>#" "$file"
    fi
  done < "$conf"
}

# applied.conf из XML-файла $1; $2 - что это за файл (для заголовка), $3 - режим (в монолите строки Mode нет).
write_applied_conf() {
  local xml="$1" label="${2:-settings.xml}" mode="${3:-}" key value
  {
    echo "# Фактические значения $label при старте контейнера. Пишет wait-for-postgres.sh, не править."
    echo "AppliedAt=$(date -Iseconds)"
    [ -z "$mode" ] || echo "Mode=$mode"
    for key in $SERVER_SETTINGS_KEYS; do
      value="$(sed -n "s#.*<$key>\([^<]*\)</$key>.*#\1#p" "$xml" | head -n1)"
      [ -z "$value" ] || echo "$key=$value"  # не [ -n ] &&: иначе статус группы 1 и mv не выполнится
    done
  } > "$SERVER_SETTINGS_DIR/applied.conf.tmp" && mv -f "$SERVER_SETTINGS_DIR/applied.conf.tmp" "$SERVER_SETTINGS_DIR/applied.conf"
}

apply_server_settings() {
  local conf="$SERVER_SETTINGS_DIR/server-settings.conf" xml="$SETTINGS_XML" tmp
  [ -d "$SERVER_SETTINGS_DIR" ] && [ -f "$xml" ] || return 0
  tmp="$xml.server-settings.tmp"

  if [ -f "$conf" ]; then
    cp "$xml" "$tmp" || return 0
    apply_conf_to_xml "$tmp"
    if ! cmp -s "$xml" "$tmp"; then
      cp "$xml" "$xml.before-server-settings"
      # Перезапись содержимого, а не mv: settings.xml - файл на bind-mount.
      cat "$tmp" > "$xml"
      echo "[server-settings] settings.xml обновлён по server-settings.conf (копия до правки - settings.xml.before-server-settings)" >&2
    else
      echo "[server-settings] settings.xml уже соответствует server-settings.conf" >&2
    fi
    rm -f "$tmp"
  fi

  write_applied_conf "$xml"
}

# Кластер: Daemon -rc|-wc <строка подключения> <файл>. Вывод отбрасывается - в нём может быть строка подключения с паролем.
# Пустой или не созданный файл после -rc - тоже сбой.
daemon_cfg() {
  local mode="$1" file="$2" rc
  [ "$mode" = rc ] && rm -f "$file"
  timeout -k 5 "$CLUSTER_TIMEOUT" "$DAEMON" "-$mode" "$CLUSTER_CONN" "$file" >/dev/null 2>&1
  rc=$?
  if [ "$rc" -eq 0 ] && [ "$mode" = rc ] && [ ! -s "$file" ]; then rc=1; fi
  if [ "$rc" -ne 0 ]; then
    echo "[server-settings] Daemon -$mode: код $rc$([ "$rc" -eq 124 ] && echo ' (тайм-аут)')" >&2
  fi
  return "$rc"
}

# Порядок: -rc, применение conf к копии, повторный -rc (конфигурацию могли изменить между чтениями - тогда заново, до 3 раз),
# -wc, проверочный -rc. applied.conf пишется только из прочитанной конфигурации, чтобы не показывать непримёненное.
apply_cluster_in() {
  local d="$1" attempt state="" wrote=0
  for attempt in 1 2 3; do
    daemon_cfg rc "$d/a.xml" || return 1
    cp "$d/a.xml" "$d/b.xml" || return 1
    apply_conf_to_xml "$d/b.xml"
    if cmp -s "$d/a.xml" "$d/b.xml"; then
      echo "[server-settings] конфигурация кластера уже соответствует server-settings.conf" >&2
      state="current"; break
    fi
    daemon_cfg rc "$d/c.xml" || return 1
    if cmp -s "$d/a.xml" "$d/c.xml"; then state="write"; break; fi
    echo "[server-settings] конфигурация кластера изменилась во время чтения (попытка $attempt из 3) - повтор" >&2
  done
  if [ -z "$state" ]; then
    echo "[server-settings] конфигурация кластера менялась при каждой попытке - настройки не применены" >&2
    return 1
  fi

  local read_from="$d/a.xml"
  if [ "$state" = write ]; then
    daemon_cfg wc "$d/b.xml"; wrote=$?
    daemon_cfg rc "$d/v.xml" || return 1
    read_from="$d/v.xml"
  fi
  write_applied_conf "$read_from" "конфигурации кластера" cluster
  [ "$wrote" -eq 0 ] || return 1

  # Проверка: повторное применение conf к прочитанному ничего не меняет.
  cp "$read_from" "$d/w.xml" && apply_conf_to_xml "$d/w.xml" quiet
  if ! cmp -s "$read_from" "$d/w.xml"; then
    echo "[server-settings] после записи конфигурации кластера значения не совпали с server-settings.conf" >&2
    return 1
  fi
  [ "$state" = write ] && echo "[server-settings] конфигурация кластера обновлена по server-settings.conf" >&2
  return 0
}

apply_cluster_settings() (
  local d rc
  [ -d "$SERVER_SETTINGS_DIR" ] || return 0
  if [ "${CLUSTER_LOCK_SETTINGS:-0}" = 1 ]; then
    exec 9>"$SERVER_SETTINGS_DIR/.apply.lock" || return 1
    flock -w "$((CLUSTER_TIMEOUT * 2 + 5))" 9 || return 1
  fi
  # Нет conf - БД не трогаем, но отмечаем режим: иначе админка до первого применения считает контур монолитом.
  if [ ! -f "$SERVER_SETTINGS_DIR/server-settings.conf" ]; then
    write_applied_conf /dev/null "конфигурации кластера" cluster
    return 0
  fi
  # mktemp -d создаёт каталог с правами 700 в /tmp контейнера, а не в смонтированных каталогах: в XML есть секреты.
  d="$(mktemp -d)" || return 1
  apply_cluster_in "$d"; rc=$?
  rm -rf "$d"
  return "$rc"
)

[ -n "$CLUSTER_CONN" ] || apply_server_settings || echo "[server-settings] не удалось применить настройки сервера - запуск с текущим settings.xml" >&2

# ---------------------------------------------------------------- ожидание PostgreSQL
probe() {
  local first
  { exec 3<>"/dev/tcp/$HOST/$PORT"; } 2>/dev/null || return 1
  printf '\x00\x00\x00\x2a\x00\x03\x00\x00user\x00pilotuser\x00database\x00postgres\x00\x00' >&3
  IFS= read -r -n1 -t 3 first <&3
  exec 3<&- 3>&-
  [ "$first" = "R" ]
}

waited=0
until probe; do
  if [ "$waited" -ge "$MAX" ]; then
    echo "[wait-for-postgres] $HOST:$PORT недоступен за ${MAX}с - запускаем сервер как есть" >&2
    break
  fi
  [ "$waited" -eq 0 ] && echo "[wait-for-postgres] ждём PostgreSQL $HOST:$PORT..." >&2
  sleep 2; waited=$((waited + 2))
done
echo "[wait-for-postgres] PostgreSQL готов (ожидание ${waited}с), запускаем pilot-server" >&2

# Кластер: конфигурация в БД, поэтому применяем после ожидания PostgreSQL; сбой не мешает старту узла.
if [ -n "$CLUSTER_CONN" ]; then
  apply_cluster_settings || echo "[server-settings] не удалось применить настройки сервера - запуск узла с текущей конфигурацией кластера" >&2
fi
exec "$DAEMON" "$@"
