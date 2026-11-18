# -*- coding: utf-8 -*-
"""Генератор дашборда Grafana «Pilot - отчёты».

SQL панелей правится здесь, JSON пересобирается:
    python gen_dashboard.py dashboard-pilot-reports.json
"""
import json
import sys

DS = {"type": "grafana-postgresql-datasource", "uid": "pilot-metrics-prod"}
T = "metrics.report_events"
F = "$__timeFilter(ts) AND env IN ($env) AND report IN ($report)"
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


# ---------------------------------------------------------------- плитки
stat("Построений", f"SELECT count(*) AS value FROM {T} WHERE {F}", 0,
     desc="Сколько раз запускали построение отчётов (Report.Begin).")
stat("Успешных", f"SELECT count(*) FILTER (WHERE is_success) AS value FROM {T} WHERE {F}", 4, color="green",
     desc="Отчёт построен до конца.")
stat("Доля успешных",
     f"SELECT round(100.0 * count(*) FILTER (WHERE is_success) / NULLIF(count(*) FILTER (WHERE status <> 'cancelled'), 0), 1) AS value FROM {T} WHERE {F}",
     8, unit="percent", steps=[("red", None), ("orange", 80), ("green", 95)],
     desc="Успешные / все, кроме отменённых пользователем.")
stat("Ошибок", f"SELECT count(*) FILTER (WHERE status = 'error') AS value FROM {T} WHERE {F}", 12,
     steps=[("green", None), ("red", 1)], desc="Исключения в скриптах отчётов - это то, что надо чинить.")
stat("Остановлено проверкой", f"SELECT count(*) FILTER (WHERE status = 'validation') AS value FROM {T} WHERE {F}", 16,
     color="orange",
     desc="Скрипт сам остановил построение через ReportException («Выберите проект» и т.п.). Не ошибка отчёта.")
stat("Пользователей", f"SELECT count(DISTINCT login) AS value FROM {T} WHERE {F}", 20, color="purple",
     desc="Сколько разных людей строили отчёты.")

