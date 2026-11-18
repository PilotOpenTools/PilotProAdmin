namespace PilotMigration.Config;

/// <summary>
/// Приёмник переноса - выгрузка базы контура в файлы: те же три файла SQLite, что у файловой базы Pilot,
/// папка с телами файлов и <c>manifest.json</c>. Читается импортом файловой базы на другом компьютере.
/// </summary>
public sealed class FileTargetOptions
{
    /// <summary>Имя папки выгрузки в каталоге выгрузок. Пусто - «&lt;контур&gt;-&lt;дата-время&gt;».</summary>
    public string Name { get; set; } = "";

    /// <summary>Выгрузить чаты и сообщения (chats.dbp).</summary>
    public bool Chats { get; set; } = true;

    /// <summary>Выгрузить журнал действий пользователей (journal.dbp): обычно в разы больше самой базы.</summary>
    public bool Journal { get; set; }

    /// <summary>Скопировать тела файлов в папку FileArchive выгрузки. Без них в базе останется только индекс файлов.</summary>
    public bool FileArchive { get; set; } = true;

    public void Normalize(string sourceKey)
    {
        Name = (Name ?? "").Trim();
        if (Name.Length == 0)
            Name = $"{sourceKey}-{DateTime.Now:yyyyMMdd-HHmm}";
    }

    /// <summary>null - имя годится для папки; иначе - что не так.</summary>
    public string? NameError()
    {
        if (Name.Length > 100)
            return "Имя выгрузки - не длиннее 100 символов.";
        if (Name is "." or ".." || Name.EndsWith(ExportFiles.PartialSuffix, StringComparison.OrdinalIgnoreCase))
            return $"Имя выгрузки «{Name}» не годится для папки.";
        var bad = Name.Where(c => c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(c)).Distinct().ToList();
        return bad.Count > 0
            ? $"В имени выгрузки недопустимые символы: {string.Join(" ", bad)}."
            : null;
    }
}
