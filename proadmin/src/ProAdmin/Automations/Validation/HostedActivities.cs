using ProAdmin.Pilot.ServerModules;

namespace ProAdmin.Automations.Validation;

/// <summary>Модуль ServerHost, в котором объявлено действие.</summary>
public sealed record HostedModule(string Name, string Version);

/// <summary>
/// Действия серверных модулей, загруженных в ServerHost без перезапуска сервера (страница «Серверные модули»).
/// Штатные .servext сюда не попадают: хост знает только их сборки, а не имена действий.
/// </summary>
public sealed class HostedActivities
{
    public bool Available { get; private init; }
    public string? Error { get; private init; }

    /// <summary>Имя действия / модуль (только загруженные и включённые модули).</summary>
    public Dictionary<string, HostedModule> Activities { get; } = new(StringComparer.Ordinal);

    /// <summary>Установленные, но выключенные или не загрузившиеся модули: их действия сейчас не вызываются.</summary>
    public List<string> InactiveModules { get; } = new();

    public static HostedActivities None { get; } = new();

    public static HostedActivities Unavailable(string error) => new() { Error = error };

    public static HostedActivities From(HostState state)
    {
        var result = new HostedActivities { Available = true };
        foreach (var module in state.Modules)
        {
            if (!module.Enabled || !module.Loaded)
            {
                result.InactiveModules.Add(module.Name);
                continue;
            }
            foreach (var activity in module.Activities)
                result.Activities.TryAdd(activity.Name, new HostedModule(module.Name, module.Version));
        }
        return result;
    }

    /// <summary>Короткая пометка для строки «Сверено с …».</summary>
    public string Note => Available
        ? $"Серверные модули: действий {Activities.Count}."
        : Error is null ? "" : "Серверные модули не проверены.";
}
