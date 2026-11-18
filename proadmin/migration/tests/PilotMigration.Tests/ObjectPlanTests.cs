using PilotMigration.Engine;
using PilotMigration.Objects;
using PilotMigration.Scheduling;

namespace PilotMigration.Tests;

public class ObjectPlanTests
{
    private const int Folder = 2;
    private const int Doc = 3;
    private const int Report = 28;
    private const int Extension = 26;

    private static ObjectCategory? Category(int type) => type switch
    {
        Report => ObjectCategory.Reports,
        Extension => ObjectCategory.ClientModules,
        _ => null,
    };

    private static readonly Guid Root = G(1);
    private static Guid G(int n) => new($"00000000-0000-0000-0000-{n:D12}");

    private static ObjectIndex Index(params (int Id, int Parent, int Type)[] items) =>
        new(items.Select(i => new ObjectHeader(G(i.Id), i.Type, i.Parent == 0 ? Guid.Empty : G(i.Parent), 0,
            items.Where(c => c.Parent == i.Id).Select(c => G(c.Id)).ToList())));

    [Fact]
    public void IdsBringAncestorsAndDescendants()
    {
        // Источник: 1 / 2 / 3 / 4; приёмник: только 1.
        var s = Index((1, 0, Folder), (2, 1, Folder), (3, 2, Folder), (4, 3, Doc), (5, 1, Doc));
        var t = Index((1, 0, Folder));
        var plan = ObjectPlan.Build(s, t, Category, Category, new ObjectSelectionRequest { TreeIds = new[] { G(3) } });

        Assert.Equal(new[] { G(2), G(3), G(4) }.ToHashSet(), plan.Copy);
        Assert.Contains(G(2), plan.AddedAncestors);
        Assert.Contains(G(1), plan.Touch);
        Assert.Empty(plan.Delete);

        // У корня приёмника появится ребёнок 2, у 2 (из источника) не будет лишних детей.
        var rootEdit = plan.ChildrenEdit(G(1), Array.Empty<Guid>());
        Assert.NotNull(rootEdit);
        Assert.Equal(new[] { G(2) }, rootEdit!.Value.Add.Select(a => a.Id));
        Assert.Null(plan.ChildrenEdit(G(2), new[] { G(3) }));
    }

    [Fact]
    public void IdsReplaceSubtreeAndDropExtraChildren()
    {
        var s = Index((1, 0, Folder), (2, 1, Folder), (3, 2, Doc));
        var t = Index((1, 0, Folder), (2, 1, Folder), (3, 2, Doc), (9, 2, Doc));
        var plan = ObjectPlan.Build(s, t, Category, Category, new ObjectSelectionRequest { TreeIds = new[] { G(2) } });

        Assert.Equal(new[] { G(2), G(3) }.ToHashSet(), plan.Copy);
        Assert.Equal(new[] { G(9) }.ToHashSet(), plan.Delete);
        Assert.Empty(plan.AddedAncestors);
    }

    [Fact]
    public void CategoryReplacementKeepsOtherObjects()
    {
        // Отчёты: в источнике 10, 11; в приёмнике 10 и лишний 12. Документ 5 - не отчёт, не трогается.
        var s = Index((1, 0, Folder), (10, 1, Report), (11, 1, Report));
        var t = Index((1, 0, Folder), (5, 1, Doc), (10, 1, Report), (12, 1, Report));
        var plan = ObjectPlan.Build(s, t, Category, Category, new ObjectSelectionRequest { Categories = new[] { ObjectCategory.Reports } });

        Assert.Equal(new[] { G(10), G(11) }.ToHashSet(), plan.Copy);
        Assert.Equal(new[] { G(12) }.ToHashSet(), plan.Delete);
        Assert.Contains(G(1), plan.Touch);

        var edit = plan.ChildrenEdit(G(1), new[] { G(5), G(10), G(12) })!.Value;
        Assert.True(edit.Keep(G(5)));
        Assert.True(edit.Keep(G(10)));
        Assert.False(edit.Keep(G(12)));
        Assert.Equal(new[] { G(11) }, edit.Add.Select(a => a.Id));
    }

