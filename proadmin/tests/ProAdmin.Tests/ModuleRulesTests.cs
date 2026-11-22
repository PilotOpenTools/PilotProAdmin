using System.IO.Compression;
using ProAdmin.Automations;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Settings;

namespace ProAdmin.Tests;

public class ModuleRulesTests
{
    [Theory]
    [InlineData("1.4.0", 1, 4, 0)]
    [InlineData("v2.10.3", 2, 10, 3)]
    [InlineData("1.2", 1, 2, 0)]
    [InlineData("1.2.3.0", 1, 2, 3)]
    public void Version_parses(string text, int major, int minor, int patch)
    {
        Assert.True(ModuleVersion.TryParse(text, out var v));
        Assert.Equal(new ModuleVersion(major, minor, patch), v);
    }

    [Fact]
    public void Version_bumps_and_compares()
    {
        var v = new ModuleVersion(1, 4, 2);
        Assert.Equal("1.5.0", v.NextMinor().ToString());
        Assert.Equal("1.4.3", v.NextPatch().ToString());
        Assert.Equal("1.4.2", v.Assembly); // версия модуля - три числа
        Assert.Equal("2.1.0", ModuleVersion.Display("2.1.0.0"));
        Assert.Equal("2.1.0", ModuleVersion.Display("2.1.0"));
        Assert.Equal("2.1.0.5", ModuleVersion.Display("2.1.0.5")); // ненулевая 4-я часть - другая сборка, видна
        Assert.Equal("2.0", ModuleVersion.Display("2.0"));
        Assert.True(new ModuleVersion(1, 10, 0) > new ModuleVersion(1, 9, 9));
        Assert.False(ModuleVersion.TryParse("release", out _));
    }

    [Fact]
    public void Branch_names_and_tasks()
    {
        Assert.Equal(new ModuleVersion(1, 4, 0), ModuleBranches.ReleaseVersion("release/1.4.0"));
        Assert.Null(ModuleBranches.ReleaseVersion("release-prep/1.4.0"));
        Assert.True(ModuleBranches.IsTaskBranch("feature/KTN-1234"));
        Assert.True(ModuleBranches.IsTaskBranch("bugfix/KTN-1"));
        Assert.False(ModuleBranches.IsTaskBranch("hotfix/KTN-1"));
        Assert.StartsWith("feature/KTN-1234-", ModuleBranches.TaskBranch(ModuleBranches.FeaturePrefix, "ktn-1234", "Экспорт в Storadge"));
        Assert.Equal("feature/KTN-1234", ModuleBranches.TaskBranch(ModuleBranches.FeaturePrefix, "KTN-1234"));
        Assert.Equal("feature/export-pdf", ModuleBranches.TaskBranch(ModuleBranches.FeaturePrefix, null, "Export PDF"));
        Assert.Equal("KTN-1234", ModuleBranches.ParseTask(" ktn-1234 ", @"KTN-\d+"));
        Assert.Equal("KTN-1234", ModuleBranches.ParseTask("ЛЕТ-1234", @"KTN-\d+")); // русская раскладка
        Assert.Equal("KTN-1234", ModuleBranches.ParseTask("1234", @"KTN-\d+"));
        Assert.Equal("KTN-1234", ModuleBranches.ParseTask("KTN 1234", @"KTN-\d+"));
        Assert.Null(ModuleBranches.ParseTask("экспорт", @"KTN-\d+"));
        Assert.Null(ModuleBranches.ParseTask("", @"KTN-\d+"));
        Assert.Equal("KTN-1357", ModuleBranches.Task("feature/ktn-1357", @"KTN-\d+"));
        Assert.Null(ModuleBranches.Task("feature/without-task", @"KTN-\d+"));
        Assert.Equal("https://k.ru/card/1234", ModuleBranches.TaskUrl("KTN-1234", "https://k.ru/card/{number}"));
        Assert.Null(ModuleBranches.TaskUrl("KTN-1234", ""));
    }

