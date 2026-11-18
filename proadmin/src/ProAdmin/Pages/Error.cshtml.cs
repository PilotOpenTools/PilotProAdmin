using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ProAdmin.Pages;

[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
[IgnoreAntiforgeryToken]
public class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }

    /// <summary>Код ответа, с которого сюда перенаправил обработчик статусов (404, 403, 429).</summary>
    [BindProperty(SupportsGet = true)] public int? Code { get; set; }

    public string Heading => Code switch
    {
        404 => "Страница не найдена",
        403 => "Доступ запрещён",
        429 => "Слишком много попыток",
        _ => "Что-то пошло не так",
    };

    public string Text => Code switch
    {
        404 => "Такого адреса в админке нет. Проверьте ссылку или вернитесь на главную.",
        403 => "У вашей сессии нет прав на это действие. Для изменений нужен вход администратором Pilot контура «Прод» и права в GitLab.",
        429 => "Слишком много неудачных попыток с вашего адреса. Подождите 5 минут и повторите.",
        _ => "Запрос завершился ошибкой. Подробности - в логах контейнера.",
    };

    public void OnGet() => RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;

    // Обработчик статусов повторяет исходный запрос, в том числе POST.
    public void OnPost() => OnGet();
}
