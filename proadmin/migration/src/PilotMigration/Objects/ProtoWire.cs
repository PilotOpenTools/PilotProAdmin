namespace PilotMigration.Objects;

/// <summary>
/// Разбор и точечная правка protobuf-сообщения DObject на уровне байтов. Объект целиком не пересериализуется,
/// поэтому поля, которых нет в нашей версии Ascon.Pilot.DataClasses, сохраняются как есть.
/// Номера полей - из контракта protobuf-net DObject / DChild (проверено на сервере 26.13).
/// </summary>
public static class ProtoWire
{
    public const int ObjectId = 1;
    public const int ObjectTypeId = 2;
    public const int ObjectParentId = 3;
    public const int ObjectLastChange = 5;
    public const int ObjectChildren = 21;

    public const int ChildTypeId = 1;
    public const int ChildObjectId = 2;

    private const int WireVarint = 0;
    private const int WireFixed64 = 1;
    private const int WireLen = 2;
    private const int WireFixed32 = 5;

    /// <summary>Поле верхнего уровня: номер, тип, границы всего поля (с тегом) и значения.</summary>
    public readonly record struct Field(int Number, int WireType, int Start, int End, int ValueStart, int ValueEnd)
    {
        public ReadOnlySpan<byte> Value(ReadOnlySpan<byte> data) => data[ValueStart..ValueEnd];
        public ReadOnlySpan<byte> Raw(ReadOnlySpan<byte> data) => data[Start..End];
    }

    public static List<Field> ReadFields(ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        var pos = 0;
        while (pos < data.Length)
        {
            var start = pos;
            var tag = ReadVarint(data, ref pos);
            var number = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            int valueStart, valueEnd;
            switch (wire)
            {
                case WireVarint:
                    valueStart = pos;
                    ReadVarint(data, ref pos);
                    valueEnd = pos;
                    break;
                case WireFixed64:
                    valueStart = pos;
                    pos += 8;
                    valueEnd = pos;
                    break;
                case WireLen:
                    var declared = ReadVarint(data, ref pos);
                    if (declared > (ulong)(data.Length - pos))
                        throw new FormatException("Сообщение protobuf обрезано: длина поля больше остатка данных.");
                    var length = (int)declared;
                    valueStart = pos;
                    pos += length;
                    valueEnd = pos;
                    break;
                case WireFixed32:
                    valueStart = pos;
                    pos += 4;
                    valueEnd = pos;
                    break;
                default:
                    throw new FormatException($"Неподдерживаемый тип поля protobuf {wire} (поле {number}) в позиции {start}.");
            }
            if (pos > data.Length)
                throw new FormatException("Сообщение protobuf обрезано.");
            fields.Add(new Field(number, wire, start, pos, valueStart, valueEnd));
        }
        return fields;
    }

