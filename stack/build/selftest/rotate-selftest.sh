#!/usr/bin/env bash
# Самопроверка shared/rotate-contour-secrets.sh без стенда: docker подменён заглушкой, всё выполняется в одноразовом контейнере Linux.
#   docker run --rm -v "<путь к stack>:/stack:ro" postgres:16 bash /stack/build/selftest/rotate-selftest.sh
# Проверяет: новые пароли в .env (права 600, остальное не тронуто), settings.xml (пароль, AccessKeyId, секрет шифруется как у Pilot -
# сверка с независимо вычисленным ключом .NET), config.json миграции и proadmin/.env, ключ выкладки, SQL и пароль проверки только
# через stdin/окружение, отказ до любых изменений (прод без подтверждения, контейнер остановлен, нет S3-архива, шифрование не совпало),
# ошибка psql без значения пароля в выводе, сбой запуска базы. Хранилище - контейнер rustfs-<контур>, команды rc (не mc),
# ключи rc идут окружением RC_HOST_<алиас>; адрес S3 в settings.xml и config.json - rustfs-<контур>.
set -u
STACK=/stack
T=/tmp/rotate-selftest
FAILED=0
ok() { if eval "$1"; then echo "  ok: $2"; else echo "  НЕ ПРОЙДЕНО: $2"; FAILED=$((FAILED + 1)); fi; }

rm -rf "$T"; mkdir -p "$T/bin"
# --- заглушка docker: журнал вызовов, stdin команд exec -i, окружение docker run ---
cat > "$T/bin/docker" <<'STUB'
#!/bin/bash
echo "docker $*" >> "$STUB_LOG"
case "$1" in
  ps) n="${3#name=^}"; n="${n%\$}"; case " ${STUB_DOWN:-} " in *" $n "*) ;; *) echo "$n" ;; esac ;;
  inspect)
    case "$*" in
      *Config.Env*) printf 'PATH=/usr/bin\nRUSTFS_ACCESS_KEY=minioadmin\nRUSTFS_SECRET_KEY=%s\n' "$STUB_ROOT_PASS" ;;
      *Config.Image*) echo postgres:16.16 ;;
    esac ;;
  compose) echo "compose in $PWD" >> "$STUB_LOG" ;;
  logs) printf '%s\n' "${STUB_LOGS_TEXT:-}" ;;
  run) env | grep '^PGPASSWORD=' >> "$STUB_RUNENV" ;;
  exec)
    case "$*" in *"-e RC_HOST_"*) env | grep '^RC_HOST_' >> "$STUB_RUNENV" ;; esac   # ключ rc приходит окружением
    if [ "$2" = -i ]; then
      { echo "--- $*"; cat; } >> "$STUB_STDIN"
      if [ "${STUB_PSQL_ERR:-0}" = 1 ] && [ "$4" = psql ]; then
        pw="$(sed -n "s/.*ALTER ROLE postgres PASSWORD '\([^']*\)'.*/\1/p" "$STUB_STDIN" | tail -1)"
        echo "ERROR:  syntax error at or near \"x\" LINE 1: ALTER ROLE postgres PASSWORD '$pw'" >&2; exit 1
      fi
    fi
    case "$*" in *"rc ls"*) printf 'a.bin\nb.bin\n' ;; esac ;;
esac
exit 0
STUB
printf '#!/bin/sh\nexit 0\n' > "$T/bin/sleep"   # ожидание базы без реальных секунд
chmod +x "$T/bin/"*
export PATH="$T/bin:$PATH" STUB_LOG="$T/docker.log" STUB_STDIN="$T/stdin.log" STUB_RUNENV="$T/run.env"
export STUB_ROOT_PASS='Abc123xyz' STUB_LOGS_TEXT='UpdateDatabases: pilot-ice_ru is launched'

# Ключ и IV Pilot (AesEx), вычисленные в .NET (Rfc2898DeriveBytes, 48 байт): сверка шифрования без кода скрипта.
KEY=bd7f7f5dc063b39e3846b0564414d0bdaa424b39126e2d1ce9a82adb56ce69e7
IV=4f835f50bb722f620b163c66be8d64d2
dec() { printf '%s' "$1" | openssl enc -d -aes-256-cbc -K "$KEY" -iv "$IV" -a -A 2>/dev/null; }
# 'Abc123xyz' / 'AbBh/qedQ4ycEQWX/LjTxQ==' (значение, посчитанное .NET)
OLD_ENC='AbBh/qedQ4ycEQWX/LjTxQ=='

