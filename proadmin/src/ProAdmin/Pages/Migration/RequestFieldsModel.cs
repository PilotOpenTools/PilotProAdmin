using ProAdmin.Migration;

namespace ProAdmin.Pages.Migration;

/// <summary>
/// Модель полей «откуда, куда, что переносить» (<c>_RequestFields</c>). Файловый источник доступен
/// только на странице ручного запуска: импорт присланной базы - разовый, по расписанию он не ставится.
/// </summary>
public sealed record RequestFieldsModel(
    RequestForm Form,
    List<MigrationEnvDto> Envs,
    ImportSourcesDto? Import = null,
    bool AllowFileSource = false);
