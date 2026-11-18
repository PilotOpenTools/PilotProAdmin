using Ascon.Pilot.DataClasses;

namespace ProAdmin.Types;

/// <summary>Имена элементов базы по Id и обратно - для перевода ссылок между базой и репозиторием.</summary>
public sealed class MetadataNames
{
    public Dictionary<int, string> TypeById { get; } = new();
    public Dictionary<Guid, string> StateById { get; } = new();
    public Dictionary<Guid, string> MachineById { get; } = new();

    public MetadataNames(DMetadata m)
    {
        foreach (var t in m.Types ?? new())
            TypeById.TryAdd(t.Id, t.Name);
        // Неудалённые - первыми: при одинаковых именах ссылка ведёт на живое состояние.
        foreach (var s in (m.UserStates ?? new()).OrderBy(s => s.IsDeleted))
            StateById.TryAdd(s.Id, s.Name);
        foreach (var x in m.StateMachines ?? new())
            MachineById.TryAdd(x.Id, x.Title);
    }

    public string StateName(Guid id) =>
        id == Guid.Empty ? TypesSnapshot.NoState : StateById.TryGetValue(id, out var n) ? n : id.ToString();

    public string? MachineTitle(Guid id) =>
        id == Guid.Empty ? null : MachineById.TryGetValue(id, out var t) ? t : id.ToString();

    public string TypeName(int id) => TypeById.TryGetValue(id, out var n) ? n : "#" + id;
}

/// <summary>Конфигурация базы Pilot / вид репозитория (файлы types/, states/, state-machines/).</summary>
public static class TypesExport
{
    /// <summary>Снимок базы: неудалённые типы и состояния (системные - с пометкой), все группы состояний.</summary>
    public static TypesSnapshot FromMetadata(DMetadata m, string? version = null)
    {
        var names = new MetadataNames(m);
        var snapshot = new TypesSnapshot();
        if (version is not null)
            snapshot.Config.Version = version;

        foreach (var t in (m.Types ?? new()).Where(t => !t.IsDeleted))
            snapshot.Types.TryAdd(t.Name, Type(t, names));
        foreach (var s in (m.UserStates ?? new()).Where(s => !s.IsDeleted))
            snapshot.States.TryAdd(s.Name, State(s));
        foreach (var x in (m.StateMachines ?? new()).OrderBy(x => x.Title, StringComparer.Ordinal))
            snapshot.Machines.Add(Machine(x, names));
        return snapshot;
    }

    public static TypeDoc Type(MType t, MetadataNames names) => new()
    {
        Name = t.Name,
        Title = Text(t.Title),
        Kind = t.Kind,
        Sort = t.Sort,
        HasFiles = t.HasFiles,
        IsMountable = t.IsMountable,
        IsService = t.IsService,
        IsProject = t.IsProject,
        IsVisibleOnStorage = t.IsVisibleOnStorage,
        IsDeleted = t.IsDeleted,
        StateMachine = names.MachineTitle(t.StateMachineId),
        Children = (t.Children ?? new()).Select(names.TypeName).ToList(),
        Configuration = TypesSnapshot.EncodeReferences(t.Configuration, names.StateById, names.MachineById),
        Icon = Icon(t.Icon),
        Attributes = (t.Attributes ?? new())
            .OrderBy(a => a.DisplaySortOrder).ThenBy(a => a.Name, StringComparer.Ordinal)
            .Select(a => Attribute(a, names)).ToList(),
    };

    public static AttributeDoc Attribute(MAttribute a, MetadataNames names) => new()
    {
        Name = a.Name,
        Title = Text(a.Title),
        Type = a.Type,
        Obligatory = a.Obligatory,
        DisplaySortOrder = a.DisplaySortOrder,
        DisplayHeight = a.DisplayHeight,
        ShowInTree = a.ShowInTree,
        IsService = a.IsService,
        JoinWithPrevious = a.JoinWithPrevious,
        Uniqueness = a.UniquenessType,
        GroupTitle = Text(a.GroupTitle),
        IgnoreIndex = a.IgnoreIndex,
        EditableForPositions = (a.EditableForPositionsSource ?? Array.Empty<string>()).ToList(),
        Configuration = TypesSnapshot.EncodeReferences(a.Configuration, names.StateById, names.MachineById),
    };

    public static StateDoc State(MUserState s) => new()
    {
        Id = s.Id,
        Name = s.Name,
        Title = Text(s.Title),
        Color = s.Color,
        IsCompletionState = s.IsCompletionState,
        IsDeleted = s.IsDeleted,
        IsSystem = s.IsSystemState,
        Icon = Icon(s.Icon),
    };

    public static MachineDoc Machine(MUserStateMachine x, MetadataNames names) => new()
    {
        Id = x.Id,
        Title = x.Title ?? "",
        States = (x.StateTransitions?.Select(p => new MachineStateDoc
        {
            State = names.StateName(p.Key),
            Transitions = (p.Value ?? Array.Empty<MTransition>()).Select(t => new TransitionDoc
            {
                To = names.StateName(t.StateTo),
                DisplayName = Text(t.DisplayName),
                Positions = (t.AvailableForPositionsSource ?? Array.Empty<string>()).ToList(),
            }).ToList(),
        }) ?? Enumerable.Empty<MachineStateDoc>()).ToList(),
    };

    private static string? Text(string? value) => string.IsNullOrEmpty(value) ? null : value.Replace("\r\n", "\n");

    private static string? Icon(byte[]? icon) => icon is { Length: > 0 } ? Convert.ToBase64String(icon) : null;
}
