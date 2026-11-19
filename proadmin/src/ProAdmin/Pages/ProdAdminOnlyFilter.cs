using ProAdmin.Sessions;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ProAdmin.Pages;

/// <summary>
/// POST-обработчики страницы разрешены только сессии с входом администратором Pilot контура «Прод»
/// (<see cref="UserSession.IsProdAdmin"/>); остальные видят страницу для чтения и получают 403. Применяется к
/// общим настройкам, разделу «Миграция» и настройкам серверов. <c>exemptHandlers</c> - обработчики без записи.
/// Отказы и разрешённые изменения пишутся в журнал.
/// </summary>
public sealed class ProdAdminOnlyFilter(params string[] exemptHandlers) : IAsyncPageFilter
{
    public bool AllowGitLabAdminWithoutProd { get; init; }

    public Task OnPageHandlerSelectionAsync(PageHandlerSelectedContext context) => Task.CompletedTask;

    public async Task OnPageHandlerExecutionAsync(PageHandlerExecutingContext context, PageHandlerExecutionDelegate next)
    {
        var handler = context.HandlerMethod;
        var guarded = handler is not null && HttpMethods.IsPost(context.HttpContext.Request.Method) &&
                      !exemptHandlers.Contains(handler.Name ?? "", StringComparer.OrdinalIgnoreCase);
        var services = context.HttpContext.RequestServices;
        var log = services.GetRequiredService<ILogger<ProdAdminOnlyFilter>>();
        var address = context.HttpContext.Connection.RemoteIpAddress;

        if (guarded)
        {
            var session = services.GetRequiredService<SessionVault>().Get(context.HttpContext.User.FindFirst(SessionVault.SidClaim)?.Value);
            var settings = services.GetRequiredService<SettingsStore>().Current;
            var gitLabAdmin = AllowGitLabAdminWithoutProd && settings.FirstRunCompleted &&
                              settings.Find(AdminSettings.Prod)?.HasServer != true &&
                              session?.GitLabUser?.IsAdmin == true && session.IsGitLabConnected(settings);
            if (!gitLabAdmin && session?.IsProdAdmin(settings) != true)
            {
                log.LogWarning("Отказ (403): {Page}/{Handler} - вход {Login} без администратора Pilot контура «Прод», адрес {Address}",
                    context.ActionDescriptor.DisplayName, handler!.Name, session?.PilotLogin, address);
                context.Result = new StatusCodeResult(StatusCodes.Status403Forbidden);
                return;
            }
        }

        var executed = await next();
        if (guarded && executed.Exception is null)
            log.LogInformation("Изменение: {Page}/{Handler}, {Login}, адрес {Address}",
                context.ActionDescriptor.DisplayName, handler!.Name, context.HttpContext.User.Identity?.Name, address);
    }
}
