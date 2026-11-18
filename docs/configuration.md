# Конфигурация

Веб-приложение читает `appsettings.json` и переменные с префиксом `App__`. Сервис миграции читает переменные с префиксом `Migration__`. Двойное подчёркивание разделяет уровни конфигурации.

| Переменная | Назначение |
| --- | --- |
| `App__DataDirectory` | Каталог настроек, операций и ключей приложения. По умолчанию `data` |
| `App__SetupKey` | Установочный ключ для подключения первого сервера Pilot |
| `App__PublicUrl` | Адрес приложения для браузеров и OAuth. Укажите HTTPS при размещении за обратным прокси |
| `App__GitLab__ClientId`, `App__GitLab__ClientSecret` | Реквизиты OAuth-приложения GitLab |
| `App__GitHubTypes__Repository` | URL репозитория типов на GitHub.com |
| `App__Migration__Url`, `App__Migration__ApiKey` | Внутренний URL и ключ сервиса миграции |
| `Migration__DataDirectory`, `Migration__ApiKey` | Каталог данных и ключ API со стороны сервиса миграции |

Сохраняйте каталоги данных при обновлении. Настройки подключения к тесту, стейджу и проду меняются на странице «Настройки».

## GitLab

Укажите адрес существующего GitLab и настройте OAuth с callback `<адрес PilotProAdmin>/GitLab/Callback`. Подключите контуры и группы проектов в админке. Роль `gitlab` установочного комплекта нужна, если хотите развернуть собственный GitLab. [Настройка GitLab и шаблоны CI](https://github.com/PilotOpenTools/PilotProAdmin/tree/main/gitlab).

## GitHub

Укажите `App__GitHubTypes__Repository=https://github.com/владелец/репозиторий` и добавьте workflow `.github/workflows/pilot-types.yml` в репозиторий типов. Редактирование и слияние выполняются в GitHub. PilotProAdmin загружает типы на тест из влитого PR после успешного GitHub Actions. [Настройка репозитория типов](https://github.com/PilotOpenTools/PilotProAdmin/blob/main/stack/Install/README.md#внешние-сервисы).

## Миграция

Задайте одинаковое случайное значение для `App__Migration__ApiKey` в приложении и `Migration__ApiKey` в сервисе. При локальном запуске используйте `App__Migration__Url=http://127.0.0.1:5320`. В Docker укажите адрес сервиса по общей сети.

Сервис использует PostgreSQL и файловый архив соответствующего контура Pilot. Для существующего S3 укажите настройки архива в Pilot и предоставьте сервису миграции доступ к хранилищу. Раздел «Миграция» проверяет подключения и показывает план операции. [Настройки каталогов, PostgreSQL и архивов](https://github.com/PilotOpenTools/PilotProAdmin/tree/main/proadmin/migration).
