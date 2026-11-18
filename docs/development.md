# Разработка

Для сборки приложения используйте .NET SDK 10.

```powershell
dotnet test proadmin/PilotProAdmin.sln -c Release
dotnet test proadmin/migration/PilotMigration.sln -c Release
dotnet run --project proadmin/src/ProAdmin/PilotProAdmin.csproj -- --urls http://127.0.0.1:5300
```

Часть интеграционных тестов требует отдельного Pilot или PostgreSQL. Параметры подключения описаны в [документации приложения](https://github.com/PilotOpenTools/PilotProAdmin/tree/main/proadmin).

## Сайт документации

Из корня репозитория выполните:

```powershell
dotnet tool restore
dotnet docfx docfx.json --warningsAsErrors
```

Результат в `_site`. Workflow Docs публикует его через GitHub Pages после сборки основной ветки.

## Сборка релиза

Команда для локальной проверки Windows-архивов:

```powershell
./scripts/package-release.ps1 -Version 1.0.0
```

Workflow Release можно запустить вручную с основной ветки, чтобы собрать архивы и проверить оба контейнера. Тег вида `v1.0.0` публикует ZIP для Windows и архив образов Linux в GitHub Releases. Образы также загружаются в GitHub Container Registry организации. Версию веб-приложения задаёт `proadmin/src/ProAdmin/PilotProAdmin.csproj`.
