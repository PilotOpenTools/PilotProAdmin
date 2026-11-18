#!/usr/bin/env bash
# Самопроверка install.sh без стенда: docker подменён заглушкой, всё выполняется в одноразовом контейнере Linux.
#   docker run --rm -v "<путь к stack>:/stack:ro" postgres:16 bash /stack/build/selftest/install-selftest.sh
# Проверяет: права 600 у .env, замену паролей-заглушек, генерацию и перенос ключей выкладки, отказ при нескольких машинах,
# отказ без .env при уже установленном стеке, права на каталоги секретов, порядок и условия восстановления GitLab.
# Шаг GitLab после установки (gitlab-post-restore.rb): вызов и порядок, ключи только через окружение, сбой и ненулевой код, напоминание о ключах.
# Обязательные пароли в .env (require_env), сверка загруженных образов с IMAGES.txt, init-скрипт PostgreSQL без пароля.
# RustFS: бакет создаётся клиентом rc из образа RustFS (пароль не в аргументах docker), данные в именованном томе (каталог и chown не нужны).
# Файлы RustFS из бэкапа: архив rustfs/rustfs_<контур>.tgz проверяется и распаковывается в том до compose up и до бакета; том с данными - отказ.
# Сам Ruby-скрипт здесь не выполняется: его чистые функции проверяет SELFTEST=1 (команда в заголовке gitlab-post-restore.rb).
set -u
STACK=/stack
T=/tmp/selftest
FAILED=0
ok() { if eval "$1"; then echo "  ok: $2"; else echo "  НЕ ПРОЙДЕНО: $2"; FAILED=$((FAILED + 1)); fi; }

mkdir -p "$T/bin" /var/log/gitlab
# --- заглушка docker: пишет вызовы в журнал, команды внутри gitlab выполняет локально ---
cat > "$T/bin/docker" <<'STUB'
#!/bin/bash
echo "docker $*" >> "$STUB_LOG"
case "$1" in
  compose) [ "${2:-}" = config ] && echo "grafana/grafana:11"; exit 0 ;;
  # load печатает содержимое файла образа как имя (ID:... - образ без имени); image inspect --format берёт ответы из $STUB_IMG
  load) n="$(cat "$4")"; case "$n" in ID:*) echo "Loaded image ID: ${n#ID:}" ;; *) echo "Loaded image: $n" ;; esac; exit 0 ;;
  image)
    if [ "${2:-}" = inspect ] && [ "${4:-}" = --format ]; then
      k="$(printf '%s' "$3" | tr '/:@' '___')"
      # как настоящий docker, вывод заканчивается переводом строки
      case "$5" in '{{.Id}}') cat "$STUB_IMG/$k.id" 2>/dev/null || exit 1 ;; *) cat "$STUB_IMG/$k.layers" 2>/dev/null || exit 1 ;; esac; echo
    fi
    exit 0 ;;
  run) case "$*" in
         *"/v/grafana.db"*) [ "${GRAFANA_BUSY:-0}" = 1 ] && echo data ;;
         # содержимое тома RustFS (ls -A /v): имена из $RUSTFS_LS, по одному в строке
         *"ls -A /v"*) [ -n "${RUSTFS_LS:-}" ] && printf '%b
' "$RUSTFS_LS" ;;
       esac; exit 0 ;;
  # запущен ли контейнер rustfs-<контур> (docker ps -q): $RUSTFS_RUNNING=1
  ps) [ "${RUSTFS_RUNNING:-0}" = 1 ] && echo abc123; exit 0 ;;
  exec)
    shift
    while [ "${1:-}" = -e ] || [ "${1:-}" = -i ]; do if [ "$1" = -e ]; then export "$2"; shift 2; else shift; fi; done
    c="$1"; shift
    case "$*" in *02-metrics-roles.sh*) exit "${STUB_ROLES_RC:-0}" ;; esac
    if [ "$c" = gitlab ]; then
      case "$1" in
        sh) shift; shift; cmd="$1"; case "$cmd" in *readiness*) exit "${STUB_READY_RC:-0}" ;; *) exec sh -c "$cmd" ;; esac ;;
        gitlab-psql) echo "${STUB_PROJECTS:-5}"; exit 0 ;;
        grep) shift; exec grep "$@" ;;
        gitlab-rails) env > "$STUB_RAILS_ENV"; exit "${STUB_HARDEN_RC:-0}" ;;
        gitlab-ctl) case "$2" in stop) exit "${STUB_STOP_RC:-0}" ;; *) exit 0 ;; esac ;;
      esac
    fi
    exit 0 ;;
  *) exit 0 ;;
esac
STUB
cat > "$T/bin/gitlab-backup" <<'STUB'
#!/bin/sh
echo "Restoring database ..."
echo "ERROR:  relation \"x\" already exists"
echo "STATEMENT:  CREATE TABLE x"
[ "${STUB_SCHEMA_ERR:-0}" = 1 ] && echo "There were errors in restoring the schema."
echo "Restoring repositories ... done"
exit "${STUB_RESTORE_RC:-0}"
STUB
cat > "$T/bin/gitlab-ctl" <<'STUB'
#!/bin/sh
# status остановленного сервиса: строка down: и ненулевой код, как у runit
if [ "$1" = status ] && [ "${STUB_STOP_DOWN:-1}" = 1 ]; then echo "down: $2: 1s"; exit 3; fi
exit 0
STUB
chmod +x "$T/bin/"*
export PATH="$T/bin:$PATH" STUB_LOG="$T/docker.log" STUB_RAILS_ENV="$T/rails.env" STUB_IMG="$T/img"

fresh() {  # новая «папка комплекта» и пустой DATA_ROOT
  rm -rf "$T/work" /opt/ascon-docker; : > "$STUB_LOG"; rm -f /var/log/gitlab/restore.log "$STUB_RAILS_ENV"
  mkdir -p "$T/work"; cp "$STACK/Install/install.sh" "$STACK/Install/gitlab-post-restore.rb" "$STACK/Install/.env.example" "$STACK/Install/docker-compose.tls.yml" "$T/work/"
  cd "$T/work" || exit 1
}
val() { sed -n "s/^$1=//p" .env | tail -1; }

echo "== чистая установка, одна машина"
fresh
out="$(bash install.sh --no-data --skip-load --roles prod,admin,metrics 2>&1)"; rc=$?
ok "[ $rc = 0 ]" "код возврата 0"
ok "[ \"\$(stat -c %a .env)\" = 600 ]" ".env имеет права 600 после всех записей env_set"
ok "[ \"\$(val PG_SUPERUSER_PASSWORD)\" != postgres ] && [ \"\$(val PG_SUPERUSER_PASSWORD | wc -c)\" -ge 40 ]" "пароль PostgreSQL заглушки заменён случайным"
ok "[ \"\$(val PILOT_ADMIN_PASSWORD)\" != change-me ] && [ \"\$(val GRAFANA_ADMIN_PASSWORD)\" != changeme ] && [ \"\$(val MINIO_PASSWORD)\" != minioadmin123 ]" "остальные заглушки заменены"
ok "[ \"\$(val GITLAB_ROOT_PASSWORD)\" = change-me-please ]" "root GitLab без роли gitlab не трогается"
k1="$(val MODULES_DEPLOY_KEY_TEST)"; k2="$(val MODULES_DEPLOY_KEY_STAGE)"; k3="$(val MODULES_DEPLOY_KEY_PROD)"
ok "[ \${#k1} = 40 ] && [ \${#k2} = 40 ] && [ \${#k3} = 40 ] && [ \"\$k1\" != \"\$k2\" ] && [ \"\$k2\" != \"\$k3\" ]" "три разных ключа выкладки по 40 hex-символов"
ok "[ -n \"\$(val COLLECTOR_API_KEY)\" ] && [ -n \"\$(val MIGRATION_API_KEY)\" ]" "ключи сборщика и миграции сгенерированы"
ok "echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked и Protected'" "напоминание про переменные группы GitLab"
ok "[ \"\$(stat -c %a /opt/ascon-docker/services/migration)\" = 700 ] && [ \"\$(stat -c %a /opt/ascon-docker/prod/pilot-server/settings)\" = 700 ]" "каталоги секретов закрыты от остальных"
ok "[ \"\$(stat -c %u /opt/ascon-docker/prod/pilot-server/settings)\" = 1654 ]" "каталог настроек отдан UID 1654 (миграция его читает)"
ok "[ -f /opt/ascon-docker/.installed ]" "метка .installed записана"
pg1="$(val PG_SUPERUSER_PASSWORD)"; ck1="$k1"
out2="$(bash install.sh --no-data --skip-load --roles prod,admin,metrics 2>&1)"
ok "[ \"\$(val PG_SUPERUSER_PASSWORD)\" = \"\$pg1\" ] && [ \"\$(val MODULES_DEPLOY_KEY_TEST)\" = \"\$ck1\" ]" "повторный запуск не меняет пароли и ключи"
ok "echo \"\$out2\" | grep -q 'DEPLOY_KEY_PROD (Masked и Protected'" "без роли gitlab напоминание о ключах печатается при каждом запуске"
ok "! grep -q 'gitlab-rails' \"\$STUB_LOG\"" "без роли gitlab шаг GitLab не вызывается"
ok "grep -q -e '-e RC_HOST_m --entrypoint rc rustfs/rustfs:1.0.5 mb --ignore-existing m/pilot-ice-ru' \"\$STUB_LOG\"" "бакет создаётся клиентом rc из образа RustFS"
ok "grep -q 'docker exec rustfs-prod curl -fsS http://127.0.0.1:9000/health' \"\$STUB_LOG\" && ! grep -q 'minio/mc\\|mc mb\\|MC_HOST' \"\$STUB_LOG\"" "перед бакетом ждём готовности RustFS; клиента mc нет"
ok "! grep -qF -e \"\$(val MINIO_PASSWORD)\" \"\$STUB_LOG\" && ! echo \"\$out\" | grep -qF -e \"\$(val MINIO_PASSWORD)\"" "пароль RustFS не попадает в аргументы docker run и вывод"
ok "[ ! -e /opt/ascon-docker/prod/rustfs ] && ! grep -q 'chown' \"\$STUB_LOG\"" "каталог данных RustFS не создаётся на хосте"

