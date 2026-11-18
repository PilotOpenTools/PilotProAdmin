using Ascon.Pilot.DataClasses;
using Ascon.Pilot.DataClasses.Utils;

namespace ProAdmin.Types;

public enum TypesChangeAction
{
    Create,
    Update,
    Delete,
}

/// <summary>Изменение одного элемента базы. Danger - может потерять данные или сломать объекты (удаление атрибута, смена типа).</summary>
public sealed record TypesChange(string Kind, string Name, TypesChangeAction Action, IReadOnlyList<string> Details, bool Danger)
{
    public string ActionTitle => Action switch
    {
        TypesChangeAction.Create => "добавить",
        TypesChangeAction.Delete => "удалить",
        _ => "изменить",
    };

    public string Line => $"{ActionTitle} {Kind} «{Name}»" + (Details.Count > 0 ? ": " + string.Join("; ", Details) : "");
}

/// <summary>
/// План записи ветки на сервер. <see cref="Metadata"/> - метаданные сервера, уже изменённые планом (их и пишет
/// UpdateMetadata); исходные версии затронутых элементов сохранены для отката.
/// </summary>
public sealed class TypesPlan
{
    public required DMetadata Metadata { get; init; }
    public long BaseVersion { get; init; }
    public List<TypesChange> Changes { get; } = new();
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();

    /// <summary>Неудалённые элементы сервера, которых нет в репозитории: план их не трогает.</summary>
    public List<string> Unmanaged { get; } = new();

    public bool HasChanges => Changes.Count > 0;
    public bool CanApply => Errors.Count == 0;
    public bool HasDanger => Changes.Any(c => c.Danger);

    // Для отката: что было до плана.
    public Dictionary<int, MType> TypesBefore { get; } = new();
    public HashSet<int> CreatedTypes { get; } = new();
    public Dictionary<Guid, MUserState> StatesBefore { get; } = new();
    public HashSet<Guid> CreatedStates { get; } = new();
    public Dictionary<Guid, MUserStateMachine> MachinesBefore { get; } = new();
    public HashSet<Guid> CreatedMachines { get; } = new();
}

/// <summary>
/// Ветка репозитория / изменения метаданных сервера. Правила (проверены на тесте и стейдже, см. README репозитория):
/// элементы сопоставляются по имени; новый тип получает Id = max+1; тип не удаляется, а помечается удалённым;
/// записываются только элементы, которые отличаются, - остальное (в том числе автоматизации) остаётся как было.
/// </summary>
public static class TypesPlanner
{
    public static TypesPlan Plan(TypesSnapshot repo, DMetadata m)
    {
        var plan = new TypesPlan { Metadata = m, BaseVersion = m.Version };
        var validation = TypesValidator.Validate(repo);
        plan.Errors.AddRange(validation.Errors);
        plan.Warnings.AddRange(validation.Warnings);
        if (!validation.Ok)
            return plan;

        PlanStates(repo, m, plan);
        PlanMachines(repo, m, plan);
        PlanTypes(repo, m, plan);
        plan.Errors.AddRange(TypesValidator.DuplicateTypeOrders(m.Types.Select(t => (t.Sort, t.Name))));
        CheckUsage(m, plan);

        foreach (var t in m.Types.Where(t => !t.IsDeleted && !repo.Types.ContainsKey(t.Name)))
            plan.Unmanaged.Add($"тип «{t.Name}»");
        foreach (var s in m.UserStates.Where(s => !s.IsDeleted && !repo.States.ContainsKey(s.Name)))
            plan.Unmanaged.Add($"состояние «{s.Name}»");
        foreach (var x in m.StateMachines.Where(x => repo.Machines.All(d => d.Id != x.Id && d.Title != x.Title)))
            plan.Unmanaged.Add($"группа состояний «{x.Title}»");
        return plan;
    }

    // ---------------------------------------------------------------- состояния

