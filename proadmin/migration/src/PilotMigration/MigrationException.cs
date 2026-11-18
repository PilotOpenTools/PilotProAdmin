namespace PilotMigration;

/// <summary>Ожидаемая ошибка миграции - текст показывается пользователю как есть.</summary>
public sealed class MigrationException : Exception
{
    public MigrationException(string message, Exception? inner = null) : base(message, inner) { }
}