echo "== несколько машин: заглушки остаются"
fresh
printf 'INTERNAL_BIND=10.0.0.1\n' >> .env.example
cp .env.example .env
out="$(bash install.sh --no-data --skip-load --roles test 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'пароли-заглушки\\|заглушки'" "установка останавливается и называет заглушки"

echo "== .env потерян, стек уже установлен"
fresh
mkdir -p /opt/ascon-docker; echo x > /opt/ascon-docker/.installed
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc != 0 ] && [ ! -f .env ] && echo \"\$out\" | grep -q 'Верните .env'" "отказ без создания .env из шаблона"

echo "== данные Postgres уже есть без метки .installed: заглушки не заменяются"
fresh
mkdir -p /opt/ascon-docker/prod/postgres; echo x > /opt/ascon-docker/prod/postgres/PG_VERSION
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ \"\$(val PG_SUPERUSER_PASSWORD)\" = postgres ] && echo \"\$out\" | grep -q 'уже есть данные без метки'" "пароль-заглушка остаётся и выводится предупреждение"

echo "== перенос старого общего ключа выкладки"
fresh
sed -i 's/^MODULES_DEPLOY_KEY_TEST=.*//' .env.example
cp .env.example .env; echo 'MODULES_DEPLOY_KEY=oldkey1234567890' >> .env
bash install.sh --no-data --skip-load --roles admin >/dev/null 2>&1
ok "[ \"\$(val MODULES_DEPLOY_KEY_TEST)\" = oldkey1234567890 ] && [ -z \"\$(val MODULES_DEPLOY_KEY)\" ]" "старый ключ стал ключом test и убран из MODULES_DEPLOY_KEY"
ok "[ \${#k2} = 40 ] && [ \"\$(val MODULES_DEPLOY_KEY_STAGE)\" != \"\$(val MODULES_DEPLOY_KEY_PROD)\" ] && [ -n \"\$(val MODULES_DEPLOY_KEY_PROD)\" ]" "для stage и prod созданы новые ключи"

echo "== права при записи в .env (env_set), в том числе после chmod 644"
fresh
cp .env.example .env; chmod 644 .env
bash install.sh --no-data --skip-load --roles prod >/dev/null 2>&1
ok "[ \"\$(stat -c %a .env)\" = 600 ]" "644 до установки становится 600"

echo "== роль gitlab добавлена позже: пароль root не остаётся заглушкой"
fresh
bash install.sh --no-data --skip-load --roles prod >/dev/null 2>&1
pgA="$(val PG_SUPERUSER_PASSWORD)"; pilA="$(val PILOT_ADMIN_PASSWORD)"
out="$(bash install.sh --no-data --skip-load --roles prod,gitlab 2>&1)"
ok "[ -n \"\$(val GITLAB_ROOT_PASSWORD)\" ] && [ \"\$(val GITLAB_ROOT_PASSWORD)\" != change-me-please ]" "каталог данных GitLab пуст: пароль root сгенерирован"
ok "[ \"\$(val PG_SUPERUSER_PASSWORD)\" = \"\$pgA\" ] && [ \"\$(val PILOT_ADMIN_PASSWORD)\" = \"\$pilA\" ]" "остальные пароли не изменились"
ok "echo \"\$out\" | grep -q 'Сгенерированы значения.*GITLAB_ROOT_PASSWORD'" "итог сообщает о сгенерированном пароле root"
echo "== роль gitlab добавлена позже, данные GitLab уже есть: .env не меняется"
fresh
bash install.sh --no-data --skip-load --roles prod >/dev/null 2>&1
mkdir -p /opt/ascon-docker/gitlab/data; echo x > /opt/ascon-docker/gitlab/data/f
out="$(bash install.sh --no-data --skip-load --roles prod,gitlab 2>&1)"
ok "[ \"\$(val GITLAB_ROOT_PASSWORD)\" = change-me-please ]" "пароль root в .env не тронут"
ok "echo \"\$out\" | grep -q 'в данных GitLab уже есть база'" "выведено предупреждение про заглушку root"
ok "! echo \"\$out\" | grep -q 'Сгенерированы значения.*GITLAB_ROOT_PASSWORD'" "пароль root не назван сгенерированным"
echo "== первая установка с gitlab генерирует пароль root"
fresh
bash install.sh --no-data --skip-load --roles prod,gitlab >/dev/null 2>&1
ok "[ -n \"\$(val GITLAB_ROOT_PASSWORD)\" ] && [ \"\$(val GITLAB_ROOT_PASSWORD)\" != change-me-please ] && [ \"\$(val GITLAB_ROOT_PASSWORD | wc -c)\" -ge 25 ]" "пароль root при первой установке случайный"

# ---------------------------------------------------------------- восстановление GitLab
# Известные значения ключей выкладки: они не должны попадать в журнал вызовов docker.
K_TEST=aaaa1111bbbb2222cccc3333dddd4444eeee5555; K_STAGE=bbbb2222cccc3333dddd4444eeee5555ffff6666; K_PROD=cccc3333dddd4444eeee5555ffff66667777aaaa
restore_setup() {
  fresh
  S="$T/work/data"; mkdir -p "$S/admin/env/gitlab" "$S/admin/env/proadmin" "$S/admin/env/prod" "$S/admin/proadmin-data" "$S/postgres/prod" "$S/gitlab/config" "$S/gitlab/runner-config" "$S/pilot/prod/pilot-server/settings"
  printf 'MODULES_DEPLOY_KEY_TEST=%s\nMODULES_DEPLOY_KEY_STAGE=%s\nMODULES_DEPLOY_KEY_PROD=%s\n' "$K_TEST" "$K_STAGE" "$K_PROD" > "$S/admin/env/proadmin/.env"
  printf '{\n  "ModulesGitLabGroupUrl": "http://10.0.0.9:8080/client-modules",\n  "ServerModulesGitLabGroupUrl": "http://10.0.0.9:8080/server-extensions",\n  "ReportsGitLabGroupUrl": "http://10.0.0.9:8080/reports"\n}\n' > "$S/admin/proadmin-data/settings.json"
  echo '{"ok": true, "created": "2026-01-01"}' > "$S/manifest.json"
  printf 'GITLAB_HOST_IP=10.0.0.9\nGITLAB_ROOT_PASSWORD=abcdefgh12345\nGITLAB_HTTP_PORT=8080\n' > "$S/admin/env/gitlab/.env"
  printf 'PG_SUPERUSER_PASSWORD=pgpass123456\nPILOT_PG_PASSWORD=pilotpass12345\nMINIO_PASSWORD=miniopass12345\n' > "$S/admin/env/prod/.env"
  echo 'select 1;' > "$S/postgres/prod/globals.sql"; echo dump > "$S/postgres/prod/pilot-ice_ru.dump"
  echo tar > "$S/gitlab/cicd_2026_gitlab_backup.tar"
  echo '<Configuration/>' > "$S/pilot/prod/pilot-server/settings/settings.xml"
}