    private static void PlanStates(TypesSnapshot repo, DMetadata m, TypesPlan plan)
    {
        foreach (var doc in repo.States.Values)
        {
            var state = m.UserStates.FirstOrDefault(s => s.Name == doc.Name && !s.IsDeleted)
                        ?? m.UserStates.FirstOrDefault(s => s.Name == doc.Name);
            if (doc.IsSystem)
            {
                if (state is null)
                    plan.Errors.Add($"Системное состояние «{doc.Name}» не найдено на сервере.");
                continue;
            }

            if (state is null)
            {
                if (doc.IsDeleted)
                    continue;
                var id = doc.Id is { } wanted && wanted != Guid.Empty && !IdTaken(m, wanted) ? wanted : Guid.NewGuid();
                state = new MUserState { Id = id, Name = doc.Name };
                SetState(state, doc);
                m.UserStates.Add(state);
                plan.CreatedStates.Add(id);
                plan.Changes.Add(new TypesChange("состояние", doc.Name, TypesChangeAction.Create, new[] { $"«{doc.Title}»" }, false));
                continue;
            }

            var current = TypesExport.State(state);
            var details = new List<string>();
            Diff(details, "заголовок", current.Title, doc.Title);
            Diff(details, "цвет", current.Color.ToString(), doc.Color.ToString());
            Diff(details, "завершающее", YesNo(current.IsCompletionState), YesNo(doc.IsCompletionState));
            if (current.Icon != doc.Icon) details.Add("иконка");
            if (current.IsDeleted != doc.IsDeleted) details.Add(doc.IsDeleted ? "помечается удалённым" : "восстанавливается");
            if (details.Count == 0)
                continue;

            plan.StatesBefore.TryAdd(state.Id, CloneState(state));
            SetState(state, doc);
            plan.Changes.Add(new TypesChange("состояние", doc.Name,
                doc.IsDeleted && !current.IsDeleted ? TypesChangeAction.Delete : TypesChangeAction.Update, details, doc.IsDeleted && !current.IsDeleted));
        }
    }

    private static void SetState(MUserState state, StateDoc doc)
    {
        state.Title = doc.Title ?? "";
        state.Color = doc.Color;
        state.IsCompletionState = doc.IsCompletionState;
        state.IsDeleted = doc.IsDeleted;
        state.Icon = doc.Icon is null ? null : Convert.FromBase64String(doc.Icon);
    }

    // ---------------------------------------------------------------- группы состояний

    private static void PlanMachines(TypesSnapshot repo, DMetadata m, TypesPlan plan)
    {
        var names = new MetadataNames(m);
        foreach (var doc in repo.Machines)
        {
            var machine = (doc.Id is { } id ? m.StateMachines.FirstOrDefault(x => x.Id == id) : null)
                          ?? m.StateMachines.FirstOrDefault(x => x.Title == doc.Title);

            if (doc.IsDeleted)
            {
                if (machine is null)
                    continue;
                plan.MachinesBefore.TryAdd(machine.Id, CloneMachine(machine));
                m.StateMachines.Remove(machine);
                plan.Changes.Add(new TypesChange("группа состояний", doc.Title, TypesChangeAction.Delete, Array.Empty<string>(), true));
                continue;
            }

            var missing = new List<string>();
            var transitions = BuildTransitions(doc, m, missing);
            if (missing.Count > 0)
            {
                plan.Errors.Add($"Группа состояний «{doc.Title}»: на сервере нет состояний {string.Join(", ", missing.Distinct())}.");
                continue;
            }

            if (machine is null)
            {
                var newId = doc.Id is { } wanted && wanted != Guid.Empty && !IdTaken(m, wanted) ? wanted : Guid.NewGuid();
                m.StateMachines.Add(new MUserStateMachine { Id = newId, Title = doc.Title, StateTransitions = transitions });
                plan.CreatedMachines.Add(newId);
                plan.Changes.Add(new TypesChange("группа состояний", doc.Title, TypesChangeAction.Create,
                    new[] { $"переходов: {doc.States.Sum(s => s.Transitions.Count)}" }, false));
                continue;
            }

            var current = TypesExport.Machine(machine, names);
            var details = new List<string>();
            Diff(details, "заголовок", current.Title, doc.Title);
            details.AddRange(TransitionDiff(current, doc));
            if (details.Count == 0 && TypesJson.Same(current.States, doc.States))
                continue;
            if (details.Count == 0)
                details.Add("порядок переходов");

            plan.MachinesBefore.TryAdd(machine.Id, CloneMachine(machine));
            machine.Title = doc.Title;
            machine.StateTransitions = transitions;
            plan.Changes.Add(new TypesChange("группа состояний", doc.Title, TypesChangeAction.Update, details, false));
        }
    }

