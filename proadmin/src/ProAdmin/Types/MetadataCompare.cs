using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ascon.Pilot.DataClasses;
using ProAdmin.Compare;

namespace ProAdmin.Types;

/// <summary>Отличие одного свойства: значение на контуре A и на контуре B.</summary>
public sealed record PropertyDiff(string Property, string? ValueA, string? ValueB)
{
    /// <summary>Длинное значение (конфигурация) - показывается построчным diff.</summary>
    public DiffResult? Lines { get; init; }
}

/// <summary>Строка сравнения элемента метаданных: тип, атрибут, состояние или группа состояний.</summary>
public sealed class MetaRow
{
    public required string Key { get; init; }
    public string? TitleA { get; init; }
    public string? TitleB { get; init; }
    public CompareState State { get; set; }
    public List<PropertyDiff> Differences { get; } = new();

    /// <summary>Для типов - атрибуты; для групп состояний - переходы.</summary>
    public List<MetaRow> Children { get; } = new();

    /// <summary>Для групп состояний - типы, которые их используют (по обоим контурам).</summary>
    public List<string> UsedBy { get; } = new();

    public string Title => TitleA ?? TitleB ?? Key;
    public bool HasChildDiffs => Children.Any(c => c.State != CompareState.Same);

    /// <summary>Краткий список того, что отличается - для таблицы.</summary>
    public IEnumerable<string> Summary()
    {
        foreach (var d in Differences)
            yield return d.Property;
        var changed = Children.Count(c => c.State == CompareState.Differs);
        var onlyA = Children.Count(c => c.State == CompareState.OnlyA);
        var onlyB = Children.Count(c => c.State == CompareState.OnlyB);
        if (changed > 0) yield return $"изменено: {changed}";
        if (onlyA > 0) yield return $"только на A: {onlyA}";
        if (onlyB > 0) yield return $"только на B: {onlyB}";
    }
}

public sealed class MetadataCompareResult
{
    public List<MetaRow> Types { get; } = new();
    public List<MetaRow> States { get; } = new();
    public List<MetaRow> StateMachines { get; } = new();
    public string? ErrorA { get; set; }
    public string? ErrorB { get; set; }
}

/// <summary>
/// Сравнение конфигурации двух баз Pilot: типы и их атрибуты, состояния, группы состояний (машины переходов).
/// Сопоставление - по имени (у групп состояний - по заголовку, затем по Id), а не по внутренним Id,
/// которые на разных серверах могут отличаться. Ссылки на Id внутри конфигураций заменяются именами.
/// </summary>
public static partial class MetadataCompare
{
    public const string NoState = "<Нет>";

    public static MetadataCompareResult Compare(DMetadata a, DMetadata b)
    {
        var result = new MetadataCompareResult();
        var ctxA = new Context(a);
        var ctxB = new Context(b);

        CompareTypes(ctxA, ctxB, result.Types);
        CompareStates(ctxA, ctxB, result.States);
        CompareMachines(ctxA, ctxB, result.StateMachines);
        return result;
    }

    private static void CompareTypes(Context a, Context b, List<MetaRow> rows)
    {
        var byA = a.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
        var byB = b.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);

        foreach (var name in byA.Keys.Union(byB.Keys).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
        {
            byA.TryGetValue(name, out var ta);
            byB.TryGetValue(name, out var tb);
            var row = new MetaRow { Key = name, TitleA = ta?.Title, TitleB = tb?.Title };

            if (ta is not null && tb is not null)
            {
                Prop(row, "Заголовок", ta.Title, tb.Title);
                Prop(row, "Вид", ta.Kind.ToString(), tb.Kind.ToString());
                Prop(row, "Порядок", ta.Sort.ToString(), tb.Sort.ToString());
                Prop(row, "Есть файлы", YesNo(ta.HasFiles), YesNo(tb.HasFiles));
                Prop(row, "Монтируемый", YesNo(ta.IsMountable), YesNo(tb.IsMountable));
                Prop(row, "Служебный", YesNo(ta.IsService), YesNo(tb.IsService));
                Prop(row, "Проект", YesNo(ta.IsProject), YesNo(tb.IsProject));
                Prop(row, "Виден в хранилище", YesNo(ta.IsVisibleOnStorage), YesNo(tb.IsVisibleOnStorage));
                Prop(row, "Группа состояний", a.MachineName(ta.StateMachineId), b.MachineName(tb.StateMachineId));
                SetProp(row, "Дочерние типы", a.TypeNames(ta.Children), b.TypeNames(tb.Children));
                if (!SameBytes(ta.Icon, tb.Icon))
                    row.Differences.Add(new PropertyDiff("Иконка", ta.Icon is { Length: > 0 } ? "есть" : "нет", tb.Icon is { Length: > 0 } ? "есть" : "нет"));
                ConfigProp(row, "Конфигурация", a.Normalize(ta.Configuration), b.Normalize(tb.Configuration));
            }

            CompareAttributes(a, b, ta, tb, row);
            row.State = ta is null ? CompareState.OnlyB
                : tb is null ? CompareState.OnlyA
                : row.Differences.Count == 0 && !row.HasChildDiffs ? CompareState.Same : CompareState.Differs;
            rows.Add(row);
        }
    }

