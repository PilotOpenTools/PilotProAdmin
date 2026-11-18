# Развёртывание Pilot-Server в Docker на Windows

> **Как это связано с установкой из папки `Install`.** Этот документ описывает ручную настройку Pilot и нужен только для
> отдельных шагов. Команда `install` сама создаёт сети, контейнеры Pilot-Server, PostgreSQL, RustFS и myAdmin Web, роль
> `pilotuser`, администратора сервера и бакет (разделы 2-4, 6, 7.1, 7.2, 8.1, 8.2).
>
> - **После установки с данными (вариант 1)** база и S3 уже настроены, документ не нужен. Он пригодится при ошибках: см. раздел 12.
> - **После чистой установки (вариант 2)** выполните разделы 5 и 7.3-7.7 (база), затем 8.3 и 9 или 10 (S3).
>
> Логины и пароли в примерах замените своими из `.env`. Образ `rustfs/rustfs:1.0.5` приходит в комплекте: его
> загружает `install` из папки `images`, поэтому `docker pull` для него не выполняйте. Названия контейнеров и
> порты в стеке совпадают с этим документом.

Инструкция по развёртыванию тестового контура Pilot: несколько независимых серверов
(prod / test / stage), перевод баз на PostgreSQL и подключение файлового архива S3 (RustFS).

