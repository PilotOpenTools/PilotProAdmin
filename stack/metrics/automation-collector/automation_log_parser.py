#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Разбор automation.log Pilot-Server и загрузка событий в PostgreSQL для Grafana.

Читает лог инкрементально (запоминает смещение), поэтому можно запускать по расписанию.
Разбираются только полностью записанные строки: недописанный хвост и незавершённый
многострочный блок ERROR дочитываются при следующем запуске. Контекст потоков
(последняя строка STARTED) сохраняется между запусками.
Ротация: если файл стал меньше запомненного смещения, чтение начинается сначала
(новый файл, к следующему запуску уже длиннее смещения, не распознаётся).

Запуск:
    python automation_log_parser.py --log /logs/automation.log --env prod

--log принимает список через запятую: путь, маска (glob) или метка=путь. Метка узла кластера
пишется в колонку node, смещение и контекст потоков хранятся отдельно на каждый узел.
Для маски метка берётся из имени родительского каталога: /logs/*/automation.log -> node1, node2.
Один путь без маски и метки работает как раньше (node пустой).

Переменные окружения (или параметры командной строки):
    PG_HOST, PG_PORT, PG_DB, PG_USER, PG_PASSWORD
    STATS_RETENTION_DAYS - срок хранения событий в днях (30 по умолчанию, 0 - не удалять)
"""

import argparse
import glob
import hashlib
import json
import os
import re
import sys
from datetime import datetime

# --------------------------------------------------------------------------
# Разбор строк лога
# --------------------------------------------------------------------------

RE_HEAD = re.compile(
    r'^(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2},\d{3}) '
    r'\[(?P<thread>\d+)\] +(?P<level>[A-Z]+) +\[AutomationLog\] (?P<msg>.*)$')

RE_TRIG = re.compile(
    r"^(?P<kind>STARTED|ENDED) '(?P<trigger>[^']+)' TRIGGER FROM '(?P<script>[^']+)' SCRIPT")

RE_FAIL = re.compile(
    r"^Failed to evaluate '(?P<param>[^']+)' parameter, "
    r"expected='(?P<exp>[^']*)', but was='(?P<got>[^']*)'")

RE_NOTFOUND = re.compile(r"^Activity (?P<activity>\S+) from (?P<script>.+?) has not been found\.")

RE_ACT = re.compile(r"^Started activity - '(?P<activity>[^']+)'")

RE_WHEN = re.compile(r'"when"\s*:\s*"([^"]+)"')
RE_ACTIVITY_JSON = re.compile(r'"activity"\s*:\s*"([^"]+)"')

UNKNOWN_SCRIPT = u'<не определён: ошибка разбора JSON>'


class Parser(object):
    """Разбирает поток строк лога в события.

    Строки STARTED задают контекст потока (thread id), к которому относятся
    последующие 'Failed to evaluate' и 'Started activity' - сами по себе эти
    строки имени сценария не содержат.
    """

    def __init__(self, last_started=None):
        self.last_started = dict(last_started or {})   # thread -> (trigger, script)
        self.cur_err = None      # накопитель многострочного блока ERROR
        self.events = []
        self.offset = 0          # смещение в файле строки, которую разбирает feed_line

    def pending_offset(self):
        """Начало незавершённого блока ERROR (продолжение могло быть ещё не записано) или None."""
        return self.cur_err['offset'] if self.cur_err is not None else None

    def feed_line(self, line, offset=0):
        self.offset = offset
        line = line.rstrip(u'\r\n')
        m = RE_HEAD.match(line)

        if not m:
            # продолжение многострочного блока ERROR
            if self.cur_err is not None:
                self.cur_err['body'].append(line)
            return

        # заголовок новой записи закрывает предыдущий блок ERROR
        self._flush_error()

        ts = datetime.strptime(m.group('ts'), '%Y-%m-%d %H:%M:%S,%f')
        thread = m.group('thread')
        level = m.group('level')
        msg = m.group('msg')

        if level == 'ERROR':
            self.cur_err = dict(ts=ts, thread=thread, body=[msg], offset=offset)
            return

        mt = RE_TRIG.match(msg)
        if mt:
            trigger, script = mt.group('trigger'), mt.group('script')
            if mt.group('kind') == 'STARTED':
                self.last_started[thread] = (trigger, script)
                self._add(ts, 'started', script, trigger, None)
            else:
                self._add(ts, 'ended', script, trigger, None)
            return

        mf = RE_FAIL.match(msg)
        if mf:
            trigger, script = self.last_started.get(thread, (None, UNKNOWN_SCRIPT))
            detail = u"%s: expected='%s', was='%s'" % (
                mf.group('param'), mf.group('exp'), mf.group('got'))
            self._add(ts, 'skipped', script, trigger, detail)
            return

        mn = RE_NOTFOUND.match(msg)
        if mn:
            self._add(ts, 'activity_not_found', mn.group('script').strip(),
                      None, u'activity=%s' % mn.group('activity'))
            return

        ma = RE_ACT.match(msg)
        if ma:
            trigger, script = self.last_started.get(thread, (None, UNKNOWN_SCRIPT))
            self._add(ts, 'activity', script, trigger, ma.group('activity'))
            return

    def finish(self):
        self._flush_error()
        return self.events

    # ---- внутреннее -------------------------------------------------------

    def _flush_error(self):
        if self.cur_err is None:
            return
        e = self.cur_err
        self.cur_err = None
        body = u'\n'.join(e['body'])

        # Имени сценария в блоке ERROR нет. Группируем по триггеру ("when")
        # и отпечатку JSON - так одна и та же поломка считается как одна.
        mw = RE_WHEN.search(body)
        trigger = mw.group(1) if mw else None
        activities = u','.join(RE_ACTIVITY_JSON.findall(body))
        fingerprint = hashlib.md5(
            re.sub(r'\s+', '', body).encode('utf-8')).hexdigest()[:12]

        if 'cannot be parsed' in body:
            detail = u'JSON parse error; activities=%s; fp=%s' % (activities, fingerprint)
        else:
            detail = u'fp=%s; %s' % (fingerprint, body[:200])

        self._add(e['ts'], 'error', UNKNOWN_SCRIPT, trigger, detail,
                  fingerprint=fingerprint, offset=e['offset'])

    def _add(self, ts, kind, script, trigger, detail, fingerprint=None, offset=None):
        self.events.append(dict(
            ts=ts, kind=kind, script=script, trigger=trigger,
            detail=detail, fingerprint=fingerprint,
            offset=self.offset if offset is None else offset))


# --------------------------------------------------------------------------
# Список логов (кластер: по файлу на узел)
# --------------------------------------------------------------------------

RE_LABELED = re.compile(r'^(?P<label>[^=/\\*?\[\]]+)=(?P<path>.+)$')
RE_MASK = re.compile(r'[*?\[]')


class LogSpecError(ValueError):
    pass


def expand_log_spec(spec):
    """Разворачивает --log в список (узел, путь).

    Элемент списка через запятую: путь, маска (* ? [) или метка=путь. Для маски узел - имя
    родительского каталога файла. Одиночный путь без метки даёт узел '' (как до кластера).
    Маска без совпадений и повторяющиеся узлы - ошибка: иначе состояние двух файлов слилось бы.
    """
    targets = []
    for item in (x.strip() for x in spec.split(u',')):
        if not item:
            continue
        label = None
        m = RE_LABELED.match(item)
        if m:
            label, item = m.group('label').strip(), m.group('path').strip()
        if RE_MASK.search(item):
            found = sorted(f for f in glob.glob(item) if os.path.isfile(f))
            if not found:
                raise LogSpecError(u'Маска не нашла файлов: %s' % item)
            for f in found:
                node = label if label is not None else os.path.basename(os.path.dirname(os.path.abspath(f)))
                targets.append((node, f))
        else:
            targets.append((label or u'', item))
    if not targets:
        raise LogSpecError(u'Не указан ни один лог')
    nodes = [n for n, _ in targets]
    dup = sorted(set(n for n in nodes if nodes.count(n) > 1))
    if dup:
        raise LogSpecError(u'Узлы повторяются: %s. Задайте разные метки (метка=путь)'
                           % u', '.join(n or u'<без метки>' for n in dup))
    return targets


def usable_logs(targets):
    """(доступные (узел, путь), код возврата): недоступный файл не мешает остальным, но код ненулевой."""
    ok, rc = [], 0
    for node, path in targets:
        if not os.path.exists(path):
            sys.stderr.write(u'Файл не найден: %s\n' % path)
            rc = max(rc, 2)
        elif not os.access(path, os.R_OK):
            sys.stderr.write(u'Нет прав на чтение %s: контейнер работает от uid 10001, нужен доступ для «остальных» '
                             u'(chmod o+r) или user: root в compose\n' % path)
            rc = max(rc, 3)
        else:
            ok.append((node, path))
    return ok, rc


# --------------------------------------------------------------------------
# Работа с БД
# --------------------------------------------------------------------------

SCHEMA = u"""
SELECT pg_advisory_xact_lock(7301);   -- миграция ключа одна: два сборщика не должны делать её одновременно

CREATE SCHEMA IF NOT EXISTS metrics;

CREATE TABLE IF NOT EXISTS metrics.automation_events (
    id           bigserial PRIMARY KEY,
    env          text        NOT NULL,
    ts           timestamptz NOT NULL,
    kind         text        NOT NULL,
    script       text        NOT NULL,
    trigger_name text,
    detail       text,
    fingerprint  text,
    is_error     boolean     NOT NULL,
    event_key    text        NOT NULL
);

-- Метка узла кластера; '' - лог одного узла (прежний режим).
-- ADD COLUMN IF NOT EXISTS берёт ACCESS EXCLUSIVE даже при готовой колонке и встал бы в очередь за запросами Grafana.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'metrics' AND table_name = 'automation_events' AND column_name = 'node') THEN
        ALTER TABLE metrics.automation_events ADD COLUMN node text NOT NULL DEFAULT '';
    END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS automation_events_key_uq
    ON metrics.automation_events (event_key);
CREATE INDEX IF NOT EXISTS automation_events_ts_idx
    ON metrics.automation_events (ts);
CREATE INDEX IF NOT EXISTS automation_events_script_idx
    ON metrics.automation_events (env, script, kind);

CREATE TABLE IF NOT EXISTS metrics.automation_log_state (
    env        text        NOT NULL,
    node       text        NOT NULL DEFAULT '',
    log_path   text        NOT NULL,
    offset_pos bigint      NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (env, node)
);

-- Таблица от прежней версии: ключ был (env). Строки получают node = '' и остаются действующими.
ALTER TABLE metrics.automation_log_state ADD COLUMN IF NOT EXISTS node text NOT NULL DEFAULT '';
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint c
        JOIN pg_attribute a ON a.attrelid = c.conrelid AND a.attnum = ANY (c.conkey)
        WHERE c.conrelid = 'metrics.automation_log_state'::regclass AND c.contype = 'p' AND a.attname = 'node') THEN
        ALTER TABLE metrics.automation_log_state DROP CONSTRAINT automation_log_state_pkey;
        ALTER TABLE metrics.automation_log_state ADD PRIMARY KEY (env, node);
    END IF;
END $$;

-- Контекст потоков (последняя STARTED) на момент смещения - чтобы строки сразу после границы чтения не теряли сценарий.
ALTER TABLE metrics.automation_log_state ADD COLUMN IF NOT EXISTS context text;
"""

# Ошибками считаем реальные поломки: битый JSON сценария и ссылку на
# несуществующую активность. 'skipped' - это штатная фильтрация по условию.
ERROR_KINDS = ('error', 'activity_not_found')


def connect(args):
    import psycopg2   # здесь, чтобы разбор лога и тесты работали без драйвера БД
    return psycopg2.connect(
        host=args.pg_host, port=args.pg_port, dbname=args.pg_db,
        user=args.pg_user, password=args.pg_password)


DEFAULT_RETENTION_DAYS = 30
PURGE_BATCH = 5000


def parse_retention_days(value):
    """Срок хранения в днях: пусто - 30, 0 - не удалять, отрицательное или не число - ошибка."""
    if value is None or not str(value).strip():
        return DEFAULT_RETENTION_DAYS
    try:
        days = int(str(value).strip())
    except ValueError:
        days = -1
    if days < 0:
        raise ValueError(u'STATS_RETENTION_DAYS должно быть целым числом не меньше 0, получено "%s"' % value)
    return days


def purge_old_events(conn, days, batch=PURGE_BATCH):
    """Удаляет события старше days суток пачками (каждая пачка - своя короткая транзакция), по индексу ts.

    Возвращает число удалённых строк. Ошибка не прерывает работу: пишется предупреждение (разбор лога
    от очистки не зависит). Таблица состояния чтения не затрагивается.
    """
    total = 0
    try:
        cur = conn.cursor()
        while True:
            cur.execute(
                "DELETE FROM metrics.automation_events WHERE id IN ("
                "SELECT id FROM metrics.automation_events "
                "WHERE ts < now() - %s * interval '1 day' ORDER BY ts LIMIT %s FOR UPDATE SKIP LOCKED)", (days, batch))
            n = cur.rowcount
            conn.commit()
            total += n
            if n < batch:
                break
        cur.close()
    except Exception as e:
        try:
            conn.rollback()
        except Exception:
            pass   # соединение уже оборвано: предупреждение важнее
        sys.stderr.write(u'Предупреждение: очистка старых событий не удалась: %s\n' % e)
    return total


def purge_if_enabled(conn, retention_days):
    if retention_days:
        # Сборщик запускается раз в INTERVAL; запрос по индексу ts без устаревших строк дешёвый, поэтому чистим при каждом запуске.
        purged = purge_old_events(conn, retention_days)
        if purged:
            sys.stdout.write(u'очистка: удалено событий старше %d дн.: %d\n' % (retention_days, purged))
    else:
        sys.stderr.write(u'Предупреждение: STATS_RETENTION_DAYS=0 - срок хранения не ограничен, '
                         u'персональные данные в событиях не удаляются\n')


def read_state(cur, env, node=u''):
    """(смещение, контекст потоков) из прошлого запуска; для нового контура или узла (0, {})."""
    cur.execute("SELECT offset_pos, context FROM metrics.automation_log_state WHERE env = %s AND node = %s",
                (env, node))
    row = cur.fetchone()
    if not row:
        return 0, {}
    context = {}
    if row[1]:
        context = dict((t, tuple(v)) for t, v in json.loads(row[1]).items())
    return row[0], context


def save_state(cur, env, node, log_path, pos, context):
    cur.execute("""
        INSERT INTO metrics.automation_log_state (env, node, log_path, offset_pos, context, updated_at)
        VALUES (%s, %s, %s, %s, %s, now())
        ON CONFLICT (env, node) DO UPDATE
           SET log_path = EXCLUDED.log_path,
               offset_pos = EXCLUDED.offset_pos,
               context = EXCLUDED.context,
               updated_at = now()
    """, (env, node, log_path, pos, json.dumps(context)))


def event_key(env, ev, node=u''):
    """Уникальный ключ события - защита от повторной вставки.

    Узел подмешивается, только когда он задан: ключи событий без узла не меняются,
    а одинаковые строки двух узлов не схлопываются.

    Включает смещение строки в файле, а не номер события в пачке: при перечитывании лога
    с другого места (--full, сброс состояния) то же событие получает тот же ключ.
    """
    parts = [env, ev['ts'].isoformat(), ev['kind'], ev['script'],
             ev['trigger'] or u'', ev['detail'] or u'', str(ev['offset'])]
    if node:
        parts.append(node)
    raw = u'|'.join(parts)
    return hashlib.sha1(raw.encode('utf-8')).hexdigest()


def read_events(path, start, last_started=None):
    """Разбирает полностью записанные строки файла начиная со смещения start.

    Возвращает (события, новое смещение, контекст потоков). Новое смещение стоит перед
    недописанной последней строкой и перед незавершённым блоком ERROR: их разберёт следующий запуск.
    """
    parser = Parser(last_started)
    end = start
    with open(path, 'rb') as f:
        f.seek(start)
        offset = start
        for raw in f:
            if not raw.endswith(b'\n'):
                break   # строка ещё пишется
            line = raw.decode('utf-8', errors='replace')
            if offset == 0 and line.startswith(u'﻿'):
                line = line[1:]
            parser.feed_line(line, offset)
            offset += len(raw)
            end = offset
    pending = parser.pending_offset()
    # Блок ERROR в конце файла может быть недописан - оставляем его на следующий запуск.
    # Если он уже ждал один запуск (начинается со смещения start), записываем как есть:
    # иначе последняя ошибка в остановившемся логе не попала бы в БД никогда.
    if pending is not None and pending != start:
        end = pending
        parser.cur_err = None
    return parser.finish(), end, parser.last_started


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--log', required=True,
                    help='путь к automation.log; список через запятую из путей, масок (*) и меток метка=путь (узлы кластера)')
    ap.add_argument('--env', required=True, help='имя контура: prod / test / stage')
    ap.add_argument('--pg-host', default=os.getenv('PG_HOST', 'localhost'))
    ap.add_argument('--pg-port', default=os.getenv('PG_PORT', '5432'))
    ap.add_argument('--pg-db', default=os.getenv('PG_DB', 'pilot_metrics'))
    ap.add_argument('--pg-user', default=os.getenv('PG_USER', 'metrics_writer'))
    ap.add_argument('--pg-password', default=os.getenv('PG_PASSWORD', 'password'))
    ap.add_argument('--retention-days', default=os.getenv('STATS_RETENTION_DAYS'),
                    help='срок хранения событий, дней (по умолчанию 30; 0 - не удалять)')
    ap.add_argument('--full', action='store_true',
                    help='перечитать лог целиком, игнорируя сохранённое смещение')
    args = ap.parse_args()

    try:
        retention_days = parse_retention_days(args.retention_days)
    except ValueError as e:
        sys.stderr.write(u'%s\n' % e)
        return 2

    try:
        targets = expand_log_spec(args.log)
    except LogSpecError as e:
        sys.stderr.write(u'%s\n' % e)
        return 2
    targets, rc = usable_logs(targets)
    if not targets:
        # логов нет (автоматизации выключены, файл переименован) - сроки хранения всё равно действуют
        if retention_days:
            try:
                conn = connect(args)
            except Exception as e:
                sys.stderr.write(u'Предупреждение: очистка старых событий не удалась: %s\n' % e)
            else:
                purge_if_enabled(conn, retention_days)
                conn.close()
        return rc

    if args.pg_password == 'password':
        sys.stderr.write(u'Предупреждение: пароль БД - значение по умолчанию (PG_PASSWORD не задан)\n')

    import psycopg2.extras
    conn = connect(args)
    conn.autocommit = False
    cur = conn.cursor()
    cur.execute(SCHEMA)
    conn.commit()

    for node, path in targets:
        title = u'%s/%s' % (args.env, node) if node else args.env
        try:
            size = os.path.getsize(path)   # файл мог исчезнуть после usable_logs (ротация переименованием)
            start, context = (0, {}) if args.full else read_state(cur, args.env, node)

            # ротация: файл стал меньше - читаем с начала
            if start > size:
                sys.stderr.write(u'[%s] Лог короче сохранённого смещения - ротация, читаю с начала\n' % title)
                start, context = 0, {}

            events, end, context = read_events(path, start, context)
        except OSError as e:
            sys.stderr.write(u'[%s] Не удалось прочитать %s: %s\n' % (title, path, e))
            rc = max(rc, 3)
            continue

        rows = []
        for ev in events:
            rows.append((
                args.env, node, ev['ts'], ev['kind'], ev['script'], ev['trigger'],
                ev['detail'], ev['fingerprint'], ev['kind'] in ERROR_KINDS,
                event_key(args.env, ev, node)))

        inserted = []
        if rows:
            inserted = psycopg2.extras.execute_values(cur, """
                INSERT INTO metrics.automation_events
                    (env, node, ts, kind, script, trigger_name, detail, fingerprint,
                     is_error, event_key)
                VALUES %s
                ON CONFLICT (event_key) DO NOTHING
                RETURNING 1
            """, rows, page_size=500, fetch=True)

        save_state(cur, args.env, node, path, end, context)
        conn.commit()

        sys.stdout.write(
            u'[%s] прочитано байт: %d..%d, событий разобрано: %d, новых: %d\n'
            % (title, start, end, len(events), len(inserted)))

    cur.close()
    purge_if_enabled(conn, retention_days)
    conn.close()
    return rc


if __name__ == '__main__':
    sys.exit(main())
