namespace ProAdmin.Types;

/// <summary>Служебные файлы репозитория типов: CI-проверка, README, .gitattributes. Кладутся при инициализации.</summary>
public static class TypesCiTemplate
{
    public const string CiFile = ".gitlab-ci.yml";
    public const string ValidatorFile = "ci/validate.py";

    public const string GitAttributes = "* text=auto eol=lf\n*.json text eol=lf\n";

    public const string Ci = """
        # Проверка репозитория типов Pilot (ProAdmin). Запись на серверы делает админка после успешного pipeline:
        #   dev / тест, release / стейдж, main / прод.
        stages: [validate]

        validate:
          stage: validate
          image: python:3.12.16-alpine@sha256:c5ba3deef00c7758561a0f03c26f44f10589263d40e732b8748e0d1f38f6dd89
          script:
            - python3 ci/validate.py
          rules:
            - if: $CI_PIPELINE_SOURCE == "merge_request_event"
            - if: $CI_COMMIT_BRANCH && $CI_OPEN_MERGE_REQUESTS == null
            - if: $CI_COMMIT_BRANCH == "main" || $CI_COMMIT_BRANCH == "release" || $CI_COMMIT_BRANCH == "dev"

        """;

    /// <summary>Те же правила, что <see cref="TypesValidator"/>: дубли, ссылки на типы, состояния и группы.</summary>
    public const string Validator = """
        #!/usr/bin/env python3
        # Проверка репозитория типов Pilot: те же правила, что в админке (ProAdmin TypesValidator).
        import json, os, re, sys

        NO_STATE = "<Нет>"
        PLACEHOLDER = re.compile(r"\{\{(state|machine):([^{}]+)\}\}")
        errors, warnings = [], []

        def load(folder):
            items = []
            if not os.path.isdir(folder):
                return items
            for name in sorted(os.listdir(folder)):
                if not name.endswith(".json"):
                    continue
                path = os.path.join(folder, name)
                try:
                    with open(path, encoding="utf-8-sig") as f:
                        items.append((path, json.load(f)))
                except Exception as e:
                    errors.append(f"{path}: некорректный JSON - {e}")
            return items

        types, states, machines = {}, {}, {}
        orders = {}
        for path, d in load("types"):
            n = d.get("name")
            if not n: errors.append(f"{path}: не указано имя типа (name)")
            elif n in types: errors.append(f"{path}: тип «{n}» описан дважды")
            else: types[n] = d
            order = d.get("sort", 0)
            if type(order) is not int:
                errors.append(f"{path}: порядок типа должен быть целым числом")
            elif n:
                orders.setdefault(order, []).append(n)
        for order, names in orders.items():
            if len(names) > 1:
                errors.append(f"Порядок {order} занят несколькими типами: {', '.join(names)}")
        for path, d in load("states"):
            n = d.get("name")
            if not n: errors.append(f"{path}: не указано имя состояния (name)")
            elif n in states: errors.append(f"{path}: состояние «{n}» описано дважды")
            else: states[n] = d
        for path, d in load("state-machines"):
            t = d.get("title")
            if not t: errors.append(f"{path}: не указан заголовок группы (title)")
            elif t in machines: errors.append(f"{path}: группа «{t}» описана дважды")
            else: machines[t] = d

        live_states = {n for n, d in states.items() if not d.get("isDeleted")}
        live_machines = {t for t, d in machines.items() if not d.get("isDeleted")}

        def refs(where, cfg):
            for kind, name in PLACEHOLDER.findall(cfg or ""):
                if kind == "state" and name not in live_states:
                    errors.append(f"{where}: конфигурация ссылается на состояние «{name}», которого нет в states/")
                if kind == "machine" and name not in live_machines:
                    errors.append(f"{where}: конфигурация ссылается на группу «{name}», которой нет в state-machines/")

        for n, d in types.items():
            if d.get("isDeleted"):
                continue
            where = f"Тип «{n}»"
            m = d.get("stateMachine")
            if m and m not in live_machines:
                errors.append(f"{where}: группа состояний «{m}» не описана в state-machines/")
            for c in d.get("children") or []:
                if c not in types: errors.append(f"{where}: дочерний тип «{c}» не описан в types/")
            refs(where, d.get("configuration"))
            seen = set()
            for a in d.get("attributes") or []:
                an = a.get("name")
                if not an: errors.append(f"{where}: атрибут без имени")
                elif an in seen: errors.append(f"{where}: атрибут «{an}» описан дважды")
                seen.add(an)
                refs(f"{where}, атрибут «{an}»", a.get("configuration"))

        # Клиент Pilot обходит дочерние типы от doc_remarksFolder и запоминает только пользовательские типы
        # (не служебные, вид User, без ExcludeFromSearch="annotations"). Цикл из незапоминаемых типов
        # (например, системный Task_Folder, дочерний сам себе) роняет клиент со StackOverflow при открытии документа.
        REMARKS_ROOT = "doc_remarksFolder"
        EXCLUDE = re.compile(r"ExcludeFromSearch\s*=\s*[\"']([^\"']*)[\"']", re.I)

        def kids(n):
            return sorted({c for c in (types.get(n, {}).get("children") or []) if c in types})

        def remembered(d):
            if d.get("isService") or (d.get("kind") or "User") != "User":
                return False
            m = EXCLUDE.search(d.get("configuration") or "")
            return not (m and "annotations" in m.group(1).lower())

        def find_loop(start, allowed):
            prev, queue, seen = {}, [start], {start}
            while queue:
                cur = queue.pop(0)
                for c in kids(cur):
                    if c not in allowed: continue
                    if c == start:
                        path, x = [start], cur
                        while x != start:
                            path.insert(1, x); x = prev[x]
                        return path + [start]
                    if c not in seen:
                        seen.add(c); prev[c] = cur; queue.append(c)
            return None

        if REMARKS_ROOT in types:
            reach, queue = {REMARKS_ROOT}, [REMARKS_ROOT]
            while queue:
                for c in kids(queue.pop(0)):
                    if c not in reach: reach.add(c); queue.append(c)
            unmarked = {n for n in reach if not remembered(types[n])}
            looping = {n: p for n in unmarked if (p := find_loop(n, unmarked))}
            def loop_text(path):
                return ("клиент Pilot при открытии документа уходит в бесконечную рекурсию и падает (StackOverflow): "
                        f"тип «{path[0]}» служебный, системный или исключён из поиска по замечаниям и через дочерние ссылается сам на себя ({' → '.join(path)})")
            if REMARKS_ROOT in looping:
                errors.append(f"Тип «{REMARKS_ROOT}»: {loop_text(looping[REMARKS_ROOT])}")
            for p in sorted(reach):
                if p in looping: continue
                for c in kids(p):
                    if c in looping:
                        errors.append(f"Тип «{p}»: дочерний тип «{c}» нельзя добавлять - {loop_text(looping[c])}. Уберите «{c}» из дочерних «{p}»")

        for t, d in machines.items():
            where = f"Группа состояний «{t}»"
            if d.get("isDeleted"):
                users = [n for n, x in types.items() if not x.get("isDeleted") and x.get("stateMachine") == t]
                if users: errors.append(f"{where} удаляется, но её используют типы: {', '.join(users)}")
                continue
            for s in d.get("states") or []:
                for name in [s.get("state")] + [x.get("to") for x in s.get("transitions") or []]:
                    if name != NO_STATE and name not in live_states:
                        errors.append(f"{where}: состояние «{name}» не описано в states/")

        try:
            with open("config.json", encoding="utf-8-sig") as f:
                version = json.load(f).get("version")
            if not re.fullmatch(r"\d+\.\d+\.\d+", version or ""):
                errors.append(f"config.json: версия «{version}» не в формате X.Y.Z")
        except FileNotFoundError:
            errors.append("нет config.json")

        print(f"Типов: {len(types)}, состояний: {len(states)}, групп состояний: {len(machines)}")
        for w in warnings: print("ПРЕДУПРЕЖДЕНИЕ:", w)
        for e in errors: print("ОШИБКА:", e)
        sys.exit(1 if errors else 0)

        """;

