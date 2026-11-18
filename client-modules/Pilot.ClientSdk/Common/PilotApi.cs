using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Ascon.Pilot.SDK;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Сервисы Pilot SDK в одном месте. Заполняются при старте Pilot.ClientSdk (IDataPlugin);
    /// модуль может передать свои сервисы сам - <see cref="Init"/> идемпотентен, повторный вызов ничего не портит.
    /// <code>
    /// [ImportingConstructor]
    /// public StartPoint(IObjectsRepository repository, ISearchService search, IObjectModifier modifier)
    /// {
    ///     PilotApi.Init(repository, search, modifier);   // если модуль стартует раньше Pilot.ClientSdk
    /// }
    ///
    /// var doc = await PilotApi.LoadAsync(id);
    /// var ids = await PilotApi.SearchIdsAsync(b => b.Must(ObjectFields.TypeId.Be(PilotApi.TypeId("0_project"))));
    /// </code>
    /// </summary>
    public static class PilotApi
    {
        public static IObjectsRepository Repository { get; private set; }
        public static ISearchService Search { get; private set; }
        public static IObjectModifier Modifier { get; private set; }
        public static IFileProvider Files { get; private set; }
        public static IPilotDialogService Dialogs { get; private set; }
        public static ITabServiceProvider Tabs { get; private set; }
        public static IClientInfo ClientInfo { get; private set; }
        public static IPersonalSettings PersonalSettings { get; private set; }
        public static IAttributeFormatParser AttributeFormatParser { get; private set; }

        /// <summary>Сервисы доступны (Pilot.ClientSdk или модуль уже вызвал Init).</summary>
        public static bool IsReady => Repository != null;

        /// <summary>
        /// Запомнить сервисы. Непереданные (null) не затирают уже сохранённые.
        /// </summary>
        public static void Init(
            IObjectsRepository repository,
            ISearchService search = null,
            IObjectModifier modifier = null,
            IFileProvider files = null,
            IPilotDialogService dialogs = null,
            ITabServiceProvider tabs = null,
            IClientInfo clientInfo = null,
            IPersonalSettings personalSettings = null,
            IAttributeFormatParser attributeFormatParser = null)
        {
            Repository = repository ?? Repository;
            Search = search ?? Search;
            Modifier = modifier ?? Modifier;
            Files = files ?? Files;
            Dialogs = dialogs ?? Dialogs;
            Tabs = tabs ?? Tabs;
            ClientInfo = clientInfo ?? ClientInfo;
            PersonalSettings = personalSettings ?? PersonalSettings;
            AttributeFormatParser = attributeFormatParser ?? AttributeFormatParser;
        }

        /// <summary>Текущий пользователь.</summary>
        public static IPerson CurrentPerson => Require(Repository, nameof(IObjectsRepository)).GetCurrentPerson();

        /// <summary>Тип по системному имени; null - такого типа нет.</summary>
        public static IType Type(string name) => Require(Repository, nameof(IObjectsRepository)).GetType(name);

        /// <summary>Id типа по системному имени; бросает исключение, если типа нет в базе.</summary>
        public static int TypeId(string name) =>
            Type(name)?.Id ?? throw new InvalidOperationException($"В базе Pilot нет типа «{name}».");

        public static Task<IDataObject> LoadAsync(Guid id, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Loader.LoadAsync(id, timeout, ct);

        public static Task<IReadOnlyList<IDataObject>> LoadAsync(IEnumerable<Guid> ids, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Loader.LoadAsync(ids, timeout, ct);

        /// <summary>Синхронная загрузка (для кода, который не может быть async). На UI-потоке интерфейс не замирает.</summary>
        public static IReadOnlyList<IDataObject> Load(IEnumerable<Guid> ids, TimeSpan? timeout = null) => Loader.Load(ids, timeout);

        public static IDataObject Load(Guid id, TimeSpan? timeout = null) => Loader.Load(id, timeout);

        public static Task<IReadOnlyList<Guid>> SearchIdsAsync(Action<IQueryBuilder> build, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Searcher.IdsAsync(build, timeout, ct);

        public static Task<IReadOnlyList<IDataObject>> SearchObjectsAsync(Action<IQueryBuilder> build, TimeSpan? timeout = null, CancellationToken ct = default) =>
            Searcher.ObjectsAsync(build, timeout, ct);

        /// <summary>Загрузчик на сервисах <see cref="PilotApi"/>.</summary>
        public static Loader Loader => new Loader(Require(Repository, nameof(IObjectsRepository)));

        /// <summary>Поиск на сервисах <see cref="PilotApi"/>.</summary>
        public static Search Searcher => new Search(Require(Search, nameof(ISearchService)), Require(Repository, nameof(IObjectsRepository)));

        /// <summary>Оргструктура (снимок на момент вызова).</summary>
        public static Org Org => new Org(Require(Repository, nameof(IObjectsRepository)));

        internal static T Require<T>(T service, string name) where T : class =>
            service ?? throw new InvalidOperationException(
                $"Сервис {name} недоступен: модуль Pilot.ClientSdk не загружен клиентом Pilot и модуль не вызвал PilotApi.Init(...).");
    }
}
