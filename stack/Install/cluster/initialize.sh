#!/usr/bin/env bash
set -euo pipefail

if [[ $# != 1 || ! -f "$1" ]]; then
    echo 'Использование: bash initialize.sh <node.env>' >&2
    exit 2
fi

kit=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
compose=(docker compose --env-file "$(realpath -- "$1")" -f "$kit/compose.yml")
exec 9<"$1"
flock -n 9 || { echo 'Инициализация с этим файлом уже запущена.' >&2; exit 1; }

existing=$("${compose[@]}" run --rm -T --no-deps check-pg exists)
if [[ "$existing" != f ]]; then
    echo 'Конфигурационная база уже существует или PostgreSQL вернул неожиданный ответ.' >&2
    exit 1
fi

"${compose[@]}" run --rm -T --no-deps initialize

ready=$("${compose[@]}" run --rm -T --no-deps check-pg ready)
if [[ "$ready" != t ]]; then
    echo 'В конфигурационной базе не найдена запись Pilot.' >&2
    exit 1
fi
echo 'Конфигурационная база Pilot создана.'
