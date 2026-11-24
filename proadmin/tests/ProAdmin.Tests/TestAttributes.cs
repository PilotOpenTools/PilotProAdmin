using System.Runtime.CompilerServices;

namespace ProAdmin.Tests;

/// <summary>
/// Тест, которому нужны переменные окружения (живой Pilot и т. п.). Без них он помечается пропущенным и в отчёте
/// виден как пропущенный, а не молча «проходит».
/// </summary>
// params-конструктор нельзя совместить с [CallerFilePath]: источник теста нужен только для перехода из IDE.
#pragma warning disable xUnit3003
public sealed class RequiresEnvFactAttribute : FactAttribute
{
    public RequiresEnvFactAttribute(params string[] variables)
    {
        var missing = variables.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        if (missing.Count > 0)
            Skip = "Нужны переменные окружения: " + string.Join(", ", missing);
    }
}
#pragma warning restore xUnit3003

/// <summary>
/// Сверка копий файлов читает соседние папки монорепозитория. Вне монорепозитория тест падает с понятным сообщением;
/// пропустить его можно только явно: SKIP_REPO_COPIES=1 (например, при сборке отдельной копии папки proadmin).
/// </summary>
public sealed class RepoCopiesFactAttribute : FactAttribute
{
    public RepoCopiesFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = RepoCopies.SkipReason;
}

public sealed class RepoCopiesTheoryAttribute : TheoryAttribute
{
    public RepoCopiesTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Skip = RepoCopies.SkipReason;
}

internal static class RepoCopies
{
    public static string? SkipReason =>
        Environment.GetEnvironmentVariable("SKIP_REPO_COPIES") == "1" ? "SKIP_REPO_COPIES=1: сверка копий файлов пропущена явно" : null;

    /// <summary>Корень монорепозитория (папка со stack/Install); тест падает, если тесты запущены вне него.</summary>
    public static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "stack", "Install", "wait-for-postgres.sh")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new Xunit.Sdk.XunitException(
            "Не найден корень монорепозитория (папка со stack/Install/wait-for-postgres.sh): сверка копий файлов не может ничего проверить. " +
            "Запускайте тесты в полном монорепозитории или задайте SKIP_REPO_COPIES=1, чтобы пропустить сверку явно.");
    }
}
