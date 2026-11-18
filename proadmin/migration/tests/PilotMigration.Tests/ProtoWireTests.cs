using Ascon.Pilot.DataClasses;
using PilotMigration.Archives;
using PilotMigration.Objects;

namespace PilotMigration.Tests;

public class ProtoWireTests
{
    private static byte[] Serialize(DObject o)
    {
        using var ms = new MemoryStream();
        ProtoBuf.Serializer.Serialize(ms, o);
        return ms.ToArray();
    }

    private static DObject Deserialize(byte[] data) => ProtoBuf.Serializer.Deserialize<DObject>(new MemoryStream(data));

    private static DObject Sample(params Guid[] children)
    {
        var o = new DObject { Id = Guid.NewGuid(), TypeId = 7, ParentId = Guid.NewGuid(), LastChange = 42, CreatorId = 3 };
        o.Attributes["name"] = new DValue { StrValue = "Папка" };
        foreach (var c in children)
            o.Children.Add(new DChild { ObjectId = c, TypeId = 5 });
        return o;
    }

    [Fact]
    public void HeaderMatchesProtobufNet()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var o = Sample(a, b);
        var h = ObjectHeader.Parse(Serialize(o));
        Assert.Equal(o.Id, h.Id);
        Assert.Equal(7, h.TypeId);
        Assert.Equal(o.ParentId, h.ParentId);
        Assert.Equal(42, h.LastChange);
        Assert.Equal(new[] { a, b }, h.Children);
    }

    [Fact]
    public void EditChildrenKeepsOtherFieldsAndOrder()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();
        var n = Guid.NewGuid();
        var o = Sample(a, b, c);

        var edited = ProtoWire.EditChildren(Serialize(o), id => id != b, new[] { (n, 9) });
        Assert.NotNull(edited);

        var back = Deserialize(edited!);
        Assert.Equal(new[] { a, c, n }, back.Children.Select(x => x.ObjectId));
        Assert.Equal(9, back.Children[2].TypeId);
        Assert.Equal(o.Id, back.Id);
        Assert.Equal(o.ParentId, back.ParentId);
        Assert.Equal("Папка", back.Attributes["name"].StrValue);
        Assert.Equal(42, back.LastChange);
    }

    [Fact]
    public void EditChildrenWithoutChangesReturnsNull()
    {
        var o = Sample(Guid.NewGuid());
        Assert.Null(ProtoWire.EditChildren(Serialize(o), _ => true, Array.Empty<(Guid, int)>()));
    }

    [Fact]
    public void AddChildToObjectWithoutChildren()
    {
        var o = Sample();
        var n = Guid.NewGuid();
        var back = Deserialize(ProtoWire.EditChildren(Serialize(o), _ => true, new[] { (n, 3) })!);
        Assert.Single(back.Children);
        Assert.Equal(n, back.Children[0].ObjectId);
        Assert.Equal("Папка", back.Attributes["name"].StrValue);
    }

    [Fact]
    public void SetLastChange()
    {
        var o = Sample();
        var back = Deserialize(ProtoWire.SetLastChange(Serialize(o), 7));
        Assert.Equal(7, back.LastChange);
        Assert.Equal(o.Id, back.Id);
    }

    [Fact]
    public void ArchivePathLikePilot()
    {
        // Реальное тело из архива теста: 103/182/bfcfe351-…
        Assert.Equal("103/182/bfcfe351-b5fd-43dc-92af-1d003c7d67b6",
            ArchiveLayout.RelativePath(Guid.Parse("bfcfe351-b5fd-43dc-92af-1d003c7d67b6")));
        Assert.Equal("14/137/3eb59166-71a1-4f95-9fa9-b6135a7f0e89",
            ArchiveLayout.RelativePath(Guid.Parse("3eb59166-71a1-4f95-9fa9-b6135a7f0e89")));
    }
}