echo "== восстановление GitLab: успех"
restore_setup
out="$(STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load --gitlab-host 10.0.0.5 2>&1)"; rc=$?
ok "[ $rc = 0 ]" "код возврата 0"
ok "grep -q 'docker stop -t 600 gitlab' \"\$STUB_LOG\" && ! grep -q 'docker restart gitlab' \"\$STUB_LOG\"" "GitLab останавливается с запасом времени, а не docker restart"
ok "grep -n 'docker stop -t 600 gitlab\\|docker start gitlab-runner\\|docker exec -e GITLAB_ASSUME_YES' \"\$STUB_LOG\" | sort -t: -k1 -n | cut -d: -f2- | tr '\\n' '|' | grep -q 'GITLAB_ASSUME_YES.*stop -t 600 gitlab.*start gitlab-runner'" "порядок: restore, остановка, запуск runner"
ok "echo \"\$out\" | grep -q 'проектов в GitLab: 14'" "число проектов проверено и напечатано"
ok "[ -f /opt/ascon-docker/.installed ] && [ ! -f /opt/ascon-docker/.restore-started ]" ".installed есть, метка .restore-started снята"
ok "[ -f /var/log/gitlab/restore.log ] && grep -q 'ERROR:' /var/log/gitlab/restore.log" "полный журнал восстановления записан"
ok "! echo \"\$out\" | grep -q 'ERROR:'" "на экран строки ERROR от psql не выводятся"
ok "echo \"\$out\" | grep -q 'Redirect URI'" "подсказка про Redirect URI OAuth"
ok "[ \"\$(stat -c %a .env)\" = 600 ] && [ \"\$(stat -c %a /opt/ascon-docker/gitlab/config)\" = 700 ]" "права .env и gitlab/config"
ok "echo \"\$out\" | grep -q 'Готово' && [ \"\$(echo \"\$out\" | grep -n 'Готово' | head -1 | cut -d: -f1)\" -gt \"\$(echo \"\$out\" | grep -n 'проектов в GitLab' | head -1 | cut -d: -f1)\" ]" "«Готово» печатается после восстановления и ожидания"
ok "[ \"\$(val ROLES)\" = prod,gitlab ]" "роли записаны в .env"
ok "grep -q 'docker cp ./gitlab-post-restore.rb gitlab:/tmp/post.rb' \"\$STUB_LOG\" && grep -q 'gitlab-rails runner /tmp/post.rb' \"\$STUB_LOG\"" "шаг GitLab: скрипт скопирован в контейнер и запущен через gitlab-rails runner"
ok "grep -n 'docker cp ./gitlab-post-restore.rb\\|docker exec gitlab chmod 644 /tmp/post.rb\\|gitlab-rails runner' \"\$STUB_LOG\" | cut -d: -f2- | tr '\\n' '|' | grep -q 'docker cp.*chmod 644 /tmp/post.rb.*gitlab-rails runner'" "скрипту в контейнере выдаются права на чтение до запуска (читает пользователь git)"
ok "grep -qx 'ALLOW_MISSING_GROUPS=0' \"\$STUB_RAILS_ENV\"" "после восстановления отсутствие группы - ошибка, а не пропуск"
ok "grep -n 'docker stop -t 600 gitlab\\|docker start gitlab-runner\\|gitlab-rails runner' \"\$STUB_LOG\" | cut -d: -f2- | tr '\\n' '|' | grep -q 'stop -t 600 gitlab.*start gitlab-runner.*gitlab-rails runner'" "порядок: перезапуск GitLab, runner, шаг GitLab"
ok "grep -q 'docker exec -e DEPLOY_KEY_TEST -e DEPLOY_KEY_STAGE -e DEPLOY_KEY_PROD ' \"\$STUB_LOG\"" "ключи передаются как -e ИМЯ без значения"
ok "! grep -qF -e \"\$K_TEST\" -e \"\$K_STAGE\" -e \"\$K_PROD\" \"\$STUB_LOG\"" "значения ключей не попадают в журнал вызовов docker"
ok "grep -qx \"DEPLOY_KEY_STAGE=$K_STAGE\" \"\$STUB_RAILS_ENV\" && grep -qx \"DEPLOY_KEY_PROD=$K_PROD\" \"\$STUB_RAILS_ENV\" && grep -qx \"DEPLOY_KEY_TEST=$K_TEST\" \"\$STUB_RAILS_ENV\"" "значения ключей доходят до скрипта через окружение"
ok "grep -qx 'GROUP_URLS=http://10.0.0.9:8080/client-modules,http://10.0.0.9:8080/server-extensions' \"\$STUB_RAILS_ENV\" && grep -qx 'REPORTS_GROUP_URL=http://10.0.0.9:8080/reports' \"\$STUB_RAILS_ENV\"" "пути групп взяты из настроек админки в снимке бэкапа"
ok "grep -qx 'OLD_HOST=10.0.0.9' \"\$STUB_RAILS_ENV\" && grep -qx 'NEW_URL=http://10.0.0.5:8080' \"\$STUB_RAILS_ENV\" && [ ! -f /opt/ascon-docker/.gitlab-old-host ]" "старый и новый адрес переданы, метка старого адреса снята после успеха"
ok "! echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked'" "с ролью gitlab и успешным шагом напоминания о ключах нет"

echo "== восстановление GitLab: сбой шага gitlab-rails"
restore_setup
out="$(STUB_HARDEN_RC=1 STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load --gitlab-host 10.0.0.5 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'Шаг GitLab после установки не выполнен'" "ненулевой код установщика и предупреждение"
ok "echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked'" "после сбоя печатается напоминание о ключах"
ok "[ -f /opt/ascon-docker/.installed ] && [ -f /opt/ascon-docker/.gitlab-old-host ] && echo \"\$out\" | grep -q 'Готово'" "данные восстановлены, остальные шаги выполнены, метка старого адреса осталась"
out="$(bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && grep -qx 'OLD_HOST=10.0.0.9' \"\$STUB_RAILS_ENV\" && [ ! -f /opt/ascon-docker/.gitlab-old-host ] && ! echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked'" "повторный запуск повторяет шаг со старым адресом из метки; после успеха метки и напоминания нет"

echo "== шаг GitLab пропущен частично: нет ключей и адресов групп"
restore_setup
rm -rf "$S/admin/env/proadmin" "$S/admin/proadmin-data"
out="$(STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load --gitlab-host 10.0.0.5 2>&1)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked' && echo \"\$out\" | grep -q 'Нет ключей MODULES_DEPLOY_KEY_'" "напоминание о ключах и причина; код 0"
ok "grep -qx 'GROUP_URLS=' \"\$STUB_RAILS_ENV\" && [ -f /opt/ascon-docker/.gitlab-old-host ]" "скрипт всё равно вызван (регистрация), метка старого адреса осталась для nuget.config"

echo "== восстановление GitLab: в базе нет проектов"
restore_setup
out="$(STUB_PROJECTS=0 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'нет проектов'" "ненулевой код и понятный текст"
ok "[ ! -f /opt/ascon-docker/.installed ] && [ -f /opt/ascon-docker/.restore-started ]" ".installed не записан, метка .restore-started осталась"
out="$(STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'restore-started'" "повторный запуск не восстанавливает данные второй раз"

echo "== восстановление GitLab: gitlab-ctl stop не дождался остановки"
restore_setup
out="$(STUB_STOP_RC=1 STUB_STOP_DOWN=1 STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ -f /opt/ascon-docker/.installed ]" "сервис всё же остановился (status down): восстановление идёт дальше"
restore_setup
out="$(STUB_STOP_RC=1 STUB_STOP_DOWN=0 STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не останавливается' && grep -q 'gitlab-ctl kill puma' \"\$STUB_LOG\"" "не остановился и после kill: понятная ошибка"
ok "[ ! -f /opt/ascon-docker/.restore-started ] && ! grep -q 'gitlab-backup restore' /var/log/gitlab/restore.log 2>/dev/null && echo \"\$out\" | grep -q 'очистите DATA_ROOT'" "метки .restore-started нет, восстановление GitLab не начиналось, текст называет очистку DATA_ROOT"

