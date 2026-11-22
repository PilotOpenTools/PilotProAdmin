using System.Text.RegularExpressions;
using Xunit;

namespace ProAdmin.Tests;

/// <summary>
/// Razor Pages проверяют antiforgery-токен у каждого POST. Тег-хелпер формы не добавляет токен, если у формы задан
/// action явно (без asp-page/asp-page-handler): такой POST получает 400 и страницу «Что-то пошло не так».
/// Так ломалась «Удалить ветку» в «Следующем шаге». Нужен asp-antiforgery="true" или @Html.AntiForgeryToken().
/// </summary>
public class AntiforgeryFormsTests
{
    [Fact]
    public void Post_forms_with_explicit_action_carry_token()
    {
        var pages = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ProAdmin", "Pages"));
        Assert.True(Directory.Exists(pages), pages);
        var bad = new List<string>();
        foreach (var file in Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (Match form in Regex.Matches(text, @"<form\b[^>]*>.*?</form>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var tag = form.Value[..(form.Value.IndexOf('>') + 1)];
                if (!Regex.IsMatch(tag, @"method=""post""", RegexOptions.IgnoreCase) || !Regex.IsMatch(tag, @"\saction="))
                    continue;
                if (tag.Contains("asp-antiforgery=\"true\"") || form.Value.Contains("AntiForgeryToken()"))
                    continue;
                bad.Add($"{Path.GetRelativePath(pages, file)}: {tag}");
            }
        }
        Assert.True(bad.Count == 0, "POST-формы с action без antiforgery-токена:\n" + string.Join("\n", bad));
    }
}
