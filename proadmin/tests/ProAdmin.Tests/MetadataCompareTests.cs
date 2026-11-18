using Ascon.Pilot.DataClasses;
using ProAdmin.Compare;
using ProAdmin.Types;

namespace ProAdmin.Tests;

public class MetadataCompareTests
{
    private static readonly Guid Issued = Guid.NewGuid();
    private static readonly Guid Done = Guid.NewGuid();
    private static readonly Guid MachineId = Guid.NewGuid();

    private static DMetadata Build(Action<DMetadata>? change = null)
    {
        var machine = new MUserStateMachine { Id = MachineId, Title = "Task", StateTransitions = new() };
        machine.StateTransitions[Guid.Empty] = new[] { new MTransition { StateTo = Issued, DisplayName = "Выдать" } };
        machine.StateTransitions[Issued] = new[] { new MTransition { StateTo = Done, DisplayName = "Завершить" } };
        machine.StateTransitions[Done] = Array.Empty<MTransition>();

        var m = new DMetadata
        {
            Types =
            {
                new()
                {
                    Id = 1, Name = "task", Title = "Задание", StateMachineId = MachineId,
                    Attributes =
                    {
                        new() { Name = "title", Title = "Название", Type = MAttrType.String, DisplaySortOrder = 0 },
                        new() { Name = "state", Title = "Состояние", Type = MAttrType.UserState, DisplaySortOrder = 1,
                            Configuration = $"<UserStateConfiguration><StateMachineId>{MachineId}</StateMachineId></UserStateConfiguration>" },
                    },
                },
            },
            UserStates =
            {
                new() { Id = Issued, Name = "issued", Title = "Выдано" },
                new() { Id = Done, Name = "done", Title = "Выполнено" },
            },
            StateMachines = { machine },
        };
        change?.Invoke(m);
        return m;
    }

    [Fact]
    public void IdenticalMetadataHasNoDifferences()
    {
        var r = MetadataCompare.Compare(Build(), Build());
        Assert.All(r.Types.Concat(r.States).Concat(r.StateMachines), x => Assert.Equal(CompareState.Same, x.State));
    }

    [Fact]
    public void DetectsAttributeChangesAndMissingAttributes()
    {
        var b = Build(m =>
        {
            var t = m.Types[0];
            t.Attributes[0].Title = "Имя";
            t.Attributes[0].Obligatory = true;
            t.Attributes.Add(new MAttribute { Name = "extra", Title = "Доп", Type = MAttrType.Integer, DisplaySortOrder = 2 });
        });
        var type = Assert.Single(MetadataCompare.Compare(Build(), b).Types);
        Assert.Equal(CompareState.Differs, type.State);
        Assert.Empty(type.Differences);
        var title = type.Children.Single(c => c.Key == "title");
        Assert.Equal(new[] { "Заголовок", "Обязательный" }, title.Differences.Select(d => d.Property));
        Assert.Equal(CompareState.OnlyB, type.Children.Single(c => c.Key == "extra").State);
        Assert.Equal(CompareState.Same, type.Children.Single(c => c.Key == "state").State);
    }

    [Fact]
    public void MatchesByNameWhenIdsDiffer()
    {
        // На другом сервере те же состояния и группа, но с другими Id: конфигурация атрибута и переходы совпадают по именам.
        var ids = new Dictionary<Guid, Guid> { [Issued] = Guid.NewGuid(), [Done] = Guid.NewGuid(), [MachineId] = Guid.NewGuid() };
        var b = Build(m =>
        {
            foreach (var s in m.UserStates) s.Id = ids[s.Id];
            var old = m.StateMachines[0];
            var machine = new MUserStateMachine { Id = ids[MachineId], Title = old.Title, StateTransitions = new() };
            foreach (var pair in old.StateTransitions)
                machine.StateTransitions[pair.Key == Guid.Empty ? Guid.Empty : ids[pair.Key]] =
                    pair.Value.Select(t => new MTransition { StateTo = ids[t.StateTo], DisplayName = t.DisplayName }).ToArray();
            m.StateMachines[0] = machine;
            m.Types[0].StateMachineId = machine.Id;
            m.Types[0].Attributes[1].Configuration = m.Types[0].Attributes[1].Configuration.Replace(MachineId.ToString(), machine.Id.ToString());
        });
        var r = MetadataCompare.Compare(Build(), b);
        Assert.Equal(CompareState.Same, r.Types[0].State);
        var machineRow = Assert.Single(r.StateMachines);
        Assert.Equal(new[] { "Id" }, machineRow.Differences.Select(d => d.Property));
        Assert.All(machineRow.Children, c => Assert.Equal(CompareState.Same, c.State));
        Assert.Contains("task", machineRow.UsedBy);
    }

    [Fact]
    public void DetectsTransitionAndStateChanges()
    {
        var b = Build(m =>
        {
            m.UserStates[1].Color = UserStateColors.Color3;
            m.StateMachines[0].StateTransitions[Done] = new[] { new MTransition { StateTo = Issued, DisplayName = "Вернуть" } };
            m.StateMachines[0].StateTransitions[Issued] = new[] { new MTransition { StateTo = Done, DisplayName = "Готово" } };
        });
        var r = MetadataCompare.Compare(Build(), b);
        Assert.Equal(new[] { "Цвет" }, r.States.Single(s => s.Key == "done").Differences.Select(d => d.Property));
        var machine = Assert.Single(r.StateMachines);
        Assert.Equal(CompareState.Differs, machine.State);
        Assert.Equal(CompareState.OnlyB, machine.Children.Single(c => c.Key == "done / issued").State);
        Assert.Equal("Готово", machine.Children.Single(c => c.Key == "issued / done").Differences.Single().ValueB);
        Assert.Equal(CompareState.Same, machine.Children.Single(c => c.Key == "<Нет> / issued").State);
    }
}
