#!/usr/bin/env python3
"""Проверка изменённых автоматизаций Pilot в GitLab CI.

Правила (из «Инструкции по работе с автоматизациями Pilot-BIM»):
  * AutomationName.json - корректный JSON-массив триггеров, у каждого есть "when";
    «Version: X.Y.Z» - в поле _comment_ первого триггера;
  * при изменении JSON версия должна быть больше, чем в базовом коммите;
  * C#-вставка лежит в AutomationName/AutomationName.cs и имеет парный JSON;
  * при изменении C# версия в JSON тоже повышается;
  * JSON и C# вне этой раскладки (JSON в подпапке, C# не в AutomationName/AutomationName.cs) сервер не читает -
    это ошибка; скрытые папки (.vscode и т. п.) не проверяются;
  * в C# ищется объявление действия, как в веб-админке (предупреждение, не ошибка);
  * в одном изменении - только одна автоматизация (SINGLE_AUTOMATION=1).
"""
import json
import os
import re
import subprocess
import sys

VERSION_RE = re.compile(r"Version\s*:\s*v?(\d+)\.(\d+)\.(\d+)", re.IGNORECASE)
ZERO_SHA = "0" * 40
# То же, что MetadataIndex.ActivityName в веб-админке: public string Name => "X"; или get { return "X"; }.
ACTIVITY_RE = re.compile(r'\bName\s*(?:=>|\{\s*get\s*(?:=>|\{\s*return))\s*"([^"]+)"')


def git(*args, check=True):
    result = subprocess.run(["git", *args], capture_output=True, text=True, encoding="utf-8")
    if check and result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {result.stderr.strip()}")
    return result


def ref_exists(ref):
    return git("rev-parse", "--verify", "-q", ref, check=False).returncode == 0


def branch_base():
    """Ближайшая точка ответвления от dev или main: новая ветка проверяется целиком, а не по последнему коммиту."""
    best = None
    for name in ("dev", "main"):
        if name == os.environ.get("CI_COMMIT_BRANCH", ""):
            continue
        ref = f"origin/{name}"
        if not ref_exists(ref):
            git("fetch", "--quiet", "origin", f"{name}:refs/remotes/{ref}", check=False)
        if not ref_exists(ref):
            continue
        mb = git("merge-base", ref, "HEAD", check=False).stdout.strip()
        if not mb:
            continue
        count = int(git("rev-list", "--count", f"{mb}..HEAD").stdout.strip())
        if best is None or count < best[0]:
            best = (count, mb)
    return best[1] if best else None


def base_commit():
    for var in ("CI_MERGE_REQUEST_DIFF_BASE_SHA", "CI_COMMIT_BEFORE_SHA"):
        sha = os.environ.get(var, "")
        if sha and sha != ZERO_SHA and git("cat-file", "-e", f"{sha}^{{commit}}", check=False).returncode == 0:
            return sha
    return branch_base()


def strip_comments(text):
    """Убирает // и /* */ комментарии вне строк (Pilot допускает закомментированные блоки)."""
    out, i, n, in_str = [], 0, len(text), False
    while i < n:
        c = text[i]
        if in_str:
            out.append(c)
            if c == "\\" and i + 1 < n:
                out.append(text[i + 1])
                i += 1
            elif c == '"':
                in_str = False
        elif c == '"':
            in_str = True
            out.append(c)
        elif text.startswith("//", i):
            while i < n and text[i] != "\n":
                i += 1
            continue
        elif text.startswith("/*", i):
            end = text.find("*/", i + 2)
            i = n if end < 0 else end + 2
            continue
        else:
            out.append(c)
        i += 1
    return re.sub(r",(\s*[\]}])", r"\1", "".join(out))


def version_of(text):
    m = VERSION_RE.search(text or "")
    return tuple(int(x) for x in m.groups()) if m else None


def comment_version(data):
    """Версия - только в _comment_ первого триггера: «Version:» в других местах файла не считается."""
    first = data[0] if isinstance(data, list) and data else None
    comment = first.get("_comment_") if isinstance(first, dict) else None
    return version_of(comment) if isinstance(comment, str) else None


def version_in_text(text):
    if text is None:
        return None
    try:
        return comment_version(json.loads(strip_comments(text)))
    except ValueError:
        return None


def layout_error(data):
    """Клиент Pilot считает каждый элемент массива триггером: элемент без "when" роняет его
    (ArgumentNullException в AutomationEventHandler.RebuildSnapshot)."""
    if not isinstance(data, list):
        return "сценарий должен быть массивом [ { \"when\": … } ]"
    for i, item in enumerate(data, 1):
        if not isinstance(item, dict) or not item.get("when"):
            return (f"элемент {i} без \"when\" роняет клиент Pilot - версию храните полем "
                    f"\"_comment_\" внутри первого триггера")
    return None


def read_text(path):
    """Текст файла; не UTF-8 - ошибка проверки, а не traceback."""
    try:
        with open(path, encoding="utf-8-sig") as f:
            return f.read(), None
    except UnicodeDecodeError as e:
        return None, f"файл не в UTF-8 - {e.reason} (позиция {e.start})"


