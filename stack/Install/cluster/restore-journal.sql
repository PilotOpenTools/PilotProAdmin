BEGIN;
SET LOCAL TIME ZONE 'UTC';
SET LOCAL lock_timeout = '10s';

SELECT nspname AS pilot_schema
FROM pg_namespace
WHERE nspname ~ '^dbp([0-9]+(\.[0-9]+)*)?$'
  AND to_regclass(format('%I.objects', nspname)) IS NOT NULL
ORDER BY string_to_array(substr(nspname, 4), '.')::integer[] DESC
LIMIT 1
\gset

SELECT count(*) = 1 AND bool_and(data = :'database_id'::uuid) AS same_database
FROM :"pilot_schema".databaseid
\gset
\if :same_database
\else
DO $$ BEGIN RAISE EXCEPTION 'Журнал SQLite принадлежит другой базе Pilot.'; END $$;
\endif

LOCK TABLE :"pilot_schema".user_actions IN SHARE ROW EXCLUSIVE MODE;
CREATE TEMP TABLE journal_import (LIKE :"pilot_schema".user_actions) ON COMMIT DROP;
ALTER TABLE journal_import ADD PRIMARY KEY (id);
\copy journal_import (id, person_id, ip, server_date_time, changeset_id, event_kind, object_id, object_type, object_title, additional_info, error_string, plugin_additional) FROM '/cluster-work/journal.tsv'

SELECT count(*) = :'expected_rows'::bigint AS complete FROM journal_import
\gset
\if :complete
\else
DO $$ BEGIN RAISE EXCEPTION 'Получены не все строки журнала SQLite.'; END $$;
\endif

SELECT EXISTS (
    SELECT 1 FROM journal_import AS incoming
    JOIN :"pilot_schema".user_actions AS saved USING (id)
    WHERE ROW(incoming.*) IS DISTINCT FROM ROW(saved.*)
) AS conflicting_id
\gset
\if :conflicting_id
DO $$ BEGIN RAISE EXCEPTION 'Номера событий совпадают с другими записями PostgreSQL.'; END $$;
\endif

INSERT INTO :"pilot_schema".user_actions OVERRIDING SYSTEM VALUE
SELECT * FROM journal_import
ON CONFLICT (id) DO NOTHING;

SELECT pg_get_serial_sequence(format('%I.user_actions', :'pilot_schema'), 'id') AS journal_sequence
\gset
SELECT format('SELECT last_value, is_called FROM %s', :'journal_sequence'::regclass) AS read_sequence \gset
:read_sequence \gset
SELECT format('ALTER SEQUENCE %s RESTART WITH %s',
    :'journal_sequence'::regclass,
    greatest(:'last_value'::bigint + CASE WHEN :'is_called'::boolean THEN 1 ELSE 0 END,
             coalesce(max(id), 0) + 1))
FROM :"pilot_schema".user_actions
\gexec

COMMIT;
