using Npgsql;

namespace PilotMigration.Objects;

/// <summary>Группы объектов базы, которые переносятся отдельными галочками.</summary>
public enum ObjectCategory
{
    Reports,
    ClientModules,
    WebModules,
    ServerModules,
}

public static class ObjectCategories
{
    /// <summary>Группа по имени типа Pilot; null - обычный объект дерева.</summary>
    public static ObjectCategory? ByTypeName(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            return null;
        // «Папка расширений» - общий контейнер, в группу не входит: при замене модулей она остаётся,
        // у неё правится только список детей.
        if (typeName == "Extension")
            return ObjectCategory.ClientModules;
        if (typeName == "WebExtension")
            return ObjectCategory.WebModules;
        if (typeName == "$PilotServerExtension")
            return ObjectCategory.ServerModules;
        // Report_6088AF81-…, Report_Folder_F2CC6F1D-… - служебные типы модуля отчётов.
        if (typeName.StartsWith("Report_", StringComparison.Ordinal) &&
            Guid.TryParse(typeName[(typeName.LastIndexOf('_') + 1)..], out _))
            return ObjectCategory.Reports;
        return null;
    }

    public static string Title(ObjectCategory c) => c switch
    {
        ObjectCategory.Reports => "Отчёты",
        ObjectCategory.ClientModules => "Клиентские модули",
        ObjectCategory.WebModules => "Веб-модули",
        ObjectCategory.ServerModules => "Серверные модули",
        _ => c.ToString(),
    };
}

/// <summary>Дерево объектов одной базы: заголовки объектов и дети по ParentId.</summary>
public sealed class ObjectIndex
{
    private readonly Dictionary<Guid, List<Guid>> _byParent = new();

    public ObjectIndex(IEnumerable<ObjectHeader> headers)
    {
        foreach (var h in headers)
        {
            Nodes[h.Id] = h;
            if (h.ParentId == Guid.Empty || h.ParentId == h.Id)
                continue;
            if (!_byParent.TryGetValue(h.ParentId, out var list))
                _byParent[h.ParentId] = list = new List<Guid>();
            list.Add(h.Id);
        }
    }

    public Dictionary<Guid, ObjectHeader> Nodes { get; } = new();

    public bool Contains(Guid id) => Nodes.ContainsKey(id);

    public IReadOnlyList<Guid> ChildrenOf(Guid id) => _byParent.TryGetValue(id, out var list) ? list : Array.Empty<Guid>();

    /// <summary>Объект и все его потомки (по ParentId).</summary>
    public HashSet<Guid> Subtree(Guid root)
    {
        var result = new HashSet<Guid>();
        if (!Nodes.ContainsKey(root))
            return result;
        var stack = new Stack<Guid>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!result.Add(id))
                continue;
            foreach (var c in ChildrenOf(id))
                stack.Push(c);
        }
        return result;
    }

    public HashSet<Guid> Subtrees(IEnumerable<Guid> roots)
    {
        var result = new HashSet<Guid>();
        foreach (var r in roots)
        {
            if (!result.Contains(r))
                result.UnionWith(Subtree(r));
        }
        return result;
    }

    public static async Task<ObjectIndex> LoadAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, string objectsTable, CancellationToken ct)
    {
        var headers = new List<ObjectHeader>();
        await using var cmd = new NpgsqlCommand($"select id, data from {objectsTable}", connection, tx);
        await using var reader = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            if (reader.IsDBNull(1))
            {
                headers.Add(new ObjectHeader(id, 0, Guid.Empty, 0, Array.Empty<Guid>()));
                continue;
            }
            var data = (byte[])reader.GetValue(1);
            ObjectHeader h;
            try
            {
                h = ObjectHeader.Parse(data);
            }
            catch (FormatException ex)
            {
                throw new MigrationException($"Не удалось разобрать объект {id}: {ex.Message}", ex);
            }
            // Id берётся из строки таблицы - в данных он может быть опущен.
            headers.Add(h with { Id = id });
        }
        return new ObjectIndex(headers);
    }
}

/// <summary>Что переносить из дерева объектов.</summary>
public sealed class ObjectSelectionRequest
{
    /// <summary>Всё дерево, кроме объектов групп (отчёты, модули) - они переносятся своими галочками.</summary>
    public bool TreeAll { get; init; }

    /// <summary>Выбранные объекты: переносятся вместе со всеми потомками и всеми родителями.</summary>
    public IReadOnlyCollection<Guid> TreeIds { get; init; } = Array.Empty<Guid>();

    public IReadOnlyCollection<ObjectCategory> Categories { get; init; } = Array.Empty<ObjectCategory>();

    public bool IsEmpty => !TreeAll && TreeIds.Count == 0 && Categories.Count == 0;
}

/// <summary>
/// План переноса объектов: какие строки источника записать в приёмник, какие объекты приёмника удалить
/// и у каких оставшихся объектов поправить список детей.
/// </summary>
public sealed class ObjectPlan
{
    /// <summary>Объекты источника, которые записываются в приёмник (заменяя одноимённые).</summary>
    public HashSet<Guid> Copy { get; } = new();

    /// <summary>Объекты приёмника, которые удаляются.</summary>
    public HashSet<Guid> Delete { get; } = new();

    /// <summary>Объекты приёмника, которые остаются, но их список детей меняется.</summary>
    public HashSet<Guid> Touch { get; } = new();

