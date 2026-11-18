import datetime
import json
import sqlite3
import stat
import subprocess
import uuid
from contextlib import closing
from pathlib import Path, PurePosixPath


COLUMNS = (
    "id", "person_id", "ip", "server_date_time", "changeset_id", "event_kind",
    "object_id", "object_type", "object_title", "additional_info", "error_string", "plugin_additional",
)


def local_path(filename, volumes):
    path = PurePosixPath(filename)
    for mount in volumes:
        target = PurePosixPath(mount["target"])
        if mount["type"] == "bind" and (target == path or target in path.parents):
            root = Path(mount["source"]).resolve()
            local = (root / path.relative_to(target)).resolve()
            if root in local.parents:
                return local
    raise ValueError("Путь к файловой базе Pilot находится вне подключённых каталогов.")


def database_id(connection):
    rows = connection.execute("SELECT data FROM databaseid").fetchall()
    if len(rows) != 1:
        raise ValueError("В файле SQLite отсутствует однозначный идентификатор базы Pilot.")
    return as_uuid(rows[0][0])


def as_uuid(value):
    return uuid.UUID(bytes_le=value) if isinstance(value, bytes) else uuid.UUID(value)


def copy_field(value):
    if value is None:
        return r"\N"
    text = str(value)
    return (text.replace("\\", "\\\\").replace("\t", r"\t")
            .replace("\n", r"\n").replace("\r", r"\r"))


def journal_rows(main, journal, output):
    for path in (main, journal):
        wal = path.with_name(path.name + "-wal")
        if wal.exists() and wal.stat().st_size:
            raise ValueError(f"SQLite ещё содержит WAL: {path.name}. Остановите Pilot и подготовьте согласованную копию.")

    def open_db(path):
        return sqlite3.connect(path.as_uri() + "?mode=ro&immutable=1", uri=True)

    with closing(open_db(main)) as base, closing(open_db(journal)) as source:
        identity = database_id(source)
        if identity != database_id(base):
            raise ValueError("Основной файл SQLite и журнал принадлежат разным базам Pilot.")

        count = 0
        for row in source.execute(f"SELECT {', '.join(COLUMNS)} FROM user_actions"):
            row = list(row)
            for index in (4, 6):
                if row[index] is not None:
                    row[index] = as_uuid(row[index])
            for index in (9, 11):
                if row[index] is not None:
                    row[index] = r"\x" + row[index].hex()
            if isinstance(row[3], int):
                row[3] = datetime.datetime(1, 1, 1) + datetime.timedelta(microseconds=row[3] // 10)
            output.write("\t".join(map(copy_field, row)) + "\n")
            count += 1
        return identity, count


def restore_journals(work, databases, required, compose):
    if not databases:
        if required:
            raise ValueError("В исходном settings.xml нет файловых баз Pilot.")
        return
    try:
        config = json.loads(subprocess.check_output([*compose, "--profile", "tools", "config", "--format", "json"]))
        volumes = config["services"]["initialize"]["volumes"]
        found = False
        for db in databases:
            main = local_path(db.get("Filename"), volumes)
            journal = main.with_suffix(".journal.dbp")
            try:
                mode = journal.stat().st_mode
            except FileNotFoundError:
                continue
            if not stat.S_ISREG(mode) or journal.resolve() != journal:
                raise ValueError("Журнал Pilot должен быть обычным файлом рядом с основной базой.")

            name = db.get("Name")
            with (work / "journal.tsv").open("w", encoding="utf-8", newline="\n") as output:
                identity, count = journal_rows(main, journal, output)
            subprocess.run(
                [*compose, "run", "--rm", "-T", "--no-deps", "--volume", f"{work}:/cluster-work:ro",
                 "check-pg", "journal", name, str(identity), str(count)],
                stdin=subprocess.DEVNULL, check=True,
            )
            print(f"Журнал базы {name}: проверено {count} событий.", flush=True)
            found = True
        if required and not found:
            raise ValueError("Рядом с файловыми базами Pilot не найдены журналы (*.journal.dbp).")
    except (sqlite3.Error, subprocess.CalledProcessError, AttributeError, TypeError) as exc:
        raise ValueError("Не удалось перенести журнал действий Pilot. Проверьте исходный файл и вывод PostgreSQL.") from exc