    public static string Readme(string title) => $$$"""
        # {{{title}}}

        Типы, атрибуты, состояния и группы состояний базы Pilot как код. Ведётся через админку ProAdmin (вкладка «Типы»).

        ## Ветки и серверы

        | Ветка     | Сервер | Как меняется |
        |-----------|--------|--------------|
        | `dev`     | тест   | только здесь редактируются типы: редактор админки / ветка изменения `types/<задача>` / Merge Request (squash) / pipeline / запись на тест |
        | `release` | стейдж | перенос выбранных коммитов из `dev` (cherry-pick) / Merge Request / pipeline / запись на стейдж |
        | `main`    | прод   | перенос выбранных коммитов из `release` + версия в `config.json` и раздел `CHANGELOG.md` / MR / pipeline / запись на прод / тег `vX.Y.Z` |

        Ветки `dev`, `release`, `main` защищены: прямой push запрещён, только Merge Request. Перенос в `main` можно связать
        с релизом клиентского или серверного модуля - тогда типы записываются на прод в той же операции, что и модуль.

        ## Файлы

        - `types/<имя>.json` - тип и его атрибуты;
        - `states/<имя>.json` - состояние (`isSystem` - системное, только для ссылок);
        - `state-machines/<заголовок>.json` - группа состояний: переходы по исходному состоянию (`<Нет>` - без состояния);
        - `config.json` - версия конфигурации (меняется только при выпуске в `main`), `CHANGELOG.md` - журнал выпусков.

        Элементы сопоставляются с базой **по имени** (группы - по Id, затем по заголовку): Id на разных серверах разные.
        Ссылки на состояния и группы внутри конфигураций записываются как `{{state:имя}}` и `{{machine:заголовок}}`.

        ## Ограничения Pilot

        - Тип нельзя удалить совсем - только `"isDeleted": true`. Новый тип получает Id = максимальный + 1.
        - Удаление атрибута не стирает значения в объектах: они вернутся, если атрибут добавить снова.
          Переименование атрибута = удаление + добавление, значения **не переносятся**.
        - Смена типа данных атрибута сервером не проверяется - админка помечает её как опасную.
        - Элементы сервера, которых нет в репозитории, не трогаются.

        """;
}
