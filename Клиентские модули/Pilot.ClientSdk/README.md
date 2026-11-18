# Pilot.ClientSdk - общий модуль для клиентских модулей Pilot

> До версии 2.0.0 назывался **Modules**: сборка `113Modules.ext2`, пространство имён `Modules`, пакет `Modules`. Перевод модуля на новое имя:
> - в `.csproj`: `Modules` / `<PackageReference Include="Pilot.ClientSdk" Version="2.0.0" />`; в `nuget.config` фильтр источника `cicd` - `Pilot.*`;
> - в коде: `using Modules…` / `using Pilot.ClientSdk…`.
>
> Модуль, собранный против `113Modules.ext2`, с 2.0.0 не загрузится: его нужно пересобрать. На серверах объект модуля тот же - веб-админка обновила его по прежнему имени (`AssemblyMetadata("PreviousNames")`).

# Общие данные

- Постановщик задания на создание: -
- Отделы, использующие модуль: разработчики клиентских модулей Pilot
- Задача в Битриксе и/или в Инфраменеджере: -
- Не явно связанные модули: `111NugetPackage.ext2` (прежний общий модуль - Pilot.ClientSdk его не заменяет автоматически, см. [раздел 5](#5-переход-с-nugetpackage)), `112Reports.ext2` (то же для отчётов)
- Веб-админка: `proadmin`, вкладка «Модули» (CI/CD модулей: тест / стейдж / прод)
- Сервис статистики: `stack/metrics/report-collector` (`POST /api/module-events`), дашборд Grafana «Pilot - клиентские модули»
- Имя общих настроек: **«Модули общие настройки»** (ключ `Pilot.ClientSdk-6C1F2E7A-3B54-4D8E-9A0B-71E5C2D4F813`), см. [2.3](#23-отправка-и-настройка); значения по умолчанию - `Pilot.ClientSdk.config.json` рядом с dll
- Путь к файлу логирования: `%AppData%\ASCON\Units Logs\Logs\Pilot.ClientSdk.log`

# Краткое описание

Клиентский модуль Pilot `113Pilot.ClientSdk.ext2` (net48, пространство имён `Pilot.ClientSdk`). Его подключают другие клиентские модули. Он делает три вещи:

1. **Статистика модулей в Grafana.** `ModuleStats` считает, какие действия модулей вызывают, сколько из них успешны, сколько длятся и с какой ошибкой падают. Отдельно модуль сообщает, какие модули и в каких версиях загружены у пользователя. Так после выкладки из админки видно, дошла ли новая версия до рабочих мест.
2. **Проверка загрузки модулей.** При старте клиента поверх главного окна Pilot открывается окно «Загрузка модулей»: все ли назначенные пользователю модули скачались, загрузились и запустились и сколько занял запуск. Пока проверка идёт, окно нельзя закрыть (до 15 с).
3. **Общие методы SDK.** Загрузка объектов, поиск, атрибуты, навигация по дереву, оргструктура, изменение данных, нумераторы, лог, команды MVVM. Это переработанные популярные методы из `NugetPackage`: без NLog и System.Reactive (в общем AppDomain клиента их версии у разных модулей конфликтуют) и без известных ошибок старых загрузчиков.

```
Модуль (ModuleStats.Run …) ──Pilot.ClientSdk (клиент Pilot) ──пачка раз в 15 с──report-metrics-collector (:5310)
                                                                                     │

                                                PostgreSQL pilot_metrics.metrics.module_events ──Grafana «Pilot - клиентские модули»
```

---

## Содержание

1. [Быстрый старт](#1-быстрый-старт)
2. [Статистика модуля (ModuleStats)](#2-статистика-модуля-modulestats)
   - [2а. Стартовый класс (IModule) и окно загрузки модулей](#2а-стартовый-класс-imodule-и-окно-загрузки-модулей)
3. [Общие методы](#3-общие-методы)
4. [Сборка, выпуск, CI/CD](#4-сборка-выпуск-cicd)
5. [Переход с NugetPackage](#5-переход-с-nugetpackage)
6. [Как это устроено внутри](#6-как-это-устроено-внутри)
7. [Типовые проблемы](#7-типовые-проблемы)

---

## 1. Быстрый старт

Подключение - NuGet-пакет `Pilot.ClientSdk` из реестра пакетов GitLab (подробно - [4.1](#41-nuget-пакет); первый раз на машине - подключить источник GitLab и войти, [4.2](#42-источник-gitlab-на-своей-машине)). Сборка в пакете лежит в `ref/net48`: она нужна только для компиляции и в `bin` и zip модуля не копируется. В клиенте её уже загрузил модуль `113Pilot.ClientSdk.ext2`, вторая копия дала бы конфликт типов.

`nuget.config` рядом с `.csproj` модуля (образец - в `Test-module`; адрес GitLab `gitlab.example.local` и `PROJECT_ID` замените на свои, см. [4.1](#41-nuget-пакет)):

```xml
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="cicd" value="http://gitlab.example.local/api/v4/projects/PROJECT_ID/packages/nuget/index.json" allowInsecureConnections="true" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
    <packageSource key="cicd"><package pattern="Pilot.*" /></packageSource>
  </packageSourceMapping>
</configuration>
```

`.csproj` модуля:

```xml
<PackageReference Include="Pilot.ClientSdk" Version="2.1.1" />
```

Копию dll в папке `lib/` модуля больше не храните.

Минимальный модуль:

```csharp
using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using Pilot.ClientSdk;
using Pilot.ClientSdk.Telemetry;

[Export(typeof(IMenu<ObjectsViewContext>))]
public class StartPoint : IMenu<ObjectsViewContext>, IModule     // IModule - обязательно, см. раздел 2а
{
    private static readonly ModuleStats Stats = ModuleStats.For<StartPoint>();   // модуль и версия - из сборки
    private static readonly ModuleLogger Log = new ModuleLogger("OpenStoradge");
    private readonly IObjectsRepository _repository;
    private Loader _loader;

    [ImportingConstructor]
    public StartPoint(IObjectsRepository repository, ISearchService search, IObjectModifier modifier)
    {
        PilotApi.Init(repository, search, modifier);   // если модуль стартует раньше Pilot.ClientSdk - не помешает
        _repository = repository;
        StartupRunner.Run(this);                          // последней строкой: замер запуска для окна загрузки
    }

    public void Initialize() => _loader = new Loader(_repository);

    public void Build(IMenuBuilder builder, ObjectsViewContext context) =>
        builder.AddItem("OpenStoradge", 0).WithHeader("Открыть Storadge");

    public async void OnMenuItemClick(string name, ObjectsViewContext context)
    {
        if (name != "OpenStoradge")
            return;
        try
        {
            await Stats.RunAsync("Открыть Storadge", async op =>
            {
                var obj = context.SelectedObjects.First();
                var project = await _loader.ProjectAsync(obj);              // папка 0_project выше по дереву
                var docs = await PilotApi.Searcher.ByTypeAsync("pir_doc", inContext: project?.Id);
                op.Items = docs.Count;                                      // сколько обработано - в Grafana
                Open(project.Str("storadge_path"), docs);
            });
        }
        catch (Exception ex)
        {
            Log.Error("Открыть Storadge", ex);                              // ошибка уже в статистике, здесь - в лог
            PilotApi.Dialogs?.ShowError(ex.Message, "Открыть Storadge");   // (текст ошибки, заголовок)
        }
    }
}
```

> **Модуль с `using Pilot.ClientSdk` работает, только если `113Pilot.ClientSdk.ext2` назначен пользователю на сервере.** Без него клиент не загрузит типы модуля. Назначьте Pilot.ClientSdk всем пользователям раньше модулей, которые на нём построены.

---

## 2. Статистика модуля (ModuleStats)

### 2.1. API

| Член | Что делает |
|---|---|
| `ModuleStats.For<T>()`, `ModuleStats.For(assembly)`, `ModuleStats.Current` | статистика модуля, которому принадлежит сборка. Имя = имя сборки (`087OpenStoradge.ext2`), название = `AssemblyProduct` (как в Pilot-myAdmin), версия = `AssemblyVersion` |
| `Run("Действие", () => …)` / `Run("Действие", op => …)` / `Run<T>(…)` | выполнить и записать: длительность, успех, ошибку (исключение пробрасывается дальше), отмену (`OperationCanceledException`) |
| `RunAsync(…)` | то же для async-кода |
| `Start("Действие")` | замер через `using`: `op.Fail(ex)`, `op.Cancel()`, `op.Items = n`; без них - успех |
| `Use("Действие", items)` | факт использования без длительности (открыли окно, нажали кнопку) |
| `Error("Действие", ex)`, `Cancelled("Действие")` | записать ошибку или отмену, пойманную модулем самим |

Название действия - то, что увидит аналитик в Grafana. Пишите его по-русски, как пункт меню: «Создание бланков», «Выгрузка в Storadge». Не вставляйте в название Id объектов и имена файлов, иначе одно действие распадётся на тысячи строк.

### 2.2. Что отправляется

| Поле | Откуда |
|---|---|
| `kind` | `action` - действие, `load` - модуль загружен клиентом (его отправляет сам Pilot.ClientSdk через минуту после старта клиента, для каждого загруженного модуля) |
| `module`, `module_title`, `module_version` | атрибуты сборки модуля |
| `action`, `status` (`success` / `error` / `cancelled`), `duration_ms`, `items` | из `Run` / `Start` / `Use` |
| `error_type`, `error_message` | тип и текст исключения (корневого), текст обрезается до 2000 символов |
| `login`, `person`, `server`, `client_type`, `client_version`, `machine`, `lib_version` | текущий пользователь, адрес сервера Pilot (по нему сервис определяет контур prod/test/stage), клиент, компьютер, версия Pilot.ClientSdk |

Данные объектов, параметры и содержимое документов **не отправляются**. Но `login`, `person` (ФИО), `machine` (имя компьютера) и `server` (адрес подключения клиента к серверу Pilot) - персональные сведения, а в тексте ошибки могут оказаться пути и имена из сообщения исключения. Отправка идёт обычным HTTP без шифрования (ключ `apiKey` - в заголовке `X-Api-Key`): направляйте её только на сервис внутри доверенной сети или задайте `https://`-адрес. Если такие сведения отправлять нельзя - `statsEnabled: false`.

### 2.3. Отправка и настройка

События копятся в памяти и уходят пачкой раз в `flushSeconds` секунд. Если набралось 100 событий, пачка уходит сразу. Событие со статусом `error` тоже отправляется сразу, не дожидаясь периода, чтобы ошибка не потерялась при закрытии клиента. Оставшееся отправляется и при закрытии клиента (ждём не больше 3 с). На диск события не пишутся. Если сервис недоступен, пачка пишется в лог и теряется, остаток очереди ждёт следующего тика. Очередь ограничена 5000 событиями. События, которые модуль прислал до старта Pilot.ClientSdk, тоже ждут в очереди.

Настройки задаются в двух местах (по убыванию приоритета):

1. **Общие настройки Pilot / «Модули общие настройки».** Их задаёт администратор: Pilot / Сервис / Настройки / Общие настройки. Значения действуют на всех пользователей и важнее файла. Клиенты получают их при запуске и при каждом изменении, без перезапуска. Статистика, трей и список исключений применяются сразу, режим окна загрузки - при следующем запуске клиента. Кнопка «Проверить» у адреса делает `GET <сервер>/health` сервиса статистики с компьютера администратора. Кнопка «Очистить раздел» возвращает настройки из файла.
2. **`Pilot.ClientSdk.config.json`** лежит рядом с dll и входит в zip модуля. Его значения действуют, пока раздел общих настроек пуст. Редактор раздела при первом открытии показывает именно их.

Формат одинаковый: раздел общих настроек хранит тот же JSON.

```json
{
  "statsEnabled": true,
  "statsUrl": "http://<docker-хост>:5310/api/module-events",
  "apiKey": "",
  "flushSeconds": 15,
  "reportLoadedModules": true,
  "startupWindowMode": "always",
  "startupLockSeconds": 15,
  "startupAutoClose": true,
  "ignoredModules": [],
  "trayMenu": true
}
```

| Поле | В редакторе | Назначение |
|---|---|---|
| `statsEnabled` | Отправлять статистику | `false` - статистика не собирается |
| `statsUrl` | Адрес сервиса статистики | адрес сервиса сбора, доступный с рабочих мест. В поставляемом `Pilot.ClientSdk.config.json` стоит `http://localhost:5310/...`: он годится только на машине, где запущен сервис; на рабочих местах задайте адрес в общих настройках |
| `apiKey` | API-ключ | если в сервисе задан `COLLECTOR_API_KEY` - тот же ключ |
| `flushSeconds` | Интервал отправки | как часто отправлять пачку, 1-3600 с |
| `reportLoadedModules` | Отправлять список модулей | отправлять список загруженных модулей (модуль = сборка `*.ext2` с `AssemblyCompany("CICD")`) |
| `startupWindowMode` | Окно «Загрузка модулей» | `always` - при каждом запуске; `problems` - только если модуль не скачан, не загрузился или упал при запуске; `never` - не показывать, проверка пишется только в лог и Grafana. Старый флаг `"startupWindow": false` равен `never` |
| `startupLockSeconds` | Сколько ждать загрузки | сколько секунд окно нельзя закрыть, пока модули грузятся, и сколько их ждать (потом незагрузившийся модуль - ошибка), 0-600 с |
| `startupAutoClose` | Закрывать окно само | закрыть окно само, если все модули запустились без ошибок |
| `ignoredModules` | Не проверять модули | части названий модулей или имён сборок, которые не показываются в окне загрузки и не считаются ошибкой (например, сторонние модули) |
| `trayMenu` | Пункты в трее | показывать пункты «Модули версии / загрузка» в меню трея |

На машине разработчика адрес можно переопределить переменной окружения `CLIENTSDK_STATS_URL`.

### 2.4. Где смотреть

Grafana / папка «CI/CD Pilot» / дашборд **«Pilot - клиентские модули»** (`stack/metrics/report-collector/grafana/dashboard-pilot-modules.json`): плитки, сводка по модулям, графики по статусам и модулям, действия внутри модулей, **версии модулей на рабочих местах**, пользователи, самые долгие действия, последние ошибки.

В клиенте Pilot: трей / **«Модули версии»** - какие модули загружены, куда уходит статистика и откуда взяты настройки; **«Модули загрузка»** - окно проверки загрузки модулей.

---

## 2а. Стартовый класс (IModule) и окно загрузки модулей

### Регламент: стартовый класс модуля

**В каждом модуле стартовый класс реализует `IModule` (или `IModuleAsync`) и последней строкой `[ImportingConstructor]` вызывает `StartupRunner.Run(this)` (`StartupRunner.RunAsync(this)`).** Стартовый класс - тот, который клиент создаёт при старте: обычно `IDataPlugin` `StartPoint`, а если `IDataPlugin` нет - главный экспорт модуля (например, `IMenu<…>`). В конструкторе остаётся только сохранение сервисов SDK, вся остальная инициализация переезжает в `Initialize()`.

```csharp
[Export(typeof(IDataPlugin))]
public sealed class StartPoint : IDataPlugin, IModule
{
    private readonly IObjectsRepository _repository;
    private readonly IObjectModifier _modifier;

    [ImportingConstructor]
    public StartPoint(IObjectsRepository repository, IObjectModifier modifier)
    {
        _repository = repository;
        _modifier = modifier;
        StartupRunner.Run(this);          // замер: время, успех, ошибка
    }

    public void Initialize()
    {
        Instance = this;
        // подписки, загрузка настроек, регистрация обработчиков …
    }
}

// Асинхронная инициализация (загрузка объектов, поиск, сеть):
public sealed class StartPoint : IDataPlugin, IModuleAsync
{
    [ImportingConstructor]
    public StartPoint(IObjectsRepository repository)
    {
        _repository = repository;
        StartupRunner.RunAsync(this);     // без await: задача никогда не падает; пока она идёт, модуль «запускается…»
    }

    public async Task InitializeAsync() => _settings = await LoadSettingsAsync();
}
```

| Что | Как |
|---|---|
| Исключение из `Initialize` / `InitializeAsync` | **не пробрасывается**: клиент не падает, остальные части модуля работают. Ошибка попадает в окно загрузки («ошибка запуска» и текст), в `Pilot.ClientSdk.log` и в Grafana действием **«Запуск модуля»** со статусом `error` |
| Время | от `StartupRunner.Run` до конца `Initialize` (для async - до завершения задачи). Видно в окне и в Grafana (`duration_ms` действия «Запуск модуля») |
| Несколько стартовых классов в модуле | `Run` можно вызвать в каждом: в окне время суммируется, ошибка любого класса - ошибка модуля |
| Пример | [`Test-module/Pilot/StartPoint.cs`](../Test-module/Pilot/StartPoint.cs) |

Модулю, собранному с `IModule`, нужен Pilot.ClientSdk с этим интерфейсом на сервере. **Сначала выложите Pilot.ClientSdk, потом модули**, иначе клиент не загрузит модуль (`TypeLoadException`).

### Окно «Загрузка модулей»

Pilot.ClientSdk открывает его, когда появляется главное окно Pilot. Окно модальное: пока оно открыто, работать с Pilot нельзя. Закрыть его можно, **когда проверены все модули или прошло `startupLockSeconds` (15 с)**, на кнопке идёт обратный отсчёт. Если всё запустилось без ошибок, окно закрывается само (`startupAutoClose`). Открыть повторно: трей / **«Модули загрузка»**. Кнопка «Скопировать отчёт» кладёт таблицу в буфер обмена, её можно приложить к заявке в поддержку.

Как проверяется каждый модуль:

| Шаг | Откуда берётся | Статус в окне |
|---|---|---|
| 1. Назначен пользователю | объекты `Extension` в папке расширений сервера (`SystemObjectIds.ExtensionRootObjectId` и подпапки), которые видит пользователь, не в корзине | - |
| 2. Скачан | клиент распаковывает zip модуля в `%LocalAppData%\ASCON\<клиент>\Databases\<база>\<версия>\Plugins\<Body.Id файла>`: есть ли эта папка | **не скачан** (ошибка) |
| 3. Загружен | сборка `*.ext2` из этой папки (или с тем же именем) есть в AppDomain клиента | загрузка… / **не загружен** (ошибка, через 15 с) |
| 4. Запущен | сторонний модуль (без `AssemblyCompany("CICD")`) дальше не проверяется | загружен |
|  | модуль без `IModule` | загружен, без IModule (замечание) |
|  | модуль с `IModule` - по отчёту `StartupRunner` | ожидание запуска… / запускается… / **запущен** / **ошибка запуска**; нет отчёта за 15 с - «нет отчёта о запуске» (замечание) |

Модули, загруженные не с сервера (папка `Development`), показываются отдельными строками с пометкой «(локально)». Если список с сервера получить не удалось (нет связи или прав на папку расширений), окно проверяет только загруженные модули и пишет об этом под заголовком.

Итог проверки пишется в `Pilot.ClientSdk.log` (каждая проблема отдельной строкой) и уходит в Grafana действием **«Проверка загрузки модулей»** модуля Pilot.ClientSdk: `items` - сколько модулей проверено; при ошибках статус `error`, а в `error_message` - список модулей (тип `ModulesNotLoadedException`).

> Администратор с правами на всю папку расширений видит и модули, которые ему лично не назначены. У него такие модули будут «не скачан». У обычных пользователей список совпадает с назначенными модулями.

---

## 3. Общие методы

Пространство имён `Pilot.ClientSdk`. Все методы чтения безопасны к `null`: если объекта, атрибута или значения нет, вернётся `""`/`null`/пустой список, исключения не будет.

### Что было в NugetPackage / что использовать

| NugetPackage | Pilot.ClientSdk | Что исправлено |
|---|---|---|
| `ObjectLoader`, `ObjectLoaderSync`, `IdLoader` | `Loader.LoadAsync(id / ids)`, `Load(…)` (синхронно), `WaitAsync(id, условие)` | один экземпляр можно вызывать параллельно. Несуществующие объекты и объекты без прав не подвешивают загрузку. Таймаут возвращает уже загруженное. Порядок результата = порядок Id |
| `SearchHelper`, `Searcher` | `Search.IdsAsync / ObjectsAsync(b => …)`, `ByTypeAsync`, `ByTypesAsync`, `ByAttributeAsync`, `ByOrgUnitAsync` | ждёт полный (Remote) результат с таймаутом. По умолчанию ищет живые и замороженные объекты. Тип можно задать по имени |
| `GetStringAttribute`, `GetAttribute<T>` | `obj.Str`, `StrOrNull`, `Attr<T>`, `Date`, `DateStr`, `Int`, `Double`, `GuidAttr`, `OrgUnitIds`, `Has` | числа приводятся между int/long/double. Даты - в локальном времени |
| `GetParentByType`, `GetProjectFolder`, `GetAllChildren` | `Loader.ParentOfTypeAsync`, `SelfOrParentOfTypeAsync`, `ProjectAsync`, `AncestorsAsync`, `ChildrenAsync(obj, типы…)`, `RelatedAsync` | не падает на корне и не уходит в бесконечную рекурсию. Дети грузятся одним запросом |
| `CreateObject`, `AddFileByPath`, `CreateRelation` | `modifier.CreateChild(parent, "тип", атрибуты)`, `SetAttributes`, `SetAttributeValue`, `AddFileObject`, `Link` | `Apply()` вызывает модуль (одна транзакция). Нет `Task.Delay` «на удачу» - после Apply используйте `Loader.WaitAsync` |
| `OrganisationUnitService`, `OrganisationUnitWrapper` | `Org`: `Parent`, `Ancestors`, `DepartmentOf`, `PositionIds`, `PersonIdsIn`, `UnitByTitle`, `ContrAgent`, `Path` | префиксы контрагентов настраиваются, дерево без циклов |
| `TypeExtensions` | `ConstAttrValue`, `AttributeTitle`, `IsTaskType`, `IsWorkflowType`, `IsWorkflowStageType` | битая конфигурация атрибута / `""` вместо исключения |
| `PilotNumeratorService` | `NumeratorService`: `GetNumeratorValue`, `SetNumeratorAttribute`, `WaitNumberAsync` | ожидание номера - через подписку, без опроса раз в 300 мс |
| `ModuleLogger` (NLog) | `ModuleLogger` (без NLog) | тот же путь `%AppData%\ASCON\Units Logs\Logs\{имя}.log` |
| `RelayCommand` | `RelayCommand`, `AsyncRelayCommand` | асинхронная команда блокирует повторный запуск и не роняет клиент исключением |
| `IconLoader` | `EmbeddedResources.GetBytes(путь, сборка)` | |
| `DValueExtensions.ToInt/ToBool` | `Text.ToInt/ToBool` | |
| - | `Text.Fio`, `ShortName`, `CleanTitle`, `JoinNonEmpty`, `CompareCodes`, `CodeComparer` | как в Reports |

Темы Jedi/Sith, стили кнопок и `ByteImageConverter` (SVG через SharpVectors) остались в NugetPackage: это WPF-ресурсы со сторонней зависимостью, к общим методам они не относятся. (В самом Pilot.ClientSdk SharpVectors подключена в `.csproj`, но не используется.)

### `Pilot` - сервисы SDK в одном месте

| Член | Описание |
|---|---|
| `Repository`, `Search`, `Modifier`, `Files`, `Dialogs`, `Tabs`, `ClientInfo`, `PersonalSettings`, `AttributeFormatParser` | заполняются при старте Pilot.ClientSdk. `PilotApi.Init(...)` модуля не затирает уже сохранённые сервисы |
| `IsReady`, `CurrentPerson`, `Type(name)`, `TypeId(name)` | |
| `LoadAsync`, `Load`, `SearchIdsAsync`, `SearchObjectsAsync` | короткие вызовы `Loader` / `Searcher` |
| `Loader`, `Searcher`, `Org` | `Loader`, `Search`, `Org` на сервисах `Pilot` |

### Примеры

```csharp
// Загрузка и атрибуты
var doc = await PilotApi.LoadAsync(id);
var code = doc.Str("code");
var deadline = doc.DateStr("deadlineDate");
if (doc.Is("pir_doc") && doc.IsAlive()) { … }

// Поиск: документы типа в папке проекта с атрибутом
var docs = await PilotApi.Searcher.ByAttributeAsync("pir_doc", "stage", "РД", inContext: project.Id);

// Любые условия
var tasks = await PilotApi.SearchObjectsAsync(b =>
{
    b.Must(ObjectFields.TypeId.Be(PilotApi.TypeId("task_approval")));
    b.Must(AttributeFields.OrgUnit("executor").BeAnyOf(PilotApi.Org.PositionIds(dept.Id).ToArray()));
});

// Создание и ожидание результата после Apply
var builder = PilotApi.Modifier.CreateChild(folder, "archive_blank", new Dictionary<string, object> { ["name"] = "Бланк" });
new NumeratorService(PilotApi.AttributeFormatParser).SetNumeratorAttribute(builder, PilotApi.Type("archive_blank"), "number");
PilotApi.Modifier.Apply();
var created = await new NumeratorService(PilotApi.AttributeFormatParser).WaitNumberAsync(PilotApi.Repository, builder.DataObject.Id, "number");

// Оргструктура
var org = PilotApi.Org;
var dept = org.DepartmentOf(PilotApi.CurrentPerson);
var people = org.PersonIdsIn(dept.Id);
```

`Load*` и `Search*` можно вызывать с UI-потока (через `await` интерфейс не блокируется) и из фонового потока. Подписки SDK модуль сам создаёт на UI-потоке. Синхронный `Load(...)` на UI-потоке ждёт во вложенном цикле сообщений, окно не замирает.

---

## 4. Сборка, выпуск, CI/CD

Требования: .NET SDK 8+ (собирает `net48`; в CI - SDK 10.0.404), Windows для отладки в клиенте.

```bash
dotnet build -c Debug          # + копия в %LocalAppData%\ASCON\Pilot-BIM\Development\113Pilot.ClientSdk.ext2
dotnet build -c Release        # bin\113Pilot.ClientSdk.ext2-<версия>.zip - для загрузки на сервер
dotnet test tests/Pilot.ClientSdk.Tests
dotnet pack -c Release -o out   # NuGet-пакет Pilot.ClientSdk (ref/net48) - в реестр его публикует CI (задания nuget_*)
```

- **Отладка.** Debug-сборка кладёт модуль в папку разработки Pilot-BIM. Клиент подхватывает его при запуске. `-p:CopyToPilot=false` - не копировать.
- **Выпуск - через CI/CD модулей** (`.gitlab-ci.yml` по шаблону `Клиентские модули/ci-template`, вкладка «Модули» в веб-админке, см. [README модулей](../README.md)). Версию, CHANGELOG и теги ставит админка. Руками в `Version` ничего поднимать не нужно.
- **NuGet-пакет - свой CI** (jobs `nuget_*` в `.gitlab-ci.yml`), см. [4.1](#41-nuget-пакет).
- **Тесты**: запуск `IModule` / `IModuleAsync` (успех; ошибка не пробрасывается), статусы окна загрузки (не скачан, не загружен, сторонний, без интерфейса, без отчёта, запускается, ошибка, локальные модули), настройки окна; JSON событий, атрибуты сборки, конфиг, `Run`/`RunAsync` (успех, ошибка, отмена), очередь до старта, выключенная статистика, недоступный сервис (остаток очереди не теряется), подписки SDK (результат, таймаут с неполным результатом, ошибка, отмена, синхронное ожидание), ФИО, шифры, оргструктура. Интеграционный тест с настоящим сервисом запускается, если задан `CLIENTSDK_IT_URL` (например `http://localhost:5310/api/module-events`); без неё xUnit показывает его как пропущенный, а не пройденный.
- **Тесты в CI.** Проект тестов - net48 и WPF, на Linux-раннере он не запускается: задание `build_tests` проверяет, что он собирается. Запуск - задание `test` на Windows-раннере с тегом `windows`. Переменная CI/CD `WINDOWS_RUNNER=true` включает его в каждом pipeline; без неё задание ручное и pipeline на него не ждёт.

### 4.1. NuGet-пакет

Пакет `Pilot.ClientSdk` публикуется в реестр пакетов этого проекта GitLab (Deploy / Package registry) заданиями `nuget_*` из `.gitlab-ci.yml`: `dotnet pack` и `dotnet nuget push` с токеном `CI_JOB_TOKEN`, отдельный ключ не нужен. Числовой id проекта (`PROJECT_ID` в адресе источника) - Settings / General / Project ID. Номер пакета берётся из `<Version>` в `.csproj`, суффикс дописывает CI:

| Ветка | Пакет | Для чего |
|---|---|---|
| merge request | собирается, не публикуется (`nuget_check`) | пакет собирается |
| `dev` | `X.Y.Z-dev.<номер pipeline>` (`nuget_dev`) | модули на тестовом сервере проверяются с новым Pilot.ClientSdk |
| `release/X.Y.Z` | `X.Y.Z-rc.<номер pipeline>` (`nuget_rc`; пока версия в csproj не равна X.Y.Z, не публикуется) | стейдж |
| `main` | `X.Y.Z` (`nuget_release`, после выкладки в прод `deploy_prod`) | прод: на эту версию ссылаются модули |

«Номер pipeline» - `CI_PIPELINE_IID`, порядковый номер pipeline внутри проекта. Тег `vX.Y.Z` и GitLab Release ставит веб-админка при выпуске в прод. Опубликованную версию пакета GitLab перезаписать не даёт: при повторе `nuget_release` дубликат может быть отклонён.

Модули ссылаются на версию из `main`. Предварительную версию (`-dev.N`, `-rc.N`) берите только на время проверки и до выпуска в прод верните стабильную. Иначе модуль в проде будет собран против Pilot.ClientSdk, которого в проде ещё нет.

Доступ к пакету:
- **CI модуля.** Job `build_module` шаблона `ci-template` находит в `nuget.config` источники с `/api/v4/` и входит в них токеном job (`gitlab-ci-token` / `CI_JOB_TOKEN`). Если restore в job получает 403/404, в проекте Pilot.ClientSdk откройте Settings / CI/CD / Job token permissions и добавьте в разрешённые группу `client-modules`.
- **Своя машина.** Один раз подключите источник GitLab и войдите в него. Пошагово - в [4.2](#42-источник-gitlab-на-своей-машине).

### 4.2. Источник GitLab на своей машине

Пакет лежит в GitLab, а не на nuget.org, поэтому Visual Studio (или `dotnet`) должна знать адрес источника и входить в него под вами. Это делается один раз на машине и действует для всех модулей.

Адрес источника (подставьте адрес вашего GitLab и id проекта Pilot.ClientSdk):

```
http://gitlab.example.local/api/v4/projects/PROJECT_ID/packages/nuget/index.json
```

#### Шаг 1. Личный токен GitLab

Реестр пакетов GitLab принимает **только токен**, обычный пароль от GitLab не подходит.

1. GitLab / аватар справа вверху / **Edit profile** / **Access tokens** / **Add new token**.
2. Имя - например `nuget`, срок - на ваше усмотрение, права (scopes) - **`read_api`**.
3. **Create personal access token** и сразу скопируйте токен: GitLab показывает его один раз.

#### Шаг 2. Источник в Visual Studio

Если в репозитории модуля есть `nuget.config` с источником `cicd` (как в `Test-module`), Visual Studio подключит источник сама при открытии решения. Переходите к шагу 3.

Если `nuget.config` нет (новый модуль) - добавьте источник вручную или лучше скопируйте `nuget.config` из `Test-module`. Вручную:

1. **Сервис / Параметры / Диспетчер пакетов NuGet / Источники пакетов** (Tools / Options / NuGet Package Manager / Package Sources).
2. Кнопка **«+»**. Внизу: **Имя** - `cicd`, **Источник** - адрес выше. **Обновить** / **ОК**.

Источник по `http://`, поэтому Visual Studio предупредит о небезопасном подключении (NU1803). Внутри сети это нормально. В новых версиях Visual Studio и .NET SDK 9+ это ошибка NU1302. Её снимает только `allowInsecureConnections="true"` у источника в `nuget.config`, поэтому лучше подключать источник через `nuget.config`, а не через окно параметров.

#### Шаг 3. Вход: окно логина и пароля

При первом восстановлении пакетов Visual Studio покажет окно входа в источник. Это бывает при сборке, при открытии решения или в **Управление пакетами NuGet** с выбранным источником `cicd`. Окно называется «Учётные данные Windows» или «Требуется проверка подлинности», в нём адрес вашего GitLab:

| Поле | Что вводить |
|---|---|
| Имя пользователя | ваш **логин GitLab** (не почта) |
| Пароль | **личный токен** из шага 1, не пароль от GitLab |

Поставьте **«Запомнить учётные данные»**: Visual Studio сохранит их в диспетчере учётных данных Windows, и окно больше не появится.

Если ввели неправильно и окно больше не появляется, а restore пишет 401:
1. Панель управления / **Диспетчер учётных данных** / **Учётные данные Windows**.
2. Удалите запись с адресом вашего GitLab (обычно начинается с `VSCredentials_`).
3. Перезапустите Visual Studio, окно входа появится снова.

#### Вариант без Visual Studio: командная строка (Rider, `dotnet build`)

`dotnet` окна входа не показывает, логин и токен записываются в пользовательский `NuGet.Config`. На Windows токен шифруется средствами DPAPI, в репозиторий он не попадает:

```
dotnet nuget add source http://gitlab.example.local/api/v4/projects/PROJECT_ID/packages/nuget/index.json -n cicd -u <логин GitLab> -p <токен> --configfile %AppData%\NuGet\NuGet.Config
```

Токен сменился - та же команда с `update` вместо `add` (без адреса):

```
dotnet nuget update source cicd -u <логин GitLab> -p <новый токен> --configfile %AppData%\NuGet\NuGet.Config
```

#### Проверка и типовые ошибки

В папке модуля: `dotnet restore`. Должно быть `Restored …`, в «Зависимости / Пакеты» проекта появится `Pilot.ClientSdk`.

| Ошибка | Причина | Что делать |
|---|---|---|
| `401 Unauthorized` | неверный логин или вместо токена введён пароль | шаг 3: удалить сохранённые учётные данные и войти логином и токеном |
| `NU1101 … No packages exist with this id in source(s): cicd` | у токена нет `read_api` или у вас нет доступа к проекту `client-modules/pilot-clientsdk` | новый токен с `read_api`; доступ к проекту - у владельца группы `client-modules` |
| `503` или таймаут | в системе задан прокси (`HTTP_PROXY`), запрос ушёл через него | добавить адрес GitLab в `NO_PROXY` (переменные среды Windows) и перезапустить Visual Studio |
| `NU1302` / `NU1803` (HTTP) | источник по `http://` | источник из `nuget.config` с `allowInsecureConnections="true"` |
| `NU1101` в CI GitLab | job модуля не допущен к пакетам Pilot.ClientSdk | в проекте Pilot.ClientSdk: Settings / CI/CD / Job token permissions / группа `client-modules` |

### Структура

```
Pilot.ClientSdk.csproj, AssemblyInfo.cs, Pilot.ClientSdk.config.json, .gitlab-ci.yml
Pilot/           ModuleStartup (IDataPlugin - точка входа), TrayMenu («Модули версии» / «- загрузка»), CommonSettings и SettingsEditor (общие настройки)
Startup/         IModule, StartupRunner (API для модулей), ModuleCheck (статусы), ModuleSources (сервер, Plugins, AppDomain),
                 StartupMonitor (когда показать окно, итог в лог и Grafana), StartupWindow
Telemetry/       ModuleStats (API), ModuleEvent (JSON), StatsSender (очередь и отправка), ModuleIdentity, ClientEnvironment
Common/          PilotApi, Loader, Search, DataObjectExtensions, Modify, Org, TypeExtensions,
                 NumeratorService, Text, ModuleLogger, RelayCommand, EmbeddedResources
Infrastructure/  ModuleConfig, ModuleLog, Json, SdkSubscription (IObservable SDK / Task)
tests/           Pilot.ClientSdk.Tests (xUnit, net48)
```

---

## 5. Переход с NugetPackage

- NugetPackage не удаляется: модули на нём продолжают работать. Переводите модуль на Pilot.ClientSdk, когда в нём и так идёт задача.
- Оба модуля можно подключить одновременно. Пространства имён разные (`Pilot.ClientSdk` и `ClientModules` / `NugetPackage`), но имена `ModuleLogger` и `RelayCommand` совпадают. Если в файле есть оба `using`, уточните тип.
- Статистику можно добавить в модуль на NugetPackage - достаточно подключить Pilot.ClientSdk и обернуть действия в `Stats.Run(...)`.

---

## 6. Как это устроено внутри

- **Загрузка.** `ModuleStartup` экспортируется как `IDataPlugin` и сам реализует `IModule`. Клиент создаёт его при старте и передаёт в конструктор сервисы SDK. Они сохраняются в `Pilot`, запускаются отправка статистики и проверка загрузки модулей.
- **Окно загрузки.** `StartupMonitor` сразу запрашивает список модулей с сервера и раз в 300 мс (до 3 минут) проверяет `Application.Current.MainWindow`. Когда главное окно видно, открывает окно проверки через `ShowDialog` (в режиме `problems` сначала молча ждёт итога и открывает окно, только если есть ошибки). Окно пересчитывает статусы раз в 400 мс. Отчёты `StartupRunner` копятся в статическом списке, поэтому не важно, создаст клиент модуль раньше Pilot.ClientSdk или позже.
- **Общие настройки.** `CommonSettings` экспортируется как `ISettingsFeature2`: раздел «Модули общие настройки» и редактор `SettingsEditor`, собранный в коде без XAML. Значение раздела - JSON `ModuleConfig`. `ModuleStartup` подписывается на него через `IPersonalSettings.SubscribeSetting`, и каждое значение попадает в `ModuleConfig.ApplyCommon`. Событие `ModuleConfig.Changed` перенастраивает `StatsSender`: новый адрес, ключ и период действуют без перезапуска, очередь событий не теряется. Окно загрузки читает настройки, когда открылось главное окно Pilot: к этому времени общие настройки уже пришли.
- **Модули видят Pilot.ClientSdk.** Модули Pilot грузятся в один AppDomain клиента. Сборка `113Pilot.ClientSdk.ext2` загружена один раз, её типы доступны всем модулям. Поэтому в zip модулей её класть нельзя: вторая копия той же сборки даст конфликт типов.
- **Без сторонних зависимостей в коде.** JSON, лог и ожидание подписок написаны вручную. Модуль не тянет NLog, Newtonsoft.Json и System.Reactive. Сторонние библиотеки в проекте только для эксперимента 2.1.0: в zip лежит `Humanizer.dll` 2.14.1 - общая библиотека для других модулей (они ссылаются на неё только для компиляции). `DocumentFormat.OpenXml` и `SharpVectors` подключены в `.csproj`, но в коде и в zip не используются. В NuGet-пакет эти зависимости не попадают (`PrivateAssets=all`): в нём одна зависимость, `Ascon.Pilot.SDK`.
- **Подписки SDK.** `SdkSubscription` превращает `IObservable` SDK в `Task`. Подписка создаётся и снимается на UI-потоке. При таймауте возвращается уже полученное, а не исключение. Отмена через `CancellationToken` снимает подписку.

---

## 7. Типовые проблемы

| Симптом | Причина | Решение |
|---|---|---|
| Модуль не загружается: `Could not load type 'Pilot.ClientSdk…'` | у пользователя не назначен Pilot.ClientSdk | назначить `113Pilot.ClientSdk.ext2` всем пользователям |
| `Сервис IObjectsRepository недоступен: модуль Pilot.ClientSdk не загружен…` | модуль вызывает `Pilot` раньше старта Pilot.ClientSdk | вызвать `PilotApi.Init(...)` в конструкторе своего модуля |
| В Grafana нет данных | неверный `statsUrl`, сервис недоступен с рабочего места | `Pilot.ClientSdk.log` («Статистика … не отправлена»); `http://<хост>:5310/health` с рабочего места |
| Сервис отвечает 401 | не совпадает `apiKey` | API-ключ в общих настройках «Модули» (или в `Pilot.ClientSdk.config.json`) = `COLLECTOR_API_KEY` |
| В Grafana модуль называется именем сборки | в сборке нет `AssemblyProduct` | задать `[assembly: AssemblyProduct("NN.NNN - Название - (Код) - (CICD)")]` |
| В окне загрузки «не скачан» | клиент не скачал zip модуля (обрыв связи при старте, нет места на диске, антивирус) | перезапустить Pilot; если не помогло - закрыть клиент и удалить папку `Databases\<база>\<версия>\Plugins` |
| В окне загрузки «не загружен» | zip скачан, но клиент не загрузил сборку (нет `*.ext2.dll`, конфликт зависимостей, модуль собран под более новый Pilot.ClientSdk) | лог клиента Pilot; выложить Pilot.ClientSdk нужной версии |
| «загружен, без IModule» | стартовый класс не реализует `IModule` | реализовать по [разделу 2а](#2а-стартовый-класс-imodule-и-окно-загрузки-модулей) |
| «нет отчёта о запуске» | `IModule` есть, но `StartupRunner.Run(this)` не вызван, или клиент не создал стартовый класс (ошибка MEF-импорта) | добавить вызов в конструктор; смотреть лог клиента Pilot |
| Окно загрузки мешает (стенд, автотесты) | - | общие настройки «Модули»: окно «Только если модуль не загрузился» или «Не показывать» |
| Сторонний модуль всегда «не загружен» | модуль назначен, но клиент его не грузит (другой клиент, старая версия) | общие настройки «Модули» / «Не проверять модули» |
| Настройки из Pilot не действуют | в разделе битый JSON или клиент не получил общие настройки | `Pilot.ClientSdk.log`: «Настройки Pilot.ClientSdk: …»; трей / «Модули версии», строка «Настройки» |
| Нет модуля в «Версии на рабочих местах» | нет `AssemblyCompany("CICD")` или сборка не `*.ext2` | задать атрибут компании |
