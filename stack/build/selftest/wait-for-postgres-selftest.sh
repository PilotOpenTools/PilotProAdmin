#!/usr/bin/env bash
# Самопроверка shared/wait-for-postgres.sh без стенда: Ascon.Pilot.Daemon подменён заглушкой, всё в одноразовом контейнере Linux.
#   docker run --rm -v "<путь к stack>:/stack:ro" postgres:16 bash /stack/build/selftest/wait-for-postgres-selftest.sh
# Проверяет: монолит (settings.xml, applied.conf) как раньше; узел кластера: -rc, применение, -wc, проверочное чтение,
# хост и порт ожидания PostgreSQL из --pgConnectionString; нет conf, сбой -rc и -wc, тайм-аут, изменение конфигурации
# между чтениями, секреты не остаются на диске и узел стартует при любом сбое.
set -u
STACK=/stack
T=/tmp/selftest
FAILED=0
ok() { if eval "$1"; then echo "  ok: $2"; else echo "  НЕ ПРОЙДЕНО: $2"; FAILED=$((FAILED + 1)); fi; }

rm -rf "$T"; mkdir -p "$T/bin"
SS="$T/ss"; DB="$T/db.xml"; LOG="$T/daemon.log"; OUT="$T/out"; ERR="$T/err"
CONN='Host=postgres-test:5432;Username=pilotuser;Password=p4ss;Include Error Detail=true;Pooling=true;DataBase=configurationdb'

# --- заглушка Daemon: «БД конфигурации» - файл $DB; поведение задают переменные STUB_* ---
cat > "$T/bin/Daemon" <<'STUB'
#!/bin/bash
mode="${1:-}"
case "$mode" in
  -rc|-wc)
    [ "$2" = "$STUB_CONN" ] && conn=ok || conn=bad
    echo "${mode#-} conn=$conn dirmode=$(stat -c %a "$(dirname "$3")")" >> "$STUB_LOG"
    if [ "$mode" = -rc ]; then
      n=$(( $(cat "$STUB_RCN" 2>/dev/null || echo 0) + 1 )); echo "$n" > "$STUB_RCN"
      [ -n "${STUB_RC_HANG:-}" ] && exec sleep 5
      [ -n "${STUB_RC_FAIL_FROM:-}" ] && [ "$n" -ge "$STUB_RC_FAIL_FROM" ] && exit 139
      [ -n "${STUB_RC_EMPTY:-}" ] && exit 0
      cp "$STUB_DB" "$3"
      for m in ${STUB_MUTATE_AFTER:-}; do  # кто-то изменил конфигурацию сразу после этого чтения
        [ "$m" = "$n" ] && sed -i "s#</Configuration>#  <Foreign n=\"$n\" />\n</Configuration>#" "$STUB_DB"
      done
    else
      [ -n "${STUB_WC_FAIL:-}" ] && exit 1
      [ -n "${STUB_WC_IGNORE:-}" ] || cp "$3" "$STUB_DB"
    fi
    exit 0 ;;
  *)
    echo "start $*" >> "$STUB_LOG"
    echo "tmpleft=$(ls -d /tmp/tmp.* 2>/dev/null | wc -l) secretsleft=$(grep -rl SECRETMARK /tmp --exclude-dir=selftest 2>/dev/null | wc -l)" >> "$STUB_LOG"
    exit 0 ;;
esac
STUB
chmod +x "$T/bin/Daemon"

