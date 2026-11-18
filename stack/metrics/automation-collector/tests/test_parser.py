# -*- coding: utf-8 -*-
"""Тесты разбора automation.log. Запуск: python -m unittest discover -s tests (из каталога automation-collector)."""

import collections
import hashlib
import io
import os
import shutil
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, os.path.join(os.path.dirname(os.path.abspath(__file__)), '..'))

import automation_log_parser as p  # noqa: E402

SAMPLE = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'automation.sample.log')

STARTED = (u"2026-09-26 09:00:00,001 [7] INFO  [AutomationLog] "
           u"STARTED 'AttributeChanged' TRIGGER FROM 'Сценарий А' SCRIPT\n")
FAILED = (u"2026-09-26 09:00:00,002 [7] INFO  [AutomationLog] "
          u"Failed to evaluate 'type' parameter, expected='a', but was='b'\n")
ENDED = (u"2026-09-26 09:00:00,003 [7] INFO  [AutomationLog] "
         u"ENDED 'AttributeChanged' TRIGGER FROM 'Сценарий А' SCRIPT\n")
ERR_HEAD = (u"2026-09-26 09:00:01,000 [9] ERROR [AutomationLog] "
            u"'{\"when\":\"AttributeChanged\"}' cannot be parsed because of error\n")
ERR_TAIL = u"the type requires a JSON array (e.g. [1,2,3]) to deserialize correctly\n"
NEXT = (u"2026-09-26 09:00:02,000 [7] INFO  [AutomationLog] "
        u"STARTED 'AttributeChanged' TRIGGER FROM 'Сценарий Б' SCRIPT\n")


def keys(events, env='test'):
    return [p.event_key(env, e) for e in events]


class LogFile(object):
    def __init__(self, test):
        fd, self.path = tempfile.mkstemp(suffix='.log')
        os.close(fd)
        test.addCleanup(os.remove, self.path)

    def write(self, text, mode='ab'):
        with open(self.path, mode) as f:
            f.write(text.encode('utf-8'))


class SampleTests(unittest.TestCase):
    def test_sample_event_counts(self):
        events, end, ctx = p.read_events(SAMPLE, 0)
        # Файл кончается блоком ERROR: первый запуск его придерживает, второй записывает.
        self.assertEqual(len(events), 48)
        last, end, _ = p.read_events(SAMPLE, end, ctx)
        events += last
        kinds = collections.Counter(e['kind'] for e in events)
        self.assertEqual(kinds['started'], 22)
        self.assertEqual(kinds['ended'], 2)
        self.assertEqual(kinds['skipped'], 21)
        self.assertEqual(kinds['activity'], 2)
        self.assertEqual(kinds['activity_not_found'], 1)
        self.assertEqual(kinds['error'], 1)
        self.assertEqual(len(events), 49)
        self.assertEqual(end, os.path.getsize(SAMPLE))

    def test_sample_keys_are_unique(self):
        events, _, _ = p.read_events(SAMPLE, 0)
        self.assertEqual(len(set(keys(events))), len(events))

    def test_full_reread_gives_same_keys_as_incremental(self):
        # Читаем по кускам, как сборщик раз в интервал, затем целиком (--full): ключи совпадают, дублей нет.
        with open(SAMPLE, 'rb') as f:
            data = f.read()
        log = LogFile(self)
        incremental, start, context = [], 0, {}
        cut = len(data) // 3
        for upto in (cut, 2 * cut, len(data)):
            with open(log.path, 'wb') as f:
                f.write(data[:upto])
            events, start, context = p.read_events(log.path, start, context)
            incremental += events
        full, _, _ = p.read_events(SAMPLE, 0)
        self.assertEqual(sorted(keys(incremental)), sorted(keys(full)))