    public static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong result = 0;
        var shift = 0;
        while (true)
        {
            if (pos >= data.Length)
                throw new FormatException("Varint обрезан.");
            var b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift > 63)
                throw new FormatException("Слишком длинный varint.");
        }
    }

    public static void WriteVarint(Stream stream, ulong value)
    {
        while (value >= 0x80)
        {
            stream.WriteByte((byte)(value | 0x80));
            value >>= 7;
        }
        stream.WriteByte((byte)value);
    }

    private static void WriteTag(Stream stream, int number, int wire) => WriteVarint(stream, (ulong)((number << 3) | wire));

    /// <summary>Guid в формате protobuf-net (bcl.Guid: fixed64 lo = 1, fixed64 hi = 2).</summary>
    public static Guid ReadBclGuid(ReadOnlySpan<byte> message)
    {
        Span<byte> bytes = stackalloc byte[16];
        foreach (var f in ReadFields(message))
        {
            if (f.WireType != WireFixed64)
                continue;
            if (f.Number == 1)
                f.Value(message).CopyTo(bytes[..8]);
            else if (f.Number == 2)
                f.Value(message).CopyTo(bytes[8..]);
        }
        return new Guid(bytes);
    }

    public static byte[] BclGuid(Guid id)
    {
        if (id == Guid.Empty)
            return Array.Empty<byte>();
        var bytes = id.ToByteArray();
        using var ms = new MemoryStream(18);
        WriteTag(ms, 1, WireFixed64);
        ms.Write(bytes, 0, 8);
        WriteTag(ms, 2, WireFixed64);
        ms.Write(bytes, 8, 8);
        return ms.ToArray();
    }

    public static byte[] EncodeChild(Guid id, int typeId)
    {
        using var ms = new MemoryStream(24);
        if (typeId != 0)
        {
            WriteTag(ms, ChildTypeId, WireVarint);
            WriteVarint(ms, unchecked((ulong)(long)typeId));
        }
        var guid = BclGuid(id);
        WriteTag(ms, ChildObjectId, WireLen);
        WriteVarint(ms, (ulong)guid.Length);
        ms.Write(guid);
        return ms.ToArray();
    }

    public static Guid ReadChildId(ReadOnlySpan<byte> childMessage)
    {
        foreach (var f in ReadFields(childMessage))
        {
            if (f.Number == ChildObjectId && f.WireType == WireLen)
                return ReadBclGuid(f.Value(childMessage));
        }
        return Guid.Empty;
    }

    /// <summary>
    /// Меняет список детей: оставляет записи, для которых <paramref name="keep"/> вернул true (в прежнем порядке),
    /// и дописывает <paramref name="add"/>. null - список не изменился.
    /// </summary>
    public static byte[]? EditChildren(byte[] data, Func<Guid, bool> keep, IReadOnlyCollection<(Guid Id, int TypeId)> add)
    {
        var fields = ReadFields(data);
        var removed = 0;
        foreach (var f in fields)
        {
            if (f.Number == ObjectChildren && f.WireType == WireLen && !keep(ReadChildId(f.Value(data))))
                removed++;
        }
        if (removed == 0 && add.Count == 0)
            return null;

        using var ms = new MemoryStream(data.Length + add.Count * 26);
        var lastChildEnd = -1;
        foreach (var f in fields)
        {
            if (f.Number == ObjectChildren && f.WireType == WireLen)
            {
                if (keep(ReadChildId(f.Value(data))))
                    ms.Write(f.Raw(data));
                lastChildEnd = (int)ms.Length;
                continue;
            }
            ms.Write(f.Raw(data));
        }

        // Новые дети - сразу за существующими (или в конец сообщения, если детей не было).
        var tail = Array.Empty<byte>();
        if (lastChildEnd >= 0 && lastChildEnd < ms.Length)
        {
            tail = ms.ToArray()[lastChildEnd..];
            ms.SetLength(lastChildEnd);
        }
        foreach (var (id, type) in add)
        {
            var child = EncodeChild(id, type);
            WriteTag(ms, ObjectChildren, WireLen);
            WriteVarint(ms, (ulong)child.Length);
            ms.Write(child);
        }
        ms.Write(tail);
        return ms.ToArray();
    }

    /// <summary>Устанавливает DObject.LastChange (поле 5).</summary>
    public static byte[] SetLastChange(byte[] data, long value)
    {
        using var ms = new MemoryStream(data.Length + 11);
        var written = false;
        foreach (var f in ReadFields(data))
        {
            if (f.Number == ObjectLastChange && f.WireType == WireVarint)
            {
                if (!written && value != 0)
                {
                    WriteTag(ms, ObjectLastChange, WireVarint);
                    WriteVarint(ms, unchecked((ulong)value));
                }
                written = true;
                continue;
            }
            ms.Write(f.Raw(data));
        }
        if (!written && value != 0)
        {
            WriteTag(ms, ObjectLastChange, WireVarint);
            WriteVarint(ms, unchecked((ulong)value));
        }
        return ms.ToArray();
    }
}

/// <summary>Минимум сведений об объекте для построения дерева.</summary>
public readonly record struct ObjectHeader(Guid Id, int TypeId, Guid ParentId, long LastChange, IReadOnlyList<Guid> Children)
{
    public static ObjectHeader Parse(byte[] data)
    {
        Guid id = Guid.Empty, parent = Guid.Empty;
        int type = 0;
        long lastChange = 0;
        List<Guid>? children = null;
        var span = (ReadOnlySpan<byte>)data;
        foreach (var f in ProtoWire.ReadFields(span))
        {
            switch (f.Number)
            {
                case ProtoWire.ObjectId when f.WireType == 2:
                    id = ProtoWire.ReadBclGuid(f.Value(span));
                    break;
                case ProtoWire.ObjectTypeId when f.WireType == 0:
                {
                    var p = f.ValueStart;
                    type = unchecked((int)(long)ProtoWire.ReadVarint(span, ref p));
                    break;
                }
                case ProtoWire.ObjectParentId when f.WireType == 2:
                    parent = ProtoWire.ReadBclGuid(f.Value(span));
                    break;
                case ProtoWire.ObjectLastChange when f.WireType == 0:
                {
                    var p = f.ValueStart;
                    lastChange = unchecked((long)ProtoWire.ReadVarint(span, ref p));
                    break;
                }
                case ProtoWire.ObjectChildren when f.WireType == 2:
                    (children ??= new List<Guid>()).Add(ProtoWire.ReadChildId(f.Value(span)));
                    break;
            }
        }
        return new ObjectHeader(id, type, parent, lastChange, (IReadOnlyList<Guid>?)children ?? Array.Empty<Guid>());
    }
}