fresh() {  # исходная «БД», каталог настроек с conf (первый аргумент - его текст; пусто - без conf), чистый журнал
  cat > "$DB" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <DatabaseParameters Name="pilot-ice_ru" />
  <PgConnectionString ConnectionString="Host=postgres-test:5432;Username=pilotuser" EncryptedPassword="SECRETMARK" />
  <RedisConnectionString>redis-test:6379</RedisConnectionString>
  <IsAutomationLoggingEnabled>false</IsAutomationLoggingEnabled>
  <IsSnapshotEnabled>false</IsSnapshotEnabled>
</Configuration>
XML
  rm -rf "$SS"; mkdir -p "$SS"
  [ -n "${1:-}" ] && printf '%b' "$1" > "$SS/server-settings.conf"
  : > "$LOG"; rm -f "$T/rcn" "$OUT" "$ERR"
  unset STUB_RC_HANG STUB_RC_FAIL_FROM STUB_RC_EMPTY STUB_MUTATE_AFTER STUB_WC_FAIL STUB_WC_IGNORE
}
run() {  # run <аргументы обёртки>
  env PILOT_DAEMON="$T/bin/Daemon" SERVER_SETTINGS_DIR="$SS" PG_WAIT_TIMEOUT="${PG_WAIT_TIMEOUT:-0}" \
      STUB_DB="$DB" STUB_LOG="$LOG" STUB_RCN="$T/rcn" STUB_CONN="$CONN" \
      STUB_RC_HANG="${STUB_RC_HANG:-}" STUB_RC_FAIL_FROM="${STUB_RC_FAIL_FROM:-}" STUB_RC_EMPTY="${STUB_RC_EMPTY:-}" \
      STUB_MUTATE_AFTER="${STUB_MUTATE_AFTER:-}" STUB_WC_FAIL="${STUB_WC_FAIL:-}" STUB_WC_IGNORE="${STUB_WC_IGNORE:-}" \
      bash "$STACK/shared/wait-for-postgres.sh" "$@" > "$OUT" 2> "$ERR"
}
node() { run "--pgConnectionString=$CONN" --indexPath=/idx --httpPort=5545; }
calls() { sed -n 's/^\(rc\|wc\|start\)\b.*/\1/p' "$LOG" | tr '\n' ' '; }
applied() { sed -n "s/^$1=//p" "$SS/applied.conf"; }
db_has() { grep -q "$1" "$DB"; }

echo "== монолит =="
cat > "$T/settings.xml" <<'XML'
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <IsAutomationLoggingEnabled>false</IsAutomationLoggingEnabled>
</Configuration>
XML
fresh 'IsAutomationLoggingEnabled=true\nCheckChangeCreator=false\nUnknownKey=true\nIsSnapshotEnabled=maybe\n'
run "$T/settings.xml" --foo
ok 'grep -q "<IsAutomationLoggingEnabled>true<" "$T/settings.xml"' "значение заменено в settings.xml"
ok 'grep -q "<CheckChangeCreator>false</CheckChangeCreator>" "$T/settings.xml"' "отсутствующий параметр вставлен перед </Configuration>"
ok '[ "$(tail -n1 "$T/settings.xml")" = "</Configuration>" ]' "закрывающий тег остался последним"
ok '[ -f "$T/settings.xml.before-server-settings" ]' "копия до правки сохранена"
ok 'grep -q "неизвестный параметр «UnknownKey»" "$ERR" && grep -q "IsSnapshotEnabled=maybe: допустимо только true/false" "$ERR"' "лишнее и не true/false пропущено с предупреждением"
ok '[ "$(applied IsAutomationLoggingEnabled)" = true ] && [ "$(applied CheckChangeCreator)" = false ]' "applied.conf с фактическими значениями"
ok '! grep -q "^Mode=" "$SS/applied.conf" && grep -q "^AppliedAt=" "$SS/applied.conf"' "в applied.conf монолита нет Mode"
ok '[ "$(calls)" = "start " ] && grep -q "^start $T/settings.xml --foo$" "$LOG"' "Daemon запущен с исходными аргументами, без -rc и -wc"

fresh ''
run "$T/settings.xml"
ok '[ "$(applied IsAutomationLoggingEnabled)" = true ] && grep -q "^AppliedAt=" "$SS/applied.conf"' "без conf settings.xml не трогается, applied.conf пишется"

