# -*- coding: utf-8 -*-
"""Генератор дашбордов Grafana по таблице metrics.module_events:
  client - «Pilot - клиентские модули» (события Pilot.ClientSdk из клиентов Pilot);
  server - «Pilot - серверные модули» (события ServerHost, client_type = 'pilot-server').

SQL панелей правится здесь, JSON пересобирается:
    python gen_dashboard_modules.py dashboard-pilot-modules.json client
    python gen_dashboard_modules.py dashboard-pilot-server-modules.json server
"""
import json
import sys

DS = {"type": "grafana-postgresql-datasource", "uid": "pilot-metrics-prod"}
T = "metrics.module_events"
MODE = sys.argv[2] if len(sys.argv) > 2 else "client"
SERVER = MODE == "server"
# Серверные события шлёт ServerHost с client_type = 'pilot-server', остальное - клиенты Pilot.
SRC = "client_type = 'pilot-server'" if SERVER else "client_type IS DISTINCT FROM 'pilot-server'"
F = f"$__timeFilter(ts) AND {SRC} AND env IN ($env) AND module_title IN ($module)"
# Подписи: на сервере действие - вызов активности автоматизации, «рабочее место» - сервер Pilot.
CALLS = "Вызовов" if SERVER else "Действий"
PLACES = "Серверов" if SERVER else "Рабочих мест"
A = F + " AND kind = 'action'"
panels = []


def add(p):
    p["id"] = len(panels) + 1
    p["datasource"] = DS
    panels.append(p)
    return p


def target(sql, fmt="table"):
    return [{"refId": "A", "datasource": DS, "editorMode": "code", "rawQuery": True, "format": fmt, "rawSql": sql.strip()}]


def thresholds(*steps):
    return {"mode": "absolute", "steps": [{"color": c, "value": v} for c, v in steps]}


def stat(title, sql, x, unit="short", color="blue", steps=None, desc=""):
    add({
        "type": "stat", "title": title, "description": desc,
        "gridPos": {"h": 4, "w": 4, "x": x, "y": 0},
        "targets": target(sql),
        "fieldConfig": {"defaults": {
            "unit": unit,
            "color": {"mode": "thresholds" if steps else "fixed", "fixedColor": color},
            "thresholds": thresholds(*(steps or [(color, None)])),
        }, "overrides": []},
        "options": {"reduceOptions": {"calcs": ["lastNotNull"], "fields": "", "values": False},
                    "colorMode": "value", "graphMode": "none", "textMode": "value"},
    })


TABLE = {"custom": {"align": "auto", "filterable": True}}

# ---------------------------------------------------------------- плитки
stat(CALLS, f"SELECT count(*) AS value FROM {T} WHERE {A}", 0,
     desc="Сколько раз сценарии автоматизаций вызывали активности серверных модулей." if SERVER
     else "Сколько раз пользователи вызывали действия модулей (ModuleStats.Run / Use).")
stat("Успешных", f"SELECT count(*) FILTER (WHERE is_success) AS value FROM {T} WHERE {A}", 4, color="green")
stat("Доля успешных",
     f"SELECT round(100.0 * count(*) FILTER (WHERE is_success) / NULLIF(count(*) FILTER (WHERE status <> 'cancelled'), 0), 1) AS value FROM {T} WHERE {A}",
     8, unit="percent", steps=[("red", None), ("orange", 90), ("green", 98)],
     desc="Успешные / все, кроме отменённых пользователем.")
stat("Ошибок", f"SELECT count(*) FILTER (WHERE status = 'error') AS value FROM {T} WHERE {A}", 12,
     steps=[("green", None), ("red", 1)], desc="Исключения в действиях модулей - это то, что надо чинить.")
if SERVER:
    stat("Модулей", f"SELECT count(DISTINCT module_title) AS value FROM {T} WHERE {F}", 16, color="purple",
         desc="Сколько разных серверных модулей загружалось или вызывалось за период.")
else:
    stat("Пользователей", f"SELECT count(DISTINCT login) AS value FROM {T} WHERE {A}", 16, color="purple",
         desc="Сколько разных людей пользовались модулями.")
