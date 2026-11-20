using System.Text.RegularExpressions;

namespace ProAdmin.Modules;

/// <summary>Что спросили у пользователя при создании модуля.</summary>
public sealed record ModuleScaffoldRequest
{
    public required ModuleKind Kind { get; init; }

    /// <summary>Код модуля латиницей: имя проекта GitLab, пространство имён, имя сборки (OpenStoradge).</summary>
    public required string Code { get; init; }

    /// <summary>Название по-русски: AssemblyProduct = «Наименование» модуля в Pilot.</summary>
    public required string Title { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Клиентский модуль: номер из трёх цифр - префикс сборки (087OpenStoradge.ext2) и часть «Наименования».</summary>
    public string Number { get; init; } = "";

    /// <summary>Клиентский модуль: раздел перед номером в «Наименовании» («01» в «01.087 - …»), пусто - «0».</summary>
    public string Section { get; init; } = "";

    /// <summary>Клиентский модуль на Pilot.ClientSdk: статистика в Grafana, окно загрузки модулей, общие методы SDK.</summary>
    public bool UseClientSdk { get; init; }

    public string ClientSdkVersion { get; init; } = ModuleScaffold.DefaultClientSdkVersion;

    /// <summary>Адрес реестра пакетов GitLab с Pilot.ClientSdk (…/api/v4/projects/ID/packages/nuget/index.json); null - образец в nuget.config.</summary>
    public string? PackageSource { get; init; }

    /// <summary>Клиент Pilot для отладки F5 (Pilot-BIM или Pilot-ICE Enterprise).</summary>
    public string DebugClient { get; init; } = ModuleScaffold.PilotBim;

    public string Author { get; init; } = "";
}

/// <summary>
/// Каркас нового модуля для репозитория GitLab: проект, точка входа с примером, CHANGELOG, README.
/// Шаблон CI кладёт создание репозитория отдельным коммитом (<see cref="ModuleCiTemplate"/>).
/// </summary>
public static partial class ModuleScaffold
{
    public const string PilotSdkVersion = "26.13.0";
    public const string DefaultClientSdkVersion = "2.1.1";
    public const string PilotBim = "Pilot-BIM";
    public const string PilotIce = "Pilot-ICE Enterprise";
    public const string InitialVersion = "1.0.0";

    public static string AssemblyName(ModuleScaffoldRequest r) =>
        r.Kind == ModuleKind.Server ? $"{r.Code}.servext" : $"{r.Number}{r.Code}.ext2";

    /// <summary>«Наименование» модуля в Pilot - как у остальных модулей стенда: «01.087 - Название - (Код) - (CICD)».</summary>
    public static string Product(ModuleScaffoldRequest r) =>
        r.Kind == ModuleKind.Server
            ? $"{r.Title.Trim()} - ({r.Code}) - (CICD)"
            : $"{(string.IsNullOrWhiteSpace(r.Section) ? "0" : r.Section.Trim())}.{r.Number} - {r.Title.Trim()} - ({r.Code}) - (CICD)";

