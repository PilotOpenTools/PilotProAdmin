using ProAdmin.GitLab;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.GitLab;

public class CallbackModel : AppPageModel
{
    private readonly GitLabAuthService _auth;
    private readonly GitLabClientFactory _factory;

    public CallbackModel(GitLabAuthService auth, GitLabClientFactory factory)
    {
        _auth = auth;
        _factory = factory;
    }

    public string? Error { get; set; }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error, string? error_description, CancellationToken ct)
    {
        var pending = CurrentSession.PendingOAuth;
        CurrentSession.PendingOAuth = null;

        // Повторный заход на callback с тем же state (браузер открыл ссылку дважды, «назад», обновление страницы):
        // вход уже выполнен первым запросом - просто продолжаем, а не показываем ошибку.
        if (pending is null && GitLabConnected)
            return Continue(CurrentSession.CompletedOAuth is { } done && done.State == state ? done.ReturnUrl : null);

        if (!string.IsNullOrEmpty(error))
        {
            Error = $"GitLab вернул ошибку: {error_description ?? error}";
            return Page();
        }
        if (pending is null || string.IsNullOrEmpty(code) || state != pending.Value.State)
        {
            Error = "Сеанс входа устарел или подменён. Начните вход заново.";
            return Page();
        }

        try
        {
            var token = await _auth.ExchangeCodeAsync(pending.Value.BaseUrl, code, pending.Value.RedirectUri, ct);
            var user = await _factory.Create(token).GetCurrentUserAsync(ct);
            CurrentSession.GitLabToken = token;
            CurrentSession.GitLabUser = user;
            CurrentSession.CompletedOAuth = (pending.Value.State, pending.Value.ReturnUrl);
            TempData["Flash"] = CurrentSession.LoginSummary();
        }
        catch (GitLabApiException ex)
        {
            Error = ex.Message;
            return Page();
        }

        return Continue(pending.Value.ReturnUrl);
    }

    private IActionResult Continue(string? returnUrl) =>
        IsLocalUrl(returnUrl) && returnUrl != "/" ? LocalRedirect(returnUrl!) : RedirectToPage("/Index");
}