echo "== восстановление GitLab: код возврата gitlab-backup restore"
restore_setup
out="$(STUB_RESTORE_RC=3 STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'завершился с кодом 3'" "ненулевой код restore останавливает установку"

echo "== восстановление GitLab: ошибки схемы"
restore_setup
out="$(STUB_SCHEMA_ERR=1 STUB_PROJECTS=14 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'ошибках схемы'" "строка об ошибках схемы даёт предупреждение (данные на месте)"

echo "== GitLab не готов за 30 минут"
restore_setup
mkdir -p "$T/fastbin"; printf '#!/bin/sh\nexit 0\n' > "$T/fastbin/sleep"; chmod +x "$T/fastbin/sleep"
out="$(PATH="$T/fastbin:$PATH" STUB_READY_RC=1 bash install.sh --roles prod,gitlab --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не стал готов за 30 минут'" "таймаут ожидания готовности"

echo "== Grafana: том с данными"
restore_setup
mkdir -p "$S/grafana"; echo tgz > "$S/grafana/grafana_grafana_data.tgz"
out="$(GRAFANA_BUSY=1 bash install.sh --roles prod,metrics --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'cicd_grafana_data'" "отказ восстановления в непустой том, имя тома из проекта по умолчанию"
restore_setup
mkdir -p "$S/grafana"; echo tgz > "$S/grafana/grafana_grafana_data.tgz"; cp .env.example .env; echo 'COMPOSE_PROJECT_NAME=lab' >> .env
out="$(GRAFANA_BUSY=1 bash install.sh --roles prod,metrics --skip-load 2>&1)"
ok "echo \"\$out\" | grep -q 'lab_grafana_data'" "имя тома выводится из COMPOSE_PROJECT_NAME"

echo "== ключи защиты данных админки при восстановлении"
# Данные комплекта (data рядом со скриптом): чужие ключи не переносятся; свой бэкап (--from-backup): ключи сохраняются, папка закрыта.
restore_setup
mkdir -p "$S/admin/proadmin-data/keys"; echo '<key/>' > "$S/admin/proadmin-data/keys/key-1.xml"
out="$(bash install.sh --roles prod,admin --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ -f /opt/ascon-docker/services/proadmin/settings.json ] && [ ! -e /opt/ascon-docker/services/proadmin/keys ]" "данные комплекта: настройки админки перенесены, ключи нет"
restore_setup
mkdir -p "$S/admin/proadmin-data/keys"; echo '<key/>' > "$S/admin/proadmin-data/keys/key-1.xml"
mv "$S" "$T/own-backup"
out="$(bash install.sh --from-backup "$T/own-backup" --roles prod,admin --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ -f /opt/ascon-docker/services/proadmin/keys/key-1.xml ]" "свой бэкап: ключи перенесены (никого не разлогинивает)"
ok "[ \"\$(stat -c %a /opt/ascon-docker/services/proadmin/keys)\" = 700 ]" "папка ключей закрыта от остальных пользователей"
rm -rf "$T/own-backup"

echo "== чистая установка с ролью gitlab"
fresh
cp .env.example .env
printf 'MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/client-modules\nSERVER_MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/server-extensions\n' >> .env
out="$(bash install.sh --no-data --skip-load --roles admin,gitlab 2>&1)"; rc=$?
ok "[ $rc = 0 ] && grep -q 'gitlab-rails runner /tmp/post.rb' \"\$STUB_LOG\"" "шаг GitLab вызывается и без восстановления (закрытие регистрации)"
ok "[ \"\$(sed -n 's/^DEPLOY_KEY_PROD=//p' \"\$STUB_RAILS_ENV\")\" = \"\$(val MODULES_DEPLOY_KEY_PROD)\" ] && [ -n \"\$(val MODULES_DEPLOY_KEY_PROD)\" ] && grep -qx 'OLD_HOST=' \"\$STUB_RAILS_ENV\"" "сгенерированный ключ доходит до скрипта, старого адреса нет"
ok "! grep -qF -e \"\$(val MODULES_DEPLOY_KEY_TEST)\" -e \"\$(val MODULES_DEPLOY_KEY_STAGE)\" -e \"\$(val MODULES_DEPLOY_KEY_PROD)\" \"\$STUB_LOG\"" "сгенерированные ключи не попадают в журнал вызовов docker"
ok "! echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked'" "ключи и группы заданы, шаг прошёл: напоминания нет"
ok "grep -qx 'ALLOW_MISSING_GROUPS=1' \"\$STUB_RAILS_ENV\"" "чистая установка: отсутствие группы допускается"

echo "== чистая установка с ролью gitlab: групп модулей в GitLab ещё нет (код 2)"
fresh
cp .env.example .env
printf 'MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/client-modules\n' >> .env
out="$(STUB_HARDEN_RC=2 bash install.sh --no-data --skip-load --roles admin,gitlab 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! echo \"\$out\" | grep -q 'Шаг GitLab после установки не выполнен'" "код 2 шага - не сбой: код возврата 0, предупреждения о сбое нет"
ok "echo \"\$out\" | grep -q 'Групп модулей в GitLab ещё нет' && echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked'" "но переменные не заданы: сообщение и напоминание о ключах"

echo "== чистая установка с ролью gitlab: GitLab не отвечает"
fresh
cp .env.example .env
printf 'MODULES_GITLAB_GROUP_URL=http://10.0.0.5:8080/client-modules\n' >> .env
out="$(PATH="$T/fastbin:$PATH" STUB_READY_RC=1 bash install.sh --no-data --skip-load --roles admin,gitlab 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'Шаг GitLab после установки не выполнен' && echo \"\$out\" | grep -q 'DEPLOY_KEY_PROD (Masked' && ! grep -q 'gitlab-rails' \"\$STUB_LOG\"" "ожидание готовности не удалось: предупреждение, напоминание, ненулевой код, скрипт не запускался"
ok "[ -f /opt/ascon-docker/.installed ]" "остальная установка завершена"

echo "== служебные порты при INTERNAL_BIND"
fresh
cp .env.example .env; sed -i 's/^PG_SUPERUSER_PASSWORD=.*/PG_SUPERUSER_PASSWORD=realpgpass123/; s/^PILOT_ADMIN_PASSWORD=.*/PILOT_ADMIN_PASSWORD=realadmin123/; s/^PILOT_PG_PASSWORD=.*/PILOT_PG_PASSWORD=realpilot123/; s/^MINIO_PASSWORD=.*/MINIO_PASSWORD=realminio123/; s/^GRAFANA_ADMIN_PASSWORD=.*/GRAFANA_ADMIN_PASSWORD=realgraf123/' .env
echo 'INTERNAL_BIND=10.0.0.1' >> .env; echo 'METRICS_HOST=10.0.0.2' >> .env; sed -i 's/^METRICS_WRITER_PASSWORD=.*/METRICS_WRITER_PASSWORD=realmw123/; s/^METRICS_READER_PASSWORD=.*/METRICS_READER_PASSWORD=realmr123/' .env
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"
ok "echo \"\$out\" | grep -q 'INTERNAL_BIND=10.0.0.1.*5433 9000 9001.*9100 9101'" "напоминание закрыть порты и их список"

