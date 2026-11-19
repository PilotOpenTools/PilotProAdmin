using ProAdmin.Pilot;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

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
        var returnUrl = HttpMethods.IsGet(request.Method) ? request.PathBase + request.Path + request.QueryString : null;
        executed.Result = new RedirectToPageResult("/PilotConnection", new { returnUrl });
        executed.ExceptionHandled = true;
    }
}
