# GitLab и GitLab Runner

GitLab CE и Runner в Docker: репозитории, CI и реестр пакетов всего стенда. Доступ по `http://<IP>:8080`, git по SSH на порту `2224`, без HTTPS.

Запускается вместе со стеком (`.\shared\stack.ps1 up admin` из папки `stack`) или отдельно, командой `docker compose up -d` в этой папке.

| Что | Где |
|---|---|
| Контейнеры | `gitlab`, `gitlab-runner` |
| Сеть | `gitlab_gitlab-net`, в ней же работают задания CI |
| Настройки | `.env` (образец - `.env.example`) |
| Данные | `./data`, `./config`, `./logs`, настройки Runner - `./runner-config`. В git не попадают |
| Артефакты CI и загрузки | тома Docker `gitlab_gitlab_artifacts` и `gitlab_gitlab_uploads`: на папке Windows GitLab не может сделать `chmod` загруженного файла и отвечает ошибкой 500 (Runner - при артефактах, API `uploads` - при вложениях и файлах релизов) |
| Общие проверки | `ci-checks/`, отдельный репозиторий `pilot-ci/checks` ([README](ci-checks/README.md)) |
| Резервные копии | `../stack/backup`, [в том числе восстановление GitLab](../stack/backup/README.md#восстановление-gitlab-вручную) |

## Требования

- Docker Desktop с WSL2 или Docker Engine на Linux. На Docker Desktop выделите не меньше 6 ГБ памяти.
- От 10 ГБ свободного места, дальше объём растёт вместе с репозиториями и артефактами.
- Брандмауэр должен пропускать порты 8080 и 2224, если GitLab открывают с других машин.

## Первый запуск

1. Скопируйте `.env.example` в `.env` и заполните:
   - `GITLAB_HOST_IP` - IP этой машины в сети, не `localhost`. Из него собирается `external_url`;
   - `GITLAB_ROOT_PASSWORD` - начальный пароль `root`, не короче 8 символов. Действует только при первом запуске, потом пароль меняют в интерфейсе.
2. Запустите `docker compose up -d`. Первый запуск занимает 3-5 минут. Статус `healthy` ставится по `/-/readiness` (до 15 минут на запуск); при сомнении проверьте вручную: команда `docker compose exec gitlab curl -fs http://localhost/-/readiness` вернёт успех (без ошибки), а `docker compose exec gitlab gitlab-ctl status` покажет `run: puma`.
3. Войдите как `root` и пройдите шаги ниже. На сети, доступной не только вам, сразу закройте саморегистрацию (шаг «Закрытие регистрации»).

## Настройка пустого GitLab

Шаги нужны только на новом GitLab. При установке с данными или восстановлении из копии всё уже настроено.

### Runner

В **Admin / CI/CD / Runners / New instance runner** выберите Docker и скопируйте токен `glrt-…`. Затем зарегистрируйте контейнер:

```powershell
docker compose exec gitlab-runner gitlab-runner register `
  --non-interactive `
  --url "http://gitlab" `
  --clone-url "http://gitlab" `
  --token "<токен>" `
  --executor "docker" `
  --docker-image "alpine:3.20" `
  --docker-network-mode "gitlab_gitlab-net" `
  --docker-pull-policy "if-not-present" `
  --docker-extra-hosts "host.docker.internal:host-gateway" `
  --description "local-docker-runner"
```

`host.docker.internal` нужен заданиям выкладки: так они находят админку. Контейнер Runner получает `docker.sock` хоста, а Runner общий для всех проектов и без тегов, поэтому код любой ветки любого проекта выполняется на Docker хоста. Давайте право push в репозитории только тем, кому доверяете это. Runner перечитывает `runner-config/config.toml` сам. Если хост работает через Proxifier, добавьте в секцию `[runners.docker]` строку `dns = ["8.8.8.8", "1.1.1.1"]`, иначе задания получат поддельные адреса `127.x`.

### Группы и проекты

| Группа или проект | Что там | Поле в настройках админки |
|---|---|---|
| `automation` | автоматизации, один репозиторий на автоматизацию, подгруппы - папки | «Группа с автоматизациями» |
| `reports` | отчёты, так же | «Группа с отчётами» |
| `client-modules` | клиентские модули, в том числе `pilot-clientsdk` и `module-for-report` (Reports) | «Группа с клиентскими модулями» |
| `server-extensions` | серверные модули | «Группа с серверными модулями» |
| `pilot-ice/types` | типы, один проект | «Репозиторий с типами» |
| `pilot-ci/checks` | общие проверки из `ci-checks/`, **публичный** проект: задания скачивают из него скрипты без токена | «Проверки автоматизаций» (`automation.yml@pilot-ci/checks`) и «Проверки отчётов» (`report.yml@pilot-ci/checks`) |

Имена можно выбрать другие: админка берёт их из своих настроек. В примерах документов встречаются и другие адреса, например `/modules` или `/reports`. На этом стенде действуют имена из таблицы. Проект проверок можно назвать иначе, но тогда задайте его путь переменной `CHECKS_PROJECT` ([README проверок](ci-checks/README.md)).

### Вход и ключи

| Что | Где настраивается | Описание |
|---|---|---|
| OAuth для админки, confidential, scope `api`, Redirect URI `http://<админка>:5300/GitLab/Callback` | Admin / Applications, затем `proadmin/.env` | [README админки](../proadmin/README.md#вход-через-gitlab-oauth-по-желанию) |
| OAuth для Reports, **не** confidential, scope `api`, Redirect URI `http://127.0.0.1:7890/callback` | Admin / Applications, затем `Reports.config.json` | [README Reports](https://github.com/PilotOpenTools/PilotReports/blob/main/docs/guide.md#настройка-входа-через-браузер-один-раз-администратор-gitlab) |
| Переменные `DEPLOY_KEY_TEST` (Masked), `DEPLOY_KEY_STAGE` и `DEPLOY_KEY_PROD` (Masked **и Protected**) в группах `client-modules` и `server-extensions`. Значения - `MODULES_DEPLOY_KEY_TEST`, `_STAGE`, `_PROD` из `.env` стека. Установщик на машине с ролью `gitlab` задаёт их сам (шаг после восстановления или чистой установки) и удаляет старую `DEPLOY_KEY`; вручную - если шаг пропущен или не удался | Группа / Settings / CI/CD / Variables | [Серверные расширения](../server-extensions/README.md#ключи-выкладки-из-ci) |
| Доступ заданий к пакетам: группа `client-modules` в Job token permissions проекта ClientSdk. Разрешите заданиям читать пакеты и **публиковать** их в реестр: задания `nuget_*` отправляют пакет с `CI_JOB_TOKEN` | Проект / Settings / CI/CD / Job token permissions | [README ClientSdk, 4.1](https://github.com/PilotOpenTools/PilotClientSdk/blob/main/docs/guide.md#41-nuget-пакет) |
| Защита веток в репозиториях модулей: `main` и `master` - Allowed to push: No one, Allowed to merge: Maintainers; `release/*` - Allowed to push: **Maintainers** (админка создаёт `release/X.Y.Z` и коммитит в неё, при No one коммиты отклонятся), Allowed to merge: Maintainers; `dev` - push и merge: Maintainers (админка коммитит в `dev` `.gitlab-ci.yml` при подключении модуля). Нужна, потому что Protected-переменные `STAGE` и `PROD` видят только задания защищённых веток. Установщик ставит её сам на машине с ролью `gitlab` во всех незаархивированных проектах групп модулей | Проект / Settings / Repository / Protected branches | [Клиентские модули](../client-modules/README.md#подключение-репозитория-модуля) |
| Проверка job-токена админкой: админка обращается к GitLab (`/api/v4/job`) из своего контейнера по адресу из настроек группы модулей; адрес должен быть доступен из контейнера админки, а не только с хоста | Настройки админки | [README админки](../proadmin/README.md) |
| «Pipelines must succeed» во всех репозиториях модулей, автоматизаций и отчётов: проверьте и включите вручную, если создавший репозиторий инструмент этого не сделал | Проект / Settings / Merge requests | |

Старая общая переменная `DEPLOY_KEY` больше не используется: после перехода на три ключа удалите её из групп. Задания проверок (например, `check_libraries`) ключа не требуют и шлют только `CI_JOB_TOKEN`.

### Закрытие регистрации

В **Admin / Settings / General / Sign-up restrictions** снимите «Sign-up enabled» (или оставьте включённым «Require admin approval for new sign-ups» и «Send confirmation email»). Параметр хранится в базе GitLab, в `GITLAB_OMNIBUS_CONFIG` его задать нельзя, поэтому после восстановления из копии проверьте его ещё раз. Установщик на машине с ролью `gitlab` снимает «Sign-up enabled» сам, и при чистой установке, и после восстановления; если его шаг после установки не прошёл, снимите флаг вручную. То же с Gravatar и отправкой статистики: параметры compose действуют на новую базу, а в восстановленной берутся значения из копии (**Admin / Settings / General / Account and limit**, **Metrics and profiling**).

### Если GitLab развёрнут из чужой копии

Копия данных (`gitlab-secrets.json`, SSH host-ключи, `runner-config/config.toml`, токены) у всех получателей одна и та же. Перед рабочим использованием замените общие секреты:

1. Смените пароль `root` и пароли учёток, переданных вместе с копией.
2. Удалите `config/ssh_host_*` и выполните `docker compose exec gitlab gitlab-ctl reconfigure`: ключи создадутся заново.
3. В **Admin / CI/CD / Runners** пересоздайте runner и зарегистрируйте контейнер заново (раздел «Runner»); старый токен отзовите.
4. Отзовите deploy-токены (в том числе `nuget-read` без срока действия) и создайте новые; пересоздайте OAuth-приложения и обновите их секреты в настройках админки и Reports.
5. Задайте новые значения `DEPLOY_KEY_TEST`, `_STAGE`, `_PROD` в группах и такие же в `.env` стека.
6. Ключи шифрования `db_key_base` и `secret_key_base` в `gitlab-secrets.json` меняются только по документации GitLab на вашу версию; без неё не меняйте, иначе зашифрованные значения (CI-переменные, 2FA) станут нечитаемыми.

## Обновление

Образы закреплены версией и digest в `docker-compose.yml`, поэтому `docker compose pull` версию не меняет. Для обновления:

1. Снимите копию: `.\backup\backup.ps1` из папки `stack`.
2. Проверьте [путь обновления GitLab](https://docs.gitlab.com/update/upgrade_paths/): обязательные промежуточные версии пропускать нельзя.
3. Поменяйте образ в `docker-compose.yml` и выполните `docker compose up -d`. Остановка (`docker compose stop`, `down`, пересоздание) ждёт GitLab до 10 минут: `stop_grace_period` даёт время корректно закрыть базу. Версию Runner держите равной версии GitLab.

Подробнее о закреплении образов - в [`stack/README.md`](../stack/README.md#версии-образов).

## Типовые проблемы

| Симптом | Причина | Что делать |
|---|---|---|
| `port is already allocated` | порт занят | поменять `GITLAB_HTTP_PORT` или `GITLAB_SSH_PORT` в `.env`, затем `docker compose up -d` |
| `Reading unsupported config value` и перезапуски по кругу | в `GITLAB_OMNIBUS_CONFIG` параметр, которого нет в этой версии образа | убрать строку, названную в `docker compose logs gitlab`, затем `docker compose up -d --force-recreate gitlab` |
| Долго нет `healthy` или падение по памяти | мало памяти | дать Docker Desktop не меньше 6 ГБ |
| Не открывается с других компьютеров | брандмауэр или неверный `GITLAB_HOST_IP` | открыть порты 8080 и 2224, указать настоящий IP |
| Задание не находит хосты, в логе адреса `127.x` | Proxifier подменяет DNS | `dns` в `config.toml`, см. «Runner» |
| Runner отвечает 500 при загрузке артефактов | артефакты на папке NTFS | должен быть подключён том `gitlab_artifacts`, см. `docker-compose.yml` |
| Вложения и zip в релизах не загружаются (500, `Errno::ENOENT` в carrierwave) | загрузки на папке NTFS | должен быть подключён том `gitlab_uploads`, см. `docker-compose.yml` |
| Gitaly отклоняет push, `dubious ownership` | после переноса данных репозитории принадлежат root | `docker exec gitlab chown -R git:git /var/opt/gitlab/git-data/repositories` |
| После переноса папки GitLab пустой | контейнер хранит старый абсолютный путь, Docker создал пустые папки | `docker compose up -d --force-recreate` из новой папки |
