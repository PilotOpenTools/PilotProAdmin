using System.Text.Json;
using ProAdmin.Settings;

namespace ProAdmin.Automations;

public sealed record CheckMessage(string Level, string Text, string? Details = null)
{
    public static CheckMessage Ok(string text) => new("ok", text);
    public static CheckMessage Info(string text) => new("info", text);
    public static CheckMessage Warn(string text) => new("warn", text);
    public static CheckMessage Error(string text, string? details = null) => new("error", text, details);
}

/// <summary>Проверки, общие для кнопки «Проверить» и для операции отправки.</summary>
public static class AutomationChecks
{
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static CheckMessage CheckJson(string triggers)
    {
        if (string.IsNullOrWhiteSpace(triggers))
            return CheckMessage.Error("JSON автоматизации пуст.");

        try
        {
            using var _ = JsonDocument.Parse(triggers, JsonOptions);
            return CheckMessage.Ok("JSON корректен.");
        }
        catch (JsonException ex)
        {
            var where = ex.LineNumber is { } line ? $" (строка {line + 1}, позиция {ex.BytePositionInLine + 1})" : "";
            return CheckMessage.Error($"JSON некорректен{where}.", ex.Message);
        }
    }

    /// <summary>
    /// Версия должна быть указана и - если JSON в ветке отличается - быть больше версии в ветке.
    /// </summary>
    public static CheckMessage CheckVersion(string triggers, string? branchText, string branch, bool csharpChanged = false)
    {
        if (branchText is not null && AutomationFiles.SameContent(branchText, triggers))
            return csharpChanged
                ? CheckMessage.Error($"C# изменён относительно ветки {branch}. Повысьте версию в поле \"_comment_\" первого триггера.")
                : CheckMessage.Ok($"Содержимое совпадает с веткой {branch}.");

        var version = FirstTriggerVersion(triggers);
        if (version is null)
            return CheckMessage.Error("Не указана версия. Добавьте в первый триггер поле \"_comment_\": \"Version: X.Y.Z\" или нажмите «+patch».");

        if (branchText is null)
            return CheckMessage.Ok($"Версия {version} (в ветке {branch} автоматизации ещё нет).");

        var old = FirstTriggerVersion(branchText);
        if (old is null)
            return CheckMessage.Ok($"Версия {version} (в ветке {branch} версия не была указана).");

        return version.Value > old.Value
            ? CheckMessage.Ok($"Версия повышена: {old} / {version}.")
            : CheckMessage.Error($"Версия не повышена: в ветке {branch} - {old}, в редакторе - {version}.");
    }

    public static AutomationVersion? FirstTriggerVersion(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 &&
                root[0].ValueKind == JsonValueKind.Object && root[0].TryGetProperty("_comment_", out var comment) &&
                comment.ValueKind == JsonValueKind.String)
                return AutomationVersion.Find(comment.GetString());
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Формат сообщения из инструкции: dev - «Имя: техническое описание»,
    /// main - «Имя: TASK-123, описание бизнес-изменения».
    /// </summary>
    public static string BuildCommitMessage(EnvironmentSettings env, string name, string? description, string? task, out string? error)
    {
        error = null;
        // Несколько строк (пунктов журнала) - в одну строку заголовка коммита/MR.
        description = string.Join("; ", Changelog.SplitNotes(description));
        if (description.Length > 200)
            description = description[..200].TrimEnd() + "…";
        task = task?.Trim() ?? "";

        if (description.Length == 0)
            error = "Опишите изменение - оно попадёт в сообщение коммита.";
        else if (env.RequireTaskReference && task.Length == 0)
            error = $"Для ветки {env.Branch} нужна ссылка на задачу.";

        return task.Length > 0 ? $"{name}: {task}, {description}" : $"{name}: {description}";
    }
}
