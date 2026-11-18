#!/usr/bin/env bash
set -euo pipefail

kit=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../Install/cluster" && pwd)
temp=$(mktemp -d)
trap 'rm -rf -- "$temp"' EXIT
export CALLS="$temp/calls" SOURCE="$temp/source.xml" CURRENT="$temp/current.xml" SAVED="$temp/saved.xml"
export PASSWORD_FILE="$temp/password.pgpass"
export JOURNAL_TSV="$temp/pasted.tsv" TEST_ROOT="$temp"
export PATH="$temp:$PATH"
touch "$temp/node.env"
echo '<Configuration><Databases /></Configuration>' > "$SOURCE"
cat > "$CURRENT" <<'XML'
<Configuration><Databases>
  <DatabaseParameters Name="pilot-prod" Id="prod-id" Filename="">
    <PgConnectionString ConnectionString="Host=prod;Database=pilot;Username=pilot" EncryptedPassword="example" />
  </DatabaseParameters>
</Databases><RedisConnectionString ConnectionString="redis:6379" /></Configuration>
XML
cat > "$temp/docker" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
step=${!#}
for arg in "$@"; do
    if [[ "$arg" == journal ]]; then step=journal; fi
done
if [[ "$step" == json ]]; then step=config; fi
echo "$step" >> "$CALLS"
for arg in "$@"; do
    if [[ "$arg" == *:/cluster-work* ]]; then work=${arg%%:/cluster-work*}; fi
done
case "$step" in
    exists) echo "$EXISTING" ;;
    copy-seed) cp "$SOURCE" "$work/source.xml" ;;
    config) printf '{"services":{"initialize":{"volumes":[{"type":"bind","source":"%s","target":"/usr/share/ascon/databases"}]}}}\n' "$TEST_ROOT" ;;
    journal)
        test "${*: -3}" = 'pilot-legacy 00112233-4455-6677-8899-aabbccddeeff 1'
        cp "$work/journal.tsv" "$JOURNAL_TSV"
        ;;
    initialize)
        if grep -q 'PgConnectionString' "$work/bootstrap.xml"; then exit 4; fi
        exit "$INIT_STATUS"
        ;;
    ready) echo "$READY" ;;
    read-config)
        if [[ -f "$SAVED" ]]; then cp "$SAVED" "$work/current.xml"; else cp "$CURRENT" "$work/current.xml"; fi
        ;;
    write-config)
        cp "$work/updated.xml" "$SAVED"
        for path in "$work/"*.pgpass; do
            [[ ! -f "$path" ]] || cp -p "$path" "$PASSWORD_FILE"
        done
        ;;
    *) exit 2 ;;
esac
SH
chmod +x "$temp/docker"

run() {
    export EXISTING="$1" INIT_STATUS="$2" READY="$3"
    shift 3
    : > "$CALLS"
    rm -f -- "$SAVED" "$PASSWORD_FILE"
    bash "$kit/initialize.sh" "$temp/node.env" "$@"
}

run f 0 t
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed initialize ready'

if run t 0 t; then exit 1; fi
test "$(cat "$CALLS")" = exists

if run f 139 t; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed initialize'

if run f 0 f; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed initialize ready'

cat > "$SOURCE" <<'XML'
<Configuration><Databases>
  <DatabaseParameters Name="pilot-dev" Id="dev-id" Filename="/old/file.dbp">
    <PgConnectionString ConnectionString='Host=test;Database=existing;Username=pilot;Password="a;b:c\d""e"' />
    <IndexPath>/old/index</IndexPath>
  </DatabaseParameters>
</Databases></Configuration>
XML
run f 0 t
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed initialize ready read-config write-config read-config'
grep -q 'Name="pilot-dev"' "$SAVED"
grep -q 'Database=existing;Username=pilot;Passfile=' "$SAVED"
! grep -qE 'Password="a|/old/' "$SAVED"
test "$(cat "$PASSWORD_FILE")" = '*:*:*:*:a;b\:c\\d"e'
test "$(stat -c '%a' "$PASSWORD_FILE")" = '600'

run t 0 t --attach pilot-dev "$temp"
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed read-config write-config read-config'
grep -q 'Name="pilot-prod"' "$SAVED"
grep -q 'Name="pilot-dev"' "$SAVED"

if run t 0 t --attach missing "$temp"; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed read-config'
sed -i 's/Id="dev-id"/Id="prod-id"/' "$SOURCE"
if run t 0 t --attach pilot-dev "$temp"; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed read-config'

cat > "$SOURCE" <<'XML'
<Configuration><Databases>
  <DatabaseParameters Name="pilot-legacy" Filename="/usr/share/ascon/databases/base.dbp" />
</Databases></Configuration>
XML
python3 - "$temp" <<'PY'
import pathlib
import sqlite3
import sys
from uuid import UUID

root = pathlib.Path(sys.argv[1])
identity = UUID("00112233-4455-6677-8899-aabbccddeeff").bytes_le
for name in ("base.dbp", "base.journal.dbp"):
    with sqlite3.connect(root / name) as db:
        db.execute("CREATE TABLE databaseid (data BLOB)")
        db.execute("INSERT INTO databaseid VALUES (?)", (identity,))
        if name.endswith(".journal.dbp"):
            db.execute("CREATE TABLE user_actions (id INTEGER PRIMARY KEY, person_id INTEGER, ip TEXT, "
                       "server_date_time TEXT, changeset_id BLOB, event_kind INTEGER, object_id BLOB, "
                       "object_type INTEGER, object_title TEXT, additional_info BLOB, error_string TEXT, plugin_additional BLOB)")
            db.execute("INSERT INTO user_actions VALUES (1,2,'::1','2026-01-01T00:00:00Z',?,3,?,4,?,?,NULL,NULL)",
                       (identity, identity, "title\twith\nbreak", b"\x01"))
PY

run f 0 t
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed initialize ready config journal'
python3 - "$JOURNAL_TSV" <<'PY'
from pathlib import Path
import sys
values = Path(sys.argv[1]).read_text().strip("\n").split("\t")
assert values[8:] == [r"title\twith\nbreak", r"\\x01", r"\N", r"\N"]
PY
run t 0 t --restore-journal
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed config journal'
printf bad > "$temp/base.journal.dbp-wal"
if run t 0 t --restore-journal; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists copy-seed config'
echo 'Проверки инициализации кластера прошли.'
