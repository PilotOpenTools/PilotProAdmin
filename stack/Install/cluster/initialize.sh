#!/usr/bin/env bash
set -euo pipefail

usage() {
    echo 'Использование: bash initialize.sh <node.env> [--restore-journal | --attach <имя-базы> <папка-с-settings.xml>]' >&2
    exit 2
}

if [[ $# != 1 && $# != 2 && $# != 4 ]] || [[ ! -f "$1" ]]; then
    usage
fi
if [[ $# == 2 && "$2" != --restore-journal ]]; then usage; fi
if [[ $# == 4 ]]; then
    [[ "$2" == --attach && -n "$3" && -d "$4" ]] || usage
    export CLUSTER_SEED_ROOT
    CLUSTER_SEED_ROOT=$(realpath -- "$4")
fi

kit=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
compose=(docker compose --env-file "$(realpath -- "$1")" -f "$kit/compose.yml")
exec 9<"$1"
flock -n 9 || { echo 'Инициализация с этим файлом уже запущена.' >&2; exit 1; }

existing=$("${compose[@]}" run --rm -T --no-deps check-pg exists)
if [[ $# != 1 ]]; then
    if [[ "$existing" != t ]]; then
        echo 'Конфигурационная база Pilot не найдена.' >&2
        exit 1
    fi
elif [[ "$existing" != f ]]; then
    echo 'Конфигурационная база уже существует или PostgreSQL вернул неожиданный ответ.' >&2
    exit 1
fi

command -v python3 >/dev/null || { echo 'Для подготовки settings.xml нужен python3.' >&2; exit 1; }
umask 077
work=$(mktemp -d)
trap 'rm -f -- "$work/"*.xml "$work/"*.pgpass "$work/journal.tsv"; rmdir -- "$work"' EXIT

tool() {
    "${compose[@]}" run --rm -T --no-deps --volume "$work:/cluster-work" initialize "$@" </dev/null
}

settings() {
    python3 -B "$kit/cluster-settings.py" "$work" "$@"
}

write_configuration() {
    tool write-config
    tool read-config
    settings verify
}

tool copy-seed
if [[ $# == 2 ]]; then
    settings journal required "${compose[@]}"
    exit 0
fi
if [[ $# == 4 ]]; then
    tool read-config
    settings attach "$3"
    write_configuration
    echo 'База PostgreSQL добавлена в конфигурацию Pilot. Запустите узлы и проверьте вход в базу.'
    exit 0
fi

mode=$(settings bootstrap)
tool initialize

ready=$("${compose[@]}" run --rm -T --no-deps check-pg ready)
if [[ "$ready" != t ]]; then
    echo 'В конфигурационной базе не найдена запись Pilot.' >&2
    exit 1
fi
if [[ "$mode" == postgres ]]; then
    tool read-config
    settings merge
    write_configuration
fi
settings journal optional "${compose[@]}"
echo 'Конфигурационная база Pilot создана.'
