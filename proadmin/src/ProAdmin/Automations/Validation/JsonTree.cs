using System.Text;
using System.Text.Json;

namespace ProAdmin.Automations.Validation;

/// <summary>Узел JSON с номером строки - чтобы сообщения линтера указывали место в редакторе.</summary>
public sealed class JNode
{
    public JsonValueKind Kind { get; init; }
    public int Line { get; init; }
    public string? Text { get; init; }
    public bool? Bool { get; init; }
    public List<JProperty> Properties { get; } = new();
    public List<JNode> Items { get; } = new();

    public JNode? this[string name] => Properties.FirstOrDefault(p => p.Name == name)?.Value;

    public bool IsString => Kind == JsonValueKind.String;
    public bool IsArray => Kind == JsonValueKind.Array;
    public bool IsObject => Kind == JsonValueKind.Object;

    /// <summary>Строка или массив строк / список строк (с номерами строк).</summary>
    public IEnumerable<(string Value, int Line)> Strings() =>
        IsString ? new[] { (Text!, Line) }
        : IsArray ? Items.Where(i => i.IsString).Select(i => (i.Text!, i.Line))
        : Enumerable.Empty<(string, int)>();

    public string KindRu => Kind switch
    {
        JsonValueKind.Object => "объект",
        JsonValueKind.Array => "массив",
        JsonValueKind.String => "строка",
        JsonValueKind.Number => "число",
        JsonValueKind.True or JsonValueKind.False => "true/false",
        JsonValueKind.Null => "null",
        _ => Kind.ToString(),
    };
}

public sealed record JProperty(string Name, JNode Value, int Line);

public static class JsonTree
{
    public static readonly JsonReaderOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Разбор с номерами строк. При синтаксической ошибке возвращает null и описание ошибки.</summary>
    public static JNode? Parse(string text, out string? error, out int errorLine)
    {
        error = null;
        errorLine = 0;
        var bytes = Encoding.UTF8.GetBytes(text.TrimStart('﻿'));
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < bytes.Length; i++)
            if (bytes[i] == (byte)'\n')
                lineStarts.Add(i + 1);

        var reader = new Utf8JsonReader(bytes, Options);
        try
        {
            if (!reader.Read())
            {
                error = "Пустой JSON.";
                errorLine = 1;
                return null;
            }
            var root = ReadValue(ref reader, lineStarts);
            if (reader.Read())
            {
                error = "После закрывающей скобки есть лишний текст.";
                errorLine = LineOf(reader.TokenStartIndex, lineStarts);
                return null;
            }
            return root;
        }
        catch (JsonException ex)
        {
            errorLine = (int)(ex.LineNumber ?? 0) + 1;
            error = Explain(ex.Message);
            return null;
        }
    }

    private static JNode ReadValue(ref Utf8JsonReader reader, List<int> lineStarts)
    {
        var line = LineOf(reader.TokenStartIndex, lineStarts);
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var node = new JNode { Kind = JsonValueKind.Object, Line = line };
                while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                {
                    var name = reader.GetString()!;
                    var nameLine = LineOf(reader.TokenStartIndex, lineStarts);
                    reader.Read();
                    node.Properties.Add(new JProperty(name, ReadValue(ref reader, lineStarts), nameLine));
                }
                return node;
            }
            case JsonTokenType.StartArray:
            {
                var node = new JNode { Kind = JsonValueKind.Array, Line = line };
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    node.Items.Add(ReadValue(ref reader, lineStarts));
                return node;
            }
            case JsonTokenType.String:
                return new JNode { Kind = JsonValueKind.String, Line = line, Text = reader.GetString() };
            case JsonTokenType.Number:
                return new JNode { Kind = JsonValueKind.Number, Line = line, Text = Encoding.UTF8.GetString(reader.ValueSpan) };
            case JsonTokenType.True:
                return new JNode { Kind = JsonValueKind.True, Line = line, Bool = true };
            case JsonTokenType.False:
                return new JNode { Kind = JsonValueKind.False, Line = line, Bool = false };
            default:
                return new JNode { Kind = JsonValueKind.Null, Line = line };
        }
    }

    private static int LineOf(long offset, List<int> lineStarts)
    {
        var idx = lineStarts.BinarySearch((int)offset);
        return (idx >= 0 ? idx : ~idx - 1) + 1;
    }

    /// <summary>Сообщения System.Text.Json на человеческом языке для самых частых ошибок.</summary>
    private static string Explain(string message)
    {
        if (message.Contains("is invalid after a value", StringComparison.Ordinal) || message.Contains("Expected either ',', '}', or ']'", StringComparison.Ordinal))
            return "Не хватает запятой между элементами или лишняя/незакрытая скобка.";
        if (message.Contains("is an invalid start of a property name", StringComparison.Ordinal))
            return "Ожидалось имя свойства в двойных кавычках (например \"params\") - проверьте кавычки и лишние запятые.";
        if (message.Contains("is an invalid start of a value", StringComparison.Ordinal))
            return "Некорректное значение: строки должны быть в двойных кавычках (\"Self\", а не Self), true/false - маленькими буквами.";
        if (message.Contains("end of data", StringComparison.OrdinalIgnoreCase) || message.Contains("incomplete", StringComparison.OrdinalIgnoreCase))
            return "JSON обрывается: не закрыта скобка ] или } или кавычка.";
        if (message.Contains("'}' is invalid", StringComparison.Ordinal) || message.Contains("']' is invalid", StringComparison.Ordinal))
            return "Лишняя закрывающая скобка или скобки перепутаны ( ] вместо } ).";
        if (message.Contains("Expected end of string", StringComparison.Ordinal))
            return "Не закрыта кавычка строки.";
        return "Синтаксическая ошибка JSON: " + message;
    }
}
