using ProAdmin.Compare;

namespace ProAdmin.Tests;

public class LineDiffTests
{
    /// <summary>Применяет diff к старому тексту и проверяет, что получился новый.</summary>
    private static void AssertReconstructs(string oldText, string newText)
    {
        var d = LineDiff.Compute(oldText, newText, context: int.MaxValue / 4);
        var rebuilt = d.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != DiffKind.Removed).Select(l => l.Text);
        var original = d.Hunks.SelectMany(h => h.Lines).Where(l => l.Kind != DiffKind.Added).Select(l => l.Text);
        Assert.Equal(newText.Split('\n'), rebuilt);
        Assert.Equal(oldText.Split('\n'), original);
    }

    [Fact]
    public void IdenticalTextsHaveNoHunks()
    {
        var d = LineDiff.Compute("a\nb\r\nc\n", "a\nb\nc");
        Assert.True(d.Identical);
        Assert.Empty(d.Hunks);
    }

    [Fact]
    public void CountsAndReconstruction()
    {
        const string a = "one\ntwo\nthree\nfour\nfive";
        const string b = "one\n2\nthree\nfour\nfive\nsix";
        var d = LineDiff.Compute(a, b);
        Assert.Equal(1, d.Removed);
        Assert.Equal(2, d.Added);
        AssertReconstructs(a, b);
    }

    [Theory]
    [InlineData("", "x\ny")]
    [InlineData("x\ny", "")]
    [InlineData("a\nb\nc\nd", "d\nc\nb\na")]
    [InlineData("a\nx\nb\nx\nc", "x\na\nb\nc\nx")]
    public void ReconstructsEdgeCases(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            var d = LineDiff.Compute(a, b);
            Assert.Equal(a.Length == 0 ? 0 : 2, d.Removed);
            Assert.Equal(b.Length == 0 ? 0 : 2, d.Added);
            return;
        }
        AssertReconstructs(a, b);
    }

    [Fact]
    public void LargeFileWithSmallChangeIsFastAndShowsContext()
    {
        var lines = Enumerable.Range(0, 20000).Select(i => $"<Item{i} Name=\"field{i}\" />").ToList();
        var a = string.Join('\n', lines);
        lines[12345] = "<Item12345 Name=\"changed\" />";
        var b = string.Join('\n', lines);

        var d = LineDiff.Compute(a, b);
        Assert.Equal(1, d.Added);
        Assert.Equal(1, d.Removed);
        var hunk = Assert.Single(d.Hunks);
        Assert.Equal(8, hunk.Lines.Count); // 3 строки контекста, −1, +1, 3 строки контекста
        Assert.Equal(12343, hunk.OldStart);
    }

    [Fact]
    public void TooManyEditsFallsBackToSummary()
    {
        var a = string.Join('\n', Enumerable.Range(0, 3000).Select(i => "a" + i));
        var b = string.Join('\n', Enumerable.Range(0, 3000).Select(i => "b" + i));
        var d = LineDiff.Compute(a, b, maxEdits: 100);
        Assert.True(d.TooLarge);
        Assert.False(d.Identical);
    }
}
