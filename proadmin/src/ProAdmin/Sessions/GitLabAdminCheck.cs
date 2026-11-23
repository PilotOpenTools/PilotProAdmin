using ProAdmin.GitLab;
using ProAdmin.Settings;

namespace ProAdmin.Sessions;

/// <summary>
/// Перед административной записью (общие настройки, миграция, серверы) права администратора GitLab
/// проверяются заново: роль в сессии получена при входе, и после её отзыва или отзыва токена
/// сессия не должна давать доступ. Не удалось подтвердить права - запись запрещена.
/// </summary>
public class GitLabAdminCheck(GitLabClientFactory factory, ILogger<GitLabAdminCheck> log)
{
    public virtual async Task<bool> ConfirmAsync(UserSession session, AdminSettings settings, CancellationToken ct)
    {
        if (!session.IsAdmin(settings))
            return false;
        // В режиме GitHub (вход в Pilot) администратор - вход на прод, он проверяется при входе в Pilot.
        if (!session.UsesServiceAccounts)
            return true;

        var known = session.GitLabUser!;
        try
        {
            var user = await factory.Create(session.GitLabToken!).GetCurrentUserAsync(ct);
            var stillAdmin = user.IsAdmin && user.Id == known.Id;
            if (!stillAdmin)
            {
                known.IsAdmin = false;
                log.LogWarning("Права администратора GitLab у {Login} больше не подтверждаются: запись запрещена", known.Username);
            }
            return stillAdmin;
        }
        catch (Exception ex) when (ex is GitLabApiException or HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("Права администратора GitLab у {Login} не удалось проверить: {Error}", known.Username, ex.Message);
            return false;
        }
    }
}
