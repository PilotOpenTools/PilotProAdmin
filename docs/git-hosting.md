# Git-хостинг

| Сервис | Что поддерживается |
| --- | --- |
| GitLab | Ветки, merge request, проверки CI, артефакты и выкладка на контуры Pilot. Можно подключить существующий GitLab или поставить новый установщиком из релиза |
| GitHub.com | Публикация типов на тест из влитого pull request с успешным GitHub Actions. Редактирование и слияние выполняются в GitHub |
| Bitbucket Cloud и Data Center, GitVerse, Azure DevOps / Azure DevOps Server (TFS), Gitea и Forgejo | Прямые интеграции отсутствуют |

## GitLab

Укажите адрес своего GitLab и группы проектов. Если нужен новый сервер, на вопрос установщика о GitLab ответьте «установить здесь». [Настройки GitLab, Runner и CI](../gitlab/README.md).

## GitHub.com

1. Укажите `App__GitHubTypes__Repository=https://github.com/владелец/репозиторий`. В `.env` папки установки этому параметру соответствует `GITHUB_TYPES_REPOSITORY`.
2. Войдите в PilotProAdmin учётной записью администратора Pilot. В разделе «Типы» скачайте экспорт типов с `ci/validate.py` и `.github/workflows/pilot-types.yml`. Поместите файлы в репозиторий и настройте обязательную проверку `validate` для pull request в `dev`. Ограничьте изменение workflow и валидатора правилами репозитория.
3. Подготовьте изменения в GitHub, влейте pull request в `dev` и дождитесь успешного запуска `pilot-types.yml` на коммите слияния.
4. Введите номер PR и токен GitHub с правами чтения Contents, Pull requests и Actions. У владельца токена должны быть права записи в репозиторий. Нажмите «Проверить CI и записать на тест».

PilotProAdmin проверяет, что коммит слияния остаётся текущим коммитом `dev`. Результат публикации появится в «Операциях».
