using Ascon.Pilot.DataClasses;
using ProAdmin.GitLab;
using ProAdmin.Types;

namespace ProAdmin.Tests;

public class TypesRepoTests
{
    private static readonly Guid Issued = Guid.NewGuid();
    private static readonly Guid Done = Guid.NewGuid();
    private static readonly Guid MachineId = Guid.NewGuid();

    private static DMetadata Build()
    {
        var machine = new MUserStateMachine { Id = MachineId, Title = "Task", StateTransitions = new() };
        machine.StateTransitions[Guid.Empty] = new[] { new MTransition { StateTo = Issued, DisplayName = "Выдать", AvailableForPositionsSource = new[] { "1" } } };
        machine.StateTransitions[Issued] = new[] { new MTransition { StateTo = Done, DisplayName = "Завершить" } };
        machine.StateTransitions[Done] = Array.Empty<MTransition>();

        return new DMetadata
        {
            Version = 7,
            Types =
            {
                new()
                {
                    Id = 1, Name = "task", Title = "Задание", Sort = 0, StateMachineId = MachineId, Children = { 2 },
                    Configuration = "<a>\r\n  <b/>\r\n</a>",
                    Attributes =
                    {
                        new() { Name = "title", Title = "Название", Type = MAttrType.String, DisplaySortOrder = 0 },
                        new() { Name = "state", Title = "Состояние", Type = MAttrType.UserState, DisplaySortOrder = 1,
                            Configuration = $"<UserStateRootNode StateMachineId=\"{MachineId}\" />" },
                    },
                },
                new() { Id = 2, Name = "file", Title = "Файл", Sort = 1, HasFiles = true },
                new() { Id = 3, Name = "old", Title = "Старый", Sort = 2, IsDeleted = true },
            },
            UserStates =
            {
                new() { Id = Issued, Name = "issued", Title = "Выдано", Color = UserStateColors.Color2 },
                new() { Id = Done, Name = "done", Title = "Выполнено", IsCompletionState = true },
            },
            StateMachines = { machine },
        };
    }

    /// <summary>База / файлы / разбор: тот же сервер не требует изменений.</summary>
    private static TypesSnapshot RoundTrip(DMetadata m) =>
        TypesSnapshot.Parse(TypesExport.FromMetadata(m, "1.0.0").Render());