    private static void CompareAttributes(Context a, Context b, MType? ta, MType? tb, MetaRow typeRow)
    {
        var listA = ta?.Attributes ?? new List<MAttribute>();
        var listB = tb?.Attributes ?? new List<MAttribute>();
        var byA = listA.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byB = listB.GroupBy(x => x.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        // Порядок как на экране: по DisplaySortOrder контура A, новые с B - в конце.
        var order = listA.OrderBy(x => x.DisplaySortOrder).Select(x => x.Name)
            .Concat(listB.OrderBy(x => x.DisplaySortOrder).Select(x => x.Name))
            .Distinct(StringComparer.Ordinal);

        foreach (var name in order)
        {
            byA.TryGetValue(name, out var xa);
            byB.TryGetValue(name, out var xb);
            var row = new MetaRow { Key = name, TitleA = xa?.Title, TitleB = xb?.Title };
            if (xa is not null && xb is not null)
            {
                Prop(row, "Заголовок", xa.Title, xb.Title);
                Prop(row, "Тип данных", xa.Type.ToString(), xb.Type.ToString());
                Prop(row, "Обязательный", YesNo(xa.Obligatory), YesNo(xb.Obligatory));
                Prop(row, "Порядок", xa.DisplaySortOrder.ToString(), xb.DisplaySortOrder.ToString());
                Prop(row, "Высота", xa.DisplayHeight.ToString(), xb.DisplayHeight.ToString());
                Prop(row, "В дереве", YesNo(xa.ShowInTree), YesNo(xb.ShowInTree));
                Prop(row, "Служебный", YesNo(xa.IsService), YesNo(xb.IsService));
                Prop(row, "Объединять с предыдущим", YesNo(xa.JoinWithPrevious), YesNo(xb.JoinWithPrevious));
                Prop(row, "Уникальность", xa.UniquenessType.ToString(), xb.UniquenessType.ToString());
                Prop(row, "Группа", xa.GroupTitle, xb.GroupTitle);
                Prop(row, "Не индексировать", YesNo(xa.IgnoreIndex), YesNo(xb.IgnoreIndex));
                SetProp(row, "Редактируют должности", xa.EditableForPositionsSource ?? Array.Empty<string>(), xb.EditableForPositionsSource ?? Array.Empty<string>());
                ConfigProp(row, "Конфигурация", a.Normalize(xa.Configuration), b.Normalize(xb.Configuration));
            }
            row.State = xa is null ? CompareState.OnlyB
                : xb is null ? CompareState.OnlyA
                : row.Differences.Count == 0 ? CompareState.Same : CompareState.Differs;
            typeRow.Children.Add(row);
        }
    }

    private static void CompareStates(Context a, Context b, List<MetaRow> rows)
    {
        var byA = a.States.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var byB = b.States.GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var name in byA.Keys.Union(byB.Keys).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase))
        {
            byA.TryGetValue(name, out var sa);
            byB.TryGetValue(name, out var sb);
            var row = new MetaRow { Key = name, TitleA = sa?.Title, TitleB = sb?.Title };
            if (sa is not null && sb is not null)
            {
                Prop(row, "Заголовок", sa.Title, sb.Title);
                Prop(row, "Цвет", sa.Color.ToString(), sb.Color.ToString());
                Prop(row, "Завершающее", YesNo(sa.IsCompletionState), YesNo(sb.IsCompletionState));
                Prop(row, "Системное", YesNo(sa.IsSystemState), YesNo(sb.IsSystemState));
                if (!SameBytes(sa.Icon, sb.Icon))
                    row.Differences.Add(new PropertyDiff("Иконка", sa.Icon is { Length: > 0 } ? "есть" : "нет", sb.Icon is { Length: > 0 } ? "есть" : "нет"));
                if (sa.Id != sb.Id)
                    row.Differences.Add(new PropertyDiff("Id", sa.Id.ToString(), sb.Id.ToString()));
            }
            row.State = sa is null ? CompareState.OnlyB
                : sb is null ? CompareState.OnlyA
                : row.Differences.Count == 0 ? CompareState.Same : CompareState.Differs;
            rows.Add(row);
        }
    }

    private static void CompareMachines(Context a, Context b, List<MetaRow> rows)
    {
        // Сначала пары по Id, остальные - по заголовку.
        var pairs = new List<(MUserStateMachine? A, MUserStateMachine? B)>();
        var restB = b.Machines.ToList();
        foreach (var ma in a.Machines)
        {
            var mb = restB.FirstOrDefault(m => m.Id == ma.Id)
                     ?? restB.FirstOrDefault(m => string.Equals(m.Title, ma.Title, StringComparison.Ordinal));
            if (mb is not null)
                restB.Remove(mb);
            pairs.Add((ma, mb));
        }
        pairs.AddRange(restB.Select(mb => ((MUserStateMachine?)null, (MUserStateMachine?)mb)));

        foreach (var (ma, mb) in pairs.OrderBy(p => (p.A ?? p.B)!.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            var key = (ma ?? mb)!.Title ?? (ma ?? mb)!.Id.ToString();
            var row = new MetaRow { Key = key, TitleA = ma?.Title, TitleB = mb?.Title };
            if (ma is not null && mb is not null)
            {
                Prop(row, "Заголовок", ma.Title, mb.Title);
                if (ma.Id != mb.Id)
                    row.Differences.Add(new PropertyDiff("Id", ma.Id.ToString(), mb.Id.ToString()));
                SetProp(row, "Состояния", a.MachineStates(ma), b.MachineStates(mb));
            }

            var ta = ma is null ? new Dictionary<string, MTransitionInfo>() : a.Transitions(ma);
            var tb = mb is null ? new Dictionary<string, MTransitionInfo>() : b.Transitions(mb);
            foreach (var t in ta.Keys.Union(tb.Keys).OrderBy(k => k, StringComparer.CurrentCultureIgnoreCase))
            {
                ta.TryGetValue(t, out var xa);
                tb.TryGetValue(t, out var xb);
                var child = new MetaRow { Key = t, TitleA = xa?.DisplayName, TitleB = xb?.DisplayName };
                if (xa is not null && xb is not null)
                {
                    Prop(child, "Название кнопки", xa.DisplayName, xb.DisplayName);
                    SetProp(child, "Доступно должностям", xa.Positions, xb.Positions);
                }
                child.State = xa is null ? CompareState.OnlyB
                    : xb is null ? CompareState.OnlyA
                    : child.Differences.Count == 0 ? CompareState.Same : CompareState.Differs;
                row.Children.Add(child);
            }

            row.UsedBy.AddRange((ma is null ? Enumerable.Empty<string>() : a.TypesUsing(ma.Id))
                .Union(mb is null ? Enumerable.Empty<string>() : b.TypesUsing(mb.Id))
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase));

            row.State = ma is null ? CompareState.OnlyB
                : mb is null ? CompareState.OnlyA
                : row.Differences.Count == 0 && !row.HasChildDiffs ? CompareState.Same : CompareState.Differs;
            rows.Add(row);
        }
    }

    private static void Prop(MetaRow row, string name, string? a, string? b)
    {
        if (!string.Equals(a ?? "", b ?? "", StringComparison.Ordinal))
            row.Differences.Add(new PropertyDiff(name, a, b));
    }

    private static void SetProp(MetaRow row, string name, IEnumerable<string> a, IEnumerable<string> b)
    {
        var sa = a.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal);
        var sb = b.Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal);
        if (sa.SetEquals(sb))
            return;
        var onlyA = sa.Except(sb).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        var onlyB = sb.Except(sa).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
        row.Differences.Add(new PropertyDiff(name,
            onlyA.Count == 0 ? "-" : "− " + string.Join(", ", onlyA),
            onlyB.Count == 0 ? "-" : "+ " + string.Join(", ", onlyB)));
    }

    private static void ConfigProp(MetaRow row, string name, string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal))
            return;
        var diff = LineDiff.Compute(a, b);
        if (diff.Identical)
            return;
        row.Differences.Add(new PropertyDiff(name, a.Length == 0 ? "пусто" : $"{diff.OldLines} строк", b.Length == 0 ? "пусто" : $"{diff.NewLines} строк") { Lines = diff });
    }

    private static string YesNo(bool v) => v ? "да" : "нет";

    private static bool SameBytes(byte[]? a, byte[]? b) =>
        (a is null || a.Length == 0) && (b is null || b.Length == 0) || a is not null && b is not null && a.AsSpan().SequenceEqual(b);

    private sealed record MTransitionInfo(string DisplayName, string[] Positions);

    /// <summary>Индексы одной базы: Id / имя для типов, состояний и групп состояний.</summary>
    private sealed class Context
    {
        public List<MType> Types { get; }
        public List<MUserState> States { get; }
        public List<MUserStateMachine> Machines { get; }
        private readonly Dictionary<int, string> _typeById;
        private readonly Dictionary<Guid, string> _stateById;
        private readonly Dictionary<Guid, string> _machineById;

        public Context(DMetadata m)
        {
            Types = (m.Types ?? new()).Where(t => !t.IsDeleted).ToList();
            States = (m.UserStates ?? new()).Where(s => !s.IsDeleted).ToList();
            Machines = m.StateMachines ?? new();
            _typeById = (m.Types ?? new()).GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First().Name);
            _stateById = (m.UserStates ?? new()).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
            _machineById = Machines.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().Title);
        }

        public string? MachineName(Guid id) =>
            id == Guid.Empty ? null : _machineById.TryGetValue(id, out var t) ? t : id.ToString();

        public string StateName(Guid id) =>
            id == Guid.Empty ? NoState : _stateById.TryGetValue(id, out var n) ? n : id.ToString();

        public IEnumerable<string> TypeNames(IEnumerable<int>? ids) =>
            (ids ?? Enumerable.Empty<int>()).Select(id => _typeById.TryGetValue(id, out var n) ? n : "#" + id);

        public IEnumerable<string> TypesUsing(Guid machineId) =>
            Types.Where(t => t.StateMachineId == machineId).Select(t => t.Name);

        public IEnumerable<string> MachineStates(MUserStateMachine machine) =>
            machine.StateTransitions?.Keys.Select(StateName) ?? Enumerable.Empty<string>();

        /// <summary>Переходы «Из / В» / подпись и должности.</summary>
        public Dictionary<string, MTransitionInfo> Transitions(MUserStateMachine machine)
        {
            var result = new Dictionary<string, MTransitionInfo>(StringComparer.Ordinal);
            if (machine.StateTransitions is null)
                return result;
            foreach (var pair in machine.StateTransitions)
                foreach (var t in pair.Value ?? Array.Empty<MTransition>())
                    result.TryAdd($"{StateName(pair.Key)} / {StateName(t.StateTo)}",
                        new MTransitionInfo(t.DisplayName ?? "", t.AvailableForPositionsSource ?? Array.Empty<string>()));
            return result;
        }

        /// <summary>Конфигурация с заменой Id состояний/групп/типов на имена и форматированием XML.</summary>
        public string Normalize(string? configuration)
        {
            if (string.IsNullOrWhiteSpace(configuration))
                return "";
            var text = configuration.Trim();
            try
            {
                if (text.StartsWith('<'))
                    text = XDocument.Parse(text).ToString();
            }
            catch (System.Xml.XmlException)
            {
                // не XML - сравниваем как есть
            }
            return GuidPattern().Replace(text, m =>
            {
                var id = Guid.Parse(m.Value);
                if (_stateById.TryGetValue(id, out var s)) return $"{{состояние:{s}}}";
                if (_machineById.TryGetValue(id, out var g)) return $"{{группа:{g}}}";
                return m.Value;
            });
        }
    }

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex GuidPattern();
}
