using ProAdmin.GitLab;
using Xunit;

namespace ProAdmin.Tests;

public class GroupAccessTests
{
    [Theory]
    [InlineData(0, false, true, true)]    // не участник публичной группы - только чтение
    [InlineData(10, false, true, true)]   // Guest
    [InlineData(20, false, true, true)]   // Reporter
    [InlineData(30, false, true, false)]  // Developer
    [InlineData(50, false, true, false)]  // Owner
    [InlineData(0, true, true, false)]    // администратор GitLab
    public void ReadOnly_ByLevel(int level, bool admin, bool found, bool readOnly) =>
        Assert.Equal(readOnly, new GitLabGroupAccess("automation", level, admin, found, null).ReadOnly);

    [Fact]
    public void UnknownAccess_Blocks()
    {
        // Сбой GitLab не открывает изменения: роль не доказана - запрещено.
        var access = new GitLabGroupAccess("automation", 0, false, false, "GitLab недоступен");
        Assert.True(access.ReadOnly);
        Assert.True(GitLabGroupAccess.CannotChange(access));
        Assert.Contains("не удалось", access.Summary);
    }

    [Fact]
    public void Summary_NamesLevel()
    {
        Assert.StartsWith("Reporter - только просмотр", new GitLabGroupAccess("g", 20, false, true, null).Summary);
        Assert.StartsWith("Maintainer - можно изменять", new GitLabGroupAccess("g", 40, false, true, null).Summary);
    }
}