echo "== брандмауэр для служебных портов: файл правил при INTERNAL_BIND"
multi_env() {  # $1 - дополнительные строки .env
  fresh
  cp .env.example .env; sed -i 's/^PG_SUPERUSER_PASSWORD=.*/PG_SUPERUSER_PASSWORD=realpgpass123/; s/^PILOT_ADMIN_PASSWORD=.*/PILOT_ADMIN_PASSWORD=realadmin123/; s/^PILOT_PG_PASSWORD=.*/PILOT_PG_PASSWORD=realpilot123/; s/^MINIO_PASSWORD=.*/MINIO_PASSWORD=realminio123/; s/^GRAFANA_ADMIN_PASSWORD=.*/GRAFANA_ADMIN_PASSWORD=realgraf123/; s/^METRICS_WRITER_PASSWORD=.*/METRICS_WRITER_PASSWORD=realmw123/; s/^METRICS_READER_PASSWORD=.*/METRICS_READER_PASSWORD=realmr123/' .env
  printf '%b' "$1" >> .env
}
multi_env 'INTERNAL_BIND=10.0.0.1\nPROD_HOST=10.0.0.2\nMETRICS_HOST=10.0.0.3\nTEST_HOST=test.example.local\nADMIN_HOST=10.0.0.2\n'
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"
ok "[ -f firewall-linux.sh ] && sh -n firewall-linux.sh" "файл правил создан и проходит разбор sh"
ok "grep -q -e '--ctorigdstport 5433 -s 10.0.0.2/32 -j RETURN' firewall-linux.sh && grep -q -e '--ctorigdstport 5433 -s 10.0.0.3/32 -j RETURN' firewall-linux.sh && grep -q -e '--ctorigdstport 9001 -s 10.0.0.1/32 -j RETURN' firewall-linux.sh" "разрешены машины стенда и сама машина на портах prod"
ok "[ \"\$(grep -c -e '-s 10.0.0.2/32 -j RETURN' firewall-linux.sh)\" = 5 ]" "повторяющийся адрес (PROD_HOST и ADMIN_HOST) записан один раз на порт (5 портов: prod и агенты)"
ok "grep -q -e '--ctorigdstport 5433 -j DROP' firewall-linux.sh && grep -q -e '--ctorigdstport 9000 -j DROP' firewall-linux.sh && ! grep -q 'DROP.*5434' firewall-linux.sh" "остальным закрыты только порты ролей этой машины"
ok "! grep -v '^#' firewall-linux.sh | grep -q 'test.example.local'" "имя хоста в правила не попадает"
ok "echo \"\$out\" | grep -q 'INTERNAL_BIND=10.0.0.1.*5433 9000 9001.*firewall-linux.sh.*Автоматически они не применяются' && echo \"\$out\" | grep -q 'не IP-адреса: TEST_HOST=test.example.local'" "одно предупреждение с путём к файлу; имя хоста названо"
ok "! grep -q 'iptables' \"\$STUB_LOG\"" "правила автоматически не применяются"
ok "grep -q -e '-i br-+ -j RETURN' firewall-linux.sh && grep -q 'DOCKER-USER' firewall-linux.sh" "трафик локальных контейнеров не режется, правила стоят в DOCKER-USER"
ok "! grep -e '^-A' firewall-linux.sh | grep -q -e '5300\\|5546\\|3000'" "порты админки, Pilot и Grafana правилами не затронуты"
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"
ok "[ \"\$(grep -c 'INTERNAL -p tcp' firewall-linux.sh)\" = 20 ]" "повторный запуск переписывает файл, а не дописывает"
multi_env 'INTERNAL_BIND=10.0.0.1\nGITLAB_HOST_IP=localhost\n'
out="$(bash install.sh --no-data --skip-load --roles metrics 2>&1)"
ok "grep -q -e '--ctorigdstport 3100 -s 10.0.0.1/32 -j RETURN' firewall-linux.sh && grep -q -e '--ctorigdstport 9090 -j DROP' firewall-linux.sh && grep -q -e '--ctorigdstport 9100 -j DROP' firewall-linux.sh && [ \"\$(grep -c -e '-p tcp.*-j RETURN' firewall-linux.sh)\" = 4 ]" "машина metrics: Prometheus, Loki и агенты; localhost в GITLAB_HOST_IP пропущен, остаётся сама машина"
multi_env 'INTERNAL_BIND=0.0.0.0\nPROD_HOST=10.0.0.2\n'
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"
ok "[ -f firewall-linux.sh ] && ! grep -q -e '0.0.0.0/32' firewall-linux.sh && grep -q -e '--ctorigdstport 5433 -s 10.0.0.2/32 -j RETURN' firewall-linux.sh" "INTERNAL_BIND=0.0.0.0 не попадает в разрешённые адреса"
ok "grep -q 'Нет цепочки DOCKER-USER' firewall-linux.sh && grep -q 'br-\*' firewall-linux.sh" "в файле есть проверка DOCKER-USER и оговорка про br-*"
sed -i '/^INTERNAL_BIND=/d' .env
out="$(bash install.sh --no-data --skip-load --roles metrics 2>&1)"
ok "[ ! -e firewall-linux.sh ]" "INTERNAL_BIND убран из .env: устаревший файл правил удалён"
ok "! echo \"\$out\" | grep -q 'INTERNAL_BIND'" "без INTERNAL_BIND предупреждения нет"

echo "== is_ipv4"
eval "$(sed -n '/^# firewall:begin/,/^# firewall:end/p' "$STACK/Install/install.sh")"
for v in 10.0.0.1 192.168.1.255 0.0.0.0 255.255.255.255; do ok "is_ipv4 $v" "$v - IPv4"; done
for v in '' host.example.local 10.0.0 10.0.0.256 1.2.3.4.5 010.0.0.1 10.0.0.1/24 10..0.1 .10.0.1 10.0.0. 'a.b.c.d' ' 10.0.0.1'; do ok "! is_ipv4 '$v'" "'$v' - не IPv4"; done

echo "== HTTPS: сертификат и ключ в certs/"
tls_setup() {  # $1 - дополнительные строки .env
  fresh
  cp "$STACK/Install/docker-compose.tls.yml" .
  cp .env.example .env; [ -z "$1" ] || printf '%b' "$1" >> .env
  mkdir -p certs
  printf -- '-----BEGIN CERTIFICATE-----\nMIIB\n-----END CERTIFICATE-----\n' > certs/tls.crt
  printf -- '-----BEGIN PRIVATE KEY-----\nMIIE\n-----END PRIVATE KEY-----\n' > certs/tls.key
}
tls_setup ''
out="$(bash install.sh --no-data --skip-load --roles admin,metrics 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ \"\$(val COMPOSE_FILE)\" = 'docker-compose.yml,docker-compose.tls.yml' ] && [ \"\$(val COMPOSE_PATH_SEPARATOR)\" = ',' ]" "с certs/ в COMPOSE_FILE подключена надстройка HTTPS"
ok "[ \"\$(val TLS_ADMIN_CRT)\" = /opt/ascon-docker/tls/admin/tls.crt ] && [ \"\$(val TLS_GRAFANA_KEY)\" = /opt/ascon-docker/tls/grafana/tls.key ]" "пути для compose записаны в .env"
ok "[ \"\$(stat -c %u:%a /opt/ascon-docker/tls/admin/tls.key)\" = 1654:600 ] && [ \"\$(stat -c %u:%a /opt/ascon-docker/tls/grafana/tls.key)\" = 472:600 ] && [ \"\$(stat -c %a /opt/ascon-docker/tls/admin)\" = 700 ]" "ключ закрыт: читает только пользователь своего контейнера (админка 1654, Grafana 472)"
ok "echo \"\$out\" | grep -q 'HTTPS включён' && echo \"\$out\" | grep -q 'Админка: https, порт' && echo \"\$out\" | grep -q 'Grafana: https, порт'" "сообщения называют https"
ok "[ \"\$(stat -c %a .env)\" = 600 ]" ".env по-прежнему 600"
out="$(bash install.sh --no-data --skip-load --roles admin,metrics 2>&1)"
ok "[ \"\$(grep -c '^COMPOSE_FILE=' .env)\" = 1 ] && [ \"\$(val COMPOSE_FILE)\" = 'docker-compose.yml,docker-compose.tls.yml' ]" "повторный запуск не дублирует COMPOSE_FILE"
rm -rf certs
out="$(bash install.sh --no-data --skip-load --roles admin,metrics 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ -z \"\$(val COMPOSE_FILE)\" ] && [ -z \"\$(val COMPOSE_PATH_SEPARATOR)\" ] && ! grep -q '^TLS_' .env" "certs/ убраны: надстройка и переменные TLS исчезают из .env, снова HTTP"
ok "echo \"\$out\" | grep -q 'Админка: http, порт' && ! echo \"\$out\" | grep -q 'HTTPS включён'" "сообщения называют http"
tls_setup ''
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"
ok "[ -z \"\$(val COMPOSE_FILE)\" ]" "роли без админки и Grafana: HTTPS не включается"
tls_setup ''; mkdir -p "$T/nochown"
printf '#!/bin/sh\nfor a; do case "$a" in */tls/*) exit 1 ;; esac; done\nexec /bin/chown "$@"\n' > "$T/nochown/chown"; chmod +x "$T/nochown/chown"
out="$(PATH="$T/nochown:$PATH" bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'Запустите install.sh от root' && [ \"\$(stat -c %a /opt/ascon-docker/tls/admin/tls.key)\" = 600 ]" "без chown (не root): остановка, ключ не открывается всем"
tls_setup ''; rm certs/tls.key
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'найден только один из файлов'" "есть только сертификат: остановка с понятным текстом, а не молчаливый HTTP"
tls_setup ''; sed -i 's/BEGIN PRIVATE KEY/BEGIN ENCRYPTED PRIVATE KEY/' certs/tls.key
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'зашифрован паролем'" "ключ с паролем отвергается: Kestrel и Grafana его не прочтут"
tls_setup ''; echo junk > certs/tls.crt
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не похож на сертификат'" "файл не в формате PEM отвергается"
tls_setup 'TLS_CERT_FILE=/srv/pki/my.crt\nTLS_KEY_FILE=/srv/pki/my.key\nADMIN_PUBLIC_URL=http://10.0.0.5:5300\n'
mkdir -p /srv/pki; mv certs/tls.crt /srv/pki/my.crt; mv certs/tls.key /srv/pki/my.key
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ \"\$(val COMPOSE_FILE)\" = 'docker-compose.yml,docker-compose.tls.yml' ] && cmp -s /srv/pki/my.crt /opt/ascon-docker/tls/admin/tls.crt" "пути TLS_CERT_FILE и TLS_KEY_FILE из .env"
ok "echo \"\$out\" | grep -q 'ADMIN_PUBLIC_URL.*http://.*https://'" "ADMIN_PUBLIC_URL с http:// при HTTPS: предупреждение"
rm -rf /srv/pki
tls_setup 'COMPOSE_FILE=a.yml:b.yml\n'
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"
ok "[ \"\$(val COMPOSE_FILE)\" = 'a.yml:b.yml:docker-compose.tls.yml' ] && [ -z \"\$(val COMPOSE_PATH_SEPARATOR)\" ]" "свой COMPOSE_FILE сохраняется, надстройка дописывается с его разделителем"
rm -rf certs
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"
ok "[ \"\$(val COMPOSE_FILE)\" = 'a.yml:b.yml' ]" "без certs в своём COMPOSE_FILE остаётся только своё"