    private static StateTransitionsCollection BuildTransitions(MachineDoc doc, DMetadata m, List<string> missing)
    {
        var result = new StateTransitionsCollection();
        foreach (var st in doc.States)
        {
            var from = StateId(m, st.State);
            if (from is null) { missing.Add($"«{st.State}»"); continue; }
            var list = new List<MTransition>();
            foreach (var t in st.Transitions)
            {
                var to = StateId(m, t.To);
                if (to is null) { missing.Add($"«{t.To}»"); continue; }
                list.Add(new MTransition { StateTo = to.Value, DisplayName = t.DisplayName ?? "", AvailableForPositionsSource = t.Positions.ToArray() });
            }
            result[from.Value] = list.ToArray();
        }
        return result;
    }

    private static IEnumerable<string> TransitionDiff(MachineDoc a, MachineDoc b)
    {
        static Dictionary<string, TransitionDoc> Flat(MachineDoc d) => d.States
            .SelectMany(s => s.Transitions.Select(t => (Key: $"{s.State} / {t.To}", t)))
            .GroupBy(x => x.Key).ToDictionary(g => g.Key, g => g.First().t, StringComparer.Ordinal);
        var fa = Flat(a);
        var fb = Flat(b);
        foreach (var k in fb.Keys.Except(fa.Keys)) yield return $"+ переход {k}";
        foreach (var k in fa.Keys.Except(fb.Keys)) yield return $"− переход {k}";
        foreach (var k in fa.Keys.Intersect(fb.Keys).Where(k => !TypesJson.Same(fa[k], fb[k]))) yield return $"~ переход {k}";
    }

    // ---------------------------------------------------------------- типы

    private static void PlanTypes(TypesSnapshot repo, DMetadata m, TypesPlan plan)
    {
        // 1. Новые типы - Id строго max+1 (иначе сервер отвечает «Sequential of MType's id is wrong»).
        var maxId = m.Types.Count == 0 ? 0 : m.Types.Max(t => t.Id);
        foreach (var doc in repo.Types.Values.Where(d => !d.IsDeleted))
        {
            if (m.Types.Any(t => t.Name == doc.Name))
                continue;
            var t = new MType { Id = ++maxId, Name = doc.Name, Title = doc.Title ?? doc.Name };
            m.Types.Add(t);
            plan.CreatedTypes.Add(t.Id);
        }

        // 2. Свойства, атрибуты и ссылки - когда все типы, состояния и группы уже на месте.
        var names = new MetadataNames(m);
        foreach (var doc in repo.Types.Values)
        {
            var t = m.Types.Where(x => x.Name == doc.Name).OrderBy(x => x.IsDeleted).FirstOrDefault();
            if (t is null)
                continue; // удалённый в репозитории и отсутствующий на сервере
            var created = plan.CreatedTypes.Contains(t.Id);
            var current = TypesExport.Type(t, names);
            if (!created && TypesJson.Same(current, doc))
                continue;

            if (!created)
                plan.TypesBefore.TryAdd(t.Id, CloneType(t));
            var details = new List<string>();
            var danger = false;
            var missing = new List<string>();
            ApplyType(t, current, doc, m, names, details, missing, ref danger);
            if (missing.Count > 0)
            {
                plan.Errors.Add($"Тип «{doc.Name}»: на сервере нет {string.Join(", ", missing.Distinct())}.");
                continue;
            }

            var action = created ? TypesChangeAction.Create
                : doc.IsDeleted && !current.IsDeleted ? TypesChangeAction.Delete
                : TypesChangeAction.Update;
            if (created)
                details.Insert(0, $"«{doc.Title}», атрибутов: {doc.Attributes.Count}");
            if (details.Count == 0)
                details.Add("порядок атрибутов или переводы строк");
            plan.Changes.Add(new TypesChange("тип", doc.Name, action, details, danger || action == TypesChangeAction.Delete));
        }
    }

