using ProAdmin.GitLab;

namespace ProAdmin.Tests;

public class ProjectNamesTests
{
    [Theory]
    [InlineData("Отчет по замороженным элементам", "otchet-po-zamorozhennym-elementam")]
    [InlineData("Уведомление о задаче", "uvedomlenie-o-zadache")]
    [InlineData("NotifyOnApproval", "notifyonapproval")]
    [InlineData("04.03 - Отчет (вторая форма) #", "04.03-otchet-vtoraya-forma")]
    [InlineData("###", "project")]
    public void SlugIsTransliterated(string name, string slug) => Assert.Equal(slug, ProjectNames.Slug(name));

    [Theory]
    [InlineData("Разрешения на внесение изменений (вторая форма)_V2 #", "Разрешения на внесение изменений вторая форма _V2")]
    [InlineData("Отчёт ОВАД", "Отчёт ОВАД")]
    public void SuggestKeepsRussianText(string name, string expected)
    {
        Assert.Equal(expected, ProjectNames.Suggest(name));
        Assert.Null(ProjectNames.NameError(ProjectNames.Suggest(name)));
    }

    [Fact]
    public void KeyIgnoresForbiddenCharsCaseAndYo()
    {
        Assert.Equal(ProjectNames.Key("Отчёт (вторая форма) #"), ProjectNames.Key("отчет вторая  форма"));
        Assert.NotEqual(ProjectNames.Key("Отчет А"), ProjectNames.Key("Отчет Б"));
    }
}
