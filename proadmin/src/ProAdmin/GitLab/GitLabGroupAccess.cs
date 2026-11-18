namespace ProAdmin.GitLab;

/// <summary>
/// Уровень доступа текущего пользователя к группе GitLab (с учётом наследования от родительских групп).
/// Изменять автоматизации/отчёты можно с уровнем Developer и выше; ниже - только просмотр.
/// </summary>
public sealed record GitLabGroupAccess(string GroupPath, int Level, bool IsAdmin, bool GroupFound, string? Error)
{
    public const int Developer = 30;

    /// <summary>
    /// Изменять нельзя: уровень ниже Developer либо его не удалось узнать (ошибка GitLab). Права проверяются
    /// по принципу «запрещено, пока не доказано обратное»: сбой GitLab не открывает изменения.
    /// </summary>
    public bool ReadOnly => Error is not null || (!IsAdmin && Level < Developer);

    /// <summary>Изменять нельзя: доступ не определён (GitLab не подключён, группа не задана) или недостаточен.</summary>
    public static bool CannotChange(GitLabGroupAccess? access) => access is null || access.ReadOnly;

    public string LevelTitle => IsAdmin && Level < 50
        ? "Администратор GitLab"
        : Level switch
        {
            >= 50 => "Owner",
            >= 40 => "Maintainer",
            >= 30 => "Developer",
            >= 20 => "Reporter",
            >= 15 => "Planner",
            >= 10 => "Guest",
            >= 5 => "Minimal access",
            _ => GroupFound ? "не участник группы" : "нет доступа",
        };

    /// <summary>«Developer - можно изменять» и т. п.</summary>
    public string Summary
    {
        get
        {
            if (Error is not null)
                return $"не удалось определить ({Error})";
            if (!GroupFound && !IsAdmin)
                return "группа не найдена или нет доступа";
            if (ReadOnly)
                return $"{LevelTitle} - только просмотр (для изменений нужен Developer или выше)";
            return Level >= 40 || IsAdmin
                ? $"{LevelTitle} - можно изменять"
                : $"{LevelTitle} - можно изменять; перенос в защищённую ветку main может требовать Maintainer";
        }
    }

    public static async Task<GitLabGroupAccess> LoadAsync(GitLabClient git, string groupPath, GitLabUser? user, CancellationToken ct)
    {
        try
        {
            user ??= await git.GetCurrentUserAsync(ct);
            var group = await git.FindGroupAsync(groupPath, ct);
            if (group is null)
                return new(groupPath, 0, user.IsAdmin, false, null);
            var member = await git.FindGroupMemberAsync(groupPath, user.Id, ct);
            return new(groupPath, member?.AccessLevel ?? 0, user.IsAdmin, true, null);
        }
        catch (GitLabApiException ex)
        {
            return new(groupPath, 0, false, false, ex.Message);
        }
    }
}