class BoundaryTests(unittest.TestCase):
    def test_partial_last_line_is_not_consumed(self):
        log = LogFile(self)
        log.write(STARTED + ENDED[:40], 'wb')
        events, end, ctx = p.read_events(log.path, 0)
        self.assertEqual([e['kind'] for e in events], ['started'])
        self.assertEqual(end, len(STARTED.encode('utf-8')))
        log.write(ENDED[40:])
        events2, end2, _ = p.read_events(log.path, end, ctx)
        self.assertEqual([e['kind'] for e in events2], ['ended'])
        self.assertEqual(end2, os.path.getsize(log.path))

    def test_context_survives_read_boundary(self):
        log = LogFile(self)
        log.write(STARTED, 'wb')
        _, end, ctx = p.read_events(log.path, 0)
        log.write(FAILED)
        events, _, _ = p.read_events(log.path, end, ctx)
        self.assertEqual(events[0]['kind'], 'skipped')
        self.assertEqual(events[0]['script'], u'Сценарий А')
        # без контекста сценарий не определился бы
        lost, _, _ = p.read_events(log.path, end)
        self.assertEqual(lost[0]['script'], p.UNKNOWN_SCRIPT)

    def test_error_block_split_between_reads_is_kept_whole(self):
        log = LogFile(self)
        log.write(STARTED + ERR_HEAD, 'wb')
        events, end, ctx = p.read_events(log.path, 0)
        self.assertEqual([e['kind'] for e in events], ['started'])   # ERROR ждёт продолжения
        log.write(ERR_TAIL + NEXT)
        events2, _, _ = p.read_events(log.path, end, ctx)
        self.assertEqual([e['kind'] for e in events2], ['error', 'started'])
        self.assertIn('JSON parse error', events2[0]['detail'])
        # тот же отпечаток и ключ, что при чтении файла целиком
        whole, _, _ = p.read_events(log.path, 0)
        self.assertEqual(sorted(keys(events + events2)), sorted(keys(whole)))
        self.assertEqual(events2[0]['fingerprint'], [e for e in whole if e['kind'] == 'error'][0]['fingerprint'])

    def test_trailing_error_is_written_on_second_run(self):
        log = LogFile(self)
        log.write(STARTED + ERR_HEAD + ERR_TAIL, 'wb')
        events, end, ctx = p.read_events(log.path, 0)
        self.assertEqual([e['kind'] for e in events], ['started'])
        events2, end2, _ = p.read_events(log.path, end, ctx)
        self.assertEqual([e['kind'] for e in events2], ['error'])
        self.assertEqual(end2, os.path.getsize(log.path))

    def test_bom_and_crlf(self):
        log = LogFile(self)
        log.write(u'﻿' + STARTED.replace(u'\n', u'\r\n') + ENDED.replace(u'\n', u'\r\n'), 'wb')
        events, end, _ = p.read_events(log.path, 0)
        self.assertEqual([e['kind'] for e in events], ['started', 'ended'])
        self.assertEqual(end, os.path.getsize(log.path))

    def test_empty_file(self):
        log = LogFile(self)
        log.write(u'', 'wb')
        self.assertEqual(p.read_events(log.path, 0), ([], 0, {}))

    def test_unreadable_log_fails_loudly(self):
        log = LogFile(self)
        log.write(STARTED, 'wb')
        argv = ['x', '--log', log.path, '--env', 'test']
        with mock.patch.object(sys, 'argv', argv), mock.patch.object(os, 'access', return_value=False),                 mock.patch.object(sys, 'stderr', io.StringIO()) as err:
            self.assertEqual(p.main(), 3)
        self.assertIn(u'Нет прав на чтение', err.getvalue())


class NodeLogs(object):
    """Каталог с подкаталогами узлов: <root>/<узел>/automation.log."""

    def __init__(self, test, **texts):
        self.root = tempfile.mkdtemp()
        test.addCleanup(shutil.rmtree, self.root)
        self.paths = {}
        for node, text in texts.items():
            os.mkdir(os.path.join(self.root, node))
            self.paths[node] = os.path.join(self.root, node, 'automation.log')
            with open(self.paths[node], 'wb') as f:
                f.write(text.encode('utf-8'))