# ---------------------------------------------------------------- обязательные пароли в .env
echo "== обязательные значения в .env (повторный запуск, данные уже есть)"
installed() { mkdir -p /opt/ascon-docker; echo x > /opt/ascon-docker/.installed; }
fresh; cp .env.example .env; installed
sed -i '/^PG_SUPERUSER_PASSWORD=/d' .env
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не заданы обязательные значения: PG_SUPERUSER_PASSWORD' && ! grep -q 'compose up' \"\$STUB_LOG\"" "роли prod нужен PG_SUPERUSER_PASSWORD: остановка до compose, ключ назван"
ok "[ -z \"\$(val PG_SUPERUSER_PASSWORD)\" ]" "пароль роли не придумывается"
fresh; cp .env.example .env; installed
sed -i '/^GRAFANA_ADMIN_PASSWORD=/d;/^MINIO_PASSWORD=/d' .env
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'обязательные значения: MINIO_PASSWORD'" "в списке только ключи, нужные ролям машины (Grafana на prod-машине не нужна)"
fresh; cp .env.example .env; installed
sed -i '/^GRAFANA_ADMIN_PASSWORD=/d' .env
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ \"\$(val GRAFANA_ADMIN_PASSWORD | wc -c)\" -ge 40 ] && echo \"\$out\" | grep -q 'Сгенерированы значения.*GRAFANA_ADMIN_PASSWORD'" "пароль Grafana на машине без роли metrics получает значение (compose требует его во всём файле)"
fresh; cp .env.example .env; installed
sed -i '/^PG_SUPERUSER_PASSWORD=/d;/^PILOT_PG_PASSWORD=/d;/^MINIO_USER=/d;/^MINIO_PASSWORD=/d' .env
for c in PROD TEST STAGE; do printf '%s_PG_SUPERUSER_PASSWORD=a\n%s_PILOT_PG_PASSWORD=a\n%s_MINIO_USER=u\n%s_MINIO_PASSWORD=a\n' $c $c $c $c >> .env; done
out="$(bash install.sh --no-data --skip-load --roles prod,test,stage,admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ -z \"\$(val PG_SUPERUSER_PASSWORD)\" ] && [ -z \"\$(val MINIO_PASSWORD)\" ]" "пароли заданы у всех трёх контуров: общие не нужны и не создаются"
fresh; cp .env.example .env; installed
sed -i '/^MINIO_USER=/d' .env
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'обязательные значения:.*MINIO_USER'" "роль admin берёт S3-учётку миграции из MINIO_USER: пустое значение останавливает установку"

# ---------------------------------------------------------------- init-скрипт PostgreSQL
echo "== init/postgres/01-pilot.sh: пароль обязателен"
cat > "$T/bin/psql" <<'STUB'
#!/bin/sh
echo "psql $*" >> "$STUB_LOG"; cat > /dev/null
STUB
chmod +x "$T/bin/psql"
: > "$STUB_LOG"
out="$(env -u PILOT_PG_PASSWORD POSTGRES_USER=postgres bash "$STACK/Install/init/postgres/01-pilot.sh" 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'PILOT_PG_PASSWORD не задан' && ! grep -q psql \"\$STUB_LOG\"" "без PILOT_PG_PASSWORD скрипт падает до psql, роль с паролем «password» не создаётся"
out="$(PILOT_PG_PASSWORD= POSTGRES_USER=postgres bash "$STACK/Install/init/postgres/01-pilot.sh" 2>&1)"; rc=$?
ok "[ $rc != 0 ] && ! grep -q psql \"\$STUB_LOG\"" "пустой пароль тоже отвергается"
out="$(PILOT_PG_PASSWORD=s3cret POSTGRES_USER=postgres bash "$STACK/Install/init/postgres/01-pilot.sh" 2>&1)"; rc=$?
ok "[ $rc = 0 ] && grep -q 'pw=s3cret' \"\$STUB_LOG\"" "с паролем он передаётся в psql"
ok "cmp -s $STACK/Install/init/postgres/01-pilot.sh $STACK/prod/init/postgres/01-pilot.sh && cmp -s $STACK/Install/init/postgres/01-pilot.sh $STACK/test/init/postgres/01-pilot.sh && cmp -s $STACK/Install/init/postgres/01-pilot.sh $STACK/stage/init/postgres/01-pilot.sh" "копии скрипта в prod, test и stage совпадают"

