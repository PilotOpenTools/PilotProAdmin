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

        v.Errors.AddRange(ClientRecursionLoops(s.Types));

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

    /// <summary>Корень, от которого клиент Pilot собирает типы для поиска по замечаниям.</summary>
    public const string RemarksRoot = "doc_remarksFolder";

    [GeneratedRegex("""ExcludeFromSearch\s*=\s*["']([^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludeFromSearchPattern();

    /// <summary>
    /// Клиент Pilot (AnnotationSearchExpressionContext.GetSearchTypesRecursively) при открытии документа обходит дочерние
    /// типы от <see cref="RemarksRoot"/> и запоминает пройденным только пользовательский тип (не служебный, вид User,
    /// без ExcludeFromSearch="annotations"). Если из корня достижим цикл только из незапоминаемых типов
    /// (например, системный Task_Folder, дочерний сам себе), обход не заканчивается: клиент падает со StackOverflow
    /// у всех пользователей. Возвращает ошибку на каждую связь «родитель → тип из такого цикла».
    /// </summary>
    internal static IEnumerable<string> ClientRecursionLoops(IReadOnlyDictionary<string, TypeDoc> types)
    {
        if (!types.ContainsKey(RemarksRoot))
            yield break;

        IEnumerable<string> ChildrenOf(string name) =>
            types.TryGetValue(name, out var t) ? t.Children.Where(types.ContainsKey).Distinct(StringComparer.Ordinal) : Enumerable.Empty<string>();

        var reachable = new HashSet<string>(StringComparer.Ordinal) { RemarksRoot };
        var queue = new Queue<string>(reachable);
        while (queue.Count > 0)
            foreach (var c in ChildrenOf(queue.Dequeue()))
                if (reachable.Add(c))
                    queue.Enqueue(c);

        var unmarked = reachable.Where(n => !RememberedByRemarksSearch(types[n])).ToHashSet(StringComparer.Ordinal);

        // Тип зацикливает обход, если из него по незапоминаемым типам можно вернуться в него же.
        var looping = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var start in unmarked)
            if (FindLoop(start, unmarked, ChildrenOf) is { } loop)
                looping[start] = loop;
        if (looping.Count == 0)
            yield break;

        if (looping.TryGetValue(RemarksRoot, out var rootLoop))
            yield return $"Тип «{RemarksRoot}»: {LoopText(types, rootLoop)}";

        foreach (var parent in reachable.Order(StringComparer.Ordinal))
        {
            if (looping.ContainsKey(parent))
                continue;
            foreach (var child in ChildrenOf(parent).Where(looping.ContainsKey).Order(StringComparer.Ordinal))
                yield return $"Тип «{parent}»: дочерний тип «{child}» нельзя добавлять - {LoopText(types, looping[child])} " +
                             $"Уберите «{child}» из дочерних «{parent}».";
        }
    }

    private static bool RememberedByRemarksSearch(TypeDoc t)
    {
        if (t.IsService || t.Kind != Ascon.Pilot.DataClasses.TypeKind.User)
            return false;
        var excluded = ExcludeFromSearchPattern().Match(t.Configuration ?? "");
        return !(excluded.Success && excluded.Groups[1].Value.Contains("annotations", StringComparison.OrdinalIgnoreCase));
    }

    private static List<string>? FindLoop(string start, HashSet<string> allowed, Func<string, IEnumerable<string>> children)
    {
        var prev = new Dictionary<string, string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(start);
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            foreach (var c in children(cur).Where(allowed.Contains))
            {
                if (c == start)
                {
                    var path = new List<string> { start };
                    for (var x = cur; x != start; x = prev[x])
                        path.Insert(1, x);
                    path.Add(start);
                    return path;
                }
                if (seen.Add(c))
                {
                    prev[c] = cur;
                    queue.Enqueue(c);
                }
            }
        }
        return null;
    }

    private static string LoopText(IReadOnlyDictionary<string, TypeDoc> types, List<string> loop)
    {
        var why = types[loop[0]] switch
        {
            { IsService: true } => "служебный",
            { Kind: not Ascon.Pilot.DataClasses.TypeKind.User } t => $"вида {t.Kind}",
            _ => "исключён из поиска по замечаниям",
        };
        return $"клиент Pilot при открытии документа уходит в бесконечную рекурсию и падает (StackOverflow): " +
               $"тип «{loop[0]}» {why} и через дочерние ссылается сам на себя ({string.Join(" → ", loop)}).";
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
