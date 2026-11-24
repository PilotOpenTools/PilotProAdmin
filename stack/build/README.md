# Самопроверки комплекта и источник данных Grafana

В папке нет сборки: установка идёт из файлов релиза, их собирают `scripts/package-release.ps1` и workflow Release (см. [«Установка и устройство пакета»](../../docs/installation-package.md)).

| Файл | Что это |
|---|---|
| `pilot-metrics.yml` | Источник данных Grafana для комплекта: релиз кладёт его в `monitoring/grafana/provisioning/datasources/` |
| `selftest/release-restore-selftest.ps1` | Восстановление установщиком из релиза: запускает workflow Release |
| `selftest/cluster-selftest.sh` | Файлы кластерного Pilot-Server: запускает workflow Release перед упаковкой `cluster.tar.gz` |
| `selftest/install-selftest.sh`, `selftest/install-selftest.ps1` | Самопроверки `install.sh` и `install.ps1` без стенда: docker подменён заглушкой |
| `selftest/compose-env-selftest.sh` | `docker compose config` на `.env`, который создаёт `install.sh` (каждая роль, повторный запуск, несколько машин, бэкап): обязательные пароли и понятный текст отказа |
| `selftest/wait-for-postgres-selftest.sh` | Обёртка запуска Pilot-Server `Install/wait-for-postgres.sh` без стенда |
| `selftest/roles-live-selftest.sh` | Роли PostgreSQL для статистики (`Install/init/postgres/02-metrics-roles.sh`) на настоящем PostgreSQL в одноразовом контейнере |
| `selftest/firewall-live-selftest.sh` | Файл правил брандмауэра `firewall-linux.sh` на настоящем `iptables` в одноразовом контейнере (`--privileged`, нужен интернет): разбор, применение, повторное применение, трафик через подмену адреса, снятие |
| `selftest/post-restore-flow.rb` | Поток `Install/gitlab-post-restore.rb` на подмене REST API GitLab: защита веток, коммит `nuget.config`, переменные, токен |

Команды ниже запускаются из папки `stack`.

## Самопроверка установщиков

Без стенда и без запуска контейнеров проекта (docker подменён заглушкой, команды GitLab выполняются локально):

```powershell
# install.sh в одноразовом контейнере Linux
docker run --rm -v "${PWD}:/stack:ro" postgres:16 bash /stack/build/selftest/install-selftest.sh
# install.ps1 в Windows PowerShell 5.1 (нужен образ alpine:3)
powershell -NoProfile -ExecutionPolicy Bypass -File build\selftest\install-selftest.ps1
```

Проверяются права `.env`, замена паролей-заглушек, генерация и перенос ключей выкладки, отказ при нескольких машинах и при потерянном `.env`, порядок и условия восстановления GitLab (код возврата, число проектов, метка `.restore-started`, таймаут), имя тома Grafana, обязательные пароли в `.env`, отказ init-скрипта PostgreSQL без пароля. Живой GitLab, Docker Desktop и сеть они не заменяют.

Обязательные пароли compose проверяются настоящим `docker compose config` (демон не нужен; `install.sh` в этом скрипте идёт с заглушкой docker):

```powershell
bash build/selftest/compose-env-selftest.sh
```

Файл правил брандмауэра для Linux проверяется на настоящем `iptables` (в контейнере создаются сетевые пространства имён, правила хоста не затрагиваются):

```powershell
docker run --rm --privileged -v "${PWD}:/stack:ro" alpine:3 sh -c 'apk add -q --no-cache bash && bash /stack/build/selftest/firewall-live-selftest.sh'
```

Генерацию правил (Linux и Windows), HTTPS-надстройку (включение, отключение, ошибки в файлах, свой `COMPOSE_FILE`) и ключи админки при восстановлении из бэкапа проверяют `install-selftest.sh` и `install-selftest.ps1`, а `compose-env-selftest.sh` пропускает через `docker compose config` `.env` с включённым HTTPS. Файл Windows проверяется разбором PowerShell и самотестом генерации; действие правил Windows Firewall на живой машине не проверялось.

Поток шага GitLab после установки проверяется отдельно, без GitLab (нужен образ ruby:3.3-alpine):

```powershell
docker run --rm -v "${PWD}:/stack:ro" ruby:3.3-alpine ruby /stack/build/selftest/post-restore-flow.rb /stack/Install/gitlab-post-restore.rb
```

Подмена API не воспроизводит GitLab: передачу окружения через обёртку `gitlab-rails` и реальную работу `[skip ci]` она не проверяет.
