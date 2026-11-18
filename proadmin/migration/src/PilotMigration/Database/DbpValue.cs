using System.Globalization;

namespace PilotMigration.Database;

/// <summary>Тип колонки приёмника: как значение из SQLite пишется в PostgreSQL.</summary>
public enum DbpKind
{
    /// <summary>bytea - BLOB как есть.</summary>
    Bytes,

    /// <summary>uuid - в SQLite 16 байт в раскладке .NET (Guid.ToByteArray) либо строка.</summary>
    Uuid,

    Int2,
    Int4,
    Int8,

    /// <summary>text / character varying.</summary>
    Text,

    /// <summary>boolean - в SQLite 0/1 либо строка «true»/«false» (так записан faslist.is_writable).</summary>
    Bool,

    /// <summary>timestamp with time zone - в SQLite строка ISO-8601 с «Z»: 2015-11-07 14:38:29.120443Z.</summary>
    Timestamp,

    /// <summary>timestamp with time zone - в SQLite число тиков .NET (так записан faslist.creation_date).</summary>
    TimestampTicks,
}

/// <summary>Преобразование значений файловой базы Pilot (SQLite) в значения PostgreSQL.</summary>
public static class DbpValue
{
    public static Guid? ToGuid(object? value) => value switch
    {
        null or DBNull => null,
        byte[] { Length: 16 } bytes => new Guid(bytes),
        byte[] { Length: 0 } => null,
        string text => Guid.TryParse(text, out var parsed) ? parsed : null,
        _ => null,
    };

    public static bool? ToBool(object? value) => value switch
    {
        null or DBNull => null,
        long number => number != 0,
        double number => number != 0,
        string text => bool.TryParse(text, out var parsed) ? parsed : text is "1",
        _ => null,
    };

    /// <summary>Дата-время UTC из строки ISO-8601 либо из тиков .NET.</summary>
    public static DateTime? ToUtc(object? value) => value switch
    {
        null or DBNull => null,
        string text => ParseUtc(text),
        long ticks => FromTicks(ticks),
        _ => null,
    };

    private static DateTime? ParseUtc(string text) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    private static DateTime? FromTicks(long ticks) =>
        ticks is >= 0 and <= 3_155_378_975_999_999_999 ? new DateTime(ticks, DateTimeKind.Utc) : null;

    /// <summary>Значение для COPY: null либо значение нужного типа. Ошибка - с указанием таблицы и колонки.</summary>
    public static object? Convert(DbpKind kind, object? value, string table, string column)
    {
        if (value is null or DBNull)
            return null;
        object? result = kind switch
        {
            DbpKind.Bytes => value as byte[],
            DbpKind.Uuid => ToGuid(value),
            DbpKind.Int2 => value is long n2 and >= short.MinValue and <= short.MaxValue ? (short)n2 : null,
            DbpKind.Int4 => value is long n4 and >= int.MinValue and <= int.MaxValue ? (int)n4 : null,
            DbpKind.Int8 => value is long n8 ? n8 : null,
            DbpKind.Text => value as string ?? value.ToString(),
            DbpKind.Bool => ToBool(value),
            DbpKind.Timestamp or DbpKind.TimestampTicks => ToUtc(value),
            _ => null,
        };
        if (result is null)
            throw new MigrationException(
                $"{table}.{column}: значение «{Describe(value)}» не приводится к типу {kind} приёмника.");
        return result;
    }

    /// <summary>Формат дат в файловой базе Pilot: 2015-11-07 14:38:29.120443Z.</summary>
    public const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.ffffff'Z'";

    /// <summary>
    /// Обратное преобразование - значение PostgreSQL в то, как оно лежит в файловой базе Pilot (для выгрузки).
    /// <see cref="Convert"/> возвращает из него то же значение.
    /// </summary>
    public static object? ToSqlite(DbpKind kind, object? value, string table, string column)
    {
        if (value is null or DBNull)
            return null;
        object? result = kind switch
        {
            DbpKind.Bytes => value as byte[],
            DbpKind.Uuid => value is Guid guid ? guid.ToByteArray() : null,
            DbpKind.Int2 or DbpKind.Int4 or DbpKind.Int8 => value is short or int or long ? System.Convert.ToInt64(value) : null,
            DbpKind.Text => value as string ?? value.ToString(),
            DbpKind.Bool => value is bool b ? (b ? 1L : 0L) : null,
            DbpKind.Timestamp => value is DateTime t ? AsUtc(t).ToString(TimestampFormat, CultureInfo.InvariantCulture) : null,
            DbpKind.TimestampTicks => value is DateTime t ? AsUtc(t).Ticks : null,
            _ => null,
        };
        if (result is null)
            throw new MigrationException(
                $"{table}.{column}: значение «{Describe(value)}» ({value.GetType().Name}) не записывается как {kind} файловой базы.");
        return result;
    }

    /// <summary>Значение колонки, которой нет в карте (databaseid): Guid - байтами, дата - строкой, остальное как есть.</summary>
    public static object ToSqliteAny(object? value) => value switch
    {
        null or DBNull => DBNull.Value,
        Guid guid => guid.ToByteArray(),
        DateTime t => AsUtc(t).ToString(TimestampFormat, CultureInfo.InvariantCulture),
        bool b => b ? 1L : 0L,
        short or int => System.Convert.ToInt64(value),
        _ => value,
    };

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static string Describe(object value) => value switch
    {
        byte[] bytes => $"BLOB {bytes.Length} байт",
        string text => text.Length > 40 ? text[..40] + "…" : text,
        _ => value.ToString() ?? "",
    };
}