class LogSpecTests(unittest.TestCase):
    def test_single_path_has_no_node(self):
        self.assertEqual(p.expand_log_spec(u'/App/logs/automation.log'), [(u'', u'/App/logs/automation.log')])

    def test_labeled_list(self):
        spec = u'node1=/logs/n1/automation.log, node2=/logs/n2/automation.log'
        self.assertEqual(p.expand_log_spec(spec),
                         [(u'node1', u'/logs/n1/automation.log'), (u'node2', u'/logs/n2/automation.log')])

    def test_mask_takes_node_from_parent_dir(self):
        logs = NodeLogs(self, node2=STARTED, node1=STARTED)
        targets = p.expand_log_spec(os.path.join(logs.root, '*', 'automation.log'))
        self.assertEqual(targets, [(u'node1', logs.paths['node1']), (u'node2', logs.paths['node2'])])

    def test_mask_mixed_with_plain_path(self):
        logs = NodeLogs(self, node1=STARTED)
        spec = u'main=/logs/automation.log,' + os.path.join(logs.root, '*', 'automation.log')
        self.assertEqual(p.expand_log_spec(spec), [(u'main', u'/logs/automation.log'), (u'node1', logs.paths['node1'])])

    def test_mask_without_matches_is_error(self):
        logs = NodeLogs(self)
        with self.assertRaises(p.LogSpecError):
            p.expand_log_spec(os.path.join(logs.root, '*', 'automation.log'))

    def test_empty_and_duplicate_nodes_are_errors(self):
        with self.assertRaises(p.LogSpecError):
            p.expand_log_spec(u' , ')
        with self.assertRaises(p.LogSpecError):   # два файла без меток дали бы одно состояние
            p.expand_log_spec(u'/a/automation.log,/b/automation.log')
        with self.assertRaises(p.LogSpecError):
            p.expand_log_spec(u'n=/a.log,n=/b.log')

    def test_usable_logs_skips_missing_but_reports_code(self):
        log = LogFile(self)
        log.write(STARTED, 'wb')
        with mock.patch.object(sys, 'stderr', io.StringIO()) as err:
            ok, rc = p.usable_logs([(u'a', log.path + '.missing'), (u'b', log.path)])
        self.assertEqual(ok, [(u'b', log.path)])
        self.assertEqual(rc, 2)
        self.assertIn(u'Файл не найден', err.getvalue())

    def test_usable_logs_unreadable_gives_code_3(self):
        log = LogFile(self)
        log.write(STARTED, 'wb')
        with mock.patch.object(os, 'access', return_value=False), mock.patch.object(sys, 'stderr', io.StringIO()):
            self.assertEqual(p.usable_logs([(u'', log.path)]), ([], 3))

    def test_main_fails_without_files(self):
        logs = NodeLogs(self)
        for spec, code in ((os.path.join(logs.root, 'none.log'), 2), (os.path.join(logs.root, '*', 'a.log'), 2)):
            argv = ['x', '--log', spec, '--env', 'test']
            with mock.patch.object(sys, 'argv', argv), mock.patch.object(sys, 'stderr', io.StringIO()):
                self.assertEqual(p.main(), code)


class NodeKeyTests(unittest.TestCase):
    def event(self):
        log = LogFile(self)
        log.write(STARTED, 'wb')
        return p.read_events(log.path, 0)[0][0]

    def test_empty_node_keeps_old_key(self):
        ev = self.event()
        old = hashlib.sha1(u'|'.join([
            'test', ev['ts'].isoformat(), ev['kind'], ev['script'],
            ev['trigger'] or u'', ev['detail'] or u'', str(ev['offset'])]).encode('utf-8')).hexdigest()
        self.assertEqual(p.event_key('test', ev), old)
        self.assertEqual(p.event_key('test', ev, u''), old)

    def test_same_event_on_two_nodes_gets_different_keys(self):
        ev = self.event()
        k1, k2 = p.event_key('test', ev, u'node1'), p.event_key('test', ev, u'node2')
        self.assertEqual(len({k1, k2, p.event_key('test', ev)}), 3)
        self.assertEqual(k1, p.event_key('test', ev, u'node1'))

    def test_identical_logs_of_two_nodes_do_not_collide(self):
        logs = NodeLogs(self, node1=STARTED + ENDED, node2=STARTED + ENDED)
        k = {}
        for node, path in logs.paths.items():
            k[node] = set(p.event_key('test', e, node) for e in p.read_events(path, 0)[0])
        self.assertEqual(len(k['node1']), 2)
        self.assertFalse(k['node1'] & k['node2'])

    def test_context_does_not_leak_between_read_events_calls(self):
        # поток [7] у обоих узлов, сценарии разные: без переданного контекста второй вызов не наследует сценарий первого
        other = STARTED.replace(u'Сценарий А', u'Сценарий В')
        logs = NodeLogs(self, node1=STARTED + FAILED, node2=other + FAILED)
        scripts = {}
        for node, path in logs.paths.items():
            first, end, ctx = p.read_events(path, 0)
            scripts[node] = [e['script'] for e in first if e['kind'] == 'skipped']
            self.assertEqual(end, os.path.getsize(path))
        self.assertEqual(scripts, {'node1': [u'Сценарий А'], 'node2': [u'Сценарий В']})