    /// <summary>Родители, добавленные из источника, потому что их нет в приёмнике.</summary>
    public HashSet<Guid> AddedAncestors { get; } = new();

    public List<Guid> NotFound { get; } = new();

    public List<string> Warnings { get; } = new();

    public required ObjectIndex Source { get; init; }
    public required ObjectIndex Target { get; init; }

    public bool Exists(Guid id) => Copy.Contains(id) || (Target.Contains(id) && !Delete.Contains(id));

    public (Guid Parent, int Type)? Final(Guid id)
    {
        if (Copy.Contains(id))
        {
            var s = Source.Nodes[id];
            return (s.ParentId, s.TypeId);
        }
        if (Target.Nodes.TryGetValue(id, out var t) && !Delete.Contains(id))
            return (t.ParentId, t.TypeId);
        return null;
    }

    /// <summary>
    /// Новый список детей для объекта из <see cref="Copy"/> или <see cref="Touch"/>: без исчезнувших и перемещённых,
    /// плюс недостающие. null - менять не нужно.
    /// </summary>
    public (Func<Guid, bool> Keep, List<(Guid Id, int TypeId)> Add)? ChildrenEdit(Guid id, IReadOnlyList<Guid> currentChildren)
    {
        bool Keep(Guid c)
        {
            var f = Final(c);
            if (f is null)
                return false;
            return !Copy.Contains(c) || f.Value.Parent == id;
        }

        var listed = currentChildren.ToHashSet();
        var add = new List<(Guid, int)>();
        IEnumerable<Guid> candidates = Source.ChildrenOf(id).Where(Copy.Contains);
        if (Copy.Contains(id))
            candidates = candidates.Concat(Target.ChildrenOf(id).Where(c => !Copy.Contains(c) && !Delete.Contains(c)));
        foreach (var c in candidates.Distinct())
        {
            var f = Final(c);
            if (f is not null && f.Value.Parent == id && !listed.Contains(c))
                add.Add((c, f.Value.Type));
        }

        var removes = currentChildren.Any(c => !Keep(c));
        if (!removes && add.Count == 0)
            return null;
        return (Keep, add);
    }

    public static ObjectPlan Build(
        ObjectIndex source,
        ObjectIndex target,
        Func<int, ObjectCategory?> sourceCategory,
        Func<int, ObjectCategory?> targetCategory,
        ObjectSelectionRequest request)
    {
        var plan = new ObjectPlan { Source = source, Target = target };

        HashSet<Guid> CategorySet(ObjectIndex index, Func<int, ObjectCategory?> category, Func<ObjectCategory, bool> filter) =>
            index.Subtrees(index.Nodes.Values.Where(n => category(n.TypeId) is { } c && filter(c)).Select(n => n.Id));

        // Замена групп целиком: всё из источника, лишнее в приёмнике удаляется.
        if (request.Categories.Count > 0)
        {
            var wanted = request.Categories.ToHashSet();
            var s = CategorySet(source, sourceCategory, wanted.Contains);
            var t = CategorySet(target, targetCategory, wanted.Contains);
            plan.Copy.UnionWith(s);
            plan.Delete.UnionWith(t.Where(id => !s.Contains(id)));
        }

        if (request.TreeAll)
        {
            var sCat = CategorySet(source, sourceCategory, _ => true);
            var tCat = CategorySet(target, targetCategory, _ => true);
            var s = source.Nodes.Keys.Where(id => !sCat.Contains(id)).ToHashSet();
            var t = target.Nodes.Keys.Where(id => !tCat.Contains(id)).ToHashSet();
            plan.Copy.UnionWith(s);
            plan.Delete.UnionWith(t.Where(id => !s.Contains(id)));
        }
        else
        {
            foreach (var id in request.TreeIds.Distinct())
            {
                if (!source.Contains(id))
                {
                    plan.NotFound.Add(id);
                    continue;
                }
                var s = source.Subtree(id);
                plan.Copy.UnionWith(s);
                plan.Delete.UnionWith(target.Subtree(id).Where(x => !s.Contains(x)));
            }
        }

        plan.Delete.ExceptWith(plan.Copy);
        plan.AddAncestors();

        // Родители удалённых и перемещённых объектов, которые остаются в приёмнике.
        foreach (var id in plan.Delete.Concat(plan.Copy.Where(target.Contains)))
        {
            var parent = target.Nodes[id].ParentId;
            if (parent != Guid.Empty && target.Contains(parent) && !plan.Delete.Contains(parent) && !plan.Copy.Contains(parent))
                plan.Touch.Add(parent);
        }
        plan.Touch.ExceptWith(plan.Copy);
        return plan;
    }

    /// <summary>Родители переносимых объектов: остаются в приёмнике (правится список детей) или берутся из источника.</summary>
    private void AddAncestors()
    {
        var queue = new Queue<Guid>(Copy);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            var parent = Source.Nodes[id].ParentId;
            if (parent == Guid.Empty || parent == id || Copy.Contains(parent))
                continue;
            if (Target.Contains(parent) && !Delete.Contains(parent))
            {
                Touch.Add(parent);
                continue;
            }
            if (Source.Contains(parent))
            {
                Delete.Remove(parent);
                Copy.Add(parent);
                AddedAncestors.Add(parent);
                queue.Enqueue(parent);
                continue;
            }
            Warnings.Add($"Родитель {parent} объекта {id} не найден ни в источнике, ни в приёмнике - объект будет без родителя.");
        }
    }
}
