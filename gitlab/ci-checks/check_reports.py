#!/usr/bin/env python3
"""Проверка изменённых отчётов Pilot в GitLab CI: каждый .repx - XML с корневым элементом макета DevExpress."""
import subprocess
import sys
from xml.parsers import expat

from check_automations import base_commit  # общая логика выбора базового коммита

ROOT_TAG = "XtraReportsLayoutSerializer"


def git(*args):
    return subprocess.run(["git", "-c", "core.quotepath=off", *args], capture_output=True, text=True, encoding="utf-8")


def forbid(*_):
    raise expat.error("DOCTYPE и объявления сущностей в макете не допускаются")


def error_of(path):
    # Макеты DevExpress без DTD; объявление сущностей допускает «взрыв» расширения при разборе.
    root = []
    parser = expat.ParserCreate()
    parser.StartElementHandler = lambda name, attrs: root.append(name) if not root else None
    parser.StartDoctypeDeclHandler = forbid
    parser.EntityDeclHandler = forbid
    try:
        with open(path, "rb") as f:
            parser.ParseFile(f)
    except expat.error as e:
        return f"некорректный XML - {e}"
    if root[0] != ROOT_TAG:
        return f"это не макет отчёта: корневой элемент <{root[0]}>, ожидается <{ROOT_TAG}>"
    return None


def main():
    base = base_commit()
    if base:
        lines = git("diff", "--name-status", "--no-renames", base, "HEAD").stdout.splitlines()
        changes = [line.split("\t", 1) for line in lines if "\t" in line]
    else:
        changes = [["A", f] for f in git("ls-files").stdout.splitlines()]

    errors, checked = [], 0
    for status, path in changes:
        if status.startswith("D") or not path.lower().endswith(".repx"):
            continue
        checked += 1
        error = error_of(path)
        if error:
            errors.append(f"{path}: {error}")
        else:
            print(f"  {path}")

    if errors:
        print("\nОшибки:")
        for e in errors:
            print(f"  X {e}")
        return 1
    print("\nПроверки пройдены." if checked else "Отчёты не изменялись.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