fresh() {  # раскладка по папкам: stack/{shared,test,prod,metrics}, proadmin, каталог данных
  rm -rf "$T/w"; : > "$STUB_LOG"; : > "$STUB_STDIN"; : > "$STUB_RUNENV"
  W="$T/w"; S="$W/stack"; D="$W/data"
  mkdir -p "$S/shared" "$S/test" "$S/prod" "$S/metrics" "$W/proadmin/migration/data"
  cp "$STACK/shared/rotate-contour-secrets.sh" "$S/shared/"
  for c in test prod; do
    printf '# контур %s\nDATA_ROOT=/x/data\nPG_SUPERUSER_PASSWORD=oldsuper\nPILOT_PG_PASSWORD=oldpilotpg\nMINIO_USER=minioadmin\nMINIO_PASSWORD=Abc123xyz\nMINIO_PILOT_USER=pilot\nMINIO_PILOT_SECRET=\nTZ=Europe/Moscow\n' "$c" > "$S/$c/.env"
    chmod 644 "$S/$c/.env"
    mkdir -p "$D/$c/pilot-server/settings"
    cat > "$D/$c/pilot-server/settings/settings.xml" <<XML
<?xml version="1.0" encoding="utf-8"?>
<Configuration>
  <ConnectionString>Host=postgres-$c:5432;Username=pilotuser;Password=oldpg;Database=pilot-ice_ru</ConnectionString>
  <FileArchiveParametersS3 Id="1"
      AccessKeyId="minioadmin"
      Url="http://rustfs-$c:9000" EncryptedSecretAccessKey="$OLD_ENC" Bucket="pilot-ice-ru" />
  <Other Value="keep" />
</Configuration>
XML
  done
  printf 'TEST_S3_ACCESS_KEY=old\nTEST_S3_SECRET_KEY=old\nMODULES_DEPLOY_KEY_TEST=olddeploykey\nOTHER=1\n' > "$W/proadmin/.env"
  printf 'PG_PASSWORD=oldmetrics\nTZ=x\n' > "$S/metrics/.env"
  cat > "$W/proadmin/migration/data/config.json" <<'JSON'
{
  "Environments": [
    {"Key": "test", "PilotAdminPassword": "adm", "S3": [{"ServerUrl": "http://rustfs-test:9000", "Url": "", "AccessKey": "old", "SecretKey": "old"}]},
    {"Key": "stage", "S3": [{"ServerUrl": "http://rustfs-stage:9000", "AccessKey": "stageold", "SecretKey": "stageold"}]}
  ],
  "UpdatedBy": "тест"
}
JSON
  cd "$S" || exit 1
}
rot() { bash "$S/shared/rotate-contour-secrets.sh" --data-root "$D" "$@" 2>&1; }
val() { sed -n "s/^$2=//p" "$1" | tail -1; }
snapshot() { cat "$S/test/.env" "$S/prod/.env" "$D/test/pilot-server/settings/settings.xml" "$W/proadmin/.env" "$W/proadmin/migration/data/config.json" | md5sum; }