class FakeCursor(object):
    def __init__(self, row=None):
        self.row, self.calls = row, []

    def execute(self, sql, params=None):
        self.calls.append((sql, params))

    def fetchone(self):
        return self.row


class StateTests(unittest.TestCase):
    def test_state_is_keyed_by_env_and_node(self):
        cur = FakeCursor()
        self.assertEqual(p.read_state(cur, 'test', u'node1'), (0, {}))
        sql, params = cur.calls[0]
        self.assertIn('node = %s', sql)
        self.assertEqual(params, ('test', u'node1'))
        p.save_state(cur, 'test', u'node1', u'/l/automation.log', 42, {'7': ('t', 's')})
        sql, params = cur.calls[1]
        self.assertIn('ON CONFLICT (env, node)', sql)
        self.assertEqual(params[:4], ('test', u'node1', u'/l/automation.log', 42))

    def test_state_without_node_uses_empty_node(self):
        cur = FakeCursor(row=(15, u'{"7": ["AttributeChanged", "Сценарий А"]}'))
        self.assertEqual(p.read_state(cur, 'test'), (15, {'7': ('AttributeChanged', u'Сценарий А')}))
        self.assertEqual(cur.calls[0][1], ('test', u''))

    def test_schema_migration_is_idempotent_ddl(self):
        for fragment in ('PRIMARY KEY (env, node)', 'IF NOT EXISTS ('):
            self.assertIn(fragment, p.SCHEMA)

    def test_events_table_is_not_altered_when_column_exists(self):
        # ADD COLUMN IF NOT EXISTS блокирует таблицу и при готовой колонке: ALTER events только под проверкой колонки
        self.assertNotIn('ALTER TABLE metrics.automation_events ADD COLUMN IF NOT EXISTS', p.SCHEMA)
        head, alter = p.SCHEMA.split('ALTER TABLE metrics.automation_events ADD COLUMN node')
        self.assertIn("table_name = 'automation_events' AND column_name = 'node'", head.split('DO $$')[-1])


class MainNodeIsolationTests(unittest.TestCase):
    def test_vanished_file_does_not_stop_other_nodes(self):
        logs = NodeLogs(self, a=STARTED, b=STARTED)
        real_getsize = os.path.getsize

        def getsize(path):
            if path == logs.paths['a']:
                raise FileNotFoundError(path)   # исчез после usable_logs
            return real_getsize(path)

        spec = u'a=%s,b=%s' % (logs.paths['a'], logs.paths['b'])
        argv = ['x', '--log', spec, '--env', 'test', '--pg-password', 'x']
        cur = mock.MagicMock()
        cur.fetchone.return_value = None
        conn = mock.MagicMock()
        conn.cursor.return_value = cur
        fake_db = {'psycopg2': mock.MagicMock(), 'psycopg2.extras': mock.MagicMock()}
        with mock.patch.dict(sys.modules, fake_db), mock.patch.object(p, 'connect', return_value=conn),                 mock.patch.object(os.path, 'getsize', side_effect=getsize),                 mock.patch.object(sys, 'argv', argv), mock.patch.object(sys, 'stderr', io.StringIO()) as err,                 mock.patch.object(sys, 'stdout', io.StringIO()):
            self.assertEqual(p.main(), 3)
        saved = [c[0][1][1] for c in cur.execute.call_args_list if 'ON CONFLICT (env, node)' in c[0][0]]
        self.assertEqual(saved, [u'b'])
        self.assertIn(u'Не удалось прочитать', err.getvalue())


