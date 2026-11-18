using System.Text.RegularExpressions;

namespace ProAdmin.Types;

public sealed class TypesValidation
{
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// Проверка репозитория типов без сервера: ссылки между файлами, дубли, имена.
/// Те же правила проверяет ci/validate.py в pipeline репозитория (<see cref="TypesCiTemplate"/>).
/// </summary>
public static partial class TypesValidator
{
    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_\-\.]*$")]
    private static partial Regex NamePattern();

    public static TypesValidation Validate(TypesSnapshot s)
    {
        var v = new TypesValidation();
        v.Errors.AddRange(s.ParseErrors);
        v.Errors.AddRange(DuplicateTypeOrders(s.Types.Values.Select(t => (t.Sort, t.Name))));

        var liveStates = s.States.Values.Where(x => !x.IsDeleted).Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
        var liveMachines = s.Machines.Where(x => !x.IsDeleted).Select(x => x.Title).ToHashSet(StringComparer.Ordinal);

        foreach (var t in s.Types.Values)
        {
            var where = $"Тип «{t.Name}»";
            if (!NamePattern().IsMatch(t.Name))
                v.Warnings.Add($"{where}: имя лучше составлять из латиницы, цифр и «_» (начиная с буквы).");
            if (t.IsDeleted)
                continue;
            if (string.IsNullOrWhiteSpace(t.Title))
                v.Warnings.Add($"{where}: не задан заголовок.");
            if (t.StateMachine is { } machine && !liveMachines.Contains(machine))
                v.Errors.Add($"{where}: группа состояний «{machine}» не описана в state-machines/ (или удалена).");
            foreach (var child in t.Children)
            {
                if (!s.Types.TryGetValue(child, out var c))
                    v.Errors.Add($"{where}: дочерний тип «{child}» не описан в types/.");
                else if (c.IsDeleted)
                    v.Warnings.Add($"{where}: дочерний тип «{child}» удалён.");
            }
            CheckReferences(v, where, t.Configuration, liveStates, liveMachines);

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in t.Attributes)
            {
                var aw = $"{where}, атрибут «{a.Name}»";
                if (string.IsNullOrWhiteSpace(a.Name))
                    v.Errors.Add($"{where}: атрибут без имени.");
                else if (!seen.Add(a.Name))
                    v.Errors.Add($"{where}: атрибут «{a.Name}» описан дважды.");
                else if (!NamePattern().IsMatch(a.Name))
                    v.Warnings.Add($"{aw}: имя лучше составлять из латиницы, цифр и «_».");
                if (string.IsNullOrWhiteSpace(a.Title))
                    v.Warnings.Add($"{aw}: не задан заголовок.");
                CheckReferences(v, aw, a.Configuration, liveStates, liveMachines);
            }
        }

        foreach (var x in s.States.Values.Where(x => !x.IsDeleted && !x.IsSystem))
            if (string.IsNullOrWhiteSpace(x.Title))
                v.Warnings.Add($"Состояние «{x.Name}»: не задан заголовок.");

        foreach (var m in s.Machines)
        {
            var where = $"Группа состояний «{m.Title}»";
            if (m.IsDeleted)
            {
                var users = s.Types.Values.Where(t => !t.IsDeleted && t.StateMachine == m.Title).Select(t => t.Name).ToList();
                if (users.Count > 0)
                    v.Errors.Add($"{where} удаляется, но её используют типы: {string.Join(", ", users)}.");
                continue;
            }
            var from = new HashSet<string>(StringComparer.Ordinal);
            foreach (var st in m.States)
            {
                if (!from.Add(st.State))
                    v.Errors.Add($"{where}: состояние «{st.State}» указано дважды.");
                CheckState(v, where, st.State, liveStates);
                foreach (var tr in st.Transitions)
                    CheckState(v, where, tr.To, liveStates);
            }
        }
        return v;
    }

    public static int NextTypeOrder(IEnumerable<int> orders)
    {
        var used = orders.ToHashSet();
        var max = used.DefaultIfEmpty(-1).Max();
        return max < int.MaxValue ? Math.Max(0, max + 1) : Enumerable.Range(0, used.Count).First(n => !used.Contains(n));
    }

    internal static IEnumerable<string> DuplicateTypeOrders(IEnumerable<(int Sort, string Name)> types) =>
        types.GroupBy(t => t.Sort).Where(g => g.Count() > 1)
            .Select(g => $"Порядок {g.Key} занят несколькими типами: {string.Join(", ", g.Select(t => $"«{t.Name}»"))}.");

    private static void CheckState(TypesValidation v, string where, string state, HashSet<string> live)
    {
        if (state != TypesSnapshot.NoState && !live.Contains(state))
            v.Errors.Add($"{where}: состояние «{state}» не описано в states/ (или удалено).");
    }

    private static void CheckReferences(TypesValidation v, string where, string? configuration, HashSet<string> states, HashSet<string> machines)
    {
        foreach (var (kind, name) in TypesSnapshot.References(configuration))
        {
            if (kind == "state" && !states.Contains(name))
                v.Errors.Add($"{where}: конфигурация ссылается на состояние «{name}», которого нет в states/.");
            if (kind == "machine" && !machines.Contains(name))
                v.Errors.Add($"{where}: конфигурация ссылается на группу состояний «{name}», которой нет в state-machines/.");
        }
    }
}
