using ProAdmin.Pilot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages;

public sealed class PilotCredentialsFilter : IAsyncPageFilter
{
    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var executed = await next();
        if (executed.Exception is not PilotCredentialsRequiredException)
            return;

        var request = context.HttpContext.Request;
        var services = context.HttpContext.RequestServices;
        var session = services.GetRequiredService<Sessions.SessionVault>().Get(context.HttpContext.User.FindFirst(Sessions.SessionVault.SidClaim)?.Value);
        if (session is { UsesServiceAccounts: true })
        {
            // Вход через GitLab: у контура нет служебной учётной записи - её задают в «Настройках».
            if (context.HandlerInstance is PageModel page)
                page.TempData["FlashError"] = "Для этого контура не задана служебная учётная запись Pilot. Её задаёт администратор в «Настройках».";
            executed.Result = new RedirectToPageResult("/Settings", null, null, "deploy-accounts");
        }
        else
        {
            var returnUrl = HttpMethods.IsGet(request.Method) ? request.PathBase + request.Path + request.QueryString : null;
            executed.Result = new RedirectToPageResult("/PilotConnection", new { returnUrl });
        }
        executed.ExceptionHandled = true;
    }
}