Основана на официальной документации - [Инфоцентр системы Pilot](https://help.pilotems.com/ru/Content/Home.htm).
Ссылки на конкретные статьи приведены в соответствующих разделах и общим списком в разделе 14.
Часть решений (ручная настройка S3 без лицензии, раздел 10) в документации не описана
и получена опытным путём.

---

## 0. Важное предупреждение перед началом

**Все имена, порты и пароли в этой инструкции - примеры.** Их можно менять как угодно,
главное - соблюдать соответствие между командами. Конкретно можно свободно менять:

- имена контейнеров (`pilot-server-prod`, `postgres-prod`, `rustfs-prod`, ...);
- имена docker-сетей (`pilot-prod`, `pilot-test`, `pilot-stage`);
- внешние (хостовые) порты - `5546`, `5433`, `9000`, `5601` и т. д.;
- логины и пароли (`admin`, `pilotuser` / `password`, `s3admin` / `s3admin123`);
- пути на диске (`C:\Ascon-Docker\...`);
- имена баз данных и бакетов S3.

**Чего менять нельзя:**

- внутренние порты контейнеров: Pilot-Server всегда слушает `5545`, PostgreSQL - `5432`,
  RustFS - `9000`, myAdmin Web - `5200`. Меняется только левая часть в `-p 5546:5545`;
- пути внутри контейнера (`/usr/share/ascon/databases`, `/App/...`);
- секрет доступа RustFS: не короче 8 символов (стек задаёт такие же; для RustFS ограничение не проверялось);
- имя бакета S3: только строчные латинские буквы, цифры и дефис, 3-63 символа
  (подчёркивание запрещено, поэтому `pilot-ice-ru`, а не `pilot-ice_ru`).

---

## 1. Таблица портов для трёх контуров

Схема: у каждого контура своя docker-сеть, свой Pilot-Server, свой PostgreSQL, свой RustFS (S3).

| Компонент          | prod  | test  | stage |
|--------------------|-------|-------|-------|
| docker-сеть        | `pilot-prod` | `pilot-test` | `pilot-stage` |
| Pilot-Server       | 5546  | 5547  | 5549¹ |
| PostgreSQL         | 5433  | 5434  | 5435  |
| RustFS API         | 9000  | 9010  | 9020  |
| RustFS Console     | 9001  | 9011  | 9021  |
| myAdmin Web        | 5601  | 5602  | 5603  |
| Папка на диске     | `C:\Ascon-Docker\prod` | `C:\Ascon-Docker\test` | `C:\Ascon-Docker\stage` |

¹ 5548 в Windows часто занят системой (http.sys, «родной» Pilot-Server для Windows), поэтому стейдж - на 5549.

> **У каждого контура - своя папка данных PostgreSQL** (`-v C:/Ascon-Docker/<контур>/postgres:…`).
> Два контейнера PostgreSQL на одной папке данных запускаются без ошибок, но портят данные друг друга.

> **Почему PostgreSQL на 5433, а не 5432.** Если на машине уже установлен «нативный»
> PostgreSQL для Windows, он занимает порт 5432, и попытка подключиться к контейнеру
> с хоста попадёт не туда. Проверить, кто занимает порт:
>
> ```powershell
> Get-Process -Id (Get-NetTCPConnection -LocalPort 5432 -State Listen).OwningProcess
> ```

---

## 2. Подготовка

1. Установить **Docker Desktop для Windows** (рекомендуется backend WSL2).
2. Разрешить доступ к диску: *Settings / Resources / File Sharing / добавить `C:\`*
   (при WSL2-backend обычно не требуется).
3. Все команды выполняются в **PowerShell**.

> **Особенность PowerShell.** Он «съедает» пустые аргументы `""` при передаче во внешние
> программы. Там, где они нужны (миграция на PostgreSQL), используется оператор `--%`.
> Подробнее - в разделе «Типовые ошибки».

---

## 3. Создание docker-сетей

Сети нужны, чтобы контейнеры видели друг друга **по имени контейнера**, а не по IP хоста.
Это принципиально: на ноутбуке IP-адрес может отсутствовать или меняться (VPN, Wi-Fi выключен),
и связка «по IP» развалится.

```powershell
docker network create pilot-prod
docker network create pilot-test
docker network create pilot-stage
```

**Правило адресации, которое важно понять один раз:**

- контейнер / контейнер: `имя-контейнера:внутренний-порт` (например `postgres-prod:5432`);
- Windows-приложение (myAdmin Desktop, DBeaver, браузер) / контейнер:
  `localhost:внешний-порт` (например `localhost:5433`).

---

## 4. Развёртывание Pilot-Server (на примере prod)

> Документация: [Установка Pilot-Server в docker-контейнере](https://help.pilotems.com/ru/Content/docker_pilot-server.htm)

### 4.1. Создать файл настроек

Это единственный файл, который создаётся вручную. Остальные папки Docker создаст сам
при первом запуске контейнера.

```powershell
New-Item -ItemType Directory -Force -Path "C:\Ascon-Docker\prod\pilot-server\settings"
"<Configuration></Configuration>" | Out-File -Encoding utf8 "C:\Ascon-Docker\prod\pilot-server\settings\settings.xml"
```

### 4.2. Загрузить образ

```powershell
docker pull registry.ascon.ru/project/pilotdev/pilot/pilot-server:release
```

> Тег `latest` - бета-версия, тег `release` - стабильная. Для воспроизводимости лучше
> зафиксировать конкретный номер версии (например `:25.10.0`), уточнив его у поставщика.

### 4.3. Запустить контейнер

```powershell
docker run -d -p 5546:5545 --name pilot-server-prod --network pilot-prod -v C:/Ascon-Docker/prod/databases:/usr/share/ascon/databases -v C:/Ascon-Docker/prod/aspnet:/root/.aspnet -v C:/Ascon-Docker/prod/license:/usr/share/ASCON -v C:/Ascon-Docker/prod/pilot-server/settings:/usr/share/ascon/pilot-server/settings -v C:/Ascon-Docker/prod/logs:/App/logs --entrypoint /App/Ascon.Pilot.Daemon registry.ascon.ru/project/pilotdev/pilot/pilot-server:release /usr/share/ascon/pilot-server/settings/settings.xml
```

Назначение томов:

| Том на хосте | Внутри контейнера | Что хранит |
|---|---|---|
| `prod\databases` | `/usr/share/ascon/databases` | файлы баз `.dbp` и файловые архивы |
| `prod\aspnet`    | `/root/.aspnet` | служебные данные ASP.NET |
| `prod\license`   | `/usr/share/ASCON` | лицензии |
| `prod\pilot-server\settings` | `.../settings` | `settings.xml` |
| `prod\logs`      | `/App/logs` | логи сервера |

### 4.4. Создать администратора сервера

```powershell
docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --admin /usr/share/ascon/pilot-server/settings/settings.xml admin ПАРОЛЬ
docker restart pilot-server-prod
```

### 4.5. Проверка

```powershell
docker ps
curl http://localhost:5546
```

Контейнер должен быть в статусе `Up`, `curl` - вернуть версию сервера.
В проводнике появится `C:\Ascon-Docker\prod` с подпапками.

### 4.6. Лицензия

Файл лицензии положить в `C:\Ascon-Docker\prod\license\Pilot Server\License\`.
После этого перезапустить контейнер.

---

## 5. Подключение демонстрационной базы

> Документация: [Присоединение существующей базы данных](https://help.pilotems.com/ru/Content/connection_base.htm),
> [Создание новой базы данных](https://help.pilotems.com/ru/Content/myAdmin_sozdanie_bazy.htm)

1. Скачать архив демобаз (`Databases.zip`) и распаковать.
2. Перенести папку базы в примонтированный каталог:

```powershell
Move-Item "C:\Users\<пользователь>\Downloads\Databases\pilot-ice_ru" "C:\Ascon-Docker\prod\databases\pilot-ice_ru"
```

3. Узнать имя файла-маркера файлового архива (имя вида GUID с расширением `.pilotfa`):

```powershell
Get-ChildItem "C:\Ascon-Docker\prod\databases\pilot-ice_ru\FileArchive"
```

4. Присоединить базу:

```powershell
docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --attach pilot-ice_ru /usr/share/ascon/databases/pilot-ice_ru/base.dbp /usr/share/ascon/databases/pilot-ice_ru/FileArchive/<GUID>.pilotfa
```

---

## 6. Pilot-myAdmin Web (опционально)

> Документация: [Установка Pilot-myAdmin Web в docker-контейнере](https://help.pilotems.com/ru/Content/docker_myAdmin_Web.htm)

Веб-версия админки, идёт в комплекте с текущей версией сервера.

```powershell
docker pull pilotdev/pilot-web-myadmin:latest
docker run -d -p 5601:5200 -e PilotServer:Url=http://pilot-server-prod:5545 --name pilot-web-myadmin-prod --network pilot-prod pilotdev/pilot-web-myadmin:latest
```

Открыть `http://localhost:5601`, войти под администратором сервера.

> Desktop-редакция Pilot-myAdmin подключается к серверу по адресу `localhost:5546`.

---

## 7. Перевод базы на PostgreSQL

> Документация: [Миграция базы данных в PostgreSQL](https://help.pilotems.com/ru/Content/PG_migration.htm),
> [Присоединение базы данных PostgreSQL](https://help.pilotems.com/ru/Content/weba-connection_base_PG.htm)

Процесс состоит из **двух независимых частей**, и обе обязательны:

1. **Миграция данных** - утилита `PostgresMigration` копирует содержимое `.dbp` в PostgreSQL.
2. **Переключение сервера** - команда `--attachpg` заставляет Pilot-Server работать с PostgreSQL.

Если сделать только первое, сервер продолжит писать в файлы, а копия в PostgreSQL
останется «мёртвой». Это самая частая ошибка.

### 7.1. Поднять PostgreSQL

```powershell
docker run -d --name postgres-prod --network pilot-prod -e POSTGRES_PASSWORD=postgres -p 5433:5432 -v C:/Ascon-Docker/prod/postgres:/var/lib/postgresql/data postgres:16
```

### 7.2. Создать роль для Pilot

```powershell
docker exec -ti postgres-prod psql -U postgres -c "CREATE ROLE pilotuser WITH NOSUPERUSER CREATEDB NOCREATEROLE NOINHERIT LOGIN NOREPLICATION NOBYPASSRLS CONNECTION LIMIT -1;"
docker exec -ti postgres-prod psql -U postgres -c "ALTER USER pilotuser PASSWORD 'password';"
```

Проверка, что роль создана:

```powershell
docker exec -ti postgres-prod psql -U postgres -c "\du"
```

### 7.3. Резервная копия

```powershell
docker stop pilot-server-prod
Copy-Item -Recurse "C:\Ascon-Docker\prod\databases" "C:\Ascon-Docker\prod\databases_backup"
```

### 7.4. Миграция данных

Обрати внимание на `--%` - без него PowerShell потеряет два пустых аргумента `"" ""`
в конце, и утилита ответит `Incorrect parameters`.

```powershell
docker run --% --rm --network pilot-prod -v C:/Ascon-Docker/prod/databases:/usr/share/ascon/databases -v C:/Ascon-Docker/prod/pilot-server/settings:/usr/share/ascon/pilot-server/settings --entrypoint /App/PostgresMigration registry.ascon.ru/project/pilotdev/pilot/pilot-server:release /usr/share/ascon/pilot-server/settings/settings.xml "Host=postgres-prod:5432;Username=pilotuser;Password=password;Include Error Detail=true;Pooling=true;Connection Lifetime=0;Keepalive=3;DataBase=postgres" "" ""
```

Два пустых аргумента в конце означают **монолитный режим** (не кластерный).

### 7.5. Переключение сервера на PostgreSQL

```powershell
docker start pilot-server-prod
docker exec -ti pilot-server-prod mkdir -p /usr/share/ascon/databases/pilot-ice_ru/Index
docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --detach pilot-ice_ru
docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --attachpg pilot-ice_ru "Host=postgres-prod:5432;Username=pilotuser;Password=password;Include Error Detail=true;Pooling=true;Connection Lifetime=0;Keepalive=3;DataBase=pilot-ice_ru" "/usr/share/ascon/databases/pilot-ice_ru/Index" "/usr/share/ascon/databases/pilot-ice_ru/FileArchive/<GUID>.pilotfa"
docker restart pilot-server-prod
```

Важно: файловый архив **не мигрирует** в PostgreSQL. В базе хранятся только метаданные,
тела файлов всегда лежат в файловом архиве (файловая система или S3).

### 7.6. Проверка

```powershell
Get-Content "C:\Ascon-Docker\prod\pilot-server\settings\settings.xml"
```

Признаки успеха у нужной базы:

- `Filename=""` (пусто);
- присутствует блок `<PgConnectionString ConnectionString="..."/>`;
- присутствует `<IndexPath>`.

В myAdmin поле «Расположение файла базы данных» должно быть пустым.

> **Проверить на дубли.** Если в `settings.xml` окажутся две записи с одинаковым `Id`
> и одним и тем же файловым архивом - лишнюю нужно отсоединить:
> `docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --detach ИМЯ_ЛИШНЕЙ_БАЗЫ`
> Использовать один файловый архив для двух баз нельзя - это ведёт к повреждению данных.

### 7.7. Подключение через DBeaver

- Host: `localhost`
- Port: `5433`
- Database: `postgres`
- Username: `pilotuser`, Password: `password`
- **Включить галку «Показать все базы данных»** - иначе будет видна только служебная
  пустая база `postgres`, а не мигрированные данные.

Схема внутри базы называется **`dbp<версия сервера>`**, например `dbp26.13.0.62219`.
Из-за точек в имени её нужно брать в двойные кавычки:

```sql
SELECT * FROM "dbp26.13.0.62219".faslist;
```

Либо задать один раз на сессию:

```sql
SET search_path TO "dbp26.13.0.62219";
```

Живая проверка: создать пользователя в myAdmin и выполнить
`SELECT * FROM people;` - он должен появиться.

---

## 8. Перевод файлового архива на S3 (RustFS)

> **Хранилище - RustFS.** Адрес в Pilot Admin - `http://rustfs-prod:9000`. Запуск RustFS, создание бакета командой `rc` и доступ по S3 проверены на живом Docker Desktop (Windows, WSL2).

### 8.1. Поднять RustFS

Данные хранятся в именованном томе Docker, а не в папке `C:/Ascon-Docker`. Это важно: на папке Windows, подключённой к контейнеру, RustFS не завершает составную загрузку больших файлов (ошибка `NoSuchKey`), а на томе работает. Том создаётся сам, права настраивать не нужно (подробности в `..\README.md`).

```powershell
docker run -d --name rustfs-prod --network pilot-prod -p 127.0.0.1:9000:9000 -p 127.0.0.1:9001:9001 -e RUSTFS_ACCESS_KEY=s3admin -e RUSTFS_SECRET_KEY=s3admin123 -e RUSTFS_VOLUMES=/data -e RUSTFS_ADDRESS=0.0.0.0:9000 -e RUSTFS_CONSOLE_ADDRESS=0.0.0.0:9001 -e RUSTFS_CONSOLE_ENABLE=true -v pilot-prod_rustfs_data:/data rustfs/rustfs:1.0.5
```

Консоль: `http://localhost:9001` (`s3admin` / `s3admin123`). Том `pilot-prod_rustfs_data` виден в `docker volume ls`; такое имя найдёт и скрипт резервной копии.

### 8.2. Создать бакет

Через консоль RustFS: *Buckets / Create Bucket*, имя `pilot-ice-ru`.

Или командой:

```powershell
docker run --rm --network pilot-prod --entrypoint sh -e HOME=/tmp rustfs/rustfs:1.0.5 -c "rc alias set m http://rustfs-prod:9000 s3admin s3admin123 && rc mb --ignore-existing m/pilot-ice-ru && rc ls m"
```

Клиент `rc` входит в образ RustFS.

### 8.3. Создать временную папку внутри контейнера Pilot-Server

```powershell
docker exec -ti pilot-server-prod mkdir -p /usr/share/ascon/databases/pilot-ice_ru/s3temp
```

---

## 9. Вариант А - при наличии лицензии (штатный путь через myAdmin)

> Документация: [Общая информация - файловые архивы](https://help.pilotems.com/ru/Content/myAdmin_obschaya_informaciya.htm)
> (описание команд «Добавить файловый архив», «Удалить файловый архив», параметры S3),
> [Перенос файловых архивов](https://help.pilotems.com/ru/Content/disconnect_files.htm)

Если лицензия установлена, все команды управления файловыми архивами доступны в интерфейсе.

1. В Pilot-myAdmin открыть базу двойным щелчком / вкладка **Общая информация**.
2. В зоне **Расположение файловых архивов** выполнить команду **Добавить файловый архив**.
3. Заполнить:
   - **Название архива** - например `S3 архив`;
   - **Тип файлового архива** - `S3`;
   - **Расположение файлового архива** - `http://rustfs-prod:9000/pilot-ice-ru`
     (обязательно **с именем бакета**, иначе myAdmin выдаст `URL does not contain bucket name`);
   - **Access key id** - `s3admin`;
   - **Secret access key** - `s3admin123`;
   - **Расположение временной папки** - `/usr/share/ascon/databases/pilot-ice_ru/s3temp`;
   - отметить архив как **записываемый**.
4. Перенести существующие файлы (при необходимости):
   команда **Перенос файлов** / источник «Файловый архив 1», приёмник «S3 архив»,
   версии - **все**. Перед запуском можно выполнить **Запустить анализ**.
5. После переноса старый архив с 0 файлов можно удалить командой
   **Удалить файловый архив** (он должен быть не записываемым).

> Массовый перенос не обязателен. Если оставить старый архив как не записываемый,
> старые файлы продолжат читаться с диска, а все новые пойдут в S3. Для тестового
> контура это экономит место и время.

---

## 10. Вариант Б - без лицензии (ручная настройка)

> Этот раздел в официальной документации отсутствует - порядок действий получен опытным путём.
> Смежные статьи: [Утилита pServerAdmin](https://help.pilotems.com/ru/Content/pServerAdmin.htm),
> [Резервное копирование данных](https://help.pilotems.com/ru/Content/rezervnoe_kopirovanie_dannyh.htm)

Если лицензии нет, кнопки управления файловыми архивами в myAdmin неактивны.
Консольная утилита `pServerAdmin` тоже не поможет: она поставляется **только с
Windows-дистрибутивом** Pilot-Server и в Linux-контейнере отсутствует, а у
`Ascon.Pilot.Daemon` команд для работы с архивами нет вовсе (проверяется через `--help`).

Остаётся ручной путь: запись в `settings.xml` + запись в таблицу `faslist` базы данных.
Нужно сделать **обе** операции - сервер связывает их по идентификатору архива.

### 10.1. Как получить `EncryptedSecretAccessKey`

Secret access key хранится в `settings.xml` **в зашифрованном виде**
(атрибут `EncryptedSecretAccessKey`, например `tNfUT990ipzrVA7759GiQA==`).
Алгоритм шифрования закрыт, вручную такую строку **сгенерировать нельзя**.

Рабочий способ - «донор»: создать временную базу с S3-архивом через myAdmin
(этот диалог доступен даже без лицензии) и скопировать готовую строку.

1. В myAdmin: **Создать новую** базу:
   - имя - например `s3probe`;
   - папка для `.dbp` - `/usr/share/ascon/databases/s3probe`;
   - тип архива - **S3**;
   - расположение - `http://rustfs-prod:9000/имя-бакета`;
   - Access key id / Secret access key - **те же, что будут у боевой базы**;
   - временная папка - например `/usr/share/ascon/databases/s3probe/s3temp`.
2. Открыть `settings.xml` и скопировать из блока `FileArchiveParametersS3`
   значение `EncryptedSecretAccessKey`.
3. Отсоединить временную базу:
   ```powershell
   docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --detach s3probe
   ```

Ограничение: скопированная строка действительна для **того же secret key и того же
сервера**. Для другого контура (test, stage) процедуру нужно повторить на его сервере,
либо использовать одинаковый secret key и проверить, что строка подходит.

### 10.2. Резервные копии

```powershell
Copy-Item "C:\Ascon-Docker\prod\pilot-server\settings\settings.xml" "C:\Ascon-Docker\prod\pilot-server\settings\settings.xml.bak"
docker exec postgres-prod pg_dump -U pilotuser -d pilot-ice_ru -F c -f /tmp/before_s3.dump
docker cp postgres-prod:/tmp/before_s3.dump C:\Ascon-Docker\prod\before_s3.dump
```

### 10.3. Сгенерировать GUID для нового архива

```powershell
[guid]::NewGuid()
```

### 10.4. Правка `settings.xml`

Сервер обязательно остановить, иначе он перезапишет файл при завершении:

```powershell
docker stop pilot-server-prod
```

У нужной базы заменить блок `<FileArchiveFolders>`:

```xml
<FileArchiveFolders>
  <FileArchiveParameters Folder="/usr/share/ascon/databases/pilot-ice_ru/FileArchive"
                         Id="67807bf1-deb8-47b7-8f8b-b00294c9d288"
                         IsWritable="false" />
  <FileArchiveParametersS3 Folder=""
                           Id="ВСТАВИТЬ_НОВЫЙ_GUID"
                           IsWritable="true"
                           AccessKeyId="s3admin"
                           Url="http://rustfs-prod:9000"
                           Bucket="pilot-ice-ru"
                           TempFolderPath="/usr/share/ascon/databases/pilot-ice_ru/s3temp"
                           EncryptedSecretAccessKey="СКОПИРОВАННАЯ_СТРОКА" />
</FileArchiveFolders>
```

Ключевые моменты:

- элемент называется `FileArchiveParametersS3`, а не `FileArchiveParameters`;
- `Folder` у S3-архива **пустой**;
- бакет указывается отдельным атрибутом `Bucket`, а в `Url` - только адрес хранилища;
- у старого архива `IsWritable="false"`, у S3 - `true`.

### 10.5. Запись в таблицу `faslist`

Без этой записи myAdmin выдаст ошибку
`File archive <GUID> has no correspondent record in the database table`,
а у архива будет пустое имя и бесконечное «Выполняется подсчет».

В DBeaver (подставить свою схему и GUID):

```sql
INSERT INTO "dbp26.13.0.62219".faslist (fa_id, name, creation_date, is_writable)
VALUES ('ВСТАВИТЬ_НОВЫЙ_GUID', 'S3 архив', now(), true);

UPDATE "dbp26.13.0.62219".faslist SET is_writable = false
WHERE fa_id = '67807bf1-deb8-47b7-8f8b-b00294c9d288';
```

GUID должен **в точности совпадать** с `Id` из `settings.xml`.
Не забыть подтвердить транзакцию (Commit), если в DBeaver выключен автокоммит.

Структура таблицы `faslist`: `fa_id` (uuid), `name` (varchar),
`creation_date` (timestamptz), `is_writable` (boolean).
Параметры S3 в базе не хранятся - они берутся из `settings.xml`.

### 10.6. Запуск и проверка

```powershell
docker start pilot-server-prod
docker logs --tail 30 pilot-server-prod
```

В myAdmin в зоне файловых архивов должно быть **два** архива, у S3 - наименование,
статус «записываемый» и счётчик файлов `0`.

Живой тест: добавить файл через клиент Pilot и проверить в консоли RustFS,
что в бакете появился объект.

Откат при неудаче: восстановить `settings.xml.bak` и удалить вставленную строку из `faslist`.

---

## 11. Развёртывание test и stage

Повторить разделы 4-10, заменив во всех командах:

- `prod` / `test` (или `stage`) в именах контейнеров, сетей и путей;
- внешние порты согласно таблице из раздела 1;
- имя бакета S3 (например `pilot-ice-ru-test`).

Внутренние порты (`5545`, `5432`, `9000`, `5200`) остаются без изменений.

> К монолитному (не кластерному) Pilot-Server можно подключить **только одну**
> базу PostgreSQL одновременно. Для схемы «отдельный сервер на контур» это не ограничение.

---

## 12. Типовые ошибки и решения

| Симптом | Причина | Решение |
|---|---|---|
| `Incorrect parameters` при миграции | PowerShell убрал пустые аргументы `"" ""` | Добавить `--%` после `docker run` или выполнить через `cmd /c '...'` |
| DBeaver не пускает `pilotuser`, а в контейнере пускает | Порт 5432 занят нативным PostgreSQL Windows | Пробросить контейнер на 5433 и указать этот порт в DBeaver |
| В контейнере `psql` не спрашивает пароль | Для локальных подключений внутри контейнера действует `trust` | Проверять из другого контейнера: `docker run --rm -ti --network pilot-prod postgres:16 psql -h postgres-prod -U pilotuser -d postgres` |
| В DBeaver видно только пустую базу `postgres` | Не включена галка «Показать все базы данных» | Включить галку |
| `relation "dbp.faslist" does not exist` | Схема называется `dbp<версия>` и содержит точки | `SELECT * FROM "dbp26.13.0.62219".faslist;` |
| Новые данные не появляются в PostgreSQL | Выполнена только миграция, без `--attachpg` | Выполнить `--detach` + `--attachpg` |
| В `settings.xml` две базы с одинаковым `Id` | Повторное присоединение базы | Отсоединить лишнюю: `--detach ИМЯ` |
| `URL does not contain bucket name` | В адресе S3 не указан бакет | Указывать `http://rustfs-prod:9000/имя-бакета` |
| `File archive ... has no correspondent record in the database table` | Архив есть в `settings.xml`, но нет записи в `faslist` | Выполнить `INSERT` из раздела 10.5 |
| База получает статус «Повреждена» | S3-хранилище выключено или недоступно | Запустить RustFS, затем в myAdmin остановить и запустить базу |
| RustFS не стартует: `Server runtime failed: Volume not found` | `docker run` запущен в Git Bash: он превращает `/data` в `-e RUSTFS_VOLUMES=/data` в путь вида `C:/Program Files/Git/data` | Запускать команды в PowerShell, как в этом документе (или в Git Bash добавить перед командой `MSYS_NO_PATHCONV=1`) |
| Не видно кнопок управления файловыми архивами | Нет лицензии | Использовать вариант Б (раздел 10) |

---

## 13. Полезные команды

```powershell
# Список контейнеров и портов
docker ps --format "{{.Names}} {{.Status}} {{.Ports}}"

# Логи сервера
docker logs --tail 50 pilot-server-prod

# Список баз на сервере
docker exec -ti pilot-server-prod ./Ascon.Pilot.Daemon --help

# Список баз PostgreSQL
docker exec -ti postgres-prod psql -U postgres -c "\l"

# Содержимое настроек
Get-Content "C:\Ascon-Docker\prod\pilot-server\settings\settings.xml"

# Смена версии образа (данные в томах сохраняются)
docker stop pilot-server-prod
docker rm pilot-server-prod
# затем повторить docker run с нужным тегом
```

---

## 14. Ссылки на официальную документацию

Все ссылки ведут в [Инфоцентр системы Pilot](https://help.pilotems.com/ru/Content/Home.htm).

**Установка в Docker**

- [Установка Pilot-Server в docker-контейнере](https://help.pilotems.com/ru/Content/docker_pilot-server.htm)
- [Установка Pilot-myAdmin Web в docker-контейнере](https://help.pilotems.com/ru/Content/docker_myAdmin_Web.htm)
- [Установка Pilot-BIM-Server в docker-контейнере](https://help.pilotems.com/ru/Content/docker_pilot-bim-server.htm)
- [Установка Pilot-Web в docker-контейнере](https://help.pilotems.com/ru/Content/docker_pilot-web.htm)
- [Установка системы Pilot-BIM](https://help.pilotems.com/ru/Content/p-BIM_ustanovka.htm)

**Базы данных**

- [Создание новой базы данных](https://help.pilotems.com/ru/Content/myAdmin_sozdanie_bazy.htm)
- [Присоединение существующей базы данных](https://help.pilotems.com/ru/Content/connection_base.htm)
- [Отсоединение и перенос базы данных](https://help.pilotems.com/ru/Content/disconnect_base.htm)
- [Миграция базы данных в PostgreSQL](https://help.pilotems.com/ru/Content/PG_migration.htm)
- [Присоединение базы данных PostgreSQL](https://help.pilotems.com/ru/Content/weba-connection_base_PG.htm)

**Файловые архивы и S3**

- [Общая информация: файловые архивы, добавление архива S3, проверка целостности](https://help.pilotems.com/ru/Content/myAdmin_obschaya_informaciya.htm)
- [Перенос файловых архивов и перенос файлов между архивами](https://help.pilotems.com/ru/Content/disconnect_files.htm)

**Служебное**

- [Утилита pServerAdmin (только Windows-дистрибутив)](https://help.pilotems.com/ru/Content/pServerAdmin.htm)
- [Резервное копирование данных](https://help.pilotems.com/ru/Content/rezervnoe_kopirovanie_dannyh.htm)
- [Системные требования](https://help.pilotems.com/ru/Content/sistemnye_trebovaniya.htm)

**Что в документации не описано**

- формат блока `FileArchiveParametersS3` в `settings.xml` (раздел 10.4);
- необходимость записи в таблицу `faslist` при ручном добавлении архива (раздел 10.5);
- требование указывать бакет прямо в URL хранилища (раздел 8.2);
- способ получить `EncryptedSecretAccessKey` через временную базу-донор (раздел 10.1).

По этим вопросам при необходимости обращаться в
[техническую поддержку АСКОН](http://support.ascon.ru/request/).
