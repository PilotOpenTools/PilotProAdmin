using System;
using System.Globalization;
using System.Text;
using Pilot.ClientSdk.Infrastructure;

namespace Pilot.ClientSdk.Telemetry
{
    /// <summary>Статусы события - те же, что понимает сервис (metrics.module_events.status).</summary>
    public static class ModuleEventStatus
    {
        public const string Success = "success";
        public const string Error = "error";
        public const string Cancelled = "cancelled";
    }

    /// <summary>Виды событий.</summary>
    public static class ModuleEventKind
    {
        /// <summary>Модуль загружен клиентом (версия модуля на рабочем месте).</summary>
        public const string Load = "load";

        /// <summary>Действие пользователя: команда меню, кнопка, обработка.</summary>
        public const string Action = "action";
    }

    /// <summary>Одно событие статистики модуля. Уходит в сервис сбора и хранится в metrics.module_events.</summary>
    internal sealed class ModuleEvent
    {
        public Guid EventId { get; set; } = Guid.NewGuid();
        public DateTime TsUtc { get; set; } = DateTime.UtcNow;
        public string Kind { get; set; } = ModuleEventKind.Action;
        public string Module { get; set; }
        public string ModuleTitle { get; set; }
        public string ModuleVersion { get; set; }
        public string Action { get; set; }
        public string Status { get; set; } = ModuleEventStatus.Success;
        public long? DurationMs { get; set; }
        public long? Items { get; set; }
        public string ErrorType { get; set; }
        public string ErrorMessage { get; set; }
        public string Login { get; set; }
        public string Person { get; set; }
        public string Server { get; set; }
        public string ClientType { get; set; }
        public string ClientVersion { get; set; }
        public string Machine { get; set; }
        public string LibVersion { get; set; }

        public static ModuleEvent For(ModuleIdentity module, string kind, string action) => new ModuleEvent
        {
            Kind = kind,
            Module = module.Module,
            ModuleTitle = module.Title,
            ModuleVersion = module.Version,
            Action = string.IsNullOrWhiteSpace(action) ? null : action.Trim(),
        };

        /// <summary>Длина текста ошибки в статистике (как в сервисе сбора): в сообщениях исключений бывают пути и имена.</summary>
        public const int MaxErrorMessage = 2000;

        internal static string Truncate(string text, int max) =>
            text != null && text.Length > max ? text.Substring(0, max) : text;

        public void SetError(Exception ex)
        {
            Status = ModuleEventStatus.Error;
            var root = ex?.GetBaseException() ?? ex;
            ErrorType = root?.GetType().FullName;
            ErrorMessage = Truncate(root?.Message, MaxErrorMessage);
        }

        /// <summary>JSON в формате, который ждёт сервис (snake_case).</summary>
        public string ToJson()
        {
            var sb = new StringBuilder(512).Append('{');
            Json.Prop(sb, "event_id", EventId.ToString());
            Json.Prop(sb, "ts", TsUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
            Json.Prop(sb, "kind", Kind);
            Json.Prop(sb, "module", Module);
            Json.Prop(sb, "module_title", ModuleTitle);
            Json.Prop(sb, "module_version", ModuleVersion);
            Json.Prop(sb, "action", Action);
            Json.Prop(sb, "status", Status);
            Json.Prop(sb, "duration_ms", DurationMs);
            Json.Prop(sb, "items", Items);
            Json.Prop(sb, "error_type", ErrorType);
            Json.Prop(sb, "error_message", ErrorMessage);
            Json.Prop(sb, "login", Login);
            Json.Prop(sb, "person", Person);
            Json.Prop(sb, "server", Server);
            Json.Prop(sb, "client_type", ClientType);
            Json.Prop(sb, "client_version", ClientVersion);
            Json.Prop(sb, "machine", Machine);
            Json.Prop(sb, "lib_version", LibVersion);
            sb.Length--; // последняя запятая
            return sb.Append('}').ToString();
        }
    }
}
