# Конфигурация

Веб-приложение читает `appsettings.json` и переменные с префиксом `App__`. Сервис миграции читает переменные с префиксом `Migration__`. Двойное подчёркивание разделяет уровни конфигурации.

| Переменная | Назначение |
| --- | --- |
| `App__DataDirectory` | Каталог настроек, операций и ключей приложения. По умолчанию `data` |
| `App__SetupKey` | Ключ установки: защищает мастер первого входа, пока админка не настроена (`ADMIN_SETUP_KEY` в `.env` папки установки) |
| `App__FirstRunSetup` | Показывать мастер первого входа (подключение GitLab, адресов Pilot и служебных учёток). Установщик включает его сам |
| `App__Defaults__*` | Начальные адреса для первого запуска: `TestPilotUrl`, `StagePilotUrl`, `ProdPilotUrl`, `GitLabGroupUrl`, `ReportsGitLabGroupUrl`, `ModulesGitLabGroupUrl`, `ServerModulesGitLabGroupUrl`, `TypesGitLabProjectUrl`, `GrafanaUrl`. Потом они меняются в «Настройках» |
| `App__CiDeploy__Keys__test`, `__stage`, `__prod` | Ключи контуров для выкладки модулей из GitLab CI (`MODULES_DEPLOY_KEY_*` в `.env`) |
| `App__PublicUrl` | Адрес приложения для браузеров и OAuth. Укажите HTTPS при размещении за обратным прокси |
| `App__GitLab__ClientId`, `App__GitLab__ClientSecret` | Реквизиты OAuth-приложения GitLab |
| `App__GitHubTypes__Repository` | URL репозитория типов на GitHub.com |
| `App__Migration__Url`, `App__Migration__ApiKey` | Внутренний URL и ключ сервиса миграции |
| `Migration__DataDirectory`, `Migration__ApiKey` | Каталог данных и ключ API со стороны сервиса миграции |

При установке из релиза эти переменные задаёт установщик: значения лежат в `.env` папки установки ([справочник параметров](../stack/Install/docs/installation.md#параметры-env)). Сохраняйте каталоги данных при обновлении. Настройки подключения к тесту, стейджу и проду меняются на странице «Настройки».

## GitLab

Укажите адрес существующего GitLab и настройте OAuth с callback `<адрес PilotProAdmin>/GitLab/Callback`. Подключите контуры и группы проектов в админке. Если нужен новый GitLab, на вопрос установщика о GitLab ответьте «установить здесь»: OAuth-приложение и runner он создаст сам. [Настройка GitLab и шаблоны CI](https://pilotopentools.github.io/PilotProAdmin/gitlab/README.html).

## GitHub

Укажите `App__GitHubTypes__Repository=https://github.com/владелец/репозиторий`. [Подключение GitHub и публикация типов](https://pilotopentools.github.io/PilotProAdmin/git-hosting.html#githubcom).

## Миграция

Задайте одинаковое случайное значение для `App__Migration__ApiKey` в приложении и `Migration__ApiKey` в сервисе. При локальном запуске используйте `App__Migration__Url=http://127.0.0.1:5320`. В Docker укажите адрес сервиса по общей сети.

Сервис использует PostgreSQL и файловый архив соответствующего контура Pilot. Для существующего S3 укажите настройки архива в Pilot и предоставьте сервису миграции доступ к хранилищу. Раздел «Миграция» проверяет подключения и показывает план операции. [Настройки каталогов, PostgreSQL и архивов](https://pilotopentools.github.io/PilotProAdmin/proadmin/migration/README.html).