echo "== смена паролей контура test"
fresh
out="$(rot --env test)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'Готово'" "код возврата 0"
[ "$rc" = 0 ] || echo "$out"
E="$S/test/.env"
pg="$(val "$E" PG_SUPERUSER_PASSWORD)"; pp="$(val "$E" PILOT_PG_PASSWORD)"; mp="$(val "$E" MINIO_PASSWORD)"; ms="$(val "$E" MINIO_PILOT_SECRET)"
ok "echo \"\$pg\$pp\$mp\$ms\" | grep -Eq '^[A-HJ-NP-Za-km-z2-9]{128}\$' && [ \${#pg} = 32 ] && [ \${#ms} = 32 ]" "четыре пароля по 32 символа из безопасного алфавита"
ok "[ \"\$pg\" != \"\$pp\" ] && [ \"\$pp\" != \"\$mp\" ] && [ \"\$mp\" != \"\$ms\" ]" "пароли разные"
ok "[ \"\$(stat -c %a \"\$E\")\" = 600 ] && [ \"\$(stat -c %a \"\$W/proadmin/.env\")\" = 600 ]" "права 600 у .env контура и proadmin/.env"
ok "grep -q '^# контур test\$' \"\$E\" && grep -q '^DATA_ROOT=/x/data\$' \"\$E\" && grep -q '^TZ=Europe/Moscow\$' \"\$E\" && [ \"\$(val \"\$E\" MINIO_USER)\" = minioadmin ] && [ \"\$(val \"\$E\" MINIO_PILOT_USER)\" = pilot ]" "прочие строки .env не тронуты"
ok "ls \"\$S\"/test/.env.before-rotate-* >/dev/null && grep -q 'oldsuper' \"\$S\"/test/.env.before-rotate-*" "копия прежнего .env"
X="$D/test/pilot-server/settings/settings.xml"
ok "grep -q \"Host=postgres-test:5432;Username=pilotuser;Password=\$pp;Database=pilot-ice_ru\" \"\$X\"" "settings.xml: новый пароль в строке подключения"
enc="$(sed -n 's/.*EncryptedSecretAccessKey="\([^"]*\)".*/\1/p' "$X")"
ok "grep -q 'AccessKeyId=\"pilot\"' \"\$X\" && [ \"\$(dec \"\$enc\")\" = \"\$ms\" ]" "settings.xml: AccessKeyId=pilot, секрет шифруется как у Pilot (расшифровывается независимым ключом .NET)"
ok "grep -q '<Other Value=\"keep\" />' \"\$X\" && grep -q 'Bucket=\"pilot-ice-ru\"' \"\$X\" && ls \"\$X\".before-rotate-* >/dev/null && grep -q 'oldpg' \"\$X\".before-rotate-*" "settings.xml: остальное не тронуто, копия рядом"
ok "[ \"\$(val \"\$W/proadmin/.env\" TEST_S3_ACCESS_KEY)\" = pilot ] && [ \"\$(val \"\$W/proadmin/.env\" TEST_S3_SECRET_KEY)\" = \"\$ms\" ] && [ \"\$(val \"\$W/proadmin/.env\" OTHER)\" = 1 ]" "proadmin/.env: S3-ключи контура, прочее не тронуто"
C="$W/proadmin/migration/data/config.json"
cat > "$T/chk.pl" <<'PL'
use JSON::PP;
my $c = decode_json(<>);
my ($t, $s) = ($c->{Environments}[0]{S3}[0], $c->{Environments}[1]{S3}[0]);
exit(($t->{AccessKey} eq 'pilot' && $t->{SecretKey} eq $ENV{SECRET} && $t->{ServerUrl} eq 'http://rustfs-test:9000'
  && $s->{AccessKey} eq 'stageold' && $s->{SecretKey} eq 'stageold'
  && $c->{Environments}[0]{PilotAdminPassword} eq 'adm' && $c->{UpdatedBy} eq "\x{442}\x{435}\x{441}\x{442}") ? 0 : 1);
PL
ok "SECRET=\"\$ms\" perl -MJSON::PP -0777 \"\$T/chk.pl\" \"\$C\"" "config.json: ключи S3 test обновлены, stage и прочие поля не тронуты"
ok "ls \"\$C\".before-rotate-* >/dev/null && grep -q 'docker restart pilot-migration' \"\$STUB_LOG\"" "config.json: копия рядом, миграция перезапущена"
dk="$(val "$W/proadmin/.env" MODULES_DEPLOY_KEY_TEST)"
ok "[ \${#dk} = 40 ] && [ \"\$dk\" != olddeploykey ] && grep -q \"compose in \$W/proadmin\" \"\$STUB_LOG\" && echo \"\$out\" | grep -q 'DEPLOY_KEY_TEST'" "ключ выкладки заменён (40 символов), админка пересоздана, напоминание про GitLab"
ok "grep -q \"compose in \$S/test\" \"\$STUB_LOG\" && ! grep -q \"compose in \$S/metrics\" \"\$STUB_LOG\" && [ \"\$(val \"\$S/metrics/.env\" PG_PASSWORD)\" = oldmetrics ] && ! grep -q 'METRICS_' \"\$E\" && ! grep -q '02-metrics-roles' \"\$STUB_LOG\"" "compose контура вызван, метрики и их роли для test не трогаются"
ok "grep -q \"ALTER ROLE postgres PASSWORD '\$pg'; ALTER ROLE pilotuser PASSWORD '\$pp';\" \"\$STUB_STDIN\" && ! grep -q \"\$pg\|\$pp\" \"\$STUB_LOG\"" "SQL с паролями идёт через stdin, в аргументах docker их нет"
ok "! grep -q \"\$dk\" \"\$STUB_LOG\" && ! echo \"\$out\" | grep -q \"\$pg\|\$pp\|\$mp\|\$ms\|\$dk\"" "пароли и ключ не печатаются и не попадают в аргументы docker (кроме rc admin user add)"
ok "grep -q '^PGPASSWORD=' \"\$STUB_RUNENV\" && grep -q \"PGPASSWORD=\$pp\" \"\$STUB_RUNENV\" && grep -q 'docker run .* -e PGPASSWORD ' \"\$STUB_LOG\"" "проверка входа: пароль передаётся окружением (-e PGPASSWORD без значения)"
ok "grep -q 'docker exec .*rustfs-test rc admin user add rot/ pilot ' \"\$STUB_LOG\" && grep -q 'rc admin policy create rot/ pilot-bucket /tmp/pilot-policy.json' \"\$STUB_LOG\" && grep -q 'policy attach rot/ pilot-bucket --user pilot' \"\$STUB_LOG\"" "RustFS: учётка pilot, политика pilot-bucket, привязка (rc admin)"
ok "! grep -qE '(^| )mc( |\$)' \"\$STUB_LOG\" && ! grep -q 'minio-test' \"\$STUB_LOG\" && ! grep -q 'alias set' \"\$STUB_LOG\" && grep -q 'docker exec .*rustfs-test rc ls --recursive chk/pilot-ice-ru' \"\$STUB_LOG\"" "вызывается rc из rustfs-test, а не mc и не minio-test; проверка учётки pilot через rc ls"
ok "grep -q '^RC_HOST_rot=http://minioadmin:Abc123xyz@127.0.0.1:9000\$' \"\$STUB_RUNENV\" && grep -q \"^RC_HOST_chk=http://pilot:\$ms@127.0.0.1:9000\\\$\" \"\$STUB_RUNENV\" && grep -q 'docker exec -e RC_HOST_rot rustfs-test rc ' \"\$STUB_LOG\" && ! grep -q 'Abc123xyz' \"\$STUB_LOG\"" "ключ root и пароль проверки идут окружением RC_HOST_*, ключа root в аргументах docker нет"
first="$pg"
out="$(rot --env test)"; rc=$?
ok "[ $rc = 0 ] && [ \"\$(val \"\$E\" PG_SUPERUSER_PASSWORD)\" != \"\$first\" ]" "повторный запуск выдаёт новые пароли (учётка pilot уже в settings.xml)"
[ "$rc" = 0 ] || echo "$out"

echo "== прод и метрики"
fresh
out="$(rot --env prod)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'confirm-prod'" "прод без --confirm-prod: отказ"
ok "[ ! -s \"\$STUB_LOG\" ] && grep -q oldsuper \"\$S/prod/.env\"" "отказ до любых вызовов docker и правок"
out="$(rot --env prod --confirm-prod prod)"; rc=$?
[ "$rc" = 0 ] || echo "$out"
mw="$(val "$S/prod/.env" METRICS_WRITER_PASSWORD)"; mr="$(val "$S/prod/.env" METRICS_READER_PASSWORD)"
ok "[ $rc = 0 ] && [ \${#mw} = 32 ] && [ \${#mr} = 32 ] && [ \"\$mw\" != \"\$mr\" ] && [ \"\$mw\" != \"\$(val \"\$S/prod/.env\" PILOT_PG_PASSWORD)\" ]" "prod: в .env новые пароли ролей метрик (32 символа, не пароль pilotuser)"
ok "[ \"\$(val \"\$S/metrics/.env\" PG_PASSWORD)\" = \"\$mw\" ] && [ \"\$(val \"\$S/metrics/.env\" METRICS_READER_PASSWORD)\" = \"\$mr\" ] && [ \"\$(val \"\$S/metrics/.env\" PG_USER)\" = metrics_writer ] && [ \"\$(val \"\$S/metrics/.env\" TZ)\" = x ] && grep -q \"compose in \$S/metrics\" \"\$STUB_LOG\"" "prod: метрики получают metrics_writer и пароли ролей (не pilotuser), сборщики и Grafana пересозданы"
ok "grep -q 'docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh' \"\$STUB_LOG\" && ! grep -q \"\$mw\|\$mr\" \"\$STUB_LOG\"" "prod: роли метрик заданы скриптом внутри контейнера, пароли не в аргументах docker"
lc="$(grep -n "compose in $S/prod\$" "$STUB_LOG" | head -1 | cut -d: -f1)"; lr="$(grep -n '02-metrics-roles' "$STUB_LOG" | head -1 | cut -d: -f1)"
ok "[ -n \"\$lc\" ] && [ -n \"\$lr\" ] && [ \"\$lc\" -lt \"\$lr\" ]" "prod: роли метрик задаются после пересоздания контура"
ok "[ \"\$(val \"\$W/proadmin/.env\" PROD_S3_ACCESS_KEY)\" = pilot ] && grep -q '^MODULES_DEPLOY_KEY_TEST=olddeploykey' \"\$W/proadmin/.env\"" "ключи S3 с префиксом PROD_, ключ выкладки другого контура не тронут"
ok "! grep -q MODULES_DEPLOY_KEY_PROD \"\$W/proadmin/.env\" && ! echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD'" "нет ключа выкладки контура: он не создаётся"

echo "== отказ до изменений"
fresh; before="$(snapshot)"
out="$(STUB_DOWN='rustfs-test' rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'rustfs-test не запущен' && [ \"\$(snapshot)\" = \"\$before\" ] && ! grep -q 'rc admin' \"\$STUB_LOG\"" "контейнер остановлен: отказ без изменений"
fresh; before="$(snapshot)"
sed -i 's#Url="http://rustfs-test:9000"#Url="http://other:9000"#' "$D/test/pilot-server/settings/settings.xml"; before="$(snapshot)"
out="$(rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'нет S3-архива' && [ \"\$(snapshot)\" = \"\$before\" ] && ! grep -q 'rc admin' \"\$STUB_LOG\"" "нет S3-архива этого хранилища: отказ без изменений"
fresh
sed -i 's#Host=postgres-test:5432#Host=elsewhere:5432#' "$D/test/pilot-server/settings/settings.xml"; before="$(snapshot)"
out="$(rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'нет строки подключения' && [ \"\$(snapshot)\" = \"\$before\" ] && ! grep -q 'rc admin' \"\$STUB_LOG\"" "нет строки подключения: отказ до смены паролей"
fresh
printf '{ broken' > "$W/proadmin/migration/data/config.json"; before="$(snapshot)"
out="$(rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не удалось разобрать' && [ \"\$(snapshot)\" = \"\$before\" ] && ! grep -q 'rc admin' \"\$STUB_LOG\"" "config.json миграции не разбирается: отказ до смены паролей"
fresh; sed -i "s#^DATA_ROOT=.*#DATA_ROOT=$D#" "$S/test/.env"
out="$(bash "$S/shared/rotate-contour-secrets.sh" --env test 2>&1)"; rc=$?
ok "[ $rc = 0 ]" "без --data-root каталог данных берётся из DATA_ROOT в .env контура"
fresh; before="$(snapshot)"
out="$(STUB_ROOT_PASS='SomethingElse1' rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'Шифрование Pilot не совпало' && [ \"\$(snapshot)\" = \"\$before\" ] && ! grep -q 'rc admin' \"\$STUB_LOG\"" "секрет в settings.xml не равен паролю root: отказ без изменений"
fresh; rm "$S/test/.env"; rm -f "$S/test/.env.example"
out="$(rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q '.env.example'" "нет .env и шаблона: понятная ошибка"
fresh; printf 'PG_SUPERUSER_PASSWORD=\n' > "$S/test/.env.example"; rm "$S/test/.env"
out="$(rot --env test)"; rc=$?
ok "[ $rc = 0 ] && [ \"\$(stat -c %a \"\$S/test/.env\")\" = 600 ] && [ \$(val \"\$S/test/.env\" PILOT_PG_PASSWORD | wc -c) -ge 32 ] && [ \"\$(val \"\$S/test/.env\" MINIO_PILOT_USER)\" = pilot ]" "нет .env: создаётся из .env.example с правами 600"
out="$(bash "$S/shared/rotate-contour-secrets.sh" --env bad 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'prod|test|stage'" "неизвестный контур отклоняется"

echo "== сбои после смены паролей"
fresh
out="$(STUB_PSQL_ERR=1 rot --env test)"; rc=$?
pw="$(sed -n "s/.*ALTER ROLE postgres PASSWORD '\([^']*\)'.*/\1/p" "$STUB_STDIN" | tail -1)"
ok "[ $rc != 0 ] && [ -n \"\$pw\" ] && ! echo \"\$out\" | grep -q \"\$pw\" && echo \"\$out\" | grep -q 'PASSWORD .\\*\\*\\*.'" "ошибка psql: значение пароля в выводе вычёркивается"
fresh
out="$(STUB_LOGS_TEXT='UpdateDatabases: pilot-ice_ru to state Broken' rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не запустилась (состояние: Broken)' && echo \"\$out\" | grep -q 'DEPLOY_KEY_TEST'" "база Broken: ошибка, напоминание о ключе выкладки уже напечатано"
fresh
out="$(STUB_LOGS_TEXT='' rot --env test)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'нет данных'" "нет строки о запуске базы за все попытки: ошибка"

echo
if [ "$FAILED" != 0 ]; then echo "НЕ ПРОЙДЕНО: $FAILED"; exit 1; fi
echo "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ"