echo "== кластер: основной сценарий =="
fresh 'IsAutomationLoggingEnabled=true\nCheckChangeCreator=false\n'
CLUSTER_LOCK_SETTINGS=1 node
ok '[ -f "$SS/.apply.lock" ]' "при общем каталоге настроек узлы используют файловую блокировку"
ok '[ "$(calls)" = "rc rc wc rc start " ]' "порядок: -rc, -rc, -wc, проверочный -rc, запуск узла (получено: $(calls))"
ok '! grep -q "conn=bad" "$LOG" && grep -q "^start --pgConnectionString=$CONN --indexPath=/idx --httpPort=5545$" "$LOG"' "строка подключения передана Daemon, узел запущен с теми же аргументами"
ok 'db_has "<IsAutomationLoggingEnabled>true<" && db_has "<CheckChangeCreator>false</CheckChangeCreator>"' "значения записаны в конфигурацию"
ok 'db_has "EncryptedPassword=\"SECRETMARK\"" && db_has "<RedisConnectionString>redis-test:6379<" && db_has "<IsSnapshotEnabled>false<"' "остальная конфигурация не тронута"
ok '[ "$(applied IsAutomationLoggingEnabled)" = true ] && [ "$(applied IsSnapshotEnabled)" = false ] && [ "$(applied Mode)" = cluster ]' "applied.conf из проверочного чтения, Mode=cluster"
ok '[ "$(grep -c "^\(rc\|wc\)" "$LOG")" = "$(grep -c "dirmode=700" "$LOG")" ]' "временный каталог с правами 700 на каждом вызове"
ok 'grep -q "tmpleft=0 secretsleft=0" "$LOG" && [ -z "$(grep -rl SECRETMARK "$SS")" ]' "до запуска узла временные файлы с секретами удалены, в server-settings их нет"
ok '! grep -q "p4ss" "$OUT" "$ERR"' "пароль не попал в вывод"

echo "== кластер: нет conf =="
fresh ''
node
ok '[ "$(calls)" = "start " ] && db_has "<IsAutomationLoggingEnabled>false<"' "БД не трогается, узел стартует"
ok '[ "$(applied Mode)" = cluster ] && grep -q "^AppliedAt=" "$SS/applied.conf" && [ "$(grep -c "=" "$SS/applied.conf")" = 2 ]' "applied.conf только с AppliedAt и Mode=cluster"

echo "== кластер: уже применено =="
fresh 'IsAutomationLoggingEnabled=false\n'
node
ok '[ "$(calls)" = "rc start " ]' "один -rc, без -wc (получено: $(calls))"
ok '[ "$(applied IsAutomationLoggingEnabled)" = false ] && [ "$(applied Mode)" = cluster ]' "applied.conf с Mode=cluster"

echo "== кластер: сбои не мешают старту =="
fresh 'IsAutomationLoggingEnabled=true\n'; STUB_WC_FAIL=1
node
ok '[ "$(calls)" = "rc rc wc rc start " ] && grep -q "Daemon -wc: код 1" "$ERR" && grep -q "запуск узла с текущей конфигурацией" "$ERR"' "-wc падает: предупреждение, узел стартует"
ok 'db_has "<IsAutomationLoggingEnabled>false<" && [ "$(applied IsAutomationLoggingEnabled)" = false ]' "applied.conf показывает то, что в БД, а не желаемое"

fresh 'IsAutomationLoggingEnabled=true\n'; STUB_WC_IGNORE=1
node
ok 'grep -q "значения не совпали" "$ERR" && [ "$(applied IsAutomationLoggingEnabled)" = false ] && [ "$(calls)" = "rc rc wc rc start " ]' "-wc ничего не записал: найдено проверкой, applied.conf не врёт"

fresh 'IsAutomationLoggingEnabled=true\n'; STUB_RC_FAIL_FROM=1
node
ok '[ "$(calls)" = "rc start " ] && grep -q "Daemon -rc: код 139" "$ERR" && [ ! -e "$SS/applied.conf" ]' "-rc падает: -wc не вызывается, applied.conf нет, узел стартует"

fresh 'IsAutomationLoggingEnabled=true\n'; STUB_RC_EMPTY=1
node
ok '[ "$(calls)" = "rc start " ] && grep -q "Daemon -rc: код 1" "$ERR"' "-rc вернул 0 без файла: сбой, -wc не вызывается"