class RetentionTests(unittest.TestCase):
    def conn_with_batches(self, counts):
        cur = mock.MagicMock()
        type(cur).rowcount = mock.PropertyMock(side_effect=counts)
        conn = mock.MagicMock()
        conn.cursor.return_value = cur
        return conn, cur

    def test_purge_repeats_batches_until_short_one(self):
        conn, cur = self.conn_with_batches([100, 100, 30])
        self.assertEqual(p.purge_old_events(conn, 30, batch=100), 230)
        self.assertEqual(cur.execute.call_count, 3)
        self.assertEqual(conn.commit.call_count, 3)   # каждая пачка - своя транзакция
        sql, params = cur.execute.call_args[0]
        self.assertIn('metrics.automation_events', sql)
        self.assertNotIn('automation_log_state', sql)
        self.assertEqual(params, (30, 100))

    def test_purge_nothing_to_delete(self):
        conn, cur = self.conn_with_batches([0])
        self.assertEqual(p.purge_old_events(conn, 30, batch=100), 0)
        self.assertEqual(cur.execute.call_count, 1)

    def test_purge_failure_is_warning_not_exception(self):
        conn, cur = self.conn_with_batches([0])
        cur.execute.side_effect = RuntimeError(u'нет прав')
        with mock.patch.object(sys, 'stderr', io.StringIO()) as err:
            self.assertEqual(p.purge_old_events(conn, 30), 0)
        self.assertIn(u'очистка старых событий не удалась', err.getvalue())
        conn.rollback.assert_called_once()

    def test_parse_retention_days(self):
        self.assertEqual(p.parse_retention_days(None), 30)
        self.assertEqual(p.parse_retention_days(' '), 30)
        self.assertEqual(p.parse_retention_days('0'), 0)
        self.assertEqual(p.parse_retention_days('7'), 7)
        for bad in ('-1', 'abc'):
            with self.assertRaises(ValueError):
                p.parse_retention_days(bad)

    def test_events_table_has_time_index(self):
        self.assertIn('automation_events_ts_idx', p.SCHEMA)

    def run_main(self, retention, purge, log=None):
        logs = NodeLogs(self, a=STARTED)
        argv = ['x', '--log', log or logs.paths['a'], '--env', 'test', '--pg-password', 'x']
        if retention is not None:
            argv += ['--retention-days', retention]
        cur = mock.MagicMock()
        cur.fetchone.return_value = None
        conn = mock.MagicMock()
        conn.cursor.return_value = cur
        fake_db = {'psycopg2': mock.MagicMock(), 'psycopg2.extras': mock.MagicMock()}
        with mock.patch.dict(sys.modules, fake_db),                 mock.patch.object(p, 'connect', return_value=conn),                 mock.patch.object(p, 'purge_old_events', purge),                 mock.patch.object(sys, 'argv', argv),                 mock.patch.object(sys, 'stderr', io.StringIO()) as err,                 mock.patch.object(sys, 'stdout', io.StringIO()) as out:
            rc = p.main()
        return rc, err.getvalue(), out.getvalue()

    def test_main_purges_with_default_30_days(self):
        purge = mock.Mock(return_value=5)
        rc, _, out = self.run_main(None, purge)
        self.assertEqual(rc, 0)
        self.assertEqual(purge.call_args[0][1], 30)
        self.assertIn(u'удалено событий старше 30 дн.: 5', out)

    def test_main_purges_even_without_readable_log(self):
        purge = mock.Mock(return_value=0)
        rc, err, _ = self.run_main(None, purge, log=os.path.join(tempfile.gettempdir(), 'no-such-automation.log'))
        self.assertEqual(rc, 2)   # код «файл не найден» сохраняется
        self.assertEqual(purge.call_args[0][1], 30)

    def test_purge_survives_broken_connection_on_rollback(self):
        conn, cur = self.conn_with_batches([0])
        cur.execute.side_effect = RuntimeError(u'обрыв')
        conn.rollback.side_effect = RuntimeError(u'соединение закрыто')
        with mock.patch.object(sys, 'stderr', io.StringIO()) as err:
            self.assertEqual(p.purge_old_events(conn, 30), 0)
        self.assertIn(u'очистка старых событий не удалась', err.getvalue())

    def test_main_zero_days_keeps_data_and_warns(self):
        purge = mock.Mock()
        rc, err, _ = self.run_main('0', purge)
        self.assertEqual(rc, 0)
        purge.assert_not_called()
        self.assertIn(u'срок хранения не ограничен', err)

    def test_main_bad_days_is_config_error(self):
        purge = mock.Mock()
        rc, err, _ = self.run_main('-5', purge)
        self.assertEqual(rc, 2)
        purge.assert_not_called()
        self.assertIn('STATS_RETENTION_DAYS', err)


if __name__ == '__main__':
    unittest.main()
