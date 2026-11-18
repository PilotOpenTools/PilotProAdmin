namespace ReportMetrics.Collector;

/// <summary>Проверка и нормализация событий модулей - по тем же правилам, что и у отчётов (контур, время, длины строк).</summary>
public sealed class ModuleEventNormalizer
{
    private readonly EnvResolver _env;

    public ModuleEventNormalizer(EnvResolver env) => _env = env;

    public bool TryNormalize(ModuleEventDto? dto, DateTimeOffset receivedAt, out ModuleEventRow? row, out string? error,
        DateTimeOffset? clientSentAt = null)
    {
        row = null;
        error = null;

        if (dto == null)
        {
            error = "пустое событие (null)";
            return false;
        }

        if (dto.EventId == Guid.Empty)
        {
            error = "event_id пустой";
            return false;
        }

        var module = Clip(dto.Module, 200)?.Trim();
        if (string.IsNullOrWhiteSpace(module))
        {
            error = "module пустой";
            return false;
        }

        var kind = dto.Kind?.Trim().ToLowerInvariant() ?? ModuleEventKinds.Action;
        if (!ModuleEventKinds.All.Contains(kind))
        {
            error = $"неизвестный kind '{EventNormalizer.ForError(dto.Kind)}'";
            return false;
        }

        var status = dto.Status?.Trim().ToLowerInvariant() ?? ModuleEventStatus.Success;
        if (!ModuleEventStatus.All.Contains(status))
        {
            error = $"неизвестный status '{EventNormalizer.ForError(dto.Status)}'";
            return false;
        }

        var action = Clip(dto.Action, 300)?.Trim();
        if (kind == ModuleEventKinds.Action && string.IsNullOrEmpty(action))
        {
            error = "action пустой";
            return false;
        }

        var server = Clip(dto.Server, 300);
        row = new ModuleEventRow(
            dto.EventId,
            _env.Resolve(server),
            EventNormalizer.FixTime(dto.Ts, receivedAt, clientSentAt).ToUniversalTime(),
            kind,
            module,
            Clip(dto.ModuleTitle, 300)?.Trim() is { Length: > 0 } title ? title : module,
            ModuleVersions.Display(Clip(dto.ModuleVersion, 50)),
            kind == ModuleEventKinds.Load ? null : action,
            status,
            dto.DurationMs is >= 0 ? dto.DurationMs : null,
            dto.Items is >= 0 ? dto.Items : null,
            Clip(dto.ErrorType, 200),
            Clip(dto.ErrorMessage, 2000),
            Clip(dto.Login, 200),
            Clip(dto.Person, 300),
            server,
            Clip(dto.ClientType, 50),
            Clip(dto.ClientVersion, 50),
            Clip(dto.Machine, 100),
            ModuleVersions.Display(Clip(dto.LibVersion, 50)));
        return true;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return null;
        return value.Length <= max ? value : value[..max];
    }
}