# ---------------------------------------------------------------- сводка
add({
    "type": "table", "title": "Сводка по отчётам за выбранный период",
    "description": "Построений - все запуски. Доля успешных считается без отменённых пользователем. "
                   "Время - от DataSourceDemanded до построения последней страницы.",
    "gridPos": {"h": 11, "w": 24, "x": 0, "y": 4},
    "targets": target(f"""
SELECT
  report                                                                  AS "Отчёт",
  count(*)                                                                AS "Построений",
  count(*) FILTER (WHERE is_success)                                      AS "Успешно",
  count(*) FILTER (WHERE status = 'error')                                AS "Ошибок",
  count(*) FILTER (WHERE status = 'validation')                           AS "Проверка",
  count(*) FILTER (WHERE status = 'cancelled')                            AS "Отменено",
  round(100.0 * count(*) FILTER (WHERE is_success)
        / NULLIF(count(*) FILTER (WHERE status <> 'cancelled'), 0), 1)     AS "Успешных, %",
  round(avg(duration_ms) FILTER (WHERE is_success) / 1000.0, 1)           AS "Среднее время, с",
  round((percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms)
        FILTER (WHERE is_success))::numeric / 1000.0, 1)                  AS "p95, с",
  count(DISTINCT login)                                                   AS "Пользователей",
  max(ts)                                                                 AS "Последнее построение"
FROM {T}
WHERE {F}
GROUP BY report
ORDER BY "Построений" DESC"""),
    "fieldConfig": {"defaults": {"custom": {"align": "auto", "filterable": True}}, "overrides": [
        {"matcher": {"id": "byName", "options": "Ошибок"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-background", "mode": "gradient"}},
            {"id": "thresholds", "value": thresholds(("transparent", None), ("orange", 1), ("red", 10))}]},
        {"matcher": {"id": "byName", "options": "Успешных, %"}, "properties": [
            {"id": "unit", "value": "percent"},
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": thresholds(("red", None), ("orange", 80), ("green", 95))}]},
    ]},
    "options": {"footer": {"show": True, "reducer": ["sum"],
                           "fields": ["Построений", "Успешно", "Ошибок", "Проверка", "Отменено"]},
                "sortBy": [{"displayName": "Построений", "desc": True}]},
})

# ---------------------------------------------------------------- графики
STATUS_COLORS = {"success": "green", "error": "red", "validation": "orange", "cancelled": "gray"}
BARS = {"drawStyle": "bars", "fillOpacity": 80, "lineWidth": 1, "showPoints": "never",
        "stacking": {"group": "A", "mode": "normal"}}
LEGEND = {"legend": {"calcs": ["sum"], "displayMode": "table", "placement": "bottom"},
          "tooltip": {"mode": "multi", "sort": "desc"}}

add({
    "type": "timeseries", "title": "Построения по статусам",
    "description": "success - построен, error - исключение в скрипте, validation - остановлен ReportException, "
                   "cancelled - окно закрыли до конца построения.",
    "gridPos": {"h": 10, "w": 12, "x": 0, "y": 15},
    "targets": target(f"""
SELECT
  $__timeGroupAlias(ts, $__interval),
  status AS metric,
  count(*) AS value
FROM {T}
WHERE {F}
GROUP BY 1, 2
ORDER BY 1""", "time_series"),
    "fieldConfig": {"defaults": {"custom": BARS, "min": 0, "unit": "short"},
                    "overrides": [{"matcher": {"id": "byName", "options": s},
                                   "properties": [{"id": "color", "value": {"mode": "fixed", "fixedColor": c}}]}
                                  for s, c in STATUS_COLORS.items()]},
    "options": LEGEND,
})

add({
    "type": "timeseries", "title": "Построения по отчётам (топ-10 за период)",
    "gridPos": {"h": 10, "w": 12, "x": 12, "y": 15},
    "targets": target(f"""
WITH top AS (
  SELECT report FROM {T}
  WHERE {F}
  GROUP BY report ORDER BY count(*) DESC LIMIT 10
)
SELECT
  $__timeGroupAlias(ts, $__interval),
  report AS metric,
  count(*) AS value
FROM {T}
WHERE {F}
  AND report IN (SELECT report FROM top)
GROUP BY 1, 2
ORDER BY 1""", "time_series"),
    "fieldConfig": {"defaults": {"custom": BARS, "min": 0, "unit": "short"}, "overrides": []},
    "options": LEGEND,
})

# ---------------------------------------------------------------- сравнение периодов
add({
    "type": "table", "title": "Сравнение с предыдущим периодом (сдвиг: $offset)",
    "description": "Текущий период - выбранный на панели времени. Предыдущий - тот же интервал, сдвинутый назад на выбранную величину.",
    "gridPos": {"h": 10, "w": 24, "x": 0, "y": 25},
    "targets": target(f"""
WITH cur AS (
  SELECT report,
         count(*)                                  AS builds,
         count(*) FILTER (WHERE status = 'error')  AS errs,
         count(DISTINCT login)                     AS users
  FROM {T}
  WHERE ts >= $__timeFrom() AND ts < $__timeTo()
    AND env IN ($env) AND report IN ($report)
  GROUP BY report
),
prev AS (
  SELECT report,
         count(*)                                  AS builds,
         count(*) FILTER (WHERE status = 'error')  AS errs,
         count(DISTINCT login)                     AS users
  FROM {T}
  WHERE ts >= $__timeFrom()::timestamptz - INTERVAL '$offset'
    AND ts <  $__timeTo()::timestamptz   - INTERVAL '$offset'
    AND env IN ($env) AND report IN ($report)
  GROUP BY report
)
SELECT
  COALESCE(c.report, p.report)                  AS "Отчёт",
  COALESCE(c.builds, 0)                         AS "Построений сейчас",
  COALESCE(p.builds, 0)                         AS "Построений ранее",
  COALESCE(c.builds, 0) - COALESCE(p.builds, 0) AS "Δ построений",
  CASE WHEN COALESCE(p.builds, 0) = 0 THEN NULL
       ELSE round(100.0 * (COALESCE(c.builds, 0) - p.builds) / p.builds, 1)
  END                                           AS "Прирост, %",
  COALESCE(c.errs, 0)                           AS "Ошибок сейчас",
  COALESCE(p.errs, 0)                           AS "Ошибок ранее",
  COALESCE(c.errs, 0) - COALESCE(p.errs, 0)     AS "Δ ошибок",
  COALESCE(c.users, 0)                          AS "Пользователей сейчас",
  COALESCE(p.users, 0)                          AS "Пользователей ранее"
FROM cur c
FULL OUTER JOIN prev p ON p.report = c.report
ORDER BY "Построений сейчас" DESC"""),
    "fieldConfig": {"defaults": {"custom": {"align": "auto", "filterable": True}}, "overrides": [
        {"matcher": {"id": "byName", "options": "Δ ошибок"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": thresholds(("green", None), ("text", 0), ("red", 1))}]},
        {"matcher": {"id": "byName", "options": "Δ построений"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "thresholds", "value": thresholds(("orange", None), ("text", 0), ("green", 1))}]},
        {"matcher": {"id": "byName", "options": "Прирост, %"}, "properties": [{"id": "unit", "value": "percent"}]},
    ]},
    "options": {"sortBy": [{"displayName": "Построений сейчас", "desc": True}]},
})

# ---------------------------------------------------------------- пользователи и время
add({
    "type": "table", "title": "Кто строит отчёты",
    "gridPos": {"h": 10, "w": 12, "x": 0, "y": 35},
    "targets": target(f"""
SELECT
  login                                         AS "Логин",
  max(person)                                   AS "Пользователь",
  count(*)                                      AS "Построений",
  count(*) FILTER (WHERE is_success)            AS "Успешно",
  count(*) FILTER (WHERE status = 'error')      AS "Ошибок",
  count(DISTINCT report)                        AS "Разных отчётов",
  max(ts)                                       AS "Последнее построение"
FROM {T}
WHERE {F}
GROUP BY login
ORDER BY "Построений" DESC"""),
    "fieldConfig": {"defaults": {"custom": {"align": "auto", "filterable": True}}, "overrides": []},
    "options": {"sortBy": [{"displayName": "Построений", "desc": True}]},
})

add({
    "type": "bargauge", "title": "Самые долгие отчёты (p95 времени построения)",
    "description": "95-й перцентиль времени успешного построения.",
    "gridPos": {"h": 10, "w": 12, "x": 12, "y": 35},
    "targets": target(f"""
SELECT
  report AS "Отчёт",
  round((percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms))::numeric / 1000.0, 1) AS "p95, с"
FROM {T}
WHERE {F} AND is_success AND duration_ms IS NOT NULL
GROUP BY report
ORDER BY 2 DESC
LIMIT 15"""),
    "fieldConfig": {"defaults": {"unit": "s", "min": 0, "color": {"mode": "continuous-GrYlRd"}}, "overrides": []},
    "options": {"orientation": "horizontal", "displayMode": "gradient", "showUnfilled": True,
                "reduceOptions": {"calcs": [], "fields": "", "values": True}},
})

# ---------------------------------------------------------------- ошибки
add({
    "type": "table", "title": "Последние неудачные построения",
    "description": "Ошибки и остановки проверкой. Текст ошибки - сообщение исключения из скрипта отчёта.",
    "gridPos": {"h": 11, "w": 24, "x": 0, "y": 45},
    "targets": target(f"""
SELECT
  ts                AS "Время",
  report            AS "Отчёт",
  status            AS "Статус",
  error_type        AS "Тип ошибки",
  error_message     AS "Текст ошибки",
  login             AS "Логин",
  env               AS "Контур",
  module_version    AS "Версия модуля",
  client_version    AS "Версия клиента"
FROM {T}
WHERE {F} AND status IN ('error', 'validation')
ORDER BY ts DESC
LIMIT 200"""),
    "fieldConfig": {"defaults": {"custom": {"align": "auto", "filterable": True}}, "overrides": [
        {"matcher": {"id": "byName", "options": "Статус"}, "properties": [
            {"id": "custom.cellOptions", "value": {"type": "color-text"}},
            {"id": "mappings", "value": [{"type": "value", "options": {
                "error": {"color": "red", "index": 0}, "validation": {"color": "orange", "index": 1}}}]}]},
        {"matcher": {"id": "byName", "options": "Текст ошибки"}, "properties": [{"id": "custom.width", "value": 500}]},
    ]},
})

ALL = {"selected": True, "text": ["All"], "value": ["$__all"]}
dashboard = {
    "uid": "pilot-reports",
    "title": "Pilot - отчёты",
    "tags": ["pilot", "reports"],
    "timezone": "browser",
    "refresh": "5m",
    "schemaVersion": 39,
    "editable": True,
    "time": {"from": "now-7d", "to": "now"},
    "templating": {"list": [
        {"type": "query", "name": "env", "label": "Контур", "datasource": DS,
         "multi": True, "includeAll": True, "refresh": 1, "sort": 1, "current": ALL, "options": [],
         "definition": f"SELECT DISTINCT env FROM {T} ORDER BY 1",
         "query": f"SELECT DISTINCT env FROM {T} ORDER BY 1"},
        {"type": "query", "name": "report", "label": "Отчёт", "datasource": DS,
         "multi": True, "includeAll": True, "refresh": 2, "sort": 1, "current": ALL, "options": [],
         "definition": f"SELECT DISTINCT report FROM {T} WHERE $__timeFilter(ts) AND env IN ($env) ORDER BY 1",
         "query": f"SELECT DISTINCT report FROM {T} WHERE $__timeFilter(ts) AND env IN ($env) ORDER BY 1"},
        {"type": "custom", "name": "offset", "label": "Сравнить с периодом назад", "hide": 0,
         "query": "1 day, 7 days, 14 days, 30 days",
         "current": {"selected": True, "text": "7 days", "value": "7 days"},
         "options": [{"selected": v == "7 days", "text": v, "value": v} for v in ["1 day", "7 days", "14 days", "30 days"]]},
    ]},
    "panels": panels,
}

with open(sys.argv[1], "w", encoding="utf-8") as f:
    json.dump(dashboard, f, ensure_ascii=False, indent=2)
print(len(panels), "panels")