# ---------------------------------------------------------------- роли PostgreSQL для метрик
echo "== роли метрик: пароли и шаг установки"
fresh
out="$(bash install.sh --no-data --skip-load --roles prod,admin,metrics 2>&1)"; rc=$?
mw="$(val METRICS_WRITER_PASSWORD)"; mr="$(val METRICS_READER_PASSWORD)"
ok "[ $rc = 0 ] && [ \${#mw} = 40 ] && [ \${#mr} = 40 ] && [ \"\$mw\" != \"\$mr\" ]" "чистая установка: пароли обеих ролей сгенерированы (40 hex)"
ok "! grep -qF -e \"\$mw\" -e \"\$mr\" \"\$STUB_LOG\" && ! echo \"\$out\" | grep -qF -e \"\$mw\" -e \"\$mr\"" "пароли ролей не попадают в аргументы docker и вывод"
ok "grep -q 'docker compose up -d --pull missing postgres-prod' \"\$STUB_LOG\" && [ \"\$(grep -c 'docker exec postgres-prod bash /docker-entrypoint-initdb.d/02-metrics-roles.sh' \"\$STUB_LOG\")\" = 2 ]" "скрипт ролей запускается в postgres-prod дважды: до запуска стека и после создания баз Pilot"
l1="$(grep -n '02-metrics-roles' "$STUB_LOG" | head -1 | cut -d: -f1)"; l2="$(grep -n 'compose up -d --pull missing --remove-orphans' "$STUB_LOG" | head -1 | cut -d: -f1)"
ok "[ \"\$l1\" -lt \"\$l2\" ]" "первый запуск - до запуска остальных сервисов (сборщики не стартуют без ролей)"
bash install.sh --no-data --skip-load --roles prod,admin,metrics >/dev/null 2>&1
ok "[ \"\$(val METRICS_WRITER_PASSWORD)\" = \"\$mw\" ] && [ \"\$(val METRICS_READER_PASSWORD)\" = \"\$mr\" ]" "повторный запуск пароли не меняет"
fresh; : > "$STUB_LOG"
out="$(bash install.sh --no-data --skip-load --roles admin,gitlab 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! grep -q '02-metrics-roles' \"\$STUB_LOG\" && [ -n \"\$(val METRICS_WRITER_PASSWORD)\" ]" "без роли prod скрипт ролей не запускается, значение для compose всё равно создаётся"
fresh; cp .env.example .env; installed
sed -i '/^METRICS_/d' .env
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ -n \"\$(val METRICS_WRITER_PASSWORD)\" ] && [ -n \"\$(val METRICS_READER_PASSWORD)\" ] && echo \"\$out\" | grep -q 'Сгенерированы значения.*METRICS_WRITER_PASSWORD'" "обновление существующей установки: ключей нет - создаются (в данных они не записаны)"
fresh; cp .env.example .env; printf 'METRICS_WRITER_PASSWORD=keepme123\nINTERNAL_BIND=10.0.0.1\nPROD_HOST=10.0.0.2\n' >> .env; installed
sed -i '/^METRICS_WRITER_PASSWORD=$/d' .env
out="$(bash install.sh --no-data --skip-load --roles metrics 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'не заданы пароли ролей метрик: METRICS_READER_PASSWORD' && [ \"\$(val METRICS_WRITER_PASSWORD)\" = keepme123 ] && ! grep -q 'compose up' \"\$STUB_LOG\"" "несколько машин: пустой пароль не придумывается, остановка до compose, заданный не тронут"
fresh; cp .env.example .env; installed
sed -i '/^PILOT_ADMIN_PASSWORD=/d' .env; : > "$STUB_LOG"
out="$(STUB_ROLES_RC=1 bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'Не удалось создать роли метрик' && ! grep -q 'compose up -d --pull missing --remove-orphans' \"\$STUB_LOG\"" "сбой скрипта ролей останавливает установку до запуска сборщиков"

echo "== init/postgres/02-metrics-roles.sh"
cat > "$T/bin/psql" <<'STUB'
#!/bin/sh
echo "psql $*" >> "$STUB_LOG"; cat >> "$STUB_STDIN"
STUB
export STUB_STDIN="$T/psql-stdin.log"; : > "$STUB_STDIN"; : > "$STUB_LOG"
out="$(env -u CREATE_METRICS_DB POSTGRES_USER=postgres bash "$STACK/Install/init/postgres/02-metrics-roles.sh" 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! grep -q psql \"\$STUB_LOG\"" "без CREATE_METRICS_DB (test, stage) скрипт ничего не делает"
out="$(CREATE_METRICS_DB=1 METRICS_READER_PASSWORD=r bash "$STACK/Install/init/postgres/02-metrics-roles.sh" 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'METRICS_WRITER_PASSWORD не задан' && ! grep -q psql \"\$STUB_LOG\"" "нет пароля записи: падает до psql"
out="$(CREATE_METRICS_DB=1 METRICS_WRITER_PASSWORD=w bash "$STACK/Install/init/postgres/02-metrics-roles.sh" 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'METRICS_READER_PASSWORD не задан' && ! grep -q psql \"\$STUB_LOG\"" "нет пароля чтения: падает до psql"
out="$(CREATE_METRICS_DB=1 METRICS_WRITER_PASSWORD=SECRETW METRICS_READER_PASSWORD=SECRETR bash "$STACK/Install/init/postgres/02-metrics-roles.sh" 2>&1)"; rc=$?
ok "[ $rc = 0 ] && grep -q 'psql -v ON_ERROR_STOP=1 -U postgres -d postgres' \"\$STUB_LOG\" && ! grep -q 'SECRETW\|SECRETR' \"\$STUB_LOG\" \"\$STUB_STDIN\"" "с паролями psql вызывается (пользователь postgres по умолчанию), значения в аргументы и текст SQL не попадают"
ok "grep -q 'getenv wpw METRICS_WRITER_PASSWORD' \"\$STUB_STDIN\" && grep -q 'REVOKE ALL ON DATABASE pilot_metrics FROM PUBLIC, pilotuser' \"\$STUB_STDIN\" && grep -q 'DEFAULT PRIVILEGES FOR ROLE metrics_writer GRANT SELECT ON TABLES TO metrics_reader' \"\$STUB_STDIN\" && ! grep -q '^REASSIGN OWNED' \"\$STUB_STDIN\"" "SQL: пароли через \\getenv, права pilotuser отозваны, права по умолчанию для будущих таблиц, без REASSIGN OWNED"
ok "for c in prod test stage; do cmp -s $STACK/Install/init/postgres/02-metrics-roles.sh $STACK/\$c/init/postgres/02-metrics-roles.sh || exit 1; done" "копии скрипта в prod, test и stage совпадают"

