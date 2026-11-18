namespace ProAdmin.Infrastructure;

/// <summary>
/// Ограничение перебора: после <c>limit</c> неудач с одного адреса за <c>window</c> адрес блокируется на <c>blockFor</c>.
/// Области (вход, ключи выкладки из CI) считаются раздельно. Счётчики в памяти процесса.
/// </summary>
public sealed class AuthFailureLimiter
{
    public const string LoginScope = "login";
    public const string CiScope = "ci";

    /// <summary>Предел числа адресов в таблице: поддельные X-Forwarded-For не растят память без границы.</summary>
    public const int MaxEntries = 10_000;

    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly TimeSpan _blockFor;
    private readonly Func<DateTimeOffset> _now;
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);
    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new();
    private DateTimeOffset _nextPrune;

    public AuthFailureLimiter(int limit = 10, TimeSpan? window = null, TimeSpan? blockFor = null, Func<DateTimeOffset>? now = null)
    {
        _limit = limit;
        _window = window ?? TimeSpan.FromMinutes(5);
        _blockFor = blockFor ?? TimeSpan.FromMinutes(5);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private sealed class Entry
    {
        public readonly Queue<DateTimeOffset> Failures = new();
        public DateTimeOffset BlockedUntil;
    }

    /// <summary>Сколько адресов сейчас отслеживается.</summary>
    internal int Tracked { get { lock (_lock) return _entries.Count; } }

    public bool IsBlocked(string scope, string? address)
    {
        lock (_lock)
            return _entries.TryGetValue(Key(scope, address), out var e) && e.BlockedUntil > _now();
    }

    /// <summary>Записывает неудачу; true - адрес только что заблокирован.</summary>
    public bool Fail(string scope, string? address)
    {
        lock (_lock)
        {
            var now = _now();
            // Очистка раз в минуту, а не на каждый вызов: при забитой таблице полный проход под lock стал бы O(n) на запрос.
            if (now >= _nextPrune)
            {
                foreach (var key in _entries.Where(p => p.Value.BlockedUntil <= now && p.Value.Failures.All(f => now - f > _window)).Select(p => p.Key).ToList())
                    _entries.Remove(key);
                _nextPrune = now + PruneEvery;
            }

            var key2 = Key(scope, address);
            if (!_entries.TryGetValue(key2, out var entry))
            {
                // shortcut: при заполнении таблицы новые адреса не считаются до очистки (блокировать их нельзя - это отказ обычным пользователям); перебор с 10 000 разных адресов лимит по адресу не остановит.
                if (_entries.Count >= MaxEntries)
                    return false;
                _entries[key2] = entry = new Entry();
            }
            while (entry.Failures.Count > 0 && now - entry.Failures.Peek() > _window)
                entry.Failures.Dequeue();
            entry.Failures.Enqueue(now);
            if (entry.Failures.Count < _limit)
                return false;
            entry.Failures.Clear();
            entry.BlockedUntil = now + _blockFor;
            return true;
        }
    }

    private static string Key(string scope, string? address) => $"{scope}|{address ?? "?"}";
}
