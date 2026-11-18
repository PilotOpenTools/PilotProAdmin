using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Серверный поиск через ISearchService. Замена SearchHelper / Searcher из NugetPackage:
    /// ждёт полный (Remote) результат, не зависает без ответа, умеет искать по имени типа и внутри папки.
    /// <code>
    /// var search = new Search(searchService, repository);
    /// var projects = await search.ByTypeAsync("0_project");
    /// var docs = await search.ByAttributeAsync("pir_doc", "code", "01-АР", inContext: project.Id);
    /// var tasks = await search.ObjectsAsync(b =>
    /// {
    ///     b.Must(ObjectFields.TypeId.Be(PilotApi.TypeId("task_approval")));
    ///     b.Must(AttributeFields.State("state").Be(stateId));
    /// });
    /// </code>
    /// По умолчанию ищутся живые и замороженные объекты (объекты в корзине и удалённые - нет).
    /// </summary>
    public sealed class Search
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        private readonly ISearchService _search;
        private readonly IObjectsRepository _repository;

        public Search(ISearchService search, IObjectsRepository repository)
        {
            _search = search ?? throw new ArgumentNullException(nameof(search));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        }

        /// <summary>Id найденных объектов. <paramref name="build"/> добавляет условия к пустому запросу объектов.</summary>
        public Task<IReadOnlyList<Guid>> IdsAsync(Action<IQueryBuilder> build, TimeSpan? timeout = null, CancellationToken ct = default,
            bool includeFrozen = true)
        {
            if (build == null) throw new ArgumentNullException(nameof(build));

            var builder = _search.GetObjectQueryBuilder();
            builder.MaxResults(int.MaxValue);
            builder.Must(includeFrozen
                ? ObjectFields.ObjectState.BeAnyOf(ObjectState.Alive, ObjectState.Frozen)
                : ObjectFields.ObjectState.Be(ObjectState.Alive));
            build(builder);

            IReadOnlyList<Guid> result = Array.Empty<Guid>();
            return SdkSubscription.RunAsync<ISearchResult, IReadOnlyList<Guid>>(
                observer => _search.Search(builder).Subscribe(observer),
                r =>
                {
                    // Сначала приходит локальный результат из кэша клиента, полный - Remote.
                    if (r == null || r.Kind != SearchResultKind.Remote)
                        return false;
                    result = r.Result?.ToList() ?? new List<Guid>();
                    return true;
                },
                () => result,
                timeout ?? DefaultTimeout, ct, "Поиск");
        }

        /// <summary>Найденные объекты, загруженные через SDK.</summary>
        public async Task<IReadOnlyList<IDataObject>> ObjectsAsync(Action<IQueryBuilder> build, TimeSpan? timeout = null, CancellationToken ct = default,
            bool includeFrozen = true)
        {
            var ids = await IdsAsync(build, timeout, ct, includeFrozen).ConfigureAwait(false);
            return await new Loader(_repository).LoadAsync(ids, timeout, ct).ConfigureAwait(false);
        }

        /// <summary>Объекты типов (по системным именам); <paramref name="inContext"/> - только внутри папки (рекурсивно).</summary>
        public Task<IReadOnlyList<IDataObject>> ByTypeAsync(string typeName, Guid? inContext = null, CancellationToken ct = default) =>
            ByTypesAsync(new[] { typeName }, inContext, ct);

        public Task<IReadOnlyList<IDataObject>> ByTypesAsync(IEnumerable<string> typeNames, Guid? inContext = null, CancellationToken ct = default) =>
            ObjectsAsync(b =>
            {
                b.Must(ObjectFields.TypeId.BeAnyOf(TypeIds(typeNames)));
                if (inContext is Guid context)
                    b.InContext(context);
            }, ct: ct);

        /// <summary>Объекты типа с точным значением строкового атрибута.</summary>
        public Task<IReadOnlyList<IDataObject>> ByAttributeAsync(string typeName, string attribute, string value, Guid? inContext = null,
            CancellationToken ct = default) =>
            ObjectsAsync(b =>
            {
                b.Must(ObjectFields.TypeId.BeAnyOf(TypeIds(new[] { typeName })));
                b.Must(AttributeFields.String(attribute).Be(value));
                if (inContext is Guid context)
                    b.InContext(context);
            }, ct: ct);

        /// <summary>Объекты типа, в атрибуте-оргединице которых одна из должностей <paramref name="positionIds"/>.</summary>
        public Task<IReadOnlyList<IDataObject>> ByOrgUnitAsync(string typeName, string attribute, IEnumerable<int> positionIds, Guid? inContext = null,
            CancellationToken ct = default) =>
            ObjectsAsync(b =>
            {
                b.Must(ObjectFields.TypeId.BeAnyOf(TypeIds(new[] { typeName })));
                b.Must(AttributeFields.OrgUnit(attribute).BeAnyOf(positionIds.ToArray()));
                if (inContext is Guid context)
                    b.InContext(context);
            }, ct: ct);

        private int[] TypeIds(IEnumerable<string> typeNames)
        {
            var ids = new List<int>();
            foreach (var name in typeNames ?? Enumerable.Empty<string>())
            {
                var type = _repository.GetType(name);
                if (type == null)
                    throw new InvalidOperationException($"В базе Pilot нет типа «{name}».");
                ids.Add(type.Id);
            }
            return ids.ToArray();
        }
    }
}
