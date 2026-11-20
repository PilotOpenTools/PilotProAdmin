# TestModule - тестовый модуль CI/CD

`114TestModule.ext2` (net48). Нужен, чтобы проверить весь путь клиентского модуля: GitLab-pipeline / админка («Модули») / сервер Pilot / статистика в Grafana.

ПКМ на документе (тип `document`) / **Тестовое меню**:

| Пункт | Что делает | В Grafana («Pilot - клиентские модули») |
|---|---|---|
| 1. Всё хорошо | сообщение | действие «Тест: всё хорошо», статус `success`, ошибок нет |
| 2. Деление на ноль без try | `Stats.Run` записывает ошибку и пробрасывает исключение в Pilot | `error`, `DivideByZeroException` |
| 3. Деление на ноль с try | `catch` / `Stats.Error` + сообщение | `error`, `DivideByZeroException` |

Требует `113Pilot.ClientSdk.ext2` на сервере (назначен пользователю). Если рядом с клоном PilotProAdmin лежит клон [PilotClientSdk](https://github.com/PilotOpenTools/PilotClientSdk) (`..\PilotClientSdk`), проект собирается по его исходнику (ProjectReference), реестр пакетов не нужен. Иначе модуль компилируется против NuGet-пакета `Pilot.ClientSdk` из реестра GitLab: в `nuget.config` образец адреса (`gitlab.example.local`, `PROJECT_ID`) замените на свой, вход на своей машине - [руководство PilotClientSdk, 4.2](https://github.com/PilotOpenTools/PilotClientSdk/blob/main/docs/guide.md#42-источник-gitlab-на-своей-машине). Недоступный источник из образца даёт предупреждение NU1900 и замедляет restore, но сборку по исходнику не ломает.
Сборка: `dotnet build -c Release` / `bin/114TestModule.ext2-<версия>.zip`. CI - `.gitlab-ci.yml`, точная копия `../ci-template/.gitlab-ci.yml` (при обновлении шаблона копируйте файл целиком).

Модуль тестовый: в нём тестовое меню с ошибками, а при загрузке он пишет `%LocalAppData%\CICD\SharedLibsProbe.log`. Через CI он доходит до всех контуров, но пользователям рабочих мест его не назначают.
