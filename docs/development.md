# Разработка

Для сборки приложения используйте .NET SDK 10.

```powershell
dotnet test proadmin/PilotProAdmin.sln -c Release
dotnet test proadmin/migration/PilotMigration.sln -c Release
dotnet run --project proadmin/src/ProAdmin/PilotProAdmin.csproj -- --urls http://127.0.0.1:5300
```

Часть интеграционных тестов требует отдельного Pilot или PostgreSQL. Параметры подключения описаны в [документации приложения](../proadmin/README.md#разработка).

## Сайт документации

Из корня репозитория выполните:

```powershell
dotnet tool restore
dotnet docfx docfx.json --warningsAsErrors
```

Результат в `_site`. Workflow Docs публикует его через GitHub Pages после сборки основной ветки.

Из этого же HTML собираются PDF инструкций. После DocFX запустите `node scripts/build-guide-pdfs.mjs` с установленным Google Chrome. Если Chrome не в `PATH`, передайте путь к исполняемому файлу аргументом.

## Сборка релиза

Команда для локальной сборки и проверки lite:

```powershell
./scripts/package-release.ps1 -Version 1.0.3
```

Workflow Release проверяет оба контейнера и сборку архивов в PR в `main`. Его также можно запустить вручную. После слияния тег `v1.0.3` публикует два архива в GitHub Releases и оба образа с тегом `1.0.3` в GHCR. Для онлайн установки пакеты `pilotproadmin` и `pilotproadmin-migration` должны быть публичными. Workflow проверяет доступ к обоим без учётных данных. Версию задаёт `proadmin/src/ProAdmin/PilotProAdmin.csproj`.