    /// <summary>Ошибки формы; пусто - можно создавать.</summary>
    public static List<string> Validate(ModuleScaffoldRequest r)
    {
        var errors = new List<string>();
        if (!CodeRegex().IsMatch(r.Code ?? ""))
            errors.Add("Код - латиница, цифры и «_», начинается с буквы, до 60 символов (например, OpenStoradge).");
        if (string.IsNullOrWhiteSpace(r.Title))
            errors.Add("Укажите название модуля.");
        else if (r.Title.IndexOfAny(new[] { '(', ')', '"', '\\', '\r', '\n' }) >= 0)
            errors.Add("В названии нельзя использовать скобки, кавычки и переводы строк: скобки в «Наименовании» занимает код модуля.");
        if (r.Kind == ModuleKind.Client)
        {
            if (!NumberRegex().IsMatch(r.Number ?? ""))
                errors.Add("Номер модуля - три цифры (например, 087): с него начинается имя сборки и папки в кэше клиента.");
            if (!string.IsNullOrWhiteSpace(r.Section) && !SectionRegex().IsMatch(r.Section.Trim()))
                errors.Add("Раздел - одна-две цифры (например, 01) или пусто.");
            if (r.UseClientSdk && !VersionRegex().IsMatch(r.ClientSdkVersion ?? ""))
                errors.Add("Версия Pilot.ClientSdk - X.Y.Z.");
        }
        if (r.PackageSource is { Length: > 0 } source && (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            errors.Add("Адрес реестра пакетов - http(s)://…");
        return errors;
    }

    /// <summary>Файлы репозитория: путь / содержимое (UTF-8, CRLF там, где их ждёт Visual Studio).</summary>
    public static List<(string Path, string Content)> Files(ModuleScaffoldRequest r)
    {
        var files = r.Kind == ModuleKind.Server ? ServerFiles(r) : ClientFiles(r);
        files.Add((".gitignore", GitIgnore));
        files.Add(("CHANGELOG.md", ChangelogText(r)));
        files.Add(("README.md", Readme(r)));
        files.Add(($"{r.Code}.sln", Solution(r.Code)));
        return files;
    }

    // ------------------------------------------------------------------ клиентский модуль

    private static List<(string, string)> ClientFiles(ModuleScaffoldRequest r)
    {
        var asm = AssemblyName(r);
        var files = new List<(string, string)>
        {
            ($"{r.Code}.csproj", ClientProject(r, asm)),
            ("AssemblyInfo.cs", AssemblyInfo(r)),
            ("Pilot/StartPoint.cs", r.UseClientSdk ? ClientStartWithSdk(r) : ClientStartPlain(r)),
            ("Pilot/ObjectMenu.cs", r.UseClientSdk ? ClientMenuWithSdk(r) : ClientMenuPlain(r)),
        };
        if (r.UseClientSdk)
            files.Add(("nuget.config", NuGetConfig(r.PackageSource)));
        return files;
    }

    private static string ClientProject(ModuleScaffoldRequest r, string asm) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">

        	<!-- {{Xml(r.Title)}}: клиентский модуль Pilot. Версия модуля - только <Version>, её поднимает веб-админка при релизе. -->
        	<PropertyGroup>
        		<TargetFramework>net48</TargetFramework>
        		<UseWPF>true</UseWPF>
        		<LangVersion>latest</LangVersion>
        		<Nullable>disable</Nullable>
        		<Version>{{InitialVersion}}</Version>
        		<AssemblyName>{{asm}}</AssemblyName>
        		<RootNamespace>{{r.Code}}</RootNamespace>
        		<OutputType>Library</OutputType>
        		<PlatformTarget>AnyCPU</PlatformTarget>
        		<!-- Сборка на Linux-раннере GitLab -->
        		<EnableWindowsTargeting>true</EnableWindowsTargeting>
        		<!-- AssemblyVersion/FileVersion генерирует SDK из <Version>, остальные атрибуты - в AssemblyInfo.cs -->
        		<GenerateAssemblyInfo>true</GenerateAssemblyInfo>
        		<GenerateAssemblyTitleAttribute>false</GenerateAssemblyTitleAttribute>
        		<GenerateAssemblyDescriptionAttribute>false</GenerateAssemblyDescriptionAttribute>
        		<GenerateAssemblyCompanyAttribute>false</GenerateAssemblyCompanyAttribute>
        		<GenerateAssemblyCopyrightAttribute>false</GenerateAssemblyCopyrightAttribute>
        		<GenerateAssemblyProductAttribute>false</GenerateAssemblyProductAttribute>
        		<GenerateAssemblyConfigurationAttribute>false</GenerateAssemblyConfigurationAttribute>
        		<GenerateAssemblyTrademarkAttribute>false</GenerateAssemblyTrademarkAttribute>
        		<GenerateNeutralResourcesLanguageAttribute>false</GenerateNeutralResourcesLanguageAttribute>
        		<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
        		<!-- В CI пути исходников не попадают в сборку: одинаковый код даёт одинаковый dll -->
        		<ContinuousIntegrationBuild Condition="'$(CI)' == 'true' Or '$(GITLAB_CI)' == 'true'">true</ContinuousIntegrationBuild>
        	</PropertyGroup>

        	<!-- F5 в Visual Studio запускает клиент Pilot, Debug-сборка лежит в его папке разработки -->
        	<PropertyGroup Condition="'$(Configuration)'=='Debug'">
        		<DebugType>full</DebugType>
        		<StartAction>Program</StartAction>
        		<StartProgram>{{DebugExe(r.DebugClient)}}</StartProgram>
        	</PropertyGroup>

        	<!-- Pilot SDK уже загружен в клиенте: нужен только для компиляции, в zip не попадает -->
        	<ItemGroup>
        		<PackageReference Include="Ascon.Pilot.SDK" Version="{{PilotSdkVersion}}" ExcludeAssets="runtime" />
        {{(r.UseClientSdk ? $"""
        		<!-- Pilot.ClientSdk (реестр пакетов GitLab, nuget.config): только для компиляции, в клиенте его загружает модуль 113Pilot.ClientSdk.ext2 -->
        		<PackageReference Include="Pilot.ClientSdk" Version="{r.ClientSdkVersion}" />

        """ : "")}}	</ItemGroup>

        	<ItemGroup>
        		<Reference Include="System.ComponentModel.Composition" />
        	</ItemGroup>

        	<!-- Release: bin\{{asm}}-<версия>.zip - его собирает CI и выкладывает веб-админка -->
        	<Target Name="CreateReleaseZip" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
        		<PropertyGroup>
        			<_ZipStage>$(MSBuildProjectDirectory)\obj\zip-stage\</_ZipStage>
        			<_ZipPath>$(MSBuildProjectDirectory)\bin\$(AssemblyName)-$(Version).zip</_ZipPath>
        		</PropertyGroup>
        		<ItemGroup>
        			<!-- Сторонние библиотеки модуля добавляйте сюда - по правилам раздела «Библиотеки» веб-админки -->
        			<_ZipFiles Include="$(TargetDir)$(AssemblyName).dll" />
        		</ItemGroup>
        		<RemoveDir Directories="$(_ZipStage)" />
        		<Copy SourceFiles="@(_ZipFiles)" DestinationFolder="$(_ZipStage)" />
        		<Delete Files="$(_ZipPath)" />
        		<ZipDirectory SourceDirectory="$(_ZipStage)" DestinationFile="$(_ZipPath)" />
        		<Message Importance="high" Text="Модуль для загрузки в Pilot: $(_ZipPath)" />
        	</Target>

        	<!-- Debug: копия в папку разработки клиента - он подхватит модуль без загрузки на сервер (-p:CopyToPilot=false - не копировать) -->
        	<PropertyGroup>
        		<DevExtensionDir>$(LocalAppData)\ASCON\{{r.DebugClient}}\Development\$(AssemblyName)</DevExtensionDir>
        	</PropertyGroup>
        	<Target Name="CopyToDevelopmentDirectory" AfterTargets="Build" Condition="'$(Configuration)' == 'Debug' AND '$(OS)' == 'Windows_NT' AND '$(CopyToPilot)' != 'false'">
        		<ItemGroup>
        			<_DevFiles Include="$(TargetDir)$(AssemblyName).*" />
        		</ItemGroup>
        		<Copy SourceFiles="@(_DevFiles)" DestinationFolder="$(DevExtensionDir)" />
        	</Target>

        </Project>

        """;

    private static string AssemblyInfo(ModuleScaffoldRequest r) => $$"""
        using System.Reflection;

        // «Наименование» модуля в Pilot - AssemblyProduct: по нему веб-админка связывает объект на сервере с этим репозиторием.
        // Меняете название - добавьте прежнее в AssemblyMetadata("PreviousNames", "Старое;Сборка.ext2"), иначе на сервере появится второй модуль.
        [assembly: AssemblyTitle("{{Cs(AssemblyName(r))}}")]
        [assembly: AssemblyDescription("{{Cs(r.Description.Trim().Length > 0 ? r.Description.Trim() : r.Title.Trim())}}")]
        [assembly: AssemblyCompany("CICD")]
        [assembly: AssemblyCopyright("Copyright © {{DateTime.UtcNow.Year}}")]
        [assembly: AssemblyProduct("{{Cs(Product(r))}}")]

        """;

    private static string ClientStartWithSdk(ModuleScaffoldRequest r) => $$"""
        using System.ComponentModel.Composition;
        using Ascon.Pilot.SDK;
        using Pilot.ClientSdk;

        namespace {{r.Code}}.Pilot
        {
            /// <summary>
            /// Точка входа модуля: Pilot создаёт её при загрузке клиента и отдаёт сервисы SDK.
            /// IModule - окно «Загрузка модулей» показывает, запустился ли модуль и за сколько.
            /// В конструкторе - только сохранение сервисов, остальное - в Initialize.
            /// </summary>
            [Export(typeof(IDataPlugin))]
            public sealed class StartPoint : IDataPlugin, IModule
            {
                [ImportingConstructor]
                public StartPoint(IObjectsRepository repository, ISearchService search, IObjectModifier modifier)
                {
                    PilotApi.Init(repository, search, modifier);
                    StartupRunner.Run(this); // последней строкой: замер запуска для окна загрузки модулей
                }

                public void Initialize()
                {
                }
            }
        }

        """;

    private static string ClientMenuWithSdk(ModuleScaffoldRequest r) => $$"""
        using System;
        using System.ComponentModel.Composition;
        using System.Linq;
        using System.Windows;
        using Ascon.Pilot.SDK;
        using Ascon.Pilot.SDK.Menu;
        using Pilot.ClientSdk;
        using Pilot.ClientSdk.Telemetry;

        namespace {{r.Code}}.Pilot
        {
            /// <summary>
            /// Пример: пункт контекстного меню объектов. Действие обёрнуто в Stats.Run - вызовы, длительность и ошибки
            /// видны в Grafana («Pilot - клиентские модули»). Замените на свою логику.
            /// </summary>
            [Export(typeof(IMenu<ObjectsViewContext>))]
            public sealed class ObjectMenu : IMenu<ObjectsViewContext>
            {
                private const string Item = "{{r.Code}}_Info";
                private const string Action = "{{Cs(r.Title.Trim())}}: сведения";

                private static readonly ModuleStats Stats = ModuleStats.For<ObjectMenu>();
                private static readonly ModuleLogger Log = new ModuleLogger("{{r.Code}}");

                public void Build(IMenuBuilder builder, ObjectsViewContext context)
                {
                    if (context.SelectedObjects?.Any() != true || builder.ItemNames.Contains(Item))
                        return;
                    builder.AddItem(Item, builder.Count).WithHeader("{{Cs(r.Title.Trim())}}");
                }

                public void OnMenuItemClick(string name, ObjectsViewContext context)
                {
                    if (name != Item)
                        return;
                    try
                    {
                        Stats.Run(Action, op =>
                        {
                            var selected = context.SelectedObjects.ToList();
                            op.Items = selected.Count; // сколько объектов обработано - в Grafana
                            var version = typeof(ObjectMenu).Assembly.GetName().Version.ToString(3);
                            MessageBox.Show($"Выбрано объектов: {selected.Count}\nТип первого: {selected[0].Type?.Title}\nВерсия модуля: {version}",
                                "{{Cs(r.Title.Trim())}}");
                        });
                    }
                    catch (Exception ex)
                    {
                        Log.Error(Action, ex); // ошибка уже в статистике, здесь - в лог %AppData%\ASCON\Units Logs\Logs
                        MessageBox.Show(ex.Message, "{{Cs(r.Title.Trim())}}", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
        }

        """;

    private static string ClientStartPlain(ModuleScaffoldRequest r) => $$"""
        using System.ComponentModel.Composition;
        using Ascon.Pilot.SDK;

        namespace {{r.Code}}.Pilot
        {
            /// <summary>Точка входа модуля: Pilot создаёт её при загрузке клиента и отдаёт сервисы SDK.</summary>
            [Export(typeof(IDataPlugin))]
            public sealed class StartPoint : IDataPlugin
            {
                [ImportingConstructor]
                public StartPoint(IObjectsRepository repository)
                {
                    Repository = repository;
                }

                public static IObjectsRepository Repository { get; private set; }
            }
        }

        """;

    private static string ClientMenuPlain(ModuleScaffoldRequest r) => $$"""
        using System.ComponentModel.Composition;
        using System.Linq;
        using System.Windows;
        using Ascon.Pilot.SDK;
        using Ascon.Pilot.SDK.Menu;

        namespace {{r.Code}}.Pilot
        {
            /// <summary>Пример: пункт контекстного меню объектов. Замените на свою логику.</summary>
            [Export(typeof(IMenu<ObjectsViewContext>))]
            public sealed class ObjectMenu : IMenu<ObjectsViewContext>
            {
                private const string Item = "{{r.Code}}_Info";

                public void Build(IMenuBuilder builder, ObjectsViewContext context)
                {
                    if (context.SelectedObjects?.Any() != true || builder.ItemNames.Contains(Item))
                        return;
                    builder.AddItem(Item, builder.Count).WithHeader("{{Cs(r.Title.Trim())}}");
                }

                public void OnMenuItemClick(string name, ObjectsViewContext context)
                {
                    if (name != Item)
                        return;
                    var selected = context.SelectedObjects.ToList();
                    var version = typeof(ObjectMenu).Assembly.GetName().Version.ToString(3);
                    MessageBox.Show($"Выбрано объектов: {selected.Count}\nВерсия модуля: {version}", "{{Cs(r.Title.Trim())}}");
                }
            }
        }

        """;

    private static string NuGetConfig(string? source)
    {
        var url = string.IsNullOrWhiteSpace(source) ? "http://gitlab.example.local/api/v4/projects/PROJECT_ID/packages/nuget/index.json" : source.Trim();
        var insecure = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? " allowInsecureConnections=\"true\"" : "";
        var note = string.IsNullOrWhiteSpace(source)
            ? "  Адрес ниже - ОБРАЗЕЦ: проект с пакетом Pilot.ClientSdk в GitLab не найден. Подставьте адрес GitLab и id проекта (Settings / General / Project ID).\n"
            : "  Адрес подставила веб-админка при создании репозитория: реестр пакетов проекта Pilot.ClientSdk этого GitLab.\n";
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <!--
              Источники пакетов модуля. cicd - реестр пакетов GitLab с Pilot.ClientSdk (и другими пакетами Pilot.*).
            {note}  CI входит в источник токеном job сам. На своей машине один раз добавьте вход личным токеном GitLab (read_api):
                dotnet nuget update source cicd -u <логин GitLab> -p <токен> -configfile %AppData%\NuGet\NuGet.Config
              (если источника cicd там ещё нет - dotnet nuget add source <адрес ниже> -n cicd -u … -p …).
              Пакет есть и в релизах https://github.com/PilotOpenTools/PilotClientSdk/releases.
            -->
            <configuration>
              <packageSources>
                <clear />
                <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
                <add key="cicd" value="{Xml(url)}"{insecure} />
              </packageSources>
              <packageSourceMapping>
                <packageSource key="nuget.org">
                  <package pattern="*" />
                </packageSource>
                <packageSource key="cicd">
                  <package pattern="Pilot.*" />
                </packageSource>
              </packageSourceMapping>
            </configuration>

            """;
    }

    // ------------------------------------------------------------------ серверный модуль

    private static List<(string, string)> ServerFiles(ModuleScaffoldRequest r) => new()
    {
        ($"{r.Code}.csproj", ServerProject(r)),
        ("AssemblyInfo.cs", AssemblyInfo(r)),
        ($"{r.Code}Activity.cs", ServerActivity(r)),
        ("Properties/launchSettings.json", LaunchSettings),
    };

    private static string ServerProject(ModuleScaffoldRequest r) => $$"""
        <Project Sdk="Microsoft.NET.Sdk">

        	<!-- {{Xml(r.Title)}}: серверный модуль Pilot-Server. Ставится в ServerHost и обновляется без перезапуска сервера. -->
        	<PropertyGroup>
        		<TargetFramework>netstandard2.0</TargetFramework>
        		<AssemblyName>{{r.Code}}.servext</AssemblyName>
        		<RootNamespace>{{r.Code}}</RootNamespace>
        		<LangVersion>latest</LangVersion>
        		<Version>{{InitialVersion}}</Version>
        		<GenerateAssemblyInfo>true</GenerateAssemblyInfo>
        		<GenerateAssemblyTitleAttribute>false</GenerateAssemblyTitleAttribute>
        		<GenerateAssemblyDescriptionAttribute>false</GenerateAssemblyDescriptionAttribute>
        		<GenerateAssemblyCompanyAttribute>false</GenerateAssemblyCompanyAttribute>
        		<GenerateAssemblyCopyrightAttribute>false</GenerateAssemblyCopyrightAttribute>
        		<GenerateAssemblyProductAttribute>false</GenerateAssemblyProductAttribute>
        		<GenerateAssemblyConfigurationAttribute>false</GenerateAssemblyConfigurationAttribute>
        		<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
        		<CopyLocalLockFileAssemblies>false</CopyLocalLockFileAssemblies>
        		<AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
        		<OutputPath>bin\$(Configuration)\</OutputPath>
        		<NoWarn>$(NoWarn);CS1998</NoWarn>
        		<!-- В CI пути исходников не попадают в сборку: одинаковый код даёт одинаковый dll -->
        		<ContinuousIntegrationBuild Condition="'$(CI)' == 'true' Or '$(GITLAB_CI)' == 'true'">true</ContinuousIntegrationBuild>
        	</PropertyGroup>

        	<!-- Ascon.Pilot.* и MEF уже есть в Pilot-Server: только для компиляции, в архив не кладём -->
        	<ItemGroup>
        		<PackageReference Include="Ascon.Pilot.ServerExtensions.SDK" Version="{{PilotSdkVersion}}" ExcludeAssets="runtime" />
        		<PackageReference Include="System.ComponentModel.Composition" Version="4.7.0" ExcludeAssets="runtime" />
        	</ItemGroup>

        	<!-- Release: bin\{{r.Code}}.servext-<версия>.zip - его собирает CI и ставит в ServerHost веб-админка -->
        	<Target Name="CreateReleaseZip" AfterTargets="Build" Condition="'$(Configuration)' == 'Release'">
        		<PropertyGroup>
        			<_ZipStage>$(MSBuildProjectDirectory)\obj\zip-stage\</_ZipStage>
        			<_ZipPath>$(MSBuildProjectDirectory)\bin\$(AssemblyName)-$(Version).zip</_ZipPath>
        		</PropertyGroup>
        		<ItemGroup>
        			<!-- В корне архива ровно одна *.servext.dll и её собственные зависимости -->
        			<_ZipFiles Include="$(TargetDir)*.dll" />
        		</ItemGroup>
        		<RemoveDir Directories="$(_ZipStage)" />
        		<Copy SourceFiles="@(_ZipFiles)" DestinationFolder="$(_ZipStage)" />
        		<Delete Files="$(_ZipPath)" />
        		<ZipDirectory SourceDirectory="$(_ZipStage)" DestinationFile="$(_ZipPath)" />
        		<Message Importance="high" Text="Модуль для загрузки в ServerHost: $(_ZipPath)" />
        	</Target>

        </Project>

        """;

    private static string ServerActivity(ModuleScaffoldRequest r) => $$"""
        using System;
        using System.ComponentModel.Composition;
        using System.Threading.Tasks;
        using Ascon.Pilot.DataClasses;
        using Ascon.Pilot.DataModifier;
        using Ascon.Pilot.ServerExtensions.SDK;

        namespace {{r.Code}}
        {
            /// <summary>
            /// Активность «{{r.Code}}»: её имя указывают в сценарии автоматизации Pilot (activity). Имя уникально среди всех модулей сервера.
            /// Пример: записывает в атрибут объекта-источника дату срабатывания. Атрибут берётся из params сценария:
            /// { "attribute": "имя_атрибута" }. Замените на свою логику.
            /// Отладка на реальных данных без выкладки: утилита debug (github.com/PilotOpenTools/PilotServerDebug),
            /// профиль «debug» в Properties/launchSettings.json, debug.json - из веб-админки («Отладить локально»).
            /// </summary>
            [Export(typeof(IServerActivity))]
            public sealed class {{r.Code}}Activity : IServerActivity
            {
                public string Name => "{{r.Code}}";

                public async Task RunAsync(IModifierBase modifier, IModifierBackend backend, IServerActivityContext ctx, IAutomationEventContext ev)
                {
                    INObject source = ev.Source;
                    if (source == null)
                        return;
                    if (!ctx.Params.TryGetValue("attribute", out var attribute) || !(attribute is string name) || name.Length == 0)
                        return;
                    // Ошибки не глушим: ServerHost запишет их в журнал и статистику модуля.
                    modifier.EditObject(source).SetAttribute(name, DateTime.UtcNow);
                }
            }
        }

        """;

    private const string LaunchSettings = """
        {
          "profiles": {
            "debug": {
              "commandName": "Executable",
              "executablePath": "C:\\Tools\\debug\\debug.exe",
              "commandLineArgs": "",
              "workingDirectory": "$(ProjectDir)"
            }
          }
        }

        """;

    // ------------------------------------------------------------------ общее

    private static string ChangelogText(ModuleScaffoldRequest r) => $"""
        # Журнал изменений - {r.Code}

        Формат: раздел `## [X.Y.Z] - дата` на каждую версию. Текст раздела становится описанием GitLab Release.
        Версию и раздел при релизе ставит веб-админка; задачи добавляйте в «Не выпущено».

        ## [Не выпущено]

        ## [{InitialVersion}] - {DateTime.UtcNow:yyyy-MM-dd}

        - Модуль создан в веб-админке{(r.Author.Length > 0 ? $" ({r.Author})" : "")}.

        """;

    private static string Readme(ModuleScaffoldRequest r)
    {
        var server = r.Kind == ModuleKind.Server;
        var asm = AssemblyName(r);
        var about = r.Description.Trim().Length > 0 ? r.Description.Trim() : (server ? "Серверный модуль Pilot-Server." : "Клиентский модуль Pilot.");
        var debug = server
            ? """
              Отладка на реальных данных без выкладки - утилита `debug` ([PilotServerDebug](https://github.com/PilotOpenTools/PilotServerDebug)):

              1. Распакуйте релиз утилиты в `C:\Tools\debug` (путь задан в `Properties/launchSettings.json`).
              2. В веб-админке «Серверные модули» / активность / «Отладить локально» скачайте `debug.json` и положите рядом с `.csproj`.
              3. Профиль «debug», F5. По умолчанию изменения только показываются и на сервер не пишутся.

              Имя активности для сценария автоматизации - значение `Name` в классе активности.
              """
            : $"""
              F5 в Visual Studio собирает Debug, копирует модуль в `%LocalAppData%\ASCON\{r.DebugClient}\Development\{asm}` и запускает клиент:
              он подхватит модуль без загрузки на сервер. `-p:CopyToPilot=false` - не копировать.
              {(r.UseClientSdk ? "\nНа сервере пользователю должен быть назначен `113Pilot.ClientSdk.ext2` ([PilotClientSdk](https://github.com/PilotOpenTools/PilotClientSdk)): без него модуль не загрузится. Пакет `Pilot.ClientSdk` берётся из реестра GitLab (`nuget.config`); вход в источник на своей машине - [руководство PilotClientSdk, 4.2](https://github.com/PilotOpenTools/PilotClientSdk/blob/main/docs/guide.md#42-источник-gitlab-на-своей-машине).\n" : "")}
              """;
        return $"""
            # {r.Title.Trim()}

            {about}

            | | |
            |---|---|
            | Сборка | `{asm}` |
            | Наименование в Pilot | `{Product(r)}` |
            | Вид | {(server ? "серверный модуль (ServerHost)" : "клиентский модуль")} |

            ## Как меняется

            | Ветка | Сервер Pilot | Что там |
            |---|---|---|
            | `feature/KTN-1234`, `bugfix/KTN-1234` | - | задача; от `dev`, в `dev` через Merge Request со squash |
            | `dev` | тест | всё проверенное; выкладывается после каждого влития |
            | `release/X.Y.Z` | стейдж | состав релиза; создаёт веб-админка |
            | `main` | прод | стабильные релизы; тег `vX.Y.Z` ставит веб-админка |

            Прямой push в `main`, `release/*` и `dev` закрыт. Выкладка, релизы и откат - веб-админка, вкладка «{(server ? "Серверные модули" : "Модули")}».
            Версию (`<Version>` в `.csproj`) и `CHANGELOG.md` при релизе поднимает админка; в задаче допишите строку в «Не выпущено».

            ## Сборка и отладка

            ```powershell
            dotnet build -c Release   # bin\{asm}-<версия>.zip
            ```

            {debug}
            """;
    }

    private const string GitIgnore = """
        bin/
        obj/
        .vs/
        .idea/
        *.user
        TestResults/
        debug.json

        """;

    private static string Solution(string code)
    {
        var project = DeterministicGuid(code + ".csproj");
        var solution = DeterministicGuid(code + ".sln");
        var lines = new[]
        {
            "",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "# Visual Studio Version 17",
            "VisualStudioVersion = 17.0.31903.59",
            "MinimumVisualStudioVersion = 10.0.40219.1",
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{code}\", \"{code}.csproj\", \"{{{project}}}\"",
            "EndProject",
            "Global",
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "\t\tDebug|Any CPU = Debug|Any CPU",
            "\t\tRelease|Any CPU = Release|Any CPU",
            "\tEndGlobalSection",
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
            $"\t\t{{{project}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            $"\t\t{{{project}}}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            $"\t\t{{{project}}}.Release|Any CPU.ActiveCfg = Release|Any CPU",
            $"\t\t{{{project}}}.Release|Any CPU.Build.0 = Release|Any CPU",
            "\tEndGlobalSection",
            "\tGlobalSection(SolutionProperties) = preSolution",
            "\t\tHideSolutionNode = FALSE",
            "\tEndGlobalSection",
            "\tGlobalSection(ExtensibilityGlobals) = postSolution",
            $"\t\tSolutionGuid = {{{solution}}}",
            "\tEndGlobalSection",
            "EndGlobal",
            "",
        };
        return string.Join("\r\n", lines);
    }

    private static string DeterministicGuid(string seed)
    {
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(seed));
        return new Guid(hash).ToString().ToUpperInvariant();
    }

    private static string DebugExe(string client) => client == PilotIce
        ? @"C:\Program Files\ASCON\Pilot-ICE Enterprise\Ascon.Pilot.PilotEnterprise.exe"
        : @"C:\Program Files\ASCON\Pilot-BIM\Ascon.Pilot.PilotBIM.exe";

    private static string Cs(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");
    private static string Xml(string text) => System.Security.SecurityElement.Escape(text) ?? "";

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{1,59}$")]
    private static partial Regex CodeRegex();

    [GeneratedRegex(@"^\d{3}$")]
    private static partial Regex NumberRegex();

    [GeneratedRegex(@"^\d{1,2}$")]
    private static partial Regex SectionRegex();

    [GeneratedRegex(@"^\d+\.\d+\.\d+$")]
    private static partial Regex VersionRegex();
}