def show(ref, path):
    r = git("show", f"{ref}:{path}", check=False)
    return r.stdout if r.returncode == 0 else None


def fmt(version):
    return ".".join(map(str, version))


def target_branch():
    return os.environ.get("CI_MERGE_REQUEST_TARGET_BRANCH_NAME") or os.environ.get("CI_COMMIT_BRANCH", "")


def show_main(path):
    """Файл из ветки main (подтягивается при необходимости)."""
    if git("rev-parse", "--verify", "-q", "origin/main", check=False).returncode != 0:
        git("fetch", "--quiet", "origin", "main:refs/remotes/origin/main", check=False)
    return show("origin/main", path)


def main():
    folder = os.environ.get("AUTOMATION_DIR", "").strip().strip("/")
    prefix = folder + "/" if folder else ""
    single = os.environ.get("SINGLE_AUTOMATION", "1") == "1"

    base = base_commit()
    if base:
        diff = git("-c", "core.quotepath=off", "diff", "--name-status", "--no-renames", base, "HEAD").stdout.splitlines()
        changes = [(line.split("\t", 1)[0][0], line.split("\t", 1)[1]) for line in diff if "\t" in line]
        print(f"Сравнение с {base[:8]}: изменено файлов - {len(changes)}")
    else:
        files = git("-c", "core.quotepath=off", "ls-files").stdout.splitlines()
        changes = [("A", f) for f in files]
        print("Базового коммита нет - проверяются все файлы.")

    errors, names, json_changed, cs_changed = [], set(), set(), set()
    for status, path in changes:
        if not path.startswith(prefix):
            continue
        rel = path[len(prefix):]
        parts = rel.split("/")

        if len(parts) == 1 and rel.endswith(".json"):
            name = rel[:-5]
            names.add(name)
            json_changed.add(name)
            if status == "D":
                print(f"  - {path}: удалён")
                continue
            text, bad = read_text(path)
            if bad:
                errors.append(f"{path}: {bad}")
                continue
            try:
                data = json.loads(strip_comments(text))
            except ValueError as e:
                errors.append(f"{path}: некорректный JSON - {e}")
                continue
            layout = layout_error(data)
            if layout:
                errors.append(f"{path}: {layout}")
                continue
            new = comment_version(data)
            if not new:
                errors.append(f"{path}: нет «Version: X.Y.Z» в _comment_")
                continue
            old_text = show(base, path) if base else None
            old = version_in_text(old_text) if old_text is not None else None
            if old and new <= old:
                errors.append(f"{path}: версия не повышена ({fmt(old)} / {fmt(new)})")
            else:
                was = f"{fmt(old)} / " if old else ""
                print(f"  {path}: версия {was}{fmt(new)}")

            # dev не может отставать от main: иначе прод меняли мимо теста.
            if target_branch() == "dev":
                main_version = version_in_text(show_main(path))
                if main_version and new < main_version:
                    errors.append(f"{path}: версия {fmt(new)} в dev ниже, чем в main ({fmt(main_version)}) - сначала перенесите изменения прода в dev")

        elif len(parts) == 2 and parts[1].endswith(".cs") and not parts[0].startswith("."):
            name = parts[0]
            names.add(name)
            cs_changed.add(name)
            if parts[1] != f"{name}.cs":
                errors.append(f"{path}: C#-файл должен называться {name}/{name}.cs")
            elif status != "D" and not os.path.exists(f"{prefix}{name}.json"):
                errors.append(f"{path}: нет парного {prefix}{name}.json")
            else:
                print(f"  {path}")
                if status != "D":
                    code, bad = read_text(path)
                    if bad:
                        errors.append(f"{path}: {bad}")
                    elif not ACTIVITY_RE.search(code):
                        print(f"  ! {path}: нет объявления действия (public string Name => \"…\"): из JSON его нельзя вызвать")

        elif status != "D" and not any(p.startswith(".") for p in parts):
            # Сервер читает только корневой JSON и Имя/Имя.cs: остальное в публикацию не попадёт.
            ext = rel.lower()
            if ext.endswith(".json"):
                errors.append(f"{path}: JSON триггеров должен лежать в корне ({prefix}Имя.json), из подпапки сервер его не прочитает")
            elif ext.endswith(".cs"):
                errors.append(f"{path}: C#-файл должен лежать в {prefix}Имя/Имя.cs, иначе сервер его не прочитает")

    # Изменение одного C# без роста версии в JSON клиенты Pilot не подхватят.
    for name in sorted(cs_changed - json_changed):
        if os.path.exists(f"{prefix}{name}.json"):
            errors.append(f"{prefix}{name}/{name}.cs изменён, а {prefix}{name}.json нет: повысьте версию в _comment_ первого триггера")

    if single and len(names) > 1:
        errors.append(f"В одном изменении несколько автоматизаций: {', '.join(sorted(names))}")

    if errors:
        print("\nОшибки:")
        for e in errors:
            print(f"  X {e}")
        return 1

    print("\nПроверки пройдены." if names else "Автоматизации не изменялись.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
