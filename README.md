<p align="center"><img src="docs/images/banner.png" alt="PilotProAdmin" width="720"></p>

# PilotProAdmin

Веб-приложение для администрирования Pilot. Управляет выкладкой автоматизаций, отчётов, модулей и типов по контурам. Сервис PilotMigration переносит базы и файловые архивы.

[Документация](https://pilotopentools.github.io/PilotProAdmin/) · [Быстрый старт](https://pilotopentools.github.io/PilotProAdmin/getting-started.html) · [Скачать релиз](https://github.com/PilotOpenTools/PilotProAdmin/releases/latest)

Подключите существующие Pilot, GitLab, PostgreSQL и S3 или разверните стенд из установочного комплекта. Рядом лежат шаблоны CI и исходники клиентских и серверных расширений.

Обычный путь изменения: ветка в GitLab / проверки CI / тест / стейдж / прод. Он зависит от вида содержимого и настроек: у отчётов стейджа по умолчанию нет, у автоматизаций тест и стейдж можно объединить, а если адрес стейджа не задан, перенос идёт из `dev` в прод. Запись на серверы Pilot идёт через веб-админку (выкладка из CI подтверждается токеном задания и ключом контура), статистика и логи собираются в Grafana.

[Устройство стенда](https://pilotopentools.github.io/PilotProAdmin/architecture.html)

## Что поддерживается

- **Git-хостинг.** Полный CI/CD работает с GitLab. GitHub.com поддерживает публикацию типов на тест. [Матрица Git-хостингов](https://pilotopentools.github.io/PilotProAdmin/git-hosting.html).
- **Pilot-Server.** Админка подключается к существующему монолиту или кластеру. Установщик разворачивает монолит. Перевод установленного контура в кластер описан отдельно и выполняется вручную. [Варианты развёртывания](https://pilotopentools.github.io/PilotProAdmin/deployment.html).

## Установка

Папка `stack/Install` передаётся на сервер целиком. Инструкции опубликованы на сайте и доступны там для скачивания в PDF.

| Документ | Для кого |
|---|---|
| [Установка стенда](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/installation.html) | администратор, устанавливающий стенд на одну или несколько машин |
| [Безопасность](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/security.html) | служба ИБ |
| [GitLab без интернета](https://pilotopentools.github.io/PilotProAdmin/stack/Install/docs/gitlab-offline.html) | администратор GitLab и разработчики в закрытом контуре |

Заполняет папку перед передачей скрипт `stack/build/build.ps1`, заказчику он не нужен. В git лежат скрипты, compose и документы; каталоги `images/`, `data/`, `monitoring/`, `modules/`, файлы `IMAGES.txt` и `PASSWORDS.md` заполняет сборка, в git они не попадают, поэтому установить стенд из чистого клона без собранного комплекта нельзя. Для закрытого контура нужен комплект без интернета (ключ `-Offline` у сборки).

## Что в папках

### [`stack/`](stack/README.md) - контуры и инфраструктура

Здесь Docker Compose и скрипты; исходники .NET есть только у сборщика статистики отчётов и модулей (`metrics/report-collector`, решение `ReportMetrics.Collector.sln` с тестами), у сборщика автоматизаций исходники на Python.

| Папка | Что делает |
|---|---|
| `prod/`, `test/`, `stage/` | Контуры Pilot: Pilot-Server, PostgreSQL и RustFS (S3-хранилище файлов), на проде ещё Pilot-myAdmin Web (в `Install` для test и stage он ставится ключом `-WithMyAdmin`) |
| `metrics/` | Мониторинг: Prometheus, Loki, Grafana и сервисы статистики автоматизаций и отчётов |
| `backup/` | Ежедневные резервные копии в `D:\Backups`, проверка и восстановление |
| `Install/` | Установочный комплект для другого сервера |
| `shared/` | Запуск всего стека и смена паролей контуров |

### [`proadmin/`](proadmin/README.md) - веб-админка и миграция

| Решение | Что делает |
|---|---|
| `PilotProAdmin.sln` | Веб-админка (:5300). Через неё автоматизации, отчёты, клиентские и серверные модули, типы и настройки серверов проходят путь от веток GitLab до серверов Pilot |
| `migration/PilotMigration.sln` | Сервис миграции (:5320, по умолчанию только на 127.0.0.1): перенос данных между контурами, импорт присланных файловых баз и выгрузка в файлы |

### [`gitlab/`](gitlab/README.md) - GitLab и CI

GitLab и Runner в Docker. Папка `ci-checks/` содержит общие проверки автоматизаций и отчётов, которые запускаются в CI этих репозиториев. Решений .NET здесь нет.

### [`client-modules/`](client-modules/README.md) - модули клиента Pilot

| Решение | Что делает |
|---|---|
| `Pilot.ClientSdk/Pilot.ClientSdk.sln` | Общий клиентский модуль и NuGet-пакет: статистика модулей в Grafana и общие функции |
| `Test-module/TestModule.sln` | Тестовый модуль для проверки CI/CD и ошибок в Grafana |

Папка `ci-template/` содержит шаблон CI для репозитория клиентского модуля.

### [`Reports/`](Reports/README.md) - модуль отчётов

Клиентский модуль: проект `Reports.csproj` и решение `Reports.sln` (проект плюс тесты). Даёт скриптам отчётов общие функции и доступ к Pilot SDK, считает статистику построений и отправляет отчёты в GitLab при сохранении.

### [`server-extensions/`](server-extensions/README.md) - модули Pilot-Server

| Решение | Что делает |
|---|---|
| `ServerHost/ServerHost.sln` | Загрузчик серверных модулей и его тесты: ставится в Pilot один раз, остальные модули обновляет без перезапуска сервера |
| `Debug/Debug.sln` | Утилита для запуска серверного модуля под отладчиком на машине разработчика, на реальных данных |
| `TestServerAction/TestServerAction.sln` | Пример обычного серверного модуля, образец для новых |

Папка `ci-template/` содержит шаблон CI для репозитория серверного модуля.

## Запуск и остановка стека

```powershell
.\stack\shared\stack.ps1 up|down|ps|build [prod|test|stage|admin|metrics]
```

Цель `admin` в `stack.ps1` включает и GitLab, и админку с миграцией (`down admin` остановит оба). В `stack/Install` роль `admin` - только админка и миграция, GitLab ставится отдельной ролью `gitlab`.

## Репозитории

В GitLab стенда модули ведутся отдельными проектами. Вложенные `.gitlab-ci.yml` повторяют шаблоны из папок `ci-template`.