    [Fact]
    public void TreeAllLeavesCategoriesAlone()
    {
        var s = Index((1, 0, Folder), (2, 1, Doc), (10, 1, Report));
        var t = Index((1, 0, Folder), (3, 1, Doc), (11, 1, Report), (20, 1, Extension));
        var plan = ObjectPlan.Build(s, t, Category, Category, new ObjectSelectionRequest { TreeAll = true });

        Assert.Equal(new[] { G(1), G(2) }.ToHashSet(), plan.Copy);
        Assert.Equal(new[] { G(3) }.ToHashSet(), plan.Delete);

        // Корень берётся из источника: ребёнок-отчёт 10 источника в приёмник не попадает, отчёт и модуль приёмника остаются.
        var edit = plan.ChildrenEdit(G(1), new[] { G(2), G(10) })!.Value;
        Assert.True(edit.Keep(G(2)));
        Assert.False(edit.Keep(G(10)));
        Assert.Equal(new[] { G(11), G(20) }.ToHashSet(), edit.Add.Select(a => a.Id).ToHashSet());
    }

    [Fact]
    public void MovedObjectLeavesOldParent()
    {
        // В приёмнике 3 лежит в 2, в источнике - в 4.
        var s = Index((1, 0, Folder), (2, 1, Folder), (4, 1, Folder), (3, 4, Doc));
        var t = Index((1, 0, Folder), (2, 1, Folder), (4, 1, Folder), (3, 2, Doc));
        var plan = ObjectPlan.Build(s, t, Category, Category, new ObjectSelectionRequest { TreeIds = new[] { G(3) } });

        Assert.Contains(G(2), plan.Touch);
        Assert.Contains(G(4), plan.Touch);
        Assert.False(plan.ChildrenEdit(G(2), new[] { G(3) })!.Value.Keep(G(3)));
        Assert.Equal(new[] { G(3) }, plan.ChildrenEdit(G(4), Array.Empty<Guid>())!.Value.Add.Select(a => a.Id));
    }

    [Fact]
    public void CategoriesByTypeName()
    {
        Assert.Equal(ObjectCategory.Reports, ObjectCategories.ByTypeName("Report_6088AF81-061E-456E-9225-CF65B7B25368"));
        Assert.Equal(ObjectCategory.Reports, ObjectCategories.ByTypeName("Report_Folder_F2CC6F1D-70E1-4E9B-B32F-BEB3E991318F"));
        Assert.Equal(ObjectCategory.ClientModules, ObjectCategories.ByTypeName("Extension"));
        Assert.Null(ObjectCategories.ByTypeName("Extension_folder"));
        Assert.Equal(ObjectCategory.WebModules, ObjectCategories.ByTypeName("WebExtension"));
        Assert.Equal(ObjectCategory.ServerModules, ObjectCategories.ByTypeName("$PilotServerExtension"));
        Assert.Null(ObjectCategories.ByTypeName("Report_card"));
    }

    [Fact]
    public void DirectionRules()
    {
        Assert.Null(MigrationContext.DirectionError("prod", "test"));
        Assert.Null(MigrationContext.DirectionError("prod", "stage"));
        Assert.Null(MigrationContext.DirectionError("test", "stage"));
        Assert.Null(MigrationContext.DirectionError("stage", "test"));
        Assert.NotNull(MigrationContext.DirectionError("test", "prod"));
        Assert.NotNull(MigrationContext.DirectionError("test", "test"));
    }

    [Fact]
    public void SameDatabaseIdBlocks()
    {
        var a = Guid.NewGuid();
        Assert.NotNull(MigrationContext.SameDatabaseIdError("prod", "stage", a, a));
        Assert.Null(MigrationContext.SameDatabaseIdError("prod", "stage", a, Guid.NewGuid()));
        Assert.Null(MigrationContext.SameDatabaseIdError("prod", "stage", Guid.Empty, Guid.Empty));
    }

    [Fact]
    public void WeeklySchedule()
    {
        var s = new ScheduleEntry { Name = "x", Kind = ScheduleKind.Weekly, TimeOfDay = "03:00", Days = new() { 1 } };
        // 2026-09-27 - воскресенье. Время расписания - местное, поэтому ожидание не зависит от часового пояса машины.
        var now = new DateTimeOffset(new DateTime(2026, 9, 27, 12, 0, 0, DateTimeKind.Local));
        var next = s.ComputeNext(now)!.Value;
        Assert.Equal(new DateTimeOffset(new DateTime(2026, 9, 28, 3, 0, 0, DateTimeKind.Local)), next);
    }
}
