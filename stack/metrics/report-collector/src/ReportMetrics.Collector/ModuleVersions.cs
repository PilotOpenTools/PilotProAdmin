namespace ReportMetrics.Collector;

/// <summary>Версия модуля - три числа X.Y.Z (как в ProAdmin и тегах vX.Y.Z).</summary>
public static class ModuleVersions
{
    /// <summary>
    /// «2.1.0.0» (AssemblyVersion .NET, старые клиенты) / «2.1.0». Ненулевая четвёртая часть остаётся -
    /// это другая сборка, а не та же версия.
    /// </summary>
    public static string? Display(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return version;
        var v = version.Trim();
        return v.Count(c => c == '.') == 3 && v.EndsWith(".0", StringComparison.Ordinal) ? v[..^2] : v;
    }
}
