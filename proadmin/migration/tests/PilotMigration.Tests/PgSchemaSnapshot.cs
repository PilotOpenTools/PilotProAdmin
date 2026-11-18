using PilotMigration.Database;

namespace PilotMigration.Tests;

/// <summary>
/// Снимок настоящей схемы Pilot в PostgreSQL - <c>dbp26.13.0.62219</c> базы pilot-ice_ru тестового контура:
/// таблицы, колонки в порядке объявления, колонки-identity и внешние ключи. Нужен, чтобы карта импорта
/// (<see cref="DbpToPgMap"/>) проверялась без живой базы: если сервер Pilot изменит схему, расхождение
/// увидит проверка перед импортом, а здесь - тест.
/// </summary>
public static class PgSchemaSnapshot
{
    public const string Name = "dbp26.13.0.62219";

    private static readonly (string Table, string Columns)[] Columns =
    {
        ("changeset_data", "id,data"),
        ("changesets", "id,data,identity"),
        ("chat_members", "chat_id,person_id,is_admin,is_deleted,is_notifiable,date_updated,is_viewer_only"),
        ("chats", "id,name,description,creator_id,type,last_message_id,creation_date_utc"),
        ("counters", "name,data"),
        ("databaseid", "id,data,version,server_version,session_id"),
        ("external_tasks", "id,type,initiator_id,date_created,date_started,date_finished,handler,task_data," +
                           "is_canceled,source_id,group_id,object_id,project_id,progress,additional"),
        ("famediator", "file_id,object_id,fa_id"),
        ("faslist", "fa_id,name,creation_date,is_writable"),
        ("history_items", "id,object_id,reason,creator_id,created,object,due_to_person"),
        ("interitable_access", "object_id,access"),
        ("message_person", "chat_id,message_id,person_id,issent,mark_as_unread,date"),
        ("message_relations", "object_id,chat_id,type,message_id,is_deleted"),
        ("messages", "id,data,creator_id,date_time_server_utc,date_time_local_utc,chat_id,related_message_id,type"),
        ("metadata", "id,data"),
        ("notifications", "id,data"),
        ("objects", "id,data,last_change"),
        ("organisationunits", "id,data,last_change"),
        ("people", "id,login,password,data,last_change"),
        ("rules", "id,description"),
        ("rules_changeset_data", "rule_id,changeset_data_id"),
        ("settings", "key,data"),
        ("subscriptions", "obj_id,person_id"),
        ("third_party_info", "third_party_info,messageid"),
        ("user_actions", "id,person_id,ip,server_date_time,changeset_id,event_kind,object_id,object_type," +
                         "object_title,additional_info,error_string,plugin_additional"),
    };

    /// <summary>Колонки, которые PostgreSQL заполняет сам (identity): их последовательности сдвигаются после переноса.</summary>
    private static readonly (string Table, string Column)[] Identity =
    {
        ("changesets", "id"), ("databaseid", "id"), ("metadata", "id"), ("notifications", "id"),
        ("organisationunits", "id"), ("people", "id"), ("user_actions", "id"),
    };

    /// <summary>
    /// Внешние ключи схемы. Среди них цикл: <c>messages.chat_id / chats.id</c> и
    /// <c>chats.last_message_id / messages.id</c> - из-за него таблицы и грузятся со снятыми ключами.
    /// </summary>
    private static readonly PgForeignKey[] ForeignKeys =
    {
        new("chat_members", "person_id", "people", "id"),
        new("chat_members", "chat_id", "chats", "id"),
        new("chats", "creator_id", "people", "id"),
        new("chats", "last_message_id", "messages", "id"),
        new("external_tasks", "initiator_id", "people", "id"),
        new("history_items", "creator_id", "people", "id"),
        new("history_items", "object_id", "objects", "id"),
        new("interitable_access", "object_id", "objects", "id"),
        new("message_person", "message_id", "messages", "id"),
        new("message_person", "chat_id", "chats", "id"),
        new("message_person", "person_id", "people", "id"),
        new("message_relations", "chat_id", "chats", "id"),
        new("message_relations", "message_id", "messages", "id"),
        new("messages", "related_message_id", "messages", "id"),
        new("messages", "creator_id", "people", "id"),
        new("messages", "chat_id", "chats", "id"),
        new("rules_changeset_data", "changeset_data_id", "changeset_data", "id"),
        new("rules_changeset_data", "rule_id", "rules", "id"),
        new("subscriptions", "person_id", "people", "id"),
        new("subscriptions", "obj_id", "objects", "id"),
        new("third_party_info", "messageid", "messages", "id"),
    };

    /// <summary>Схема; <paramref name="without"/> убирает колонку - так проверяется реакция на несовпадение версий.</summary>
    public static PilotPgSchema Load((string Table, string Column)? without = null)
    {
        var tables = new Dictionary<string, PgTable>(StringComparer.Ordinal);
        foreach (var (table, columns) in Columns)
        {
            var list = columns.Split(',', StringSplitOptions.TrimEntries)
                .Where(c => without is null || without.Value.Table != table || without.Value.Column != c)
                .ToList();
            var identity = Identity.Where(i => i.Table == table).Select(i => i.Column).ToList();
            tables[table] = new PgTable(table, list, identity);
        }
        return new PilotPgSchema { Schema = Name, Tables = tables, ForeignKeys = ForeignKeys };
    }
}
