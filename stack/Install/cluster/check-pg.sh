#!/usr/bin/env bash
set -euo pipefail

case "${1:-}" in
    exists)
        psql -XqAt -w --set=ON_ERROR_STOP=1 --set=cluster_db="$CLUSTER_CONFIG_DATABASE" --dbname=postgres <<'SQL'
SELECT EXISTS (SELECT FROM pg_database WHERE datname = :'cluster_db');
SQL
        ;;
    ready)
        psql -XqAt -w --set=ON_ERROR_STOP=1 --dbname="$CLUSTER_CONFIG_DATABASE" \
            -c 'SELECT EXISTS (SELECT FROM config.settings WHERE id = 1);'
        ;;
    journal)
        export PGDATABASE="$2"
        exec psql -XqAt -w --set=ON_ERROR_STOP=1 --set=database_id="$3" \
            --set=expected_rows="$4" --file=/restore-journal.sql
        ;;
    *)
        exit 2
        ;;
esac
