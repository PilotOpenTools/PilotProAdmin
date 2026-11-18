using System.Text.RegularExpressions;
using ProAdmin.Automations.Validation;

namespace ProAdmin.Automations;

public enum UsageKind
{
    Type,
    Attribute,
    State,
    Activity,

    /// <summary>Имя встречается только в тексте C#-вставки, что это такое - неизвестно.</summary>
    Mention,
}

/// <summary>Упоминание имени в сценарии: Place - где именно («AttributeChanged · sourceTypes»).</summary>
public sealed record UsageRef(UsageKind Kind, string Name, string Place);

/// <summary>Разбирает сценарий автоматизации на типы, атрибуты, состояния и действия. Значения параметров классифицируются по справочнику <see cref="AutomationSchema"/>.</summary>
public static class AutomationUsage
{
    public const string CSharpDeclaration = "C#-вставка: объявлено действие";
    public const string CSharpMention = "C#-вставка: встречается в тексте";

    /// <summary>Параметры действий, которых нет в справочнике (C# и серверные модули), распознаются по имени параметра.</summary>
    private static readonly Dictionary<string, ParamSpec> SpecsByName = AutomationSchema.Triggers.Values.SelectMany(t => t.Params)
        .Concat(AutomationSchema.CommonActivityParams)
        .Concat(AutomationSchema.Activities.Values.SelectMany(a => a.Params))
        .Where(p => p.Check != NameCheck.None)
        .GroupBy(p => p.Name, StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>null - JSON не разбирается.</summary>
    public static List<UsageRef>? Extract(string triggersJson)
    {
        var root = JsonTree.Parse(triggersJson, out _, out _);
        if (root is null)
            return null;

        var refs = new List<UsageRef>();
        foreach (var trigger in root.IsArray ? root.Items : new List<JNode> { root })
        {
            if (!trigger.IsObject)
                continue;
            var when = trigger["when"]?.Text ?? "триггер";
            CollectParams(trigger["params"], AutomationSchema.Triggers.GetValueOrDefault(when)?.Params, when, refs);

            foreach (var action in trigger["then"]?.Items ?? new List<JNode>())
            {
                if (!action.IsObject || action["activity"]?.Text is not { Length: > 0 } activity)
                    continue;
                refs.Add(new UsageRef(UsageKind.Activity, activity, $"{when} · then"));
                CollectParams(action["params"], AutomationSchema.Activities.GetValueOrDefault(activity)?.Params, activity, refs);
            }
        }
        return refs;
    }

    private static void CollectParams(JNode? parameters, ParamSpec[]? specs, string owner, List<UsageRef> refs)
    {
        if (parameters is not { IsObject: true })
            return;
        foreach (var p in parameters.Properties)
        {
            var spec = specs?.FirstOrDefault(s => s.Name == p.Name) ?? SpecsByName.GetValueOrDefault(p.Name);
            if (spec is not null)
                Collect(spec.Check, p.Value, $"{owner} · {p.Name}", refs);
        }
    }

    private static void Collect(NameCheck check, JNode value, string place, List<UsageRef> refs)
    {
        void Add(UsageKind kind, string name)
        {
            if (name.Length > 0)
                refs.Add(new UsageRef(kind, name, place));
        }

        switch (check)
        {
            case NameCheck.Types:
                foreach (var (name, _) in value.Strings()) Add(UsageKind.Type, name);
                break;
            case NameCheck.Attributes:
                foreach (var (name, _) in value.Strings()) Add(UsageKind.Attribute, name);
                break;
            case NameCheck.States:
                foreach (var (name, _) in value.Strings()) Add(UsageKind.State, name);
                break;
            case NameCheck.Expressions:
                foreach (var (expression, _) in value.Strings())
                    foreach (var attribute in AutomationSchema.ExpressionAttributes(expression))
                        Add(UsageKind.Attribute, attribute);
                break;
            case NameCheck.Template:
                foreach (Match m in AutomationLinter.TemplateExpr().Matches(value.Text ?? ""))
                    foreach (var attribute in AutomationSchema.ExpressionAttributes(m.Groups[1].Value))
                        Add(UsageKind.Attribute, attribute);
                break;
            case NameCheck.StateToAttributes when value.IsObject:
                foreach (var p in value.Properties)
                {
                    Add(UsageKind.State, p.Name);
                    foreach (var (attribute, _) in p.Value.Strings()) Add(UsageKind.Attribute, attribute);
                }
                break;
        }
    }
}

/// <summary>Одна автоматизация в результате поиска: где находится имя и в каких источниках (серверы, ветки).</summary>
public sealed class UsageAutomation
{
    public required string Name { get; init; }
    public SortedSet<string> Places { get; } = new(StringComparer.Ordinal);
    public List<string> Sources { get; } = new();

