using System.Security.Cryptography;
using System.Text;

namespace PilotMigration;

/// <summary>Блокировка перебора ключа API: после <c>limit</c> неверных ключей с одного адреса за <c>window</c> - отказ на <c>window</c>.</summary>
public sealed class AuthThrottle
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, Entry> _entries = new();
    private readonly object _lock = new();

    public AuthThrottle(int limit = 10, TimeSpan? window = null, Func<DateTimeOffset>? now = null)
    {
        _limit = limit;
        _window = window ?? TimeSpan.FromMinutes(5);
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Сколько ещё ждать, пока адрес заблокирован; null - не заблокирован.</summary>
    public TimeSpan? BlockedFor(string client)
    {
        lock (_lock)
        {
            if (_entries.TryGetValue(client, out var e) && e.BlockedUntil is { } until && until > _now())
                return until - _now();
            return null;
        }
    }

    public void Failure(string client)
    {
        lock (_lock)
        {
            var now = _now();
            if (_entries.Count > 1000)
            {
                foreach (var key in _entries.Where(p => p.Value.Expired(now, _window)).Select(p => p.Key).ToList())
                    _entries.Remove(key);
            }
            var e = _entries.TryGetValue(client, out var old) && !old.Expired(now, _window) ? old : new Entry { Start = now };
            e.Count++;
            if (e.Count >= _limit)
                e.BlockedUntil = now + _window;
            _entries[client] = e;
        }
    }

    private sealed class Entry
    {
        public int Count;
        public DateTimeOffset Start;
        public DateTimeOffset? BlockedUntil;

        public bool Expired(DateTimeOffset now, TimeSpan window) =>
            (BlockedUntil is { } until ? until : Start + window) <= now;
    }
}

public static class ApiSecurity
{
    /// <summary>Сравнение ключей за постоянное время; длина ключа не раскрывается (сравниваются хеши).</summary>
    public static bool KeyEquals(string actual, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(actual)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    /// <summary>Автор запуска из заголовка X-User: без управляющих символов, не длиннее 100.</summary>
    public static string CleanUser(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return "неизвестно";
        var text = new string(Uri.UnescapeDataString(header).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (text.Length > 100)
            text = text[..100];
        return text.Length == 0 ? "неизвестно" : text;
    }
}