    [Fact]
    public void Tags_rc_and_prod_releases()
    {
        var v = new ModuleVersion(1, 4, 0);
        Assert.Equal("v1.4.0", ModuleBranches.ProdTag(v));
        Assert.Equal(3, ModuleBranches.NextRc(v, new[] { "v1.4.0-rc.1", "v1.4.0-rc.2", "v1.3.0-rc.7", "v1.4.0" }));
        Assert.Equal(1, ModuleBranches.NextRc(v, Array.Empty<string>()));

        var tags = new[] { "v1.3.0", "v1.4.0-rc.1", "v1.10.0", "v1.4.0", "vX" }.Select(t => new GitLabTag { Name = t });
        Assert.Equal(new[] { "v1.10.0", "v1.4.0", "v1.3.0" }, ModuleBranches.ProdReleases(tags).Select(r => r.Tag.Name));
    }

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <Version>1.2.1</Version>
            <FileVersion>1.2.1.0</FileVersion>
            <AssemblyName>087OpenStoradge.ext2</AssemblyName>
          </PropertyGroup>
        </Project>
        """;

    private const string AssemblyInfo = """
        [assembly: AssemblyProduct("01.087 - Открыть Storadge - (OpenStoradge) - (CICD)")]
        [assembly: AssemblyVersion("1.2.1.0")]
        [assembly: AssemblyFileVersion("1.2.1.0")]
        """;

    [Fact]
    public void Single_version_moves_everything_to_csproj_version()
    {
        // Как в Test-module: GenerateAssemblyInfo=false, версия в трёх местах.
        const string proj = "<Project Sdk=\"Microsoft.NET.Sdk\">\n\t<PropertyGroup>\n\t\t<TargetFramework>net48</TargetFramework>\n" +
                            "\t\t<Version>1.3.0</Version>\n\t\t<FileVersion>1.3.0</FileVersion>\n\t\t<GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n" +
                            "\t</PropertyGroup>\n</Project>\n";
        const string info = "using System.Reflection;\n\n[assembly: AssemblyProduct(\"0 - Тест - (TestModule) - (CICD)\")]\n" +
                            "[assembly: AssemblyVersion(\"1.3.0\")]\n[assembly: AssemblyFileVersion(\"1.3.0\")]\n" +
                            "[assembly: AssemblyMetadata(\"PreviousNames\", \"MIPTestModule\")]\n";

        var p = ModuleSources.SingleVersionProject(proj, info);
        Assert.Contains("<Version>1.3.0</Version>", p);
        Assert.DoesNotContain("<FileVersion>", p);
        Assert.DoesNotContain("<GenerateAssemblyInfo>false", p);
        Assert.Contains("\t\t<GenerateAssemblyInfo>true</GenerateAssemblyInfo>\n", p);
        Assert.Contains("<GenerateAssemblyProductAttribute>false</GenerateAssemblyProductAttribute>", p);
        Assert.Contains("<GenerateAssemblyTitleAttribute>false</GenerateAssemblyTitleAttribute>", p);
        Assert.Equal(p, ModuleSources.SingleVersionProject(p, info)); // повторно - без изменений

        var i = ModuleSources.SingleVersionAssemblyInfo(info);
        Assert.DoesNotContain("AssemblyVersion", i);
        Assert.DoesNotContain("AssemblyFileVersion", i);
        Assert.Contains("AssemblyProduct", i);
        Assert.Contains("PreviousNames", i);
        Assert.Equal(i, ModuleSources.SingleVersionAssemblyInfo(i));
        Assert.Equal(new ModuleVersion(1, 3, 0), ModuleSources.Version(p, i));

        // Нет <Version> - переносится из AssemblyInfo; SDK-проект без версии в AssemblyInfo не трогается.
        var noVersion = ModuleSources.SingleVersionProject("<Project>\n  <PropertyGroup>\n    <TargetFramework>net48</TargetFramework>\n  </PropertyGroup>\n</Project>\n",
            "[assembly: AssemblyVersion(\"2.0.1.0\")]\n");
        Assert.Contains("    <TargetFramework>net48</TargetFramework>\n    <Version>2.0.1</Version>\n", noVersion);
        const string clean = "<Project>\n  <PropertyGroup>\n    <Version>1.0.3</Version>\n  </PropertyGroup>\n</Project>\n";
        Assert.Equal(clean, ModuleSources.SingleVersionProject(clean, null));
    }

    [Fact]
    public void Sources_read_and_bump_version()
    {
        Assert.Equal("087OpenStoradge.ext2", ModuleSources.AssemblyName("src/OpenStoradge.csproj", Csproj));
        Assert.Equal("OpenStoradge", ModuleSources.AssemblyName("OpenStoradge.csproj", "<Project/>"));
        Assert.Equal(new ModuleVersion(1, 2, 1), ModuleSources.Version(Csproj, AssemblyInfo));
        Assert.Equal(new ModuleVersion(1, 2, 1), ModuleSources.Version(null, AssemblyInfo));
        Assert.Equal("01.087 - Открыть Storadge - (OpenStoradge) - (CICD)", ModuleSources.Product(Csproj, AssemblyInfo));

        var v = new ModuleVersion(1, 3, 0);
        var project = ModuleSources.BumpProject(Csproj, v);
        Assert.Contains("<Version>1.3.0</Version>", project);
        Assert.Contains("<FileVersion>1.3.0</FileVersion>", project);
        var info = ModuleSources.BumpAssemblyInfo(AssemblyInfo, v);
        Assert.Contains("AssemblyVersion(\"1.3.0\")", info);
        Assert.Contains("AssemblyFileVersion(\"1.3.0\")", info);
        Assert.Contains("AssemblyProduct(\"01.087", info);
    }

    [Fact]
    public void Sources_find_project_and_ci()
    {
        var tree = new[] { "tests/X.Tests/X.Tests.csproj", "OpenStoradge.csproj", "Properties/AssemblyInfo.cs", "tests/AssemblyInfo.cs", ".gitlab-ci.yml" }
            .Select(p => new GitLabTreeItem { Path = p, Type = "blob", Name = Path.GetFileName(p) }).ToList();
        Assert.Equal("OpenStoradge.csproj", ModuleSources.FindProject(tree));
        Assert.Equal("Properties/AssemblyInfo.cs", ModuleSources.FindAssemblyInfo(tree, "OpenStoradge.csproj"));
        Assert.True(ModuleSources.HasCi(tree, null));
        Assert.False(ModuleSources.HasCi(tree, "ci/other.yml"));
        Assert.True(ModuleSources.HasCi(tree, "ci.yml@group/templates"));
    }

    [Fact]
    public void Sources_project_with_test_in_name_is_not_a_test_project()
    {
        var tree = new[] { "TestModule.csproj", "TestModule.Tests.csproj", "AssemblyInfo.cs" }
            .Select(p => new GitLabTreeItem { Path = p, Type = "blob", Name = p }).ToList();
        Assert.Equal("TestModule.csproj", ModuleSources.FindProject(tree));
        Assert.Equal("AssemblyInfo.cs", ModuleSources.FindAssemblyInfo(tree, "TestModule.csproj"));
    }

    [Fact]
    public void Deploy_reason_carries_commit()
    {
        var reason = ModuleDeploy.Reason("1.4.0.0", "release/1.4.0", "1a2b3c4d5e6f7a8b9c0d", new[] { "KTN-101: экспорт", "KTN-103: фикс" });
        Assert.Equal("1.4.0.0 · release/1.4.0 @ 1a2b3c4d - KTN-101: экспорт; KTN-103: фикс", reason);
        Assert.Equal("1a2b3c4d", ModuleDeploy.ShaFromReason(reason));
        Assert.True(ModuleDeploy.SameCommit("1a2b3c4d", "1a2b3c4d5e6f7a8b9c0d"));
        Assert.False(ModuleDeploy.SameCommit("1a2b3c4d", "ffff"));
        Assert.Null(ModuleDeploy.ShaFromReason("Загружено вручную"));
    }

    [Fact]
    public void Package_reads_assembly_from_zip_and_artifacts()
    {
        var dll = File.ReadAllBytes(typeof(ModulePackage).Assembly.Location);
        var zip = Zip(("ProAdmin.ext2.dll", dll), ("config.json", "{}"u8.ToArray()));

        var info = ModulePackage.Analyze(zip, "ProAdmin.ext2-1.0.0.zip");
        Assert.Equal("PilotProAdmin", info.AssemblyName);
        Assert.Equal(ModuleVersion.Display(typeof(ModulePackage).Assembly.GetName().Version!.ToString()), info.Version);
        Assert.StartsWith("Версия " + info.Version + " - Размер ", info.ExtensionAdditional);

        var artifacts = Zip(("bin/Release/net48/other.txt", "x"u8.ToArray()), ("bin/ProAdmin.ext2-1.0.0.zip", zip));
        var (inner, name) = ModulePackage.FromArtifacts(artifacts);
        Assert.Equal("ProAdmin.ext2-1.0.0.zip", name);
        Assert.Equal(zip, inner);

        Assert.Throws<InvalidDataException>(() => ModulePackage.Analyze(Zip(("readme.txt", "x"u8.ToArray())), "bad.zip"));
        Assert.Throws<InvalidDataException>(() => ModulePackage.FromArtifacts(Zip(("readme.txt", "x"u8.ToArray()))));
    }

    [Fact]
    public void Package_reads_previous_names_from_assembly_metadata()
    {
        // Модуль сообщает прежние имена атрибутом AssemblyMetadata("PreviousNames", "А;Б") (см. AssemblyInfo.cs тестов).
        var dll = File.ReadAllBytes(typeof(ModuleRulesTests).Assembly.Location);
        var info = ModulePackage.Analyze(Zip(("114Test.ext2.dll", dll)), "114Test.ext2.zip");
        Assert.Equal(new[] { "OldTestModule", "113OldTestModule.ext2" }, info.Previous);
    }

    [Fact]
    public void Package_without_previous_names_has_none()
    {
        var dll = File.ReadAllBytes(typeof(ModulePackage).Assembly.Location);
        Assert.Empty(ModulePackage.Analyze(Zip(("ProAdmin.ext2.dll", dll)), "ProAdmin.ext2.zip").Previous ?? Array.Empty<string>());
    }

    [Fact]
    public void Zip_bomb_is_rejected_before_unpacking()
    {
        // Запись заявляет 300 МБ нулей (сжимается до сотен КБ): в память она не читается.
        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var entry = archive.CreateEntry("Bomb.ext2.dll").Open())
        {
            var chunk = new byte[1 << 20];
            for (var i = 0; i < 300; i++)
                entry.Write(chunk);
        }
        var ex = Assert.Throws<InvalidDataException>(() => ModulePackage.Analyze(ms.ToArray(), "Bomb.ext2.zip"));
        Assert.Contains("распакованный размер", ex.Message);
    }

    [RequiresEnvFact("REAL_MODULE_ZIP")]
    public void Package_of_real_module_zip_when_built()
    {
        // Собранный модуль (например, Pilot.ClientSdk: dotnet build -c Release), путь к zip - в REAL_MODULE_ZIP.
        var zipPath = Environment.GetEnvironmentVariable("REAL_MODULE_ZIP")!;
        var info = ModulePackage.Analyze(File.ReadAllBytes(zipPath), Path.GetFileName(zipPath));
        Assert.Equal("113Pilot.ClientSdk.ext2", info.AssemblyName);
        Assert.Equal("0 - Общий модуль для клиентских модулей - (Pilot.ClientSdk) - (CICD)", info.ExtensionName);
        Assert.Equal("Pilot.ClientSdk", ModulePackage.CodeFromName(info.ExtensionName));
        Assert.Equal(new[] { "Modules", "113Modules.ext2" }, info.Previous);
    }

    [Fact]
    public void Renamed_module_updates_object_with_previous_name()
    {
        var old = new PilotModule(Guid.NewGuid(), "0 - Общий модуль для клиентских модулей - (Modules) - (CICD)",
            "Версия 1.1.1.0 - Размер 58 КБ", "Расширения", Guid.NewGuid(), "113Modules.ext2-1.1.1.zip", Guid.NewGuid(), 1, null, null);
        var other = new PilotModule(Guid.NewGuid(), "0 - Хранилище - (OpenStoradge) - (CICD)",
            "Версия 1.0.0.0 - Размер 10 КБ", "Расширения", Guid.NewGuid(), "087OpenStoradge.ext2.zip", Guid.NewGuid(), 1, null, null);
        var renamed = new ModulePackageInfo(Array.Empty<byte>(), "113Pilot.ClientSdk.ext2-2.0.0.zip", "113Pilot.ClientSdk.ext2", "2.0.0.0",
            "0 - Общий модуль для клиентских модулей - (Pilot.ClientSdk) - (CICD)", new[] { "Modules", "113Modules.ext2" });

        Assert.Same(old, ModuleWorkflowBase.MatchPackage(new[] { other, old }, renamed));
        // Без прежних имён - нового модуля на сервере нет (будет создан).
        Assert.Null(ModuleWorkflowBase.MatchPackage(new[] { other, old }, renamed with { PreviousNames = null }));
    }

    [Theory]
    [InlineData("Версия 1.2.1.0 - Размер 93 КБ", "1.2.1.0")]
    [InlineData("Версия 1.0.1 - Размер 1,8 МБ", "1.0.1")]
    [InlineData("что-то", null)]
    public void Version_from_additional(string additional, string? expected) =>
        Assert.Equal(expected, ModulePackage.VersionFromAdditional(additional));

    [Fact]
    public void Size_and_code()
    {
        Assert.Equal("93 КБ", ModulePackage.FormatSize(95_000));
        Assert.Equal("1,8 МБ", ModulePackage.FormatSize(1_900_000));
        Assert.Equal("OpenStoradge", ModulePackage.CodeFromName("01.087 - Открыть Storadge - (OpenStoradge) - (CICD)"));
        Assert.Null(ModulePackage.CodeFromName("Ascon.Pilot.Reports.DevExpress"));
    }

    private static PilotModule Pilot(string name, string? reason = null) =>
        new(Guid.NewGuid(), name, "Версия 1.0.0.0 - Размер 10 КБ", "", Guid.Empty, "m.zip", Guid.NewGuid(), 10, reason, null);

    private static ModuleRepoState Repo(string name, string? asm = null, string? product = null) => new()
    {
        Repo = new Automations.AutomationRepo { Name = name, ProjectPath = "modules/" + name.ToLowerInvariant(), Folder = "", WebUrl = "" },
        AssemblyName = asm,
        Product = product,
    };

    [Fact]
    public void Repo_matches_pilot_module_by_product_name_or_code()
    {
        var modules = new[]
        {
            Pilot("0 - Модуль для отчётов - (Reports) - (CICD)"),
            Pilot("01.087 - Открыть Storadge - (OpenStoradge) - (CICD)", "1.0.1.0 · dev @ abcdef12"),
            Pilot("Ascon.Pilot.Reports.DevExpress"),
        };

        Assert.Equal(modules[1], ModuleCatalog.Match(Repo("OpenStoradge"), modules));
        Assert.Equal(modules[0], ModuleCatalog.Match(Repo("reports-module", "112Reports.ext2"), modules));
        Assert.Equal(modules[0], ModuleCatalog.Match(Repo("x", null, "0 - Модуль для отчётов - (Reports) - (CICD)"), modules));
        Assert.Null(ModuleCatalog.Match(Repo("Unknown", "999Unknown.ext2"), modules));
    }

    [Fact]
    public void Env_state_by_deployed_commit_and_version()
    {
        var settings = new AdminSettings();
        settings.Normalize();
        settings.Find(AdminSettings.Test)!.PilotUrl = "http://h:5547/db";
        settings.Find(AdminSettings.Prod)!.PilotUrl = "http://h:5546/db";
        settings.StagePilotUrl = "http://h:5549/db";
        var envs = ModuleEnv.All(settings);
        Assert.Equal(new[] { "test", "stage", "prod" }, envs.Select(e => e.Key));

        var repo = new ModuleRepoState
        {
            Repo = Repo("m").Repo,
            Dev = new GitLabBranch { Name = "dev", Commit = new GitLabCommit { Id = "abcdef1234567890" } },
            Main = new GitLabBranch { Name = "main", Commit = new GitLabCommit { Id = "1111111111" } },
            MainVersion = new ModuleVersion(1, 0, 0),
        };
        Assert.Equal(ModuleSync.Synced, ModuleCatalog.State(envs[0], repo, Pilot("m", "1.1.0.0 · dev @ abcdef12"), true).State);
        Assert.Equal(ModuleSync.Behind, ModuleCatalog.State(envs[0], repo, Pilot("m", "1.1.0.0 · dev @ 99999999"), true).State);
        // Стейдж без активного релиза: на нём выпущенная версия (= main) - «выпущен в прод», более старая - «нет релиза».
        Assert.Equal(ModuleSync.Released, ModuleCatalog.State(envs[1], repo, Pilot("m"), true).State);
        Assert.Equal(ModuleSync.Released, ModuleCatalog.State(envs[1], repo, Pilot("m", "1.0.0 · release/1.0.0 @ 11111111"), true).State);
        var old = new PilotModule(Guid.NewGuid(), "m", "Версия 0.9.0 - Размер 10 КБ", "", Guid.Empty, "m.zip", Guid.NewGuid(), 10, "0.9.0 · release/0.9.0 @ 22222222", null);
        Assert.Equal(ModuleSync.NoBranch, ModuleCatalog.State(envs[1], repo, old, true).State);
        // Ветка release/1.0.0 удалена после выпуска: на стейдже коммит релиза (не голова main), версия = main - выпущен.
        Assert.Equal(ModuleSync.Released, ModuleCatalog.State(envs[1], repo, Pilot("m", "1.0.0 · release/1.0.0 @ 33333333"), true).State);
        Assert.Equal(ModuleSync.SameVersion, ModuleCatalog.State(envs[2], repo, Pilot("m"), true).State);
        Assert.Equal(ModuleSync.Missing, ModuleCatalog.State(envs[2], repo, null, true).State);
        Assert.Equal(ModuleSync.Unknown, ModuleCatalog.State(envs[2], repo, null, false).State);

        // «Выпустить в прод» выкладывает голову release/X.Y.Z, затем вливает её в main merge-коммитом - на проде то же, что в main.
        repo.Main.Commit = new GitLabCommit { Id = "3c6b0551aaaa", ParentIds = new() { "fbdcc72eaaaa", "9793faa4aaaa" } };
        Assert.Equal(ModuleSync.Synced, ModuleCatalog.State(envs[2], repo, Pilot("m", "1.3.0 · release/1.3.0 @ 9793faa4"), true).State);
        Assert.Equal(ModuleSync.Behind, ModuleCatalog.State(envs[2], repo, Pilot("m", "1.2.1 · main @ fbdcc72e"), true).State);
    }

    [Fact]
    public void Merge_request_checks_use_the_latest_pipeline_of_each_source()
    {
        GitLabPipeline P(long id, string status, string source) => new() { Id = id, Status = status, Source = source };

        var latest = MergeRequestChecks.Latest(new[] { P(1, "failed", "push"), P(2, "success", "push"), P(3, "failed", "merge_request_event") });
        Assert.Equal(new long[] { 2, 3 }, latest.Select(p => p.Id).OrderBy(id => id));
        Assert.Equal(3, MergeRequestChecks.Summary(latest)!.Id);

        Assert.Equal(5, MergeRequestChecks.Summary(new[] { P(4, "success", "push"), P(5, "running", "merge_request_event") })!.Id);
        Assert.Equal(7, MergeRequestChecks.Summary(new[] { P(6, "success", "push"), P(7, "success", "merge_request_event") })!.Id);
        Assert.Null(MergeRequestChecks.Summary(Array.Empty<GitLabPipeline>()));

        Assert.True(P(1, "failed", "push").CanRetry);
        Assert.False(P(1, "manual", "push").CanRetry);
        Assert.True(GitLabPipeline.Blocks("manual"));
        Assert.False(GitLabPipeline.Blocks("running"));
        Assert.False(GitLabPipeline.Blocks(null));
    }

    [Fact]
    public void Next_step_does_not_suggest_merging_a_failed_pipeline()
    {
        var failed = new NextStepMr(4, "старая задача", Draft: false, Reviewed: true, "http://mr/4", "failed");
        var green = new NextStepMr(10, "новая задача", Draft: false, Reviewed: true, "http://mr/10", "success");
        NextStepInput Base() => new() { Test = ModuleSync.Synced, Stage = ModuleSync.Released, Prod = ModuleSync.Synced };

        var merge = ModuleNextSteps.For(Base() with { DevMrs = new[] { failed, green } });
        Assert.Equal("Влейте !10 в dev", merge.Title);

        var retry = ModuleNextSteps.For(Base() with { DevMrs = new[] { failed } });
        Assert.Equal((NextStepKind.Action, "#tasks"), (retry.Kind, retry.Href));
        Assert.Contains("!4", retry.Title);
        Assert.Contains("не прошёл", retry.Title);
    }

    [Fact]
    public void Next_step_follows_the_task_path()
    {
        var mr = new NextStepMr(7, "KTN-1 фича", Draft: false, Reviewed: false, "http://mr/7", "success");
        NextStepInput Base() => new() { NeedReview = true, Test = ModuleSync.Synced, Stage = ModuleSync.Released, Prod = ModuleSync.Synced, LastProd = new(1, 3, 0) };

        var done = ModuleNextSteps.For(Base());
        Assert.Equal((FlowStage.Done, NextStepKind.Done), (done.Stage, done.Kind));
        Assert.Contains("v1.3.0", done.Title);

        var review = ModuleNextSteps.For(Base() with { DevMrs = new[] { mr } });
        Assert.Equal((FlowStage.Task, NextStepKind.Wait, "http://mr/7"), (review.Stage, review.Kind, review.Href));
        var merge = ModuleNextSteps.For(Base() with { DevMrs = new[] { mr with { Reviewed = true } } });
        Assert.Equal((FlowStage.Task, NextStepKind.Action, "#tasks"), (merge.Stage, merge.Kind, merge.Href));

        Assert.Equal(NextStepKind.Wait, ModuleNextSteps.For(Base() with { DeployingTest = "http://pl/1", Test = ModuleSync.Behind }).Kind);
        var toRelease = ModuleNextSteps.For(Base() with { Unreleased = 2 });
        Assert.Equal((FlowStage.Test, "#release"), (toRelease.Stage, toRelease.Href));
        Assert.Contains("2 задачи", toRelease.Title);

        var staging = Base() with { Release = "release/1.4.0", Stage = ModuleSync.Behind, Unreleased = 2 };
        Assert.Equal("#release", ModuleNextSteps.For(staging with { ReleaseMrs = 1 }).Href);
        Assert.Equal(NextStepKind.Wait, ModuleNextSteps.For(staging with { DeployingStage = "http://pl/2" }).Kind);
        var prod = ModuleNextSteps.For(staging with { Stage = ModuleSync.Synced });
        Assert.Equal((FlowStage.Stage, "#release-prod"), (prod.Stage, prod.Href));
        Assert.Contains("1.4.0", prod.Title);

        Assert.Equal((FlowStage.Prod, "#hotfix"), (ModuleNextSteps.For(staging with { Hotfix = "hotfix/KTN-9" }) is var h ? (h.Stage, h.Href) : default));
        Assert.Equal(NextStepKind.Warn, ModuleNextSteps.For(Base() with { Ready = false }).Kind);

        var noRepo = ContentNextSteps.For(new ContentFlowInput { HasRepo = false, CreateUrl = "/c" });
        Assert.Equal((NextStepKind.Warn, "/c"), (noRepo.Kind, noRepo.Href));
        ContentFlowInput Content() => new() { Test = SyncState.Synced, Prod = SyncState.Synced, PromoteUrl = "/p", BackportUrl = "/b", CompareUrl = "/cmp" };
        Assert.Equal(NextStepKind.Done, ContentNextSteps.For(Content() with { LastRelease = "v1.0.0" }).Kind);
        Assert.Equal((FlowStage.Test, "/p"), ContentNextSteps.For(Content() with { DevAhead = true }) is var pr ? (pr.Stage, pr.Href) : default);
        Assert.Equal("/b", ContentNextSteps.For(Content() with { DevAhead = true, MainAhead = true }).Href); // сначала вернуть хотфикс
        Assert.Equal("/cmp", ContentNextSteps.For(Content() with { DevAhead = true, Test = SyncState.Differs }).Href);
        Assert.Equal(FlowStage.Prod, ContentNextSteps.For(Content() with { Prod = SyncState.Differs }).Stage);
        Assert.Equal(3, FlowCard.WithoutStage(FlowStage.Done));

        // Со стейджем: dev впереди / собрать релиз; релиз есть / на стейдж / в прод.
        var staged = Content() with { HasStage = true, DevAhead = true };
        Assert.Equal("#release", ContentNextSteps.For(staged).Href);
        Assert.Equal(NextStepKind.Warn, ContentNextSteps.For(staged with { ReleaseBlocked = "версия" }).Kind);
        Assert.Equal((FlowStage.Stage, "#release"), ContentNextSteps.For(staged with { Release = "release/1.4.0", Stage = SyncState.Differs }) is var st ? (st.Stage, st.Href) : default);
        var ship = ContentNextSteps.For(staged with { Release = "release/1.4.0", Stage = SyncState.Synced });
        Assert.Equal((FlowStage.Stage, "/p"), (ship.Stage, ship.Href));
        Assert.Contains("1.4.0", ship.Title);

        // Hotfix важнее всего; готовая ветка задачи - раньше переноса dev.
        Assert.Equal((FlowStage.Prod, "/h"), ContentNextSteps.For(staged with { Hotfix = "hotfix/x", HotfixUrl = "/h", MainAhead = true }) is var hf ? (hf.Stage, hf.Href) : default);
        Assert.Equal((FlowStage.Task, "#tasks"), ContentNextSteps.For(staged with { ReadyTask = "feature/KTN-1" }) is var tk ? (tk.Stage, tk.Href) : default);
        Assert.Equal("/p", ContentNextSteps.For(staged with { ReadyTask = "feature/KTN-1", Release = "release/1.4.0", Stage = SyncState.Synced }).Href);

        var rel = new[] { new GitLabBranch { Name = "release/1.3.0" }, new GitLabBranch { Name = "release/1.4.0" }, new GitLabBranch { Name = "dev" } };
        Assert.Equal("release/1.4.0", ProAdmin.Content.ContentReleases.ActiveAutomationRelease(rel, new AutomationVersion(1, 3, 0))?.Name);
        Assert.Null(ProAdmin.Content.ContentReleases.ActiveAutomationRelease(rel, new AutomationVersion(1, 4, 0)));
        Assert.Equal(2, ProAdmin.Content.ContentReleases.ReleasedAutomationReleases(rel, new AutomationVersion(1, 4, 0)).Count);
        var reportRel = new[] { new GitLabBranch { Name = "release/2026.10.29" }, new GitLabBranch { Name = "release/2026.10.30" } };
        Assert.Equal("release/2026.10.30", ProAdmin.Content.ContentReleases.ActiveReportRelease(reportRel, new[] { "2026.10.29" })?.Name);
        Assert.Equal("release/2026.10.29", ProAdmin.Content.ContentReleases.ReleasedReportReleases(reportRel, new[] { "2026.10.29" }).Single().Name);

        var empty = ModuleNextSteps.For(Base() with { EmptyBranch = "feature/KTN-5" });
        Assert.Equal((FlowStage.Task, NextStepKind.Wait), (empty.Stage, empty.Kind));
        Assert.Contains("feature/KTN-5", empty.Title);
    }

    [Fact]
    public void New_module_skeleton_is_not_deployed_before_first_task()
    {
        NextStepInput Base() => new() { Test = ModuleSync.Synced, Stage = ModuleSync.NoBranch, Prod = ModuleSync.Missing };
        var fresh = Base() with { Test = ModuleSync.Missing };
        // Ветка задачи с коммитом - сначала MR, а не выкладка пустого каркаса.
        var mr = ModuleNextSteps.For(fresh with { BranchWithoutMr = "feature/KTN-4432", NewMrUrl = "http://mr" });
        Assert.Equal((FlowStage.Task, "http://mr"), (mr.Stage, mr.Href));
        // Ни веток, ни задач - предлагается начать задачу.
        Assert.Equal((FlowStage.Task, "#tasks"), ModuleNextSteps.For(fresh) is var start ? (start.Stage, start.Href) : default);
        // В dev уже есть задачи, а на тесте модуля нет - выложить dev.
        Assert.Equal("#envs", ModuleNextSteps.For(fresh with { Unreleased = 1 }).Href);
        Assert.Equal("#envs", ModuleNextSteps.For(Base() with { Test = ModuleSync.Behind, BranchWithoutMr = "feature/x" }).Href);
        // Несколько веток без MR: в шаге каждая со своей кнопкой «Создать MR».
        var two = ModuleNextSteps.For(Base() with { BranchWithoutMr = "feature/a", NewMrUrl = "http://mr/a",
            BranchesWithoutMr = new[] { ("feature/a", "http://mr/a"), ("feature/b", "http://mr/b") } });
        Assert.Equal(new[] { ("feature/a", "http://mr/a"), ("feature/b", "http://mr/b") }, two.Items.Select(i => (i.Title, i.Href)));
        Assert.Null(two.ActionLabel);
        Assert.Contains("2 ветки", two.Title);
        var one = ModuleNextSteps.For(Base() with { BranchWithoutMr = "feature/a", NewMrUrl = "http://mr/a",
            BranchesWithoutMr = new[] { ("feature/a", "http://mr/a") } });
        Assert.Equal(("Ветка feature/a: создайте MR в dev", "http://mr/a"), (one.Title, one.Items.Single().Href));
        // Одна задача уже на тесте, другая ветка без MR: шаг «соберите релиз», но «Создать MR» не пропадает.
        var both = ModuleNextSteps.For(Base() with { Unreleased = 1, BranchWithoutMr = "feature/b", NewMrUrl = "http://mr/b",
            BranchesWithoutMr = new[] { ("feature/b", "http://mr/b") } });
        Assert.Equal(("#release", "http://mr/b"), (both.Href, both.Items.Single().Href));
        // Релиз на стейдже, в dev задача вне релиза: «Добавить в релиз» рядом с выпуском в прод.
        var candidate = new NextStepItem("fix: Log", "Добавить в релиз", "#add-to-release") { IsBranch = false, PostAction = "/add", PostValue = "c002" };
        var inRelease = ModuleNextSteps.For(Base() with { Release = "release/1.2.0", Stage = ModuleSync.Synced, Unreleased = 2,
            ReleaseCandidates = new[] { candidate } });
        Assert.Equal(("#release-prod", "c002"), (inRelease.Href, inRelease.Items.Single().PostValue));
        // Без активного релиза кандидаты не показываются.
        Assert.DoesNotContain(candidate, ModuleNextSteps.For(Base() with { Unreleased = 2, ReleaseCandidates = new[] { candidate } }).Items);
    }

    [Fact]
    public void New_mr_url_targets_branch_of_task()
    {
        var url = ProAdmin.Pages.Modules.ModuleModel.NewMrUrl("http://gl/client/m", "bugfix/KTN-1-x", "release/1.2.0");
        Assert.Contains("source_branch%5D=bugfix%2FKTN-1-x", url);
        Assert.Contains("target_branch%5D=release%2F1.2.0", url);
    }

    [Fact]
    public void Ci_template_has_build_job_and_branch_rules()
    {
        var settings = new AdminSettings();
        settings.Normalize();
        var yml = ModuleCiTemplate.Content(settings);
        Assert.Contains("build_module:", yml);
        Assert.Contains("bin/**/*.zip", yml);
        Assert.Contains("ok '^(feature|bugfix)/.'", yml); // номер задачи в имени ветки необязателен
        Assert.DoesNotContain("__", yml);
        Assert.Contains("check_version:", yml);
        Assert.Contains("deploy_test:", yml);
        Assert.Contains("/api/ci/modules/deploy", yml);
        Assert.DoesNotContain("tag_name:", yml); // тег и GitLab Release ставит веб-админка
    }

    [Theory]
    [InlineData("dev", ModuleEnv.Test)]
    [InlineData("release/1.4.0", ModuleEnv.Stage)]
    [InlineData("main", ModuleEnv.Prod)]
    [InlineData("master", ModuleEnv.Prod)]
    [InlineData("feature/KTN-1", null)]
    [InlineData("release-prep/1.4.0", null)]
    [InlineData("hotfix/KTN-2", null)]
    public void Ci_deploy_env_follows_branch(string reference, string? env) =>
        Assert.Equal(env, ModuleCiDeployEndpoint.EnvForRef(reference));

    private static byte[] Zip(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }
}