    [Fact]
    public void ExportedRepositoryMatchesItsServer()
    {
        var repo = RoundTrip(Build());
        Assert.Empty(repo.ParseErrors);
        Assert.Equal(new[] { "file", "task" }, repo.Types.Keys);
        Assert.Contains("{{machine:Task}}", repo.Types["task"].Attributes[1].Configuration);
        Assert.Equal("<a>\n  <b/>\n</a>", repo.Types["task"].Configuration);
        Assert.Equal("Task", repo.Types["task"].StateMachine);
        Assert.Equal(TypesSnapshot.NoState, repo.Machines[0].States[0].State);

        var plan = TypesPlanner.Plan(repo, Build());
        Assert.Empty(plan.Errors);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void SameRepositoryAppliesToServerWithOtherIds()
    {
        var repo = RoundTrip(Build());

        // Другой сервер: те же имена, другие Id состояний и группы, типа «task» нет.
        var other = Build();
        other.Types.RemoveAll(t => t.Name == "task");
        foreach (var s in other.UserStates)
            s.Id = Guid.NewGuid();
        other.StateMachines.Clear();

        var plan = TypesPlanner.Plan(repo, other);
        Assert.Empty(plan.Errors);
        var task = Assert.Single(other.Types, t => t.Name == "task");
        Assert.Equal(4, task.Id); // max + 1, с учётом удалённого
        var machine = Assert.Single(other.StateMachines);
        Assert.Equal(MachineId, machine.Id); // Id из репозитория свободен - используется он
        Assert.Equal(machine.Id, task.StateMachineId);
        Assert.Contains(machine.Id.ToString(), task.Attributes[1].Configuration);
        Assert.Equal(new[] { 2 }, task.Children);
        var issued = other.UserStates.Single(s => s.Name == "issued").Id;
        Assert.Equal(issued, machine.StateTransitions[Guid.Empty][0].StateTo);
        Assert.Contains(plan.Changes, c => c.Kind == "тип" && c.Action == TypesChangeAction.Create);

        // После записи ветка и сервер совпадают.
        Assert.Empty(TypesPlanner.Plan(RoundTrip(Build()), other).Changes);
    }

    [Fact]
    public void AttributeChangesAreDetailedAndDangerous()
    {
        var repo = RoundTrip(Build());
        var task = repo.Types["task"];
        task.Attributes.RemoveAll(a => a.Name == "title");
        task.Attributes.Add(new AttributeDoc { Name = "due", Title = "Срок", Type = MAttrType.DateTime, DisplaySortOrder = 2 });
        task.Attributes[0].Title = "Статус";

        var server = Build();
        var plan = TypesPlanner.Plan(repo, server);
        var change = Assert.Single(plan.Changes);
        Assert.True(change.Danger);
        Assert.Contains(change.Details, d => d.StartsWith("+ атрибут due"));
        Assert.Contains(change.Details, d => d.StartsWith("− атрибут title"));
        Assert.Contains(change.Details, d => d.StartsWith("~ атрибут state"));
        Assert.Equal(new[] { "state", "due" }, server.Types[0].Attributes.Select(a => a.Name));
        // Конфигурация неизменённого атрибута осталась байт в байт.
        Assert.Equal($"<UserStateRootNode StateMachineId=\"{MachineId}\" />", server.Types[0].Attributes[0].Configuration);
    }

    [Fact]
    public void RevertRestoresTouchedAndTombstonesCreated()
    {
        var repo = RoundTrip(Build());
        repo.Types["task"].Title = "Задача";
        repo.Types["new_type"] = new TypeDoc { Name = "new_type", Title = "Новый", Sort = 3 };
        repo.States["review"] = new StateDoc { Name = "review", Title = "На проверке" };

        var server = Build();
        var plan = TypesPlanner.Plan(repo, server);
        Assert.Empty(plan.Errors);
        Assert.Equal(3, plan.Changes.Count);

        var after = server; // то, что записано
        TypesPlanner.Revert(plan, after);
        Assert.Equal("Задание", after.Types.Single(t => t.Name == "task").Title);
        Assert.True(after.Types.Single(t => t.Name == "new_type").IsDeleted);
        Assert.DoesNotContain(after.UserStates, s => s.Name == "review");
    }

    [Fact]
    public void ValidatorFindsBrokenReferences()
    {
        var repo = RoundTrip(Build());
        repo.Types["task"].Children.Add("missing");
        repo.Types["task"].StateMachine = "Нет такой";
        repo.Machines[0].States[1].Transitions.Add(new TransitionDoc { To = "ghost" });

        var v = TypesValidator.Validate(repo);
        Assert.Equal(3, v.Errors.Count);
        var plan = TypesPlanner.Plan(repo, Build());
        Assert.False(plan.CanApply);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void NewTypeUsesAFreeOrder()
    {
        Assert.Equal(0, TypesValidator.NextTypeOrder(Array.Empty<int>()));
        Assert.Equal(8, TypesValidator.NextTypeOrder(new[] { 0, 3, 7 }));
        Assert.Equal(1, TypesValidator.NextTypeOrder(new[] { 0, int.MaxValue }));
    }

    [Fact]
    public void ValidatorReportsTypesSharingAnOrder()
    {
        var repo = RoundTrip(Build());
        repo.Types["file"].Sort = repo.Types["task"].Sort;

        var error = Assert.Single(TypesValidator.Validate(repo).Errors);
        Assert.Equal("Порядок 0 занят несколькими типами: «file», «task».", error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlanRejectsOrderAlreadyTakenOnlyOnPilot(bool deleted)
    {
        var repo = RoundTrip(Build());
        repo.Types["new_type"] = new TypeDoc { Name = "new_type", Title = "Новый", Sort = 3 };
        var server = Build();
        server.Types.Add(new MType { Id = 4, Name = "local", Title = "Локальный", Sort = 3, IsDeleted = deleted });

        Assert.True(TypesValidator.Validate(repo).Ok);
        var plan = TypesPlanner.Plan(repo, server);
        Assert.False(plan.CanApply);
        Assert.Contains("Порядок 3 занят несколькими типами", Assert.Single(plan.Errors));
    }

    [Fact]
    public void DeletingMachineUsedByTypeIsAnError()
    {
        var repo = RoundTrip(Build());
        repo.Machines[0].IsDeleted = true;
        Assert.Contains(TypesValidator.Validate(repo).Errors, e => e.Contains("используют типы: task"));
    }

    [Fact]
    public void PendingCommitsSkipCherryPicked()
    {
        var a = new GitLabCommit { Id = new string('a', 40), Title = "KTN-1: типы" };
        var picked = new GitLabCommit { Id = new string('c', 40), Message = TypesRepository.PickMessage(a) };
        Assert.Equal(new[] { a.Id }, TypesRepository.PickedFrom(picked.Message));
        Assert.True(TypesRepository.IsServiceCommit(new GitLabCommit { Title = TypesRepository.ReleaseCommitPrefix + "1.2.0: config.json" }));

        // Повторный перенос main / release: пометки накапливаются.
        var twice = TypesRepository.PickMessage(new GitLabCommit { Id = picked.Id, Message = picked.Message });
        Assert.Equal(new[] { a.Id, picked.Id }, TypesRepository.PickedFrom(twice));
    }

    [Fact]
    public void DiffActionsKeepExistingFilePaths()
    {
        var files = TypesExport.FromMetadata(Build(), "1.0.0").Render();
        var text = files["types/task.json"];
        files.Remove("types/task.json");
        files["types/Task-renamed.json"] = text; // файл назван не по имени типа
        var repo = TypesSnapshot.Parse(files);

        var server = TypesExport.FromMetadata(Build());
        server.Types["task"].Title = "Задача";
        var actions = TypesRepository.DiffActions(repo, server, includeDeletes: false);
        var action = Assert.Single(actions);
        Assert.Equal("types/Task-renamed.json", action.FilePath);
        Assert.Equal("update", action.Action);
    }
}