# ---------------------------------------------------------------- целостность образов из images/
echo "== сверка загруженных образов с IMAGES.txt"
ID1=sha256:$(printf 'a%.0s' $(seq 64)); ID2=sha256:$(printf 'b%.0s' $(seq 64))
LAY1='["sha256:1111","sha256:2222"]'; LAY2='["sha256:3333"]'
H1="$(printf '%s' "$LAY1" | sha256sum | cut -d' ' -f1)"
img_setup() {  # $1 - имя образа в файле комплекта, $2 - идентификатор и $3 - слои образа после загрузки, $4 - строка IMAGES.txt
  fresh; cp .env.example .env; mkdir -p images "$STUB_IMG"; rm -f "$STUB_IMG"/*
  printf '%s\n' "$1" > images/a.tar.gz
  k="$(printf '%s' "$1" | tr '/:@' '___')"; printf '%s' "$2" > "$STUB_IMG/$k.id"; printf '%s' "$3" > "$STUB_IMG/$k.layers"
  if [ -n "$4" ]; then printf 'Папка собрана 2026-01-01 00:00\n\nВ папке (images\\):\n%s\n' "$4" > IMAGES.txt; fi
}
img_setup pilot-proadmin:latest "$ID1" "$LAY1" "  pilot-proadmin:latest                               $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! echo \"\$out\" | grep -q 'не совпал'" "идентификатор совпал: установка идёт дальше"
img_setup pilot-proadmin:latest "$ID2" "$LAY2" "  pilot-proadmin:latest                               $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'pilot-proadmin:latest из a.tar.gz не совпал с IMAGES.txt' && ! grep -q 'compose up' \"\$STUB_LOG\"" "расхождение идентификатора и слоёв: ошибка с именем образа и файла, до запуска сервисов"
img_setup pilot-proadmin:latest "$ID2" "$LAY1" "  pilot-proadmin:latest                               $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ]" "другое хранилище образов (идентификатор иной, слои те же): не ошибка"
img_setup pilot-proadmin:latest "$ID1" "$LAY1" "  pilot-proadmin:latest                               $ID1  2026-01-01  $H1"
sed -i 's/$/\r/' IMAGES.txt  # IMAGES.txt, собранный build.ps1 на Windows, идёт с CRLF
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! echo \"\$out\" | grep -q 'нет полной записи' && ! echo \"\$out\" | grep -q 'не совпал'" "IMAGES.txt с CRLF, идентификатор совпал: без предупреждений"
img_setup pilot-proadmin:latest "$ID2" "$LAY2" "  pilot-proadmin:latest                               $ID1  2026-01-01  $H1"
sed -i 's/$/\r/' IMAGES.txt
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'pilot-proadmin:latest из a.tar.gz не совпал с IMAGES.txt'" "IMAGES.txt с CRLF, расхождение: ошибка"
img_setup pilot-proadmin:latest "$ID1" "$LAY1" ""
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'Нет IMAGES.txt'" "нет IMAGES.txt: предупреждение, установка продолжается"
img_setup pilot-proadmin:latest "$ID1" "$LAY1" "  pilot-migration:latest                              $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'pilot-proadmin:latest в IMAGES.txt нет полной записи'" "нет строки для образа: предупреждение"
img_setup pilot-proadmin:latest "$ID2" "$LAY2" "  pilot-proadmin:latest                               ${ID1:7:12}  2026-01-01"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'в IMAGES.txt нет полной записи'" "строка старого формата (12 символов): предупреждение, не ошибка"
img_setup rustfs/rustfs:1.0.5 "$ID1" "$LAY1" "  rustfs/rustfs:1.0.5@sha256:$(printf 'c%.0s' $(seq 64))   $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! echo \"\$out\" | grep -q 'нет полной записи'" "запись с digest в имени сопоставляется с загруженным тегом"
img_setup ID:sha256:abc "$ID1" "$LAY1" "  x   $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ] && echo \"\$out\" | grep -q 'загружен без имени'" "образ без имени: предупреждение"
img_setup pilot-proadmin:latest "$ID2" "$LAY2" "  pilot-proadmin:latest                               $ID1  2026-01-01  $H1"
out="$(bash install.sh --no-data --skip-load --roles admin 2>&1)"; rc=$?
ok "[ $rc = 0 ]" "с --skip-load сверка не выполняется"
rm -rf "$STUB_IMG"

# ---------------------------------------------------------------- RustFS
echo "== бакет RustFS: пароль со спецсимволами"
fresh; cp .env.example .env
sed -i 's/^MINIO_PASSWORD=.*/MINIO_PASSWORD=p@ss:w\/o rd#1/; s/^PG_SUPERUSER_PASSWORD=.*/PG_SUPERUSER_PASSWORD=realpgpass123/; s/^PILOT_ADMIN_PASSWORD=.*/PILOT_ADMIN_PASSWORD=realadmin123/; s/^PILOT_PG_PASSWORD=.*/PILOT_PG_PASSWORD=realpilot123/; s/^GRAFANA_ADMIN_PASSWORD=.*/GRAFANA_ADMIN_PASSWORD=realgraf123/' .env
out="$(bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "grep -q -e '-e RCU -e RCP ' \"\$STUB_LOG\" && grep -q -e '--entrypoint sh rustfs/rustfs:1.0.5 -c rc alias set m http://rustfs-prod:9000' \"\$STUB_LOG\" && grep -q 'rc mb --ignore-existing m/pilot-ice-ru' \"\$STUB_LOG\"" "спецсимволы: логин и пароль идут переменными окружения внутри sh -c, бакет создаёт rc"
ok "! grep -qF -e 'p@ss' -e 'w/o rd' \"\$STUB_LOG\"" "пароль со спецсимволами не попадает в аргументы docker"
echo 'RUSTFS_IMAGE=registry.local/rustfs:9' >> .env
bash install.sh --no-data --skip-load --roles prod >/dev/null 2>&1
ok "grep -q 'registry.local/rustfs:9 -c rc alias set m' \"\$STUB_LOG\"" "образ клиента берётся из RUSTFS_IMAGE"

echo "== RustFS: каталог данных и права не нужны"
fresh; mkdir -p "$T/nochown"
printf '#!/bin/sh\nexit 1\n' > "$T/nochown/chown"; chmod +x "$T/nochown/chown"
out="$(PATH="$T/nochown:$PATH" bash install.sh --no-data --skip-load --roles prod 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ ! -e /opt/ascon-docker/prod/rustfs ] && ! echo \"\$out\" | grep -qi 'uid 10001\|chown\|/rustfs'" "chown не вызывается (даже сломанный chown не мешает), каталога rustfs нет, в выводе нет слов про права и каталог"

echo "== восстановление из бэкапа: хранилище (файлы S3)"
rustfs_snap() { restore_setup; mkdir -p "$S/rustfs"; echo x > "$S/rustfs/rustfs_prod.tgz"; }
rustfs_pos() { grep -n -e "$1" "$STUB_LOG" | head -1 | cut -d: -f1; }
rustfs_snap
out="$(bash install.sh --roles prod --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && [ ! -e /opt/ascon-docker/prod/rustfs ]" "бэкап с rustfs/rustfs_<контур>.tgz: код 0, в DATA_ROOT ничего не копируется"
ok "grep -q 'docker run --rm --user 0 --entrypoint sh -v cicd_rustfs_prod:/v:ro rustfs/rustfs:1.0.5 -c ls -A /v' \"\$STUB_LOG\"" "содержимое тома cicd_rustfs_prod проверяется (ls -A) в образе RustFS"
ok "grep -qF -e '-v cicd_rustfs_prod:/v -v $T/work/data/rustfs:/b:ro rustfs/rustfs:1.0.5 -c tar tzf /b/rustfs_prod.tgz > /dev/null && tar xzf /b/rustfs_prod.tgz -C /v' \"\$STUB_LOG\"" "архив проверяется (tar tzf) и распаковывается в том cicd_rustfs_prod одной командой (непроверенный не распаковывается)"
tx="$(rustfs_pos 'tar xzf /b/rustfs_prod.tgz')"; cu="$(rustfs_pos 'remove-orphans')"; mb="$(rustfs_pos 'mb --ignore-existing')"; pg="$(rustfs_pos 'compose up -d postgres-prod')"
ok "[ -n \"\$tx\" ] && [ -n \"\$cu\" ] && [ -n \"\$mb\" ] && [ \"\$tx\" -lt \"\$cu\" ] && [ \"\$tx\" -lt \"\$mb\" ]" "распаковка - до compose up (старт pilot-server) и до создания бакета"
ok "[ -n \"\$pg\" ] && [ \"\$(rustfs_pos 'compose up --no-start')\" -lt \"\$tx\" ]" "том и контейнеры созданы заранее (compose up --no-start), затем распаковка"
ok "! echo \"\$out\" | grep -q 'restore.ps1\|после установки восстановите'" "предупреждения «после установки восстановите» нет"
ok "! grep -q 'docker stop rustfs-prod' \"\$STUB_LOG\"" "rustfs-prod не запущен: не останавливается"

rustfs_snap
out="$(RUSTFS_LS='.rustfs\n.rustfs.sys' bash install.sh --roles prod --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && grep -q 'tar xzf /b/rustfs_prod.tgz' \"\$STUB_LOG\"" "том только со служебными .rustfs и .rustfs.sys считается пустым: восстановление идёт"

rustfs_snap
out="$(RUSTFS_LS='.rustfs\nphotos' bash install.sh --roles prod --skip-load 2>&1)"; rc=$?
ok "[ $rc != 0 ] && echo \"\$out\" | grep -q 'cicd_rustfs_prod уже есть данные RustFS' && echo \"\$out\" | grep -q 'restore.ps1 -Env prod -Parts rustfs -ConfirmOverwrite rustfs'" "том с данными: отказ с понятным текстом и подсказкой про restore.ps1 -ConfirmOverwrite rustfs"
ok "! grep -q 'tar xzf' \"\$STUB_LOG\" && ! grep -q 'remove-orphans' \"\$STUB_LOG\"" "при отказе ничего не распаковано и стек не запущен"

rustfs_snap
out="$(RUSTFS_RUNNING=1 bash install.sh --roles prod --skip-load 2>&1)"; rc=$?
st="$(rustfs_pos 'docker stop rustfs-prod')"; tx="$(rustfs_pos 'tar xzf /b/rustfs_prod.tgz')"
ok "[ $rc = 0 ] && [ -n \"\$st\" ] && [ -n \"\$tx\" ] && [ \"\$st\" -lt \"\$tx\" ] && [ \"\$tx\" -lt \"\$(rustfs_pos 'remove-orphans')\" ]" "запущенный rustfs-prod останавливается перед записью; запускает его compose up после"

restore_setup
out="$(bash install.sh --roles prod --skip-load 2>&1)"; rc=$?
ok "[ $rc = 0 ] && ! grep -q 'tar xzf\|ls -A /v' \"\$STUB_LOG\" && ! echo \"\$out\" | grep -qi 'rustfs.*бэкап'" "бэкап без части rustfs: без ошибок, том не трогается (прежнее поведение)"
echo
if [ "$FAILED" = 0 ]; then echo "ВСЕ ПРОВЕРКИ ПРОЙДЕНЫ"; else echo "НЕ ПРОЙДЕНО: $FAILED"; exit 1; fi