stat(PLACES, f"SELECT count(DISTINCT machine) AS value FROM {T} WHERE {F} AND kind = 'load'", 20, color="blue",
     desc="Сколько серверов Pilot загрузили серверные модули (ServerHost)." if SERVER
     else "Сколько компьютеров запускали клиент с модулями (по событию загрузки).")

# ---------------------------------------------------------------- сводка
add({
    "type": "table", "title": "Сводка по модулям",
    "description": "Действия, ошибки и пользователи за период. Версии - какие версии модуля присылали события.",
    "gridPos": {"h": 11, "w": 24, "x": 0, "y": 4},
    "targets": target(f"""
SELECT
  module_title                                                            AS "Модуль",
  count(*) FILTER (WHERE kind = 'action')                                 AS "{CALLS}",
  count(*) FILTER (WHERE kind = 'action' AND is_success)                  AS "Успешно",
  count(*) FILTER (WHERE kind = 'action' AND status = 'error')            AS "Ошибок",
  count(*) FILTER (WHERE kind = 'action' AND status = 'cancelled')        AS "Отменено",
  round(100.0 * count(*) FILTER (WHERE kind = 'action' AND is_success)
        / NULLIF(count(*) FILTER (WHERE kind = 'action' AND status <> 'cancelled'), 0), 1) AS "Успешных, %",
  round(avg(duration_ms) FILTER (WHERE kind = 'action' AND is_success) / 1000.0, 2)       AS "Среднее время, с",
  sum(items) FILTER (WHERE kind = 'action')                               AS "Обработано объектов",
  count(DISTINCT login) FILTER (WHERE kind = 'action')                    AS "Пользователей",
  count(DISTINCT machine) FILTER (WHERE kind = 'load')                    AS "{PLACES}",
  string_agg(DISTINCT module_version, ', ')                               AS "Версии",
  max(ts) FILTER (WHERE kind = 'action')                                  AS "Последнее действие"
FROM {T}
WHERE {F}
GROUP BY module_title
ORDER BY "{CALLS}" DESC"""),
    "fieldConfig": {"defaults": TABLE, "overrides": [
        {"matcher": {"id": "byName", "options": "Ошибок"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-background", "mode": "gradient"}},
            {"id": "thresholds", "value": thresholds(("transparent", None), ("orange", 1), ("red", 10))}]},
        {"matcher": {"id": "byName", "options": "Успешных, %"}, "properties": [
            {"id": "unit", "value": "percent"},
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": thresholds(("red", None), ("orange", 90), ("green", 98))}]},
    ]},
    "options": {"footer": {"show": True, "reducer": ["sum"], "fields": [CALLS, "Успешно", "Ошибок", "Отменено"]},
                "sortBy": [{"displayName": CALLS, "desc": True}]},
})

# ---------------------------------------------------------------- графики
STATUS_COLORS = {"success": "green", "error": "red", "cancelled": "gray"}
BARS = {"drawStyle": "bars", "fillOpacity": 80, "lineWidth": 1, "showPoints": "never",
        "stacking": {"group": "A", "mode": "normal"}}
LEGEND = {"legend": {"calcs": ["sum"], "displayMode": "table", "placement": "bottom"},
          "tooltip": {"mode": "multi", "sort": "desc"}}

add({
    "type": "timeseries", "title": f"{CALLS} по статусам",
    "gridPos": {"h": 10, "w": 12, "x": 0, "y": 15},
    "targets": target(f"""
SELECT $__timeGroupAlias(ts, $__interval), status AS metric, count(*) AS value
FROM {T} WHERE {A}
GROUP BY 1, 2 ORDER BY 1""", "time_series"),
    "fieldConfig": {"defaults": {"custom": BARS, "min": 0, "unit": "short"},
                    "overrides": [{"matcher": {"id": "byName", "options": s},
                                   "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": c}}]}
                                  for s, c in STATUS_COLORS.items()]},
    "options": LEGEND,
})

