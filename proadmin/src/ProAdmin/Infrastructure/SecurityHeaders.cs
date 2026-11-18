using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace ProAdmin.Infrastructure;

/// <summary>
/// Заголовки безопасности: CSP (скрипты - только свои, с одноразовым nonce на запрос, CodeMirror лежит в wwwroot/vendor; inline-обработчиков
/// событий нет), nosniff, Referrer-Policy, запрет встраивания в чужие страницы.
/// </summary>
public static class SecurityHeaders
{
    public const string NonceKey = "csp-nonce";

    public static string ContentSecurityPolicy(string nonce) =>
        "default-src 'self'; " +
        $"script-src 'self' 'nonce-{nonce}'; " +
        "style-src 'self' 'unsafe-inline'; " + // CodeMirror ставит стили элементам
        "img-src 'self' data: http: https:; " + // аватары GitLab
        "font-src 'self' data:; connect-src 'self'; object-src 'none'; base-uri 'self'; frame-ancestors 'none'";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceKey] = nonce;
            // OnStarting: обработчик ошибок очищает ответ, заголовки должны ставиться в момент отправки.
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Content-Security-Policy"] = ContentSecurityPolicy(nonce);
                headers["X-Content-Type-Options"] = "nosniff";
                headers["Referrer-Policy"] = "same-origin";
                headers["X-Frame-Options"] = "DENY";
                return Task.CompletedTask;
            });
            await next();
        });
}

/// <summary>Добавляет nonce текущего запроса ко всем &lt;script&gt; страниц: без него CSP не выполнит и встроенные скрипты.</summary>
[HtmlTargetElement("script")]
public sealed class CspNonceTagHelper : TagHelper
{
    [Microsoft.AspNetCore.Razor.TagHelpers.HtmlAttributeNotBound, Microsoft.AspNetCore.Mvc.ViewFeatures.ViewContext]
    public ViewContext ViewContext { get; set; } = null!;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (!output.Attributes.ContainsName("nonce") && ViewContext.HttpContext.Items[SecurityHeaders.NonceKey] is string nonce)
            output.Attributes.SetAttribute("nonce", nonce);
    }
}
