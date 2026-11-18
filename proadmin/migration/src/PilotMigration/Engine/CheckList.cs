namespace PilotMigration.Engine;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum CheckLevel
{
    Info,
    Warning,
    Blocker,
}

public sealed record CheckItem(CheckLevel Level, string Text);

/// <summary>Результат проверки перед переносом: блокирующие ошибки, предупреждения, сведения.</summary>
public sealed class CheckList
{
    private readonly List<CheckItem> _items = new();
    private readonly object _lock = new();

    public IReadOnlyList<CheckItem> Items
    {
        get { lock (_lock) return _items.ToList(); }
    }

    public bool HasBlockers
    {
        get { lock (_lock) return _items.Any(i => i.Level == CheckLevel.Blocker); }
    }

    public void Block(string text) => Add(CheckLevel.Blocker, text);
    public void Warn(string text) => Add(CheckLevel.Warning, text);
    public void Note(string text) => Add(CheckLevel.Info, text);

    public void Add(CheckLevel level, string text)
    {
        lock (_lock)
        {
            if (!_items.Any(i => i.Level == level && i.Text == text))
                _items.Add(new CheckItem(level, text));
        }
    }
}