    internal void Add(string place, string source)
    {
        Places.Add(place);
        if (!Sources.Contains(source))
            Sources.Add(source);
    }
}

public sealed class UsageEntry
{
    public required UsageKind Kind { get; init; }
    public required string Name { get; init; }
    public List<UsageAutomation> Automations { get; } = new();

    internal UsageAutomation For(string automation)
    {
        var found = Automations.FirstOrDefault(a => a.Name == automation);
        if (found is null)
            Automations.Add(found = new UsageAutomation { Name = automation });
        return found;
    }
}

/// <summary>Сводка «где используется» по всем автоматизациям каталога.</summary>
public sealed partial class AutomationUsageIndex
{
    private readonly List<CSharpWords> _csharp = new();

    public List<UsageEntry> Entries { get; } = new();

    /// <summary>Сценарии, JSON которых не разбирается: в сводку они не попали, их можно найти только поиском по тексту.</summary>
    public List<(string Automation, string Source)> Unparsed { get; } = new();

    public static AutomationUsageIndex Build(IEnumerable<CatalogRow> rows)
    {
        var index = new AutomationUsageIndex();
        var entries = new Dictionary<(UsageKind, string), UsageEntry>();

        void Add(UsageRef reference, string automation, string source)
        {
            var key = (reference.Kind, reference.Name);
            if (!entries.TryGetValue(key, out var entry))
                entries[key] = entry = new UsageEntry { Kind = reference.Kind, Name = reference.Name };
            entry.For(automation).Add(reference.Place, source);
        }

        foreach (var row in rows)
        {
            foreach (var text in row.Texts())
            {
                var refs = AutomationUsage.Extract(text.Triggers);
                if (refs is null)
                    index.Unparsed.Add((row.Name, text.Source));
                foreach (var reference in refs ?? new())
                    Add(reference, row.Name, text.Source);

                if (string.IsNullOrWhiteSpace(text.CSharp))
                    continue;
                foreach (var activity in MetadataIndex.DeclaredActivities(text.CSharp))
                    Add(new UsageRef(UsageKind.Activity, activity, AutomationUsage.CSharpDeclaration), row.Name, text.Source);
                index._csharp.Add(new CSharpWords(row.Name, text.Source,
                    Word().Matches(text.CSharp).Select(m => m.Value).ToHashSet(StringComparer.Ordinal)));
            }
        }

        foreach (var entry in entries.Values)
            index.AddMentions(entry);
        index.Entries.AddRange(entries.Values
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        foreach (var entry in index.Entries)
            entry.Automations.Sort((x, y) => string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
        return index;
    }

    /// <summary>
    /// Имена, содержащие запрос, без учёта регистра. Если точного имени в сценариях нет, оно ищется в C#-вставках:
    /// там имя может быть строкой в коде, которую разбор JSON не видит.
    /// </summary>
    public List<UsageEntry> Find(UsageKind? kind, string? query)
    {
        var q = query?.Trim() ?? "";
        var found = Entries
            .Where(e => kind is null || e.Kind == kind)
            .Where(e => q.Length == 0 || e.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (q.Length > 0 && !found.Any(e => e.Name == q))
        {
            var mention = new UsageEntry { Kind = kind ?? UsageKind.Mention, Name = q };
            AddMentions(mention);
            if (mention.Automations.Count > 0)
                found.Insert(0, mention);
        }
        return found;
    }

    private void AddMentions(UsageEntry entry)
    {
        foreach (var words in _csharp.Where(c => c.Words.Contains(entry.Name)))
        {
            var automation = entry.For(words.Automation);
            // Объявленное здесь действие уже показано, его имя в тексте кода ничего не добавляет.
            if (!automation.Places.Contains(AutomationUsage.CSharpDeclaration))
                automation.Add(AutomationUsage.CSharpMention, words.Source);
        }
    }

    private sealed record CSharpWords(string Automation, string Source, HashSet<string> Words);

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Word();
}
