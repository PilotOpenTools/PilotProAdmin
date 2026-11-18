using System.Text.RegularExpressions;
using Ascon.Pilot.DataClasses;

namespace ProAdmin.Automations.Validation;

/// <summary>Имена из конфигурации базы Pilot: типы, атрибуты, состояния и действия из C#-скриптов.</summary>
public sealed partial class MetadataIndex
{
    public HashSet<string> Types { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Attributes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> States { get; } = new(StringComparer.Ordinal);

    /// <summary>Действия, объявленные в C#-скриптах всех автоматизаций сервера: имя / автоматизация.</summary>
    public Dictionary<string, string> ScriptActivities { get; } = new(StringComparer.Ordinal);

    public static MetadataIndex From(DMetadata metadata)
    {
        var index = new MetadataIndex();
        foreach (var type in metadata.Types.Where(t => !t.IsDeleted))
        {
            index.Types.Add(type.Name);
            foreach (var attr in type.Attributes ?? new())
                index.Attributes.Add(attr.Name);
        }
        foreach (var state in metadata.UserStates.Where(s => !s.IsDeleted))
            index.States.Add(state.Name);
        foreach (var script in metadata.AutomationConfig ?? new())
            foreach (var name in DeclaredActivities(script.CSharpCode))
                index.ScriptActivities.TryAdd(name, script.Name);
        return index;
    }

    /// <summary>Имена действий из C#-кода: <c>public string Name => "X";</c> или <c>get { return "X"; }</c>.</summary>
    public static IEnumerable<string> DeclaredActivities(string? csharp)
    {
        if (string.IsNullOrWhiteSpace(csharp))
            yield break;
        foreach (Match m in ActivityName().Matches(csharp))
            yield return m.Groups[1].Value;
    }

    [GeneratedRegex(@"\bName\s*(?:=>|\{\s*get\s*(?:=>|\{\s*return))\s*""([^""]+)""")]
    private static partial Regex ActivityName();
}
