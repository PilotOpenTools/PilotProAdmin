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
    /// Загрузка объектов Pilot по Id. Замена ObjectLoader / ObjectLoaderSync / IdLoader из NugetPackage:
    /// экземпляр без состояния (можно вызывать параллельно), таймаут не теряет уже загруженное,
    /// несуществующие и недоступные объекты не подвешивают загрузку.
    /// <code>
    /// var loader = new Loader(repository);
    /// var obj = await loader.LoadAsync(id);                      // null - нет объекта или нет прав
    /// var many = await loader.LoadAsync(ids, TimeSpan.FromSeconds(30));
    /// var fresh = await loader.WaitAsync(id, o => o.Str("number") != "");   // дождаться изменения после Apply
    /// </code>
    /// </summary>
    public sealed class Loader
    {
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

        private readonly IObjectsRepository _repository;

        public Loader(IObjectsRepository repository) =>
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

        /// <summary>Объект или null (нет, удалён безвозвратно, нет прав, не дождались).</summary>
        public async Task<IDataObject> LoadAsync(Guid id, TimeSpan? timeout = null, CancellationToken ct = default) =>
            (await LoadAsync(new[] { id }, timeout, ct).ConfigureAwait(false)).FirstOrDefault();

        /// <summary>
        /// Объекты в порядке <paramref name="ids"/>; отсутствующие пропускаются.
        /// <paramref name="minChangeset"/> - ждать версию объекта не старше этого changeset (после своего Apply).
        /// </summary>
        public Task<IReadOnlyList<IDataObject>> LoadAsync(IEnumerable<Guid> ids, TimeSpan? timeout = null, CancellationToken ct = default,
            long minChangeset = 0)
        {
            var wanted = ids?.Where(id => id != Guid.Empty).Distinct().ToList() ?? throw new ArgumentNullException(nameof(ids));
            if (wanted.Count == 0)
                return Task.FromResult<IReadOnlyList<IDataObject>>(Array.Empty<IDataObject>());

            var loaded = new Dictionary<Guid, IDataObject>();
            var finished = new HashSet<Guid>();

            return SdkSubscription.RunAsync<IDataObject, IReadOnlyList<IDataObject>>(
                observer => _repository.SubscribeObjects(wanted).Subscribe(observer),
                obj =>
                {
                    if (obj == null || !IsFinal(obj.State))
                        return false;
                    if (obj.State == DataState.Loaded)
                    {
                        if (minChangeset > 0 && obj.LastChange() < minChangeset)
                            return false;
                        loaded[obj.Id] = obj;
                    }
                    finished.Add(obj.Id);
                    return finished.Count == wanted.Count;
                },
                () => wanted.Where(loaded.ContainsKey).Select(id => loaded[id]).ToList(),
                timeout ?? DefaultTimeout, ct, "Загрузка объектов");
        }

        /// <summary>
        /// Ждёт, пока объект станет удовлетворять условию (например, нумератор посчитан или атрибут записан
        /// после <c>modifier.Apply()</c>). null - не дождались за таймаут.
        /// </summary>
        public Task<IDataObject> WaitAsync(Guid id, Func<IDataObject, bool> condition, TimeSpan? timeout = null, CancellationToken ct = default)
        {
            if (condition == null) throw new ArgumentNullException(nameof(condition));
            IDataObject match = null;
            return SdkSubscription.RunAsync<IDataObject, IDataObject>(
                observer => _repository.SubscribeObjects(new[] { id }).Subscribe(observer),
                obj =>
                {
                    if (obj == null || obj.State != DataState.Loaded || !condition(obj))
                        return false;
                    match = obj;
                    return true;
                },
                () => match,
                timeout ?? DefaultTimeout, ct, "Ожидание изменения объекта");
        }

        /// <summary>Синхронно (для кода, который не может быть async). На UI-потоке интерфейс не замирает.</summary>
        public IReadOnlyList<IDataObject> Load(IEnumerable<Guid> ids, TimeSpan? timeout = null) =>
            SdkSubscription.Wait(LoadAsync(ids, timeout));

        public IDataObject Load(Guid id, TimeSpan? timeout = null) => Load(new[] { id }, timeout).FirstOrDefault();

        /// <summary>Дочерние объекты; <paramref name="typeNames"/> - только этих типов.</summary>
        public async Task<IReadOnlyList<IDataObject>> ChildrenAsync(IDataObject parent, params string[] typeNames)
        {
            if (parent == null || parent.Children == null || parent.Children.Count == 0)
                return Array.Empty<IDataObject>();
            var children = await LoadAsync(parent.Children).ConfigureAwait(false);
            return typeNames == null || typeNames.Length == 0
                ? children
                : children.Where(c => typeNames.Contains(c.Type?.Name)).ToList();
        }

        /// <summary>Родитель; null - это корень.</summary>
        public Task<IDataObject> ParentAsync(IDataObject obj) =>
            obj == null || obj.ParentId == Guid.Empty || obj.ParentId == obj.Id
                ? Task.FromResult<IDataObject>(null)
                : LoadAsync(obj.ParentId);

        /// <summary>Предки от ближайшего родителя к корню.</summary>
        public async Task<IReadOnlyList<IDataObject>> AncestorsAsync(IDataObject obj)
        {
            var result = new List<IDataObject>();
            var current = obj;
            for (var depth = 0; depth < 100; depth++)
            {
                current = await ParentAsync(current).ConfigureAwait(false);
                if (current == null || current.Type?.Name == SystemTypeNames.GLOBAL_ROOT_TYPE_NAME)
                    break;
                result.Add(current);
            }
            return result;
        }

        /// <summary>
        /// Ближайший предок одного из типов (сам объект не проверяется). Замена GetParentByType / GetProjectFolder:
        /// не падает на корне и не уходит в бесконечную рекурсию.
        /// </summary>
        public async Task<IDataObject> ParentOfTypeAsync(IDataObject obj, params string[] typeNames)
        {
            var current = obj;
            for (var depth = 0; depth < 100; depth++)
            {
                current = await ParentAsync(current).ConfigureAwait(false);
                if (current == null || current.Type?.Name == SystemTypeNames.GLOBAL_ROOT_TYPE_NAME)
                    return null;
                if (typeNames.Contains(current.Type?.Name))
                    return current;
            }
            return null;
        }

        /// <summary>Сам объект, если он нужного типа, иначе ближайший предок этого типа.</summary>
        public Task<IDataObject> SelfOrParentOfTypeAsync(IDataObject obj, params string[] typeNames) =>
            obj != null && typeNames.Contains(obj.Type?.Name) ? Task.FromResult(obj) : ParentOfTypeAsync(obj, typeNames);

        /// <summary>Папка проекта (тип <c>0_project</c> по умолчанию).</summary>
        public Task<IDataObject> ProjectAsync(IDataObject obj, string projectType = "0_project") =>
            SelfOrParentOfTypeAsync(obj, projectType);

        /// <summary>Объекты, на которые ссылаются связи объекта (только загруженные и доступные).</summary>
        public Task<IReadOnlyList<IDataObject>> RelatedAsync(IDataObject obj, ObjectRelationType? type = null, string relationName = null)
        {
            var ids = (obj?.Relations ?? Enumerable.Empty<IRelation>())
                .Where(r => r != null && (type == null || r.Type == type) && (relationName == null || r.Name == relationName))
                .Select(r => r.TargetId);
            return LoadAsync(ids);
        }

        private static bool IsFinal(DataState state) =>
            state == DataState.Loaded || state == DataState.NoData || state == DataState.NonExistent;
    }
}
