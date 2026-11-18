using System.Text;
using ProAdmin.GitLab;
using ProAdmin.Reports;

namespace ProAdmin.Tests;

public class ReportRulesTests
{
    private static List<GitLabTreeItem> Tree(params string[] paths) =>
        paths.Select((p, i) => new GitLabTreeItem { Type = "blob", Path = p, Id = i.ToString() }).ToList();

    [Fact]
    public void FindsReportFileByNameOrSingleRepx()
    {
        Assert.Equal("Реестр НОПРИЗ.repx", ReportFiles.FindPath(Tree("README.md", "Реестр НОПРИЗ.repx", "old/Другой.repx"), "Реестр НОПРИЗ"));
        // имя проекта без запрещённых символов совпадает с именем файла, где они были
        Assert.Equal("reports/Отчет (вторая форма).repx", ReportFiles.FindPath(Tree("reports/Отчет (вторая форма).repx", "x.repx"), "Отчет вторая форма"));
        // единственный .repx подходит всегда (репозитории, созданные старым модулем)
        Assert.Equal("report.repx", ReportFiles.FindPath(Tree("report.repx", "README.md"), "Любое имя"));
        Assert.Null(ReportFiles.FindPath(Tree("a.repx", "b.repx"), "Третий"));
        Assert.Null(ReportFiles.FindPath(null, "X"));
    }

    [Fact]
    public void BlobShaMatchesGitAndNormalizationIgnoresBomAndLineEndings()
    {
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a", ReportFiles.BlobSha(Encoding.UTF8.GetBytes("hello\n")));
        var withBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("<a>\r\n<b/>\r\n")).ToArray();
        Assert.True(ReportFiles.SameContent(withBom, Encoding.UTF8.GetBytes("<a>\n<b/>")));
        Assert.False(ReportFiles.SameContent(withBom, Encoding.UTF8.GetBytes("<a>\n<c/>")));
        Assert.False(ReportFiles.SameContent(null, withBom));
    }

    [Theory]
    [InlineData("01-Сводные отчёты", "01-folder")]
    [InlineData("01-Сводные отчёты/Подпапка", "01-folder")]
    [InlineData("XX-ARCHIVE", "XX-other")]
    [InlineData("12-ОАСУ", "12-ОАСУ")]
    [InlineData("99-Нет такой", "")]
    [InlineData("", "")]
    public void GuessesGitFolderFromPilotFolder(string pilotFolder, string expected) =>
        Assert.Equal(expected, ReportRepositories.GuessFolder(pilotFolder, new[] { "", "01-folder", "12-ОАСУ", "XX-other" }));

    [Fact]
    public void ReadmeMentionsBranches() =>
        Assert.Contains("`dev` - отчёт на тестовом сервере", ReportRepositories.Readme("Отчёт", ""));

    [Fact]
    public void ReadmeSaysReportsGoToDevOrHotfixNotMain()
    {
        var readme = ReportRepositories.Readme("Отчёт", "");
        Assert.Contains("`hotfix/…` от `main`", readme);
        Assert.DoesNotContain("в `dev` или `main`", readme);
    }
}
