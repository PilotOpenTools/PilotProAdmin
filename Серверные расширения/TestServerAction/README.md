# TestServerAction - пример серверного действия под автоматизацию

`TestServerAction.servext.dll` (netstandard2.0). Активность `TestServerAction` (`IServerActivity`): при срабатывании
автоматизации записывает в атрибут `documentVersion` объекта-источника текущие дату и время (UTC). Ошибки активность не
подавляет: их фиксирует хост (журнал и статистика модуля).

Модуль грузится не штатным загрузчиком Pilot, а через **ServerHost** - обновляется без перезапуска сервера.

## Сборка

```bash
dotnet build -c Release
```

Результат - `bin/Release/TestServerAction.servext.dll` и `dist/TestServerAction.zip`.
`Ascon.Pilot.*` и `System.ComponentModel.Composition` уже есть в Pilot-Server и в архив не попадают.

## Отладка

`debug.json` - настройки для `debug` (запуск модуля под отладчиком против реального Pilot, F5 в Visual Studio).
Файл выдаёт админка на странице «Серверные модули». В репозитории лежит образец с `"apply": false`: на сервер ничего не
записывается, пока вы сами не включите `"apply": true` (или `--apply`); запись перед запуском подтверждается в консоли.
Профиль отладки - `Properties/launchSettings.json` (относительный путь к `Debug`, нужна публикация `dotnet publish -c Release`).

## Ветки и выкладка

CI - `.gitlab-ci.yml` (копия `Серверные расширения/ci-template`; переменные группы GitLab для выкладки - в `Серверные расширения/README.md`):

| Ветка | Контур |
|---|---|
| `dev` | тест |
| `release/X.Y.Z` | стейдж |
| `main` | прод (тег `vX.Y.Z` и GitLab Release ставит веб-админка) |

Версию (`<Version>` в `.csproj`) и раздел `CHANGELOG.md` выставляет веб-админка при релизе («Релиз / стейдж», «Hotfix»);
вручную их меняют только при первом выпуске модуля. `VERSION` активности берётся из версии сборки.