    private static void ApplyType(MType t, TypeDoc cur, TypeDoc doc, DMetadata m, MetadataNames names,
        List<string> details, List<string> missing, ref bool danger)
    {
        if (Diff(details, "заголовок", cur.Title, doc.Title)) t.Title = doc.Title ?? "";
        if (Diff(details, "вид", cur.Kind.ToString(), doc.Kind.ToString())) t.Kind = doc.Kind;
        if (Diff(details, "порядок", cur.Sort.ToString(), doc.Sort.ToString())) t.Sort = doc.Sort;
        if (Diff(details, "есть файлы", YesNo(cur.HasFiles), YesNo(doc.HasFiles))) t.HasFiles = doc.HasFiles;
        if (Diff(details, "монтируемый", YesNo(cur.IsMountable), YesNo(doc.IsMountable))) t.IsMountable = doc.IsMountable;
        if (Diff(details, "служебный", YesNo(cur.IsService), YesNo(doc.IsService))) t.IsService = doc.IsService;
        if (Diff(details, "проект", YesNo(cur.IsProject), YesNo(doc.IsProject))) t.IsProject = doc.IsProject;
        if (Diff(details, "виден в хранилище", YesNo(cur.IsVisibleOnStorage), YesNo(doc.IsVisibleOnStorage))) t.IsVisibleOnStorage = doc.IsVisibleOnStorage;
        if (cur.IsDeleted != doc.IsDeleted)
        {
            details.Add(doc.IsDeleted ? "помечается удалённым" : "восстанавливается");
            t.IsDeleted = doc.IsDeleted;
        }

        if (Diff(details, "группа состояний", cur.StateMachine, doc.StateMachine))
        {
            if (doc.StateMachine is null)
                t.StateMachineId = Guid.Empty;
            else if (MachineId(m, doc.StateMachine) is { } mid)
                t.StateMachineId = mid;
            else
                missing.Add($"группы состояний «{doc.StateMachine}»");
        }

        if (!cur.Children.SequenceEqual(doc.Children, StringComparer.Ordinal))
        {
            var added = doc.Children.Except(cur.Children).ToList();
            var removed = cur.Children.Except(doc.Children).ToList();
            details.Add("дочерние типы" + (added.Count > 0 ? " +" + string.Join(", +", added) : "") + (removed.Count > 0 ? " −" + string.Join(", −", removed) : ""));
            var ids = new List<int>();
            foreach (var child in doc.Children)
            {
                var c = m.Types.Where(x => x.Name == child).OrderBy(x => x.IsDeleted).FirstOrDefault();
                if (c is null) missing.Add($"дочернего типа «{child}»");
                else ids.Add(c.Id);
            }
            t.Children.Clear();
            t.Children.AddRange(ids);
        }

        if (!SameText(cur.Configuration, doc.Configuration))
        {
            details.Add("конфигурация");
            t.Configuration = Decode(doc.Configuration, m, missing) ?? "";
        }
        if (cur.Icon != doc.Icon)
        {
            details.Add("иконка");
            t.Icon = doc.Icon is null ? null : Convert.FromBase64String(doc.Icon);
        }

        if (!TypesJson.Same(cur.Attributes, doc.Attributes))
        {
            var existing = (t.Attributes ?? new()).GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var curByName = cur.Attributes.GroupBy(a => a.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var result = new List<MAttribute>();
            foreach (var ad in doc.Attributes)
            {
                if (!existing.TryGetValue(ad.Name, out var orig) || !curByName.TryGetValue(ad.Name, out var ac))
                {
                    var a = new MAttribute { Name = ad.Name };
                    // Сравнение - с умолчаниями самого MAttribute, иначе совпавшие с умолчаниями документа поля не запишутся.
                    ApplyAttribute(a, TypesExport.Attribute(a, names), ad, m, missing, new List<string>());
                    result.Add(a);
                    details.Add($"+ атрибут {ad.Name} ({ad.Type})");
                    continue;
                }
                var copy = CloneAttribute(orig);
                var changed = new List<string>();
                ApplyAttribute(copy, ac, ad, m, missing, changed);
                if (ac.Type != ad.Type)
                    danger = true;
                if (changed.Count > 0)
                    details.Add($"~ атрибут {ad.Name}: {string.Join(", ", changed)}");
                result.Add(copy);
            }
            foreach (var removed in cur.Attributes.Where(a => doc.Attributes.All(d => d.Name != a.Name)))
            {
                details.Add($"− атрибут {removed.Name} (значения в объектах останутся скрытыми)");
                danger = true;
            }
            t.Attributes?.Clear();
            t.Attributes?.AddRange(result);
        }
    }

    private static void ApplyAttribute(MAttribute a, AttributeDoc cur, AttributeDoc doc, DMetadata m, List<string> missing, List<string> changed)
    {
        if (Diff(changed, "заголовок", cur.Title, doc.Title)) a.Title = doc.Title ?? "";
        if (Diff(changed, "тип данных", cur.Type.ToString(), doc.Type.ToString())) a.Type = doc.Type;
        if (Diff(changed, "обязательный", YesNo(cur.Obligatory), YesNo(doc.Obligatory))) a.Obligatory = doc.Obligatory;
        if (Diff(changed, "порядок", cur.DisplaySortOrder.ToString(), doc.DisplaySortOrder.ToString())) a.DisplaySortOrder = doc.DisplaySortOrder;
        if (Diff(changed, "высота", cur.DisplayHeight.ToString(), doc.DisplayHeight.ToString())) a.DisplayHeight = doc.DisplayHeight;
        if (Diff(changed, "в дереве", YesNo(cur.ShowInTree), YesNo(doc.ShowInTree))) a.ShowInTree = doc.ShowInTree;
        if (Diff(changed, "служебный", YesNo(cur.IsService), YesNo(doc.IsService))) a.IsService = doc.IsService;
        if (Diff(changed, "объединять с предыдущим", YesNo(cur.JoinWithPrevious), YesNo(doc.JoinWithPrevious))) a.JoinWithPrevious = doc.JoinWithPrevious;
        if (Diff(changed, "уникальность", cur.Uniqueness.ToString(), doc.Uniqueness.ToString())) a.UniquenessType = doc.Uniqueness;
        if (Diff(changed, "группа", cur.GroupTitle, doc.GroupTitle)) a.GroupTitle = doc.GroupTitle;
        if (Diff(changed, "не индексировать", YesNo(cur.IgnoreIndex), YesNo(doc.IgnoreIndex))) a.IgnoreIndex = doc.IgnoreIndex;
        if (!cur.EditableForPositions.SequenceEqual(doc.EditableForPositions, StringComparer.Ordinal))
        {
            changed.Add("редактируют должности");
            a.EditableForPositionsSource = doc.EditableForPositions.ToArray();
        }
        if (!SameText(cur.Configuration, doc.Configuration))
        {
            changed.Add("конфигурация");
            a.Configuration = Decode(doc.Configuration, m, missing);
        }
    }

    private static string? Decode(string? configuration, DMetadata m, List<string> missing) =>
        TypesSnapshot.DecodeReferences(configuration, name => StateId(m, name) is { } id && id != Guid.Empty ? id : null, title => MachineId(m, title), missing);

    // ---------------------------------------------------------------- проверки после плана

    private static void CheckUsage(DMetadata m, TypesPlan plan)
    {
        var liveStates = m.UserStates.Where(s => !s.IsDeleted).Select(s => s.Id).Append(Guid.Empty).ToHashSet();
        foreach (var x in m.StateMachines)
        {
            var used = (x.StateTransitions ?? new StateTransitionsCollection())
                .SelectMany(p => (p.Value ?? Array.Empty<MTransition>()).Select(t => t.StateTo).Append(p.Key))
                .Where(id => !liveStates.Contains(id)).Distinct().ToList();
            if (used.Count > 0 && (plan.MachinesBefore.ContainsKey(x.Id) || plan.CreatedMachines.Contains(x.Id) ||
                                   used.Any(id => plan.StatesBefore.ContainsKey(id))))
                plan.Errors.Add($"Группа состояний «{x.Title}» использует удалённые состояния: " +
                                string.Join(", ", used.Select(id => m.UserStates.FirstOrDefault(s => s.Id == id)?.Name ?? id.ToString())) + ".");
        }

        var machines = m.StateMachines.Select(x => x.Id).ToHashSet();
        foreach (var t in m.Types.Where(t => !t.IsDeleted && t.StateMachineId != Guid.Empty && !machines.Contains(t.StateMachineId)))
            if (plan.MachinesBefore.ContainsKey(t.StateMachineId))
                plan.Errors.Add($"Группа состояний удаляется, но её использует тип «{t.Name}», которого нет в репозитории.");
    }

    // ---------------------------------------------------------------- помощники

    private static Guid? StateId(DMetadata m, string name)
    {
        if (name == TypesSnapshot.NoState)
            return Guid.Empty;
        return (m.UserStates.FirstOrDefault(s => s.Name == name && !s.IsDeleted) ?? m.UserStates.FirstOrDefault(s => s.Name == name))?.Id;
    }

    private static Guid? MachineId(DMetadata m, string title) =>
        m.StateMachines.FirstOrDefault(x => x.Title == title)?.Id
        ?? (Guid.TryParse(title, out var g) && m.StateMachines.Any(x => x.Id == g) ? g : null);

    private static bool IdTaken(DMetadata m, Guid id) =>
        m.UserStates.Any(s => s.Id == id) || m.StateMachines.Any(x => x.Id == id);

    private static bool Diff(List<string> details, string what, string? a, string? b)
    {
        if (string.Equals(a ?? "", b ?? "", StringComparison.Ordinal))
            return false;
        details.Add(Short(a) is { } sa && Short(b) is { } sb ? $"{what}: {sa} / {sb}" : what);
        return true;
    }

    private static string? Short(string? v) =>
        v is null ? "-" : v.Length <= 40 && !v.Contains('\n') ? $"«{v}»" : null;

    private static bool SameText(string? a, string? b) =>
        (a ?? "").Replace("\r\n", "\n").TrimEnd() == (b ?? "").Replace("\r\n", "\n").TrimEnd();

    private static string YesNo(bool v) => v ? "да" : "нет";

    // ---------------------------------------------------------------- копии для отката

    public static MType CloneType(MType t)
    {
        var copy = new MType
        {
            Id = t.Id, Name = t.Name, Title = t.Title, Icon = t.Icon?.ToArray(), Sort = t.Sort, HasFiles = t.HasFiles,
            IsDeleted = t.IsDeleted, Kind = t.Kind, IsMountable = t.IsMountable, IsService = t.IsService, IsProject = t.IsProject,
            Configuration = t.Configuration, IsVisibleOnStorage = t.IsVisibleOnStorage, StateMachineId = t.StateMachineId,
        };
        copy.Children.AddRange(t.Children ?? new List<int>());
        copy.Attributes.AddRange((t.Attributes ?? new List<MAttribute>()).Select(CloneAttribute));
        return copy;
    }

    public static MAttribute CloneAttribute(MAttribute a) => new()
    {
        Name = a.Name, Title = a.Title, Obligatory = a.Obligatory, Type = a.Type, DisplayHeight = a.DisplayHeight,
        ShowInTree = a.ShowInTree, DisplaySortOrder = a.DisplaySortOrder, IsService = a.IsService, Configuration = a.Configuration,
        JoinWithPrevious = a.JoinWithPrevious, UniquenessType = a.UniquenessType, EditableForPositionsSource = a.EditableForPositionsSource?.ToArray(),
        GroupTitle = a.GroupTitle, IgnoreIndex = a.IgnoreIndex,
    };

    public static MUserState CloneState(MUserState s) => new()
    {
        Id = s.Id, Name = s.Name, Title = s.Title, Icon = s.Icon?.ToArray(), Color = s.Color, IsDeleted = s.IsDeleted, IsCompletionState = s.IsCompletionState,
    };

    public static MUserStateMachine CloneMachine(MUserStateMachine x)
    {
        var transitions = new StateTransitionsCollection();
        foreach (var p in x.StateTransitions ?? new StateTransitionsCollection())
            transitions[p.Key] = (p.Value ?? Array.Empty<MTransition>())
                .Select(t => new MTransition { StateTo = t.StateTo, DisplayName = t.DisplayName, AvailableForPositionsSource = t.AvailableForPositionsSource?.ToArray() })
                .ToArray();
        return new MUserStateMachine { Id = x.Id, Title = x.Title, StateTransitions = transitions };
    }

    /// <summary>
    /// Откат плана поверх текущих метаданных сервера: затронутые элементы возвращаются к прежнему виду, созданные
    /// состояния и группы убираются, созданные типы помечаются удалёнными (совсем удалить тип Pilot не даёт).
    /// Прочие изменения, сделанные за это время (автоматизации, другие типы), не трогаются.
    /// </summary>
    public static List<string> Revert(TypesPlan plan, DMetadata m)
    {
        var notes = new List<string>();
        foreach (var (id, before) in plan.TypesBefore)
        {
            var i = m.Types.FindIndex(t => t.Id == id);
            if (i >= 0) m.Types[i] = CloneType(before);
        }
        foreach (var id in plan.CreatedTypes)
            if (m.Types.FirstOrDefault(t => t.Id == id) is { } t)
            {
                t.IsDeleted = true;
                notes.Add($"тип «{t.Name}» помечен удалённым (совсем удалить тип Pilot не позволяет)");
            }
        foreach (var (id, before) in plan.StatesBefore)
        {
            var i = m.UserStates.FindIndex(s => s.Id == id);
            if (i >= 0) m.UserStates[i] = CloneState(before);
        }
        m.UserStates.RemoveAll(s => plan.CreatedStates.Contains(s.Id));
        foreach (var (id, before) in plan.MachinesBefore)
        {
            var i = m.StateMachines.FindIndex(x => x.Id == id);
            if (i >= 0) m.StateMachines[i] = CloneMachine(before);
            else m.StateMachines.Add(CloneMachine(before));
        }
        m.StateMachines.RemoveAll(x => plan.CreatedMachines.Contains(x.Id));
        return notes;
    }
}