fresh 'IsAutomationLoggingEnabled=true\n'; STUB_RC_FAIL_FROM=3; echo "IsAutomationLoggingEnabled=old" > "$SS/applied.conf"
node
ok '[ "$(calls)" = "rc rc wc rc start " ] && [ "$(cat "$SS/applied.conf")" = "IsAutomationLoggingEnabled=old" ]' "проверочное чтение упало: applied.conf не перезаписан"

fresh 'IsAutomationLoggingEnabled=true\n'; STUB_RC_HANG=1
CLUSTER_CONFIG_TIMEOUT=0.3 node
ok '[ "$(calls)" = "rc start " ]' "тайм-аут: узел стартует (получено: $(calls))"
ok 'grep -q "Daemon -rc: код 124 (тайм-аут)" "$ERR" && ! grep -q "^wc" "$LOG"' "тайм-аут -rc распознан, -wc не вызывается"
unset STUB_RC_HANG

echo "== кластер: конфигурацию изменили между чтениями =="
fresh 'IsAutomationLoggingEnabled=true\n'; STUB_MUTATE_AFTER="1"
node
ok '[ "$(calls)" = "rc rc rc rc wc rc start " ] && grep -q "попытка 1 из 3" "$ERR"' "повтор после изменения (получено: $(calls))"
ok 'db_has "<Foreign n=\"1\" />" && db_has "<IsAutomationLoggingEnabled>true<"' "чужая правка сохранена, наша применена"

fresh 'IsAutomationLoggingEnabled=true\n'; STUB_MUTATE_AFTER="1 3 5"
node
ok '! grep -q "^wc" "$LOG" && grep -q "менялась при каждой попытке" "$ERR" && [ "$(calls)" = "rc rc rc rc rc rc start " ]' "после 3 попыток -wc не вызывается, узел стартует (получено: $(calls))"
ok 'db_has "<IsAutomationLoggingEnabled>false<" && [ ! -e "$SS/applied.conf" ]' "конфигурация не тронута, applied.conf нет"

echo "== ожидание PostgreSQL =="
waits() { sed -n 's/.*\] \(.*\) недоступен.*/\1/p' "$ERR"; }
fresh ''
run
ok '[ "$(waits)" = "postgres:5432" ]' "монолит без аргумента кластера ждёт postgres:5432"
fresh ''
node
ok '[ "$(waits)" = "postgres-test:5432" ]' "узел кластера ждёт хост и порт из строки подключения"
CONN_SAVE="$CONN"; CONN='Server=pg.local;Port=6543;Username=pilotuser;Password=p4ss'
fresh ''
node
ok '[ "$(waits)" = "pg.local:6543" ]' "Server=... и Port=... разобраны"
fresh ''
PG_WAIT_HOST=other node
ok '[ "$(waits)" = "other:5432" ]' "PG_WAIT_HOST перекрывает хост из строки"
CONN="$CONN_SAVE"

# Настоящее ожидание: слушатель на 127.0.0.1 отвечает 'R', как сервер PostgreSQL, готовый принять подключение.
perl -MIO::Socket::INET -e '
  my $s = IO::Socket::INET->new(Listen => 5, LocalAddr => "127.0.0.1", LocalPort => 15432, ReuseAddr => 1) or die;
  while (my $c = $s->accept) { sysread($c, my $b, 100); syswrite($c, "R"); close $c }' &
LISTENER=$!
sleep 0.5
CONN='Host=127.0.0.1:15432;Username=pilotuser;Password=p4ss'
fresh ''
PG_WAIT_TIMEOUT=10 node
kill "$LISTENER" 2>/dev/null; wait "$LISTENER" 2>/dev/null
ok 'grep -q "PostgreSQL готов (ожидание 0с)" "$ERR" && [ "$(calls)" = "start " ]' "готовый PostgreSQL по строке подключения замечен сразу"

rm -rf "$T"
if [ "$FAILED" -ne 0 ]; then echo "Не пройдено проверок: $FAILED"; exit 1; fi
echo "Все проверки пройдены"
