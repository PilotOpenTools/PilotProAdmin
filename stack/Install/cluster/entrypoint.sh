#!/usr/bin/env bash
set -euo pipefail

quote() {
    printf '"%s"' "${1//\"/\"\"}"
}

common="Host=$(quote "$CLUSTER_PG_HOST");Port=$CLUSTER_PG_PORT;Username=$(quote "$CLUSTER_PG_USER");Password=$(quote "$CLUSTER_PG_PASSWORD")"
config="$common;Database=$(quote "$CLUSTER_CONFIG_DATABASE")"
work=/cluster-work

case "${1:-}" in
    node)
        export PG_WAIT_HOST="$CLUSTER_PG_HOST" PG_WAIT_PORT="$CLUSTER_PG_PORT"
        exec /usr/bin/bash /wait-for-postgres.sh \
            "--pgConnectionString=$config;Pooling=true;Connection Lifetime=0;Keepalive=3" \
            --indexPath=/node-indexes --httpPort=5545
        ;;
    copy-seed)
        settings=/usr/share/ascon/pilot-server/settings/settings.xml
        if [[ ! -r "$settings" ]]; then
            echo 'Не найден исходный settings.xml.' >&2
            exit 1
        fi
        # The host reads these files through a private temporary directory.
        exec install -m 0644 -- "$settings" "$work/source.xml"
        ;;
    initialize)
        : "${CLUSTER_REDIS_CONNECTION:?Set CLUSTER_REDIS_CONNECTION}"
        : "${CLUSTER_PILOT_ADMIN:?Set CLUSTER_PILOT_ADMIN}"
        : "${CLUSTER_PILOT_PASSWORD:?Set CLUSTER_PILOT_PASSWORD}"
        exec /App/PostgresMigration "$work/bootstrap.xml" "$common;Database=postgres" \
            "$CLUSTER_REDIS_CONNECTION" "$CLUSTER_CONFIG_DATABASE" "$CLUSTER_PILOT_ADMIN" "$CLUSTER_PILOT_PASSWORD"
        ;;
    read-config)
        rm -f -- "$work/current.xml"
        /App/Ascon.Pilot.Daemon --readconfiguration "$config" "$work/current.xml"
        chmod 0644 -- "$work/current.xml"
        ;;
    write-config)
        for passfile in "$work/"*.pgpass; do
            [[ -f "$passfile" ]] || continue
            credentials=/usr/share/ascon/databases/.cluster-pgpass
            install -d -m 0700 -- "$credentials"
            install -m 0600 -- "$passfile" "$credentials/${passfile##*/}"
        done
        exec /App/Ascon.Pilot.Daemon --writeconfiguration "$config" "$work/updated.xml"
        ;;
    *)
        echo 'Неизвестная операция кластера.' >&2
        exit 2
        ;;
esac
