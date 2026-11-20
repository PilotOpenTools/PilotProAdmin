using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace ProAdmin.Pages.GitLab;

public class CallbackModel(GitLabAuthService auth, GitLabSignIn signIn, GitLabClientFactory gitLab,
    IDataProtectionProvider protection, IOptions<AppOptions> options, SettingsStore store) : PageModel
{
    public string? Error { get; private set; }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, string? error_description, CancellationToken ct)
    {
        var pending = signIn.TakePending(HttpContext, state);
        if (pending is null || !auth.OAuthEnabled)
        {
            Error = "Сеанс входа устарел. Начните вход заново.";
            return Page();
        }
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
        {
            Error = !string.IsNullOrEmpty(error) ? $"GitLab вернул ошибку: {error_description ?? error}" : "В ответе GitLab отсутствует код входа.";
            return Page();
        }
        // Вход из мастера первой настройки: только пока мастер открыт.
        if (pending.Setup && !SetupModel.IsOpen(options.Value, store))
            return RedirectToPage("/Login");

        try
        {
            var token = await auth.ExchangeCodeAsync(pending.BaseUrl, code, pending.RedirectUri, ct);
            if (pending.Setup)
            {
                // Доступ к группам проверит завершение мастера: групп в настройках ещё может не быть.
                var user = await gitLab.Create(token).GetCurrentUserAsync(ct);
                SetupModel.IssueGrant(HttpContext, protection, token, user.Username);
                return RedirectToPage("/Setup");
            }
            var session = await signIn.CompleteAsync(HttpContext, token, ct);
            TempData["Flash"] = session.LoginSummary();
            return Url.IsLocalUrl(pending.ReturnUrl) ? LocalRedirect(pending.ReturnUrl) : RedirectToPage("/Index");
        }
        catch (GitLabApiException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }
}
