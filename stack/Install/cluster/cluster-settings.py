import re
import shutil
import sys
import uuid
from pathlib import Path
from xml.etree import ElementTree


OPTION = re.compile(r"""\s*([^=;]+?)\s*=\s*("(?:[^"]|"")*"|'(?:[^']|'')*'|[^;"'][^;]*|)\s*(?:;|$)""")
PASSWORD_KEYS = {"password", "pwd"}
PASSFILES = "/usr/share/ascon/databases/.cluster-pgpass"


def read(path):
    config = ElementTree.parse(path).getroot()
    if config.tag != "Configuration" or len(config.findall("Databases")) > 1:
        raise ValueError("Неверная структура конфигурации Pilot.")
    return config


def databases(config):
    group = config.find("Databases")
    if group is None:
        return {}
    result = {}
    for db in group:
        name = db.get("Name", "").strip()
        if db.tag != "DatabaseParameters" or not name or name.casefold() in result:
            raise ValueError("Отсутствует имя базы Pilot или имя повторяется.")
        result[name.casefold()] = db
    return result


def pg_connection(db):
    pg = db.find("PgConnectionString")
    if pg is not None and not pg.get("ConnectionString", "").strip():
        raise ValueError("Пустая строка подключения PostgreSQL в описании базы Pilot.")
    return pg


def save(config, path):
    ElementTree.ElementTree(config).write(path, encoding="utf-8", xml_declaration=True)


def bootstrap(work):
    source = read(work / "source.xml")
    postgres = [db for db in databases(source).values() if pg_connection(db) is not None]
    path = work / "bootstrap.xml"
    if not postgres:
        shutil.copyfile(work / "source.xml", path)
        return "vendor"

    group = source.find("Databases")
    for db in postgres:
        group.remove(db)
    default = source.find("DefaultDbName")
    if default is not None and (default.text or "").casefold() not in databases(source):
        default.text = None
    save(source, path)
    return "postgres"


def database_id(db):
    value = db.get("Id", "").strip().strip("{}").casefold()
    return "" if value == "00000000-0000-0000-0000-000000000000" else value


def protect_password(pg, work):
    remaining = pg.get("ConnectionString")
    if not re.search(r"(?:^|;)\s*(?:password|pwd)\s*=", remaining, re.IGNORECASE):
        return

    fields = []
    password = ""
    while remaining := remaining.lstrip("; \t\r\n"):
        option = OPTION.match(remaining)
        if option is None:
            raise ValueError("Не удалось прочитать параметры строки подключения PostgreSQL.")
        key, value = option.groups()
        key = key.strip()
        value = value.strip()
        if key.casefold() in PASSWORD_KEYS:
            if value.startswith(("'", '"')):
                quote = value[0]
                value = value[1:-1].replace(quote * 2, quote)
            password = value
        else:
            fields.append((key, value))
        remaining = remaining[option.end():]

    if password and not pg.get("EncryptedPassword"):
        if "\r" in password or "\n" in password:
            raise ValueError("Пароль PostgreSQL содержит перенос строки и не помещается в файл pgpass.")
        name = f"{uuid.uuid4().hex}.pgpass"
        escaped = password.replace("\\", "\\\\").replace(":", "\\:")
        (work / name).write_text(f"*:*:*:*:{escaped}\n", encoding="utf-8")
        fields = [(key, value) for key, value in fields if key.casefold() != "passfile"]
        fields.append(("Passfile", f"{PASSFILES}/{name}"))

    pg.set("ConnectionString", ";".join(f"{key}={value}" for key, value in fields))


def merge(work, name=None):
    source = read(work / "source.xml")
    candidates = databases(source)
    if name is None:
        added = {key: db for key, db in candidates.items() if pg_connection(db) is not None}
    else:
        key = name.casefold()
        if key not in candidates or pg_connection(candidates[key]) is None:
            raise ValueError("В исходном settings.xml не найдена указанная база PostgreSQL.")
        added = {key: candidates[key]}
    if not added:
        raise ValueError("После подготовки settings.xml отсутствуют базы PostgreSQL.")

    current = read(work / "current.xml")
    redis = current.find("RedisConnectionString")
    if redis is None or not redis.get("ConnectionString", "").strip():
        raise ValueError("В конфигурации Pilot отсутствует подключение Redis.")
    existing = databases(current)
    ids = {database_id(db) for db in existing.values()}
    group = current.find("Databases")
    if group is None:
        group = ElementTree.SubElement(current, "Databases")
    for key, db in added.items():
        identity = database_id(db)
        if key in existing or (identity and identity in ids):
            raise ValueError("Имя или идентификатор подключаемой базы уже есть в конфигурации Pilot.")
        ids.add(identity)
        protect_password(pg_connection(db), work)
        # The node's --indexPath takes precedence for databases restored from PostgreSQL.
        index = db.find("IndexPath")
        if index is not None:
            db.remove(index)
        db.set("Filename", "")
        group.append(db)

    if name is None:
        source_default = (source.findtext("DefaultDbName") or "").strip()
        if source_default.casefold() in added:
            default = current.find("DefaultDbName")
            if default is None:
                default = ElementTree.SubElement(current, "DefaultDbName")
            default.text = source_default
    save(current, work / "updated.xml")


def verify(work):
    expected = databases(read(work / "updated.xml"))
    actual = databases(read(work / "current.xml"))
    if actual.keys() != expected.keys() or any(
        database_id(actual[key]) != database_id(db)
        or (pg_connection(db) is not None and (
            pg_connection(actual[key]) is None or actual[key].get("Filename")
        ))
        for key, db in expected.items()
    ):
        raise ValueError("Записанные определения баз Pilot не совпадают с ожидаемыми.")


def main():
    work, action, *args = sys.argv[1:]
    work = Path(work)
    if action == "bootstrap":
        print(bootstrap(work))
    elif action in {"merge", "attach"}:
        merge(work, *args)
    elif action == "verify":
        verify(work)
    elif action == "journal":
        from cluster_journal import restore_journals

        files = [db for db in databases(read(work / "source.xml")).values()
                 if pg_connection(db) is None and db.get("Filename")]
        restore_journals(work, files, args[0] == "required", args[1:])
    else:
        raise ValueError("Неизвестная операция с конфигурацией Pilot.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, ElementTree.ParseError) as exc:
        sys.exit(f"Настройки кластера Pilot: {exc}")
