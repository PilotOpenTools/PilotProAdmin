using System.Text.Json;
using System.Text.Json.Serialization;
using Ascon.Pilot.DataClasses;

namespace ProAdmin.Types;

// Файлы репозитория типов. Один элемент - один файл: тогда коммиты по разным типам не конфликтуют при переносе
// (cherry-pick) между ветками. Элементы сопоставляются с базой по имени, а не по Id: Id типов и состояний на
// разных серверах разные. Ссылки на состояния и группы внутри конфигураций хранятся как {{state:имя}} / {{machine:заголовок}}.

/// <summary>Тип (types/&lt;имя&gt;.json) вместе с атрибутами.</summary>
public sealed class TypeDoc
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public TypeKind Kind { get; set; } = TypeKind.User;
    public int Sort { get; set; }
    public bool HasFiles { get; set; }
    public bool IsMountable { get; set; }
    public bool IsService { get; set; }
    public bool IsProject { get; set; }
    public bool IsVisibleOnStorage { get; set; }

    /// <summary>Тип удалён (в Pilot тип нельзя удалить совсем - он помечается удалённым).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Заголовок группы состояний (машины переходов); null - нет.</summary>
    public string? StateMachine { get; set; }

    /// <summary>Имена дочерних типов.</summary>
    public List<string> Children { get; set; } = new();

    public string? Configuration { get; set; }

    /// <summary>Иконка, base64.</summary>
    public string? Icon { get; set; }

    public List<AttributeDoc> Attributes { get; set; } = new();
}

public sealed class AttributeDoc
{
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public MAttrType Type { get; set; } = MAttrType.String;
    public bool Obligatory { get; set; }
    public int DisplaySortOrder { get; set; }
    public int DisplayHeight { get; set; }
    public bool ShowInTree { get; set; }
    public bool IsService { get; set; }
    public bool JoinWithPrevious { get; set; }
    public AttributeUniquenessType Uniqueness { get; set; } = AttributeUniquenessType.NotUnique;
    public string? GroupTitle { get; set; }
    public bool IgnoreIndex { get; set; }
    public List<string> EditableForPositions { get; set; } = new();
    public string? Configuration { get; set; }
}

/// <summary>Состояние (states/&lt;имя&gt;.json).</summary>
public sealed class StateDoc
{
    /// <summary>Id, с которым состояние создаётся на сервере, где его ещё нет (если не занят). Сопоставление - по имени.</summary>
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    public string? Title { get; set; }
    public UserStateColors Color { get; set; } = UserStateColors.None;
    public bool IsCompletionState { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Системное состояние Pilot: в репозитории только для ссылок, на сервер не пишется.</summary>
    public bool IsSystem { get; set; }

    public string? Icon { get; set; }
}

/// <summary>Группа состояний - машина переходов (state-machines/&lt;заголовок&gt;.json).</summary>
public sealed class MachineDoc
{
    /// <summary>Сопоставление - по Id, затем по заголовку; новый на сервере создаётся с этим Id.</summary>
    public Guid? Id { get; set; }
    public string Title { get; set; } = "";

    /// <summary>Удалить группу с серверов (если её не использует ни один тип).</summary>
    public bool IsDeleted { get; set; }

    /// <summary>Переходы по исходному состоянию в порядке Pilot. «&lt;Нет&gt;» - объект без состояния.</summary>
    public List<MachineStateDoc> States { get; set; } = new();
}

public sealed class MachineStateDoc
{
    public string State { get; set; } = "";
    public List<TransitionDoc> Transitions { get; set; } = new();
}

public sealed class TransitionDoc
{
    public string To { get; set; } = "";
    public string? DisplayName { get; set; }
    public List<string> Positions { get; set; } = new();
}

/// <summary>config.json - версия конфигурации (меняется только при выпуске в main).</summary>
public sealed class TypesConfigInfo
{
    public string Version { get; set; } = "0.0.0";
    public string? Description { get; set; }
}

/// <summary>Сериализация файлов: LF, отступы, кириллица без экранирования, перевод строки в конце.</summary>
public static class TypesJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Write<T>(T value) =>
        JsonSerializer.Serialize(value, Options).Replace("\r\n", "\n") + "\n";

    public static T Read<T>(string text, string path)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(text, Options) ?? throw new TypesFormatException($"{path}: пустой файл.");
        }
        catch (JsonException ex)
        {
            throw new TypesFormatException($"{path}: некорректный JSON - {ex.Message}");
        }
    }

    /// <summary>Сравнение документов по содержимому (одинаковая сериализация).</summary>
    public static bool Same<T>(T a, T b) => Write(a) == Write(b);
}

public sealed class TypesFormatException(string message) : Exception(message);
