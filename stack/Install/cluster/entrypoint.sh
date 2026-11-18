#!/usr/bin/env bash
set -euo pipefail

quote() {
    printf '"%s"' "${1//\"/\"\"}"
}

common="Host=$(quote "$CLUSTER_PG_HOST");Port=$CLUSTER_PG_PORT;Username=$(quote "$CLUSTER_PG_USER");Password=$(quote "$CLUSTER_PG_PASSWORD")"

case "${1:-}" in
    node)
        export PG_WAIT_HOST="$CLUSTER_PG_HOST" PG_WAIT_PORT="$CLUSTER_PG_PORT"
        exec /usr/bin/bash /wait-for-postgres.sh \
            "--pgConnectionString=$common;Database=$(quote "$CLUSTER_CONFIG_DATABASE");Pooling=true;Connection Lifetime=0;Keepalive=3" \
            --indexPath=/node-indexes --httpPort=5545
        ;;
    initialize)
        : "${CLUSTER_REDIS_CONNECTION:?Set CLUSTER_REDIS_CONNECTION}"
        : "${CLUSTER_PILOT_ADMIN:?Set CLUSTER_PILOT_ADMIN}"
        : "${CLUSTER_PILOT_PASSWORD:?Set CLUSTER_PILOT_PASSWORD}"
        settings=/usr/share/ascon/pilot-server/settings/settings.xml
        if [[ ! -r "$settings" ]]; then
            echo 'Не найден исходный settings.xml.' >&2
            exit 1
        fi
        exec /App/PostgresMigration "$settings" "$common;Database=postgres" \
            "$CLUSTER_REDIS_CONNECTION" "$CLUSTER_CONFIG_DATABASE" "$CLUSTER_PILOT_ADMIN" "$CLUSTER_PILOT_PASSWORD"
        ;;
    *)
        echo 'Неизвестная операция кластера.' >&2
        exit 2
        ;;
esac
