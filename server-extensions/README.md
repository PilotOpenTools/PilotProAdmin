# Серверные расширения Pilot

Всё, что выполняется внутри Pilot-Server или помогает это разрабатывать. Два независимых решения, по одному на папку, и шаблон CI:

| Папка | Решение | Что это |
|---|---|---|
| [`ServerHost/`](ServerHost/README.md) | `ServerHost.sln` | Загрузчик серверных модулей и его тесты. Единственный `.servext`, который ставится в Pilot вручную; остальные модули он загружает и обновляет сам, без перезапуска сервера |
| [`TestServerAction/`](TestServerAction/README.md) | `TestServerAction.sln` | Пример обычного серверного модуля, образец для новых. Отдельный репозиторий в GitLab |
| `ci-template/` | - | Шаблон `.gitlab-ci.yml` для репозитория серверного модуля |

Утилита `debug` для запуска модуля под отладчиком на машине разработчика на реальных данных ведётся в отдельном репозитории [PilotServerDebug](https://github.com/PilotOpenTools/PilotServerDebug). Веб-админка для неё не обязательна.

## Шаблон CI (`ci-template`)

`ci-template/.gitlab-ci.yml` кладётся в корень репозитория модуля (то же самое выдаёт админка): проверка версии и CHANGELOG, сборка zip, проверка библиотек, выкладка через админку в ServerHost контура.
Тег `vX.Y.Z` и GitLab Release ставит веб-админка при выпуске в прод - в CI их нет.

Это копия `proadmin/.../Modules/ModuleCiTemplate.cs`, файлы должны совпадать. Совпадение проверяет тест `ServerModulesCiTests` админки; он ищет папку `server-extensions` по имени, поэтому не переименовывайте её и `ci-template`.

Подпись zip необязательна: если в группе серверных модулей задана переменная `MODULE_SIGNING_KEY`, шаг `build_module` кладёт в архив `signature.json`,
и ServerHost, которому выдан открытый ключ, принимает только такие архивы. Нет переменной - zip собирается без подписи, как раньше.
Зачем это нужно и как включить: [`ServerHost/README.md`](ServerHost/README.md), раздел «Подпись модулей».

## Как написать новый серверный модуль

1. Отдельный репозиторий в группе серверных модулей GitLab, в корне - один `.csproj`
   (образец - `TestServerAction.csproj`):
   - `netstandard2.0`, `AssemblyName` = `<Имя>.servext`, `<Version>X.Y.Z</Version>`;
   - `Ascon.Pilot.ServerExtensions.SDK` и `System.ComponentModel.Composition` с `ExcludeAssets="runtime"` -
     они уже есть в Pilot-Server.
2. Класс `[Export(typeof(IServerActivity))]`, `Name` - имя активности, которое указывают в сценарии автоматизации.
   Имя должно быть уникальным среди всех модулей.
3. `CHANGELOG.md` с разделом `## [X.Y.Z]`, `.gitlab-ci.yml` - из `ci-template/` (или из админки).
4. Ветки: `feature/KTN-…` / `dev` (тест) / `release/X.Y.Z` (стейдж) / `main` (прод; тег и GitLab Release ставит админка).
   `main` и `release/*` в GitLab защищены (см. ниже).
5. Отладка на реальных данных без выкладки - [`Debug`](https://github.com/PilotOpenTools/PilotServerDebug).

## Ключи выкладки из CI

Job `deploy_*` отправляет zip в админку с двумя доказательствами: заголовком `X-Deploy-Key` (ключ контура) и заголовком
`X-Job-Token` (`$CI_JOB_TOKEN` задания). Админка проверяет токен в GitLab (`GET /api/v4/job`) и берёт оттуда ветку, коммит,
проект и pipeline; поля формы `ref`, `sha`, `project`, `user`, `pipeline_url` служат только для диагностики и обязаны совпасть
с проверенными значениями. Проект должен лежать в группе серверных модулей из настроек админки. Контур выбирается по **проверенной**
ветке: `dev` - тест, `release/*` - стейдж, `main`/`master` - прод; ключ сверяется с ключом этого контура.
Задание `check_libraries` ничего не выкладывает и шлёт только `X-Job-Token`.

Ключи раздельные, по одному на контур; админка читает их из `.env` папки установки (установщик создаёт их сам):

| Контур | Переменная `.env` | Переменная группы GitLab | Флаги в GitLab |
|---|---|---|---|
| тест | `MODULES_DEPLOY_KEY_TEST` (/ `App__CiDeploy__Keys__test`) | `DEPLOY_KEY_TEST` | Masked |
| стейдж | `MODULES_DEPLOY_KEY_STAGE` (/ `App__CiDeploy__Keys__stage`) | `DEPLOY_KEY_STAGE` | Masked **и Protected** |
| прод | `MODULES_DEPLOY_KEY_PROD` (/ `App__CiDeploy__Keys__prod`) | `DEPLOY_KEY_PROD` | Masked **и Protected** |

Пустой ключ контура - выкладка в этот контур из CI выключена.

Выпустить ключи (по одному на контур; при смене обновить значение и в `.env` папки установки, и в GitLab):

```bash
openssl rand -hex 32
```

1. Записать значения в `.env` папки установки (`MODULES_DEPLOY_KEY_TEST`, `_STAGE`, `_PROD`), затем `docker compose up -d proadmin` в ней.
2. GitLab / группа / Settings / CI/CD / Variables / Add variable: три переменные из таблицы, Visibility **Masked**;
   для `DEPLOY_KEY_STAGE` и `DEPLOY_KEY_PROD` ещё и **Protect variable**. Группы: `client-modules` и `server-extensions`.
3. В каждом репозитории модуля защитить ветки `main` и `release/*` (Settings / Repository / Protected branches):
   Allowed to push - **No one**, Allowed to merge - **Maintainers**. Protected-ключи доступны только заданиям на защищённых ветках,
   поэтому разработчик без права выпуска не прочитает ключ стейджа и прода в задании своей ветки.
4. Админка не на этом Docker-хосте - там же переменная `ADMIN_URL`. Адрес `http://host.docker.internal:5300` годится только
   внутри одного Docker-хоста; для админки на другой машине задайте `https://…`, иначе ключи и zip идут открытым текстом.

### Переход со старого общего ключа

Раньше использовалась одна переменная `DEPLOY_KEY` (`MODULES_DEPLOY_KEY`) без флага Protected и без `X-Job-Token`.
Теперь заданиям нужны токен задания и ключ своего контура, поэтому прежний `.gitlab-ci.yml` выкладку не пройдёт: обновите его из
`ci-template/` (или кнопкой «Обновить CI и версию» в админке) и задайте три новые переменные. Старый общий ключ на стороне админки принимается
только для контура test и только пока не задан `MODULES_DEPLOY_KEY_TEST`; для stage и прод - никогда. После перехода удалите `DEPLOY_KEY` из групп GitLab.
