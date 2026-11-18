#!/usr/bin/env bash
set -euo pipefail

kit=$(cd "$(dirname "${BASH_SOURCE[0]}")/../../Install/cluster" && pwd)
temp=$(mktemp -d)
trap 'rm -rf -- "$temp"' EXIT
export CALLS="$temp/calls"
export PATH="$temp:$PATH"
touch "$temp/node.env"
cat > "$temp/docker" <<'SH'
#!/usr/bin/env bash
set -euo pipefail
step=${!#}
echo "$step" >> "$CALLS"
case "$step" in
    exists) echo "$EXISTING" ;;
    initialize) exit "$INIT_STATUS" ;;
    ready) echo "$READY" ;;
    *) exit 2 ;;
esac
SH
chmod +x "$temp/docker"

run() {
    export EXISTING="$1" INIT_STATUS="$2" READY="$3"
    : > "$CALLS"
    bash "$kit/initialize.sh" "$temp/node.env"
}

run f 0 t
test "$(paste -sd' ' "$CALLS")" = 'exists initialize ready'

if run t 0 t; then exit 1; fi
test "$(cat "$CALLS")" = exists

if run f 139 t; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists initialize'

if run f 0 f; then exit 1; fi
test "$(paste -sd' ' "$CALLS")" = 'exists initialize ready'

echo 'Проверки инициализации кластера прошли.'
