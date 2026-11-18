using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProAdmin.Pilot.ServerModules;

/// <summary>
/// Кейс локальной отладки (debug.json для debug): сервер контура, логин разработчика и - из автоматизаций
/// контура - событие, sourceTypes и params, с которыми сервер вызывает активность. Пароль в файл не пишется.
/// Формат - server-extensions/Debug/DebugCase.cs.
/// </summary>
public static class DebugCaseBuilder
{
    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Build(string pilotUrl, string login, int licenseType, string activity, string? moduleName,
        Guid? sourceId, IReadOnlyList<AutomationSnapshot> automations, bool apply = false, string? contour = null)
    {
        var uses = FindUses(automations, activity);
        var first = uses.FirstOrDefault();

        var json = new JsonObject
        {
            ["server"] = pilotUrl,
            ["login"] = login,
            ["licenseType"] = licenseType,
            // Обычно перекрывается из launchSettings проекта модуля: --module "$(TargetPath)".
            ["module"] = moduleName is null ? null : $"bin/Debug/{moduleName}.servext.dll",
            ["activity"] = activity,
            ["sourceId"] = sourceId?.ToString(),
            ["when"] = first?.When,
            ["sourceTypes"] = new JsonArray((first?.SourceTypes ?? new List<string>()).Select(s => (JsonNode?)JsonValue.Create(s)).ToArray()),
            ["params"] = first?.Params?.DeepClone() ?? new JsonObject(),
            ["automations"] = new JsonArray(uses.Select(u => (JsonNode?)JsonValue.Create(u.Describe())).ToArray()),
            // true - debug записывает изменения (с подтверждением в консоли), false - только показывает их.
            ["apply"] = apply,
        };
        // Контур (test/stage/prod): для prod debug требует ввести слово prod перед записью. Поле необязательное.
        if (!string.IsNullOrWhiteSpace(contour))
            json["contour"] = contour;
        if (uses.Count == 0)
            json["_comment_"] = $"Активность {activity} не найдена в автоматизациях контура - params и sourceTypes заполните сами.";
        else if (uses.Count > 1)
            json["_comment_"] = "Активность вызывается из нескольких мест - params взяты из первого (список в automations).";
        return json.ToJsonString(Indented);
    }

    public sealed record ActivityUse(string Automation, string? When, List<string> SourceTypes, JsonObject? Params, bool Paused)
    {
        public string Describe() => $"{Automation} · {When ?? "?"}{(Paused ? " · на паузе" : "")}";
    }

    /// <summary>Все места, где сценарии контура вызывают активность: when / then[].activity.</summary>
    public static List<ActivityUse> FindUses(IReadOnlyList<AutomationSnapshot> automations, string activity)
    {
        var result = new List<ActivityUse>();
        foreach (var automation in automations)
        {
            JsonArray? triggers;
            try { triggers = JsonNode.Parse(automation.Triggers) as JsonArray; }
            catch (JsonException) { continue; }
            if (triggers is null)
                continue;

            foreach (var trigger in triggers.OfType<JsonObject>())
            {
                var when = trigger["when"]?.GetValue<string>();
                var sourceTypes = (trigger["params"]?["sourceTypes"] as JsonArray)?
                    .Select(n => n?.ToString()).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList() ?? new List<string>();
                foreach (var step in (trigger["then"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (step["activity"]?.ToString() != activity)
                        continue;
                    result.Add(new ActivityUse(automation.Name, when, sourceTypes, step["params"] as JsonObject, automation.IsPaused));
                }
            }
        }
        return result;
    }
}