add({
    "type": "timeseries", "title": f"{CALLS} по модулям (топ-10 за период)",
    "gridPos": {"h": 10, "w": 12, "x": 12, "y": 15},
    "targets": target(f"""
WITH top AS (
  SELECT module_title FROM {T} WHERE {A}
  GROUP BY module_title ORDER BY count(*) DESC LIMIT 10
)
SELECT $__timeGroupAlias(ts, $__interval), module_title AS metric, count(*) AS value
FROM {T}
WHERE {A} AND module_title IN (SELECT module_title FROM top)
GROUP BY 1, 2 ORDER BY 1""", "time_series"),
    "fieldConfig": {"defaults": {"custom": BARS, "min": 0, "unit": "short"}, "overrides": []},
    "options": LEGEND,
})

add({
    "type": "table", "title": "Активности серверных модулей" if SERVER else "Действия внутри модулей",
    "description": "Какие активности вызывают сценарии и насколько они надёжны." if SERVER else "Какие команды модулей используют и насколько они надёжны.",
    "gridPos": {"h": 10, "w": 12, "x": 0, "y": 25},
    "targets": target(f"""
SELECT
  module_title                                         AS "Модуль",
  action                                               AS "Действие",
  count(*)                                             AS "Раз",
  count(*) FILTER (WHERE status = 'error')             AS "Ошибок",
  round(avg(duration_ms) FILTER (WHERE is_success) / 1000.0, 2)                                    AS "Среднее, с",
  round((percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms))::numeric / 1000.0, 2)          AS "p95, с",
  count(DISTINCT login)                                AS "Пользователей"
FROM {T}
WHERE {A}
GROUP BY module_title, action
ORDER BY "Раз" DESC
LIMIT 300"""),
    "fieldConfig": {"defaults": TABLE, "overrides": [
        {"matcher": {"id": "byName", "options": "Ошибок"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": thresholds(("text", None), ("red", 1))}]},
    ]},
    "options": {"sortBy": [{"displayName": "Раз", "desc": True}]},
})

add({
    "type": "table", "title": "Версии модулей на серверах" if SERVER else "Версии модулей на рабочих местах",
    "description": "По событию загрузки модуля в ServerHost (старт сервера, установка, перезагрузка, откат). " if SERVER else "По событию загрузки (Pilot.ClientSdk отправляет его через минуту после старта клиента). "
                   "Последняя версия у каждого компьютера - проверка, что выкладка из админки дошла до пользователей.",
    "gridPos": {"h": 10, "w": 12, "x": 12, "y": 25},
    "targets": target(f"""
WITH latest AS (
  SELECT DISTINCT ON (module_title, machine, env) module_title, env, machine, module_version, ts
  FROM {T}
  WHERE {F} AND kind = 'load'
  ORDER BY module_title, machine, env, ts DESC
)
SELECT
  module_title          AS "Модуль",
  env                   AS "Контур",
  module_version        AS "Версия",
  count(*)              AS "{PLACES}",
  max(ts)               AS "Последний запуск"
FROM latest
GROUP BY module_title, env, module_version
ORDER BY "Модуль", "Контур", "Версия" DESC"""),
    "fieldConfig": {"defaults": TABLE, "overrides": []},
})

# ---------------------------------------------------------------- пользователи и ошибки
add({
    "type": "table", "title": "Вызовы по контурам и серверам",
    "gridPos": {"h": 10, "w": 12, "x": 0, "y": 35},
    "targets": target(f"""
SELECT
  env                                       AS "Контур",
  server                                    AS "Сервер",
  count(*)                                  AS "Вызовов",
  count(*) FILTER (WHERE status = 'error')  AS "Ошибок",
  count(DISTINCT module_title)              AS "Разных модулей",
  max(ts)                                   AS "Последний вызов"
FROM {T}
WHERE {A}
GROUP BY env, server
ORDER BY "Вызовов" DESC"""),
    "fieldConfig": {"defaults": TABLE, "overrides": []},
    "options": {"sortBy": [{"displayName": "Вызовов", "desc": True}]},
}) if SERVER else add({
    "type": "table", "title": "Кто пользуется модулями",
    "gridPos": {"h": 10, "w": 12, "x": 0, "y": 35},
    "targets": target(f"""
SELECT
  login                                     AS "Логин",
  max(person)                               AS "Пользователь",
  count(*)                                  AS "{CALLS}",
  count(*) FILTER (WHERE status = 'error')  AS "Ошибок",
  count(DISTINCT module_title)              AS "Разных модулей",
  max(ts)                                   AS "Последнее действие"
FROM {T}
WHERE {A}
GROUP BY login
ORDER BY "{CALLS}" DESC"""),
    "fieldConfig": {"defaults": TABLE, "overrides": []},
    "options": {"sortBy": [{"displayName": CALLS, "desc": True}]},
})

add({
    "type": "bargauge", "title": "Самые долгие действия (p95)",
    "gridPos": {"h": 10, "w": 12, "x": 12, "y": 35},
    "targets": target(f"""
SELECT
  module_title || ' · ' || action AS "Действие",
  round((percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms))::numeric / 1000.0, 2) AS "p95, с"
FROM {T}
WHERE {A} AND is_success AND duration_ms IS NOT NULL
GROUP BY module_title, action
ORDER BY 2 DESC
LIMIT 15"""),
    "fieldConfig": {"defaults": {"unit": "s", "min": 0, "color": {"mode": "continuous-GrYlRd"}}, "overrides": []},
    "options": {"orientation": "horizontal", "displayMode": "gradient", "showUnfilled": True,
                "reduceOptions": {"calcs": [], "fields": "", "values": True}},
})

add({
    "type": "table", "title": "Последние ошибки",
    "description": "Исключения активностей, перехваченные ServerHost." if SERVER else "Текст - сообщение исключения, пойманного ModuleStats.Run / Error.",
    "gridPos": {"h": 11, "w": 24, "x": 0, "y": 45},
    "targets": target(f"""
SELECT
  ts               AS "Время",
  module_title     AS "Модуль",
  module_version   AS "Версия",
  action           AS "Действие",
  error_type       AS "Тип ошибки",
  error_message    AS "Текст ошибки",
  login            AS "Логин",
  env              AS "Контур",
  {"server           AS \"Сервер\"" if SERVER else "client_version   AS \"Версия клиента\""}
FROM {T}
WHERE {A} AND status = 'error'
ORDER BY ts DESC
LIMIT 200"""),
    "fieldConfig": {"defaults": TABLE, "overrides": [
        {"matcher": {"id": "byName", "options": "Текст ошибки"}, "properties": [{"id": "custom.width", "value": 500}]},
    ]},
})

ALL = {"selected": True, "text": ["All"], "value": ["$__all"]}
dashboard = {
    "uid": "pilot-server-modules" if SERVER else "pilot-modules",
    "title": "Pilot - серверные модули" if SERVER else "Pilot - клиентские модули",
    "tags": ["pilot", "modules", "server" if SERVER else "client"],
    "timezone": "browser",
    "refresh": "5m",
    "schemaVersion": 39,
    "editable": True,
    "time": {"from": "now-7d", "to": "now"},
    "templating": {"list": [
        {"type": "query", "name": "env", "label": "Контур", "datasource": DS,
         "multi": True, "includeAll": True, "refresh": 1, "sort": 1, "current": ALL, "options": [],
         "definition": f"SELECT DISTINCT env FROM {T} WHERE {SRC} ORDER BY 1",
         "query": f"SELECT DISTINCT env FROM {T} WHERE {SRC} ORDER BY 1"},
        {"type": "query", "name": "module", "label": "Модуль", "datasource": DS,
         "multi": True, "includeAll": True, "refresh": 2, "sort": 1, "current": ALL, "options": [],
         "definition": f"SELECT DISTINCT module_title FROM {T} WHERE $__timeFilter(ts) AND {SRC} AND env IN ($env) ORDER BY 1",
         "query": f"SELECT DISTINCT module_title FROM {T} WHERE $__timeFilter(ts) AND {SRC} AND env IN ($env) ORDER BY 1"},
    ]},
    "panels": panels,
}

with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump(dashboard, f, ensure_ascii=False, indent=2)
print(len(panels), "panels")
