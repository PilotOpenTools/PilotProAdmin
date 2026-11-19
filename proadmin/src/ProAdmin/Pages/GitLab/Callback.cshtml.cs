using ProAdmin.GitLab;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages.GitLab;

public class CallbackModel(GitLabAuthService auth, GitLabSignIn signIn) : PageModel
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

        try
        {
            var token = await auth.ExchangeCodeAsync(pending.BaseUrl, code, pending.RedirectUri, ct);
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
