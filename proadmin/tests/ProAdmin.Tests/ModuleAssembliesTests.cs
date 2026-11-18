using System.IO.Compression;
using ProAdmin.Modules;

namespace ProAdmin.Tests;

public class ModuleAssembliesTests
{
    [Theory]
    [InlineData("", "1.2.3.4", true)]
    [InlineData("13.0.0.0", "13.0.0.0", true)]
    [InlineData("13.0", "13.0.0.0", true)]
    [InlineData("13.0.0.0", "13.0.1.0", false)]
    [InlineData("6.*", "6.1.0.0", true)]
    [InlineData("6.*", "7.0.0.0", false)]
    [InlineData("13.0.3.*", "13.0.3.27908", true)]
    [InlineData("13.0.3.*", "13.0.4.30916", false)]
    [InlineData(">=6.0 <7", "6.1.0.0", true)]
    [InlineData(">=6.0 <7", "7.0.0.0", false)]
    [InlineData("4.0.1.2; 4.0.5.0", "4.0.5.0", true)]
    [InlineData("4.0.1.2; 4.0.5.0", "4.0.2.0", false)]
    [InlineData("13.0.0.0", null, false)]
    public void Version_pattern_matches(string pattern, string? version, bool expected) =>
        Assert.Equal(expected, VersionPattern.Matches(pattern, version));

    [Theory]
    [InlineData("13.0.0.0", true)]
    [InlineData(">=6.0 <7; 8.*", true)]
    [InlineData("abc", false)]
    [InlineData(">=x", false)]
    public void Version_pattern_validates(string pattern, bool ok) =>
        Assert.Equal(ok, VersionPattern.Validate(pattern) is null);

    [Fact]
    public void Lists_assemblies_with_versions_and_main()
    {
        var zip = Zip();
        var package = ModulePackage.Analyze(zip, "114Test.ext2-1.0.0.zip");
        var list = package.Assemblies;

        Assert.Equal(4, list.Count);
        Assert.True(list[0].IsMain);
        Assert.Equal("114Test.ext2", list[0].Name);
        var xunit = list.Single(a => a.Name == "xunit.assert");
        Assert.True(xunit.IsManaged);
        Assert.Equal(typeof(Assert).Assembly.GetName().Version!.ToString(), xunit.AssemblyVersion);
        Assert.NotNull(xunit.FileVersion);
        var native = list.Single(a => a.Name == "SQLite.Interop");
        Assert.False(native.IsManaged);
        Assert.Equal("x64/SQLite.Interop.dll", native.Path);
        Assert.True(list.Single(a => a.Name == "113Other.ext2").IsOtherModule);
    }

    [Fact]
    public void Checks_against_spec()
    {
        var package = ModulePackage.Analyze(Zip(), "114Test.ext2-1.0.0.zip");
        var xunitVersion = typeof(Assert).Assembly.GetName().Version!.ToString();

        var empty = AssemblyChecker.Check(new AssemblySpec(), package);
        Assert.Equal(AssemblyVerdict.Main, Verdict(empty, "114Test.ext2"));
        Assert.Equal(AssemblyVerdict.Unknown, Verdict(empty, "xunit.assert"));
        Assert.Equal(AssemblyVerdict.Unknown, Verdict(empty, "SQLite.Interop"));
        Assert.Equal(AssemblyVerdict.OtherModule, Verdict(empty, "113Other.ext2"));

        var spec = new AssemblySpec
        {
            Entries =
            {
                new() { Name = "xunit.assert", AssemblyVersion = xunitVersion },
                new() { Name = "x64\\SQLite.Interop.dll", AssemblyVersion = "1.0.0.0" },
            },
        };
        spec.Normalize();
        var ok = AssemblyChecker.Check(spec, package);
        Assert.Equal(AssemblyVerdict.Ok, Verdict(ok, "xunit.assert"));
        Assert.Equal(AssemblyVerdict.Ok, Verdict(ok, "SQLite.Interop")); // native - версия не проверяется
        Assert.Contains("чужой модуль внутри: 1", ok.Summary);

        spec.Find("xunit.assert")!.AssemblyVersion = "0.1.*";
        Assert.Equal(AssemblyVerdict.WrongVersion, Verdict(AssemblyChecker.Check(spec, package), "xunit.assert"));

        spec.Find("xunit.assert")!.AssemblyVersion = "";
        spec.Find("xunit.assert")!.Modules = new() { "Другой модуль" };
        Assert.Equal(AssemblyVerdict.NotForModule, Verdict(AssemblyChecker.Check(spec, package), "xunit.assert"));
        spec.Find("xunit.assert")!.Modules = new() { "ProAdmin.Tests" }; // имя главной сборки
        Assert.Equal(AssemblyVerdict.Ok, Verdict(AssemblyChecker.Check(spec, package), "xunit.assert"));

        spec.Find("xunit.assert")!.Rule = AssemblyRule.Forbidden;
        Assert.Equal(AssemblyVerdict.Forbidden, Verdict(AssemblyChecker.Check(spec, package), "xunit.assert"));
    }

    [Theory]
    [InlineData(AssemblySpecMode.SharedModule, true)]
    [InlineData(AssemblySpecMode.AllowList, true)]
    [InlineData(AssemblySpecMode.WarnList, false)]
    public void Mode_decides_error_or_warning(AssemblySpecMode mode, bool blocks) =>
        Assert.Equal(blocks, new AssemblySpec { Mode = mode }.Blocks);

    [Fact]
    public void Old_per_module_mode_becomes_warn_list()
    {
        var spec = new AssemblySpec { Mode = AssemblySpecMode.PerModule };
        spec.Normalize();
        Assert.Equal(AssemblySpecMode.WarnList, spec.Mode);
    }

    [Fact]
    public void Allow_list_forbids_libraries_in_zip_and_checks_references()
    {
        var spec = new AssemblySpec
        {
            Mode = AssemblySpecMode.AllowList,
            Entries =
            {
                new() { Name = "Newtonsoft.Json", AssemblyVersion = "13.*" },
                new() { Name = "Humanizer", AssemblyVersion = "2.14.*" },
                new() { Name = "SQLite.Interop", Modules = { "Reports" } },
                new() { Name = "Bad", Rule = AssemblyRule.Forbidden },
            },
        };
        spec.Normalize();
        var refs = new List<AssemblyRef>
        {
            new("mscorlib", "4.0.0.0", "b77a5c561934e089"),
            new("Ascon.Pilot.SDK", "26.13.0.0", null),
            new("113Pilot.ClientSdk.ext2", "2.0.0.0", null),
            new("Newtonsoft.Json", "13.0.0.0", "30ad4fe6b2a6aeed"),
            new("Humanizer", "2.13.0.0", "979442b78dfc278e"),
            new("System.Reactive", "6.0.0.0", "94bc3704cddfc263"),
            new("Bad", "1.0.0.0", null),
        };
        var module = new[] { MainAssembly("114Test.ext2") with { References = refs }, Lib("Newtonsoft.Json", "13.0.0.0") };
        var check = AssemblyChecker.Check(spec, module, new[] { "Test" });

        Assert.Equal(AssemblyVerdict.SharedOnly, FileVerdict(check, "Newtonsoft.Json")); // разрешена, но в zip - нельзя
        var refsBad = check.Items.Where(i => i.Verdict == AssemblyVerdict.NotAllowedReference).Select(i => i.Subject.Split(' ')[0]).ToList();
        Assert.Equal(new[] { "Humanizer", "System.Reactive", "Bad" }, refsBad); // не та версия, нет в списке, запрещена
        Assert.True(spec.Blocks);

        // Исключение «только модули»: native-dll в zip модуля Reports допустима.
        var reports = AssemblyChecker.Check(spec, new[] { MainAssembly("112Reports.ext2"), Lib("SQLite.Interop", null) }, new[] { "Reports" });
        Assert.False(reports.HasProblems, reports.Details);
    }

    [Fact]
    public void Warn_list_allows_listed_libraries_in_zip()
    {
        var spec = new AssemblySpec { Mode = AssemblySpecMode.WarnList, Entries = { new() { Name = "Humanizer", AssemblyVersion = "2.14.*" } } };
        spec.Normalize();
        var module = new[]
        {
            MainAssembly("114Test.ext2") with { References = new List<AssemblyRef> { new("Humanizer", "2.14.0.0", null), new("Other", "1.0.0.0", null) } },
            Lib("Humanizer", "2.14.0.0"), Lib("Other", "1.0.0.0"),
        };
        var check = AssemblyChecker.Check(spec, module, new[] { "Test" });
        Assert.Equal(AssemblyVerdict.Ok, FileVerdict(check, "Humanizer"));
        Assert.Equal(AssemblyVerdict.Unknown, FileVerdict(check, "Other"));
        Assert.Single(check.Items, i => i.Verdict == AssemblyVerdict.NotAllowedReference && i.Subject.StartsWith("Other"));
        Assert.False(spec.Blocks);
    }

    [Fact]
    public void Server_modules_ignore_server_assemblies()
    {
        var spec = AssemblySpecStore.Seed(ModuleKind.Server);
        spec.Mode = AssemblySpecMode.AllowList;
        var main = MainAssembly("Approval.servext") with
        {
            References = new List<AssemblyRef>
            {
                new("System.Runtime", "8.0.0.0", "b03f5f7f11d50a3a"),
                new("System.ComponentModel.Composition", "8.0.0.0", "b77a5c561934e089"),
                new("Microsoft.Extensions.Logging.Abstractions", "8.0.0.0", "adb9793829ddae60"),
                new("Ascon.Pilot.ServerExtensions.SDK", "25.5.0.0", null),
                new("Dapper", "2.0.0.0", null),
            },
        };
        var check = AssemblyChecker.Check(spec, new[] { main, Lib("Ascon.Pilot.Common", "26.0.0.0"), Lib("System.Text.Json", "8.0.0.0") },
            new[] { "Approval" }, kind: ModuleKind.Server);
        Assert.Equal(AssemblyVerdict.ProvidedByServer, FileVerdict(check, "Ascon.Pilot.Common"));
        Assert.Equal(AssemblyVerdict.ProvidedByServer, FileVerdict(check, "System.Text.Json"));
        var problem = Assert.Single(check.Problems);
        Assert.StartsWith("Dapper", problem.Subject);
    }

    [Fact]
    public void Shared_module_mode_reports_references_missing_in_shared_module()
    {
        var spec = new AssemblySpec { Mode = AssemblySpecMode.SharedModule, SharedModule = "Libs" };
        spec.Normalize();
        var shared = new SharedModuleState("Libs", "1.0", "", new[] { MainAssembly("Libs.servext"), Lib("Dapper", "2.0.0.0") });
        var main = MainAssembly("Approval.servext") with
        {
            References = new List<AssemblyRef> { new("Dapper", "2.0.0.0", null), new("Polly", "8.0.0.0", null), new("System.Runtime", "8.0.0.0", null) },
        };
        var check = AssemblyChecker.Check(spec, new[] { main }, new[] { "Approval" }, shared, ModuleKind.Server);
        var problem = Assert.Single(check.Problems);
        Assert.Equal(AssemblyVerdict.MissingReference, problem.Verdict);
        Assert.StartsWith("Polly", problem.Subject);

        // Сам общий модуль может содержать любые библиотеки.
        var self = AssemblyChecker.Check(spec, shared.Assemblies, new[] { "Libs" }, shared, ModuleKind.Server);
        Assert.False(self.HasProblems, self.Details);
    }

    [Fact]
    public void Describes_changes()
    {
        var before = AssemblySpecStore.Seed();
        var after = before.Clone();
        after.Entries.Add(new() { Name = "Ookii.Dialogs.Wpf", AssemblyVersion = "5.0.0.0", FileVersion = "5.0.1.*" });
        after.Entries.RemoveAll(e => e.Name == "Ascon.Pilot.Theme");
        after.Mode = AssemblySpecMode.SharedModule;
        after.Normalize();

        var changes = AssemblySpecStore.Describe(before, after);
        Assert.Contains("+ Ookii.Dialogs.Wpf [разрешена] 5.0.0.0 файл 5.0.1.*", changes);
        Assert.Contains("− Ascon.Pilot.Theme", changes);
        Assert.Contains(changes, c => c.StartsWith("режим: Запретить доп. библиотеки"));
    }

    [Fact]
    public void Shared_mode_allows_libraries_only_in_shared_module()
    {
        var spec = new AssemblySpec
        {
            Mode = AssemblySpecMode.SharedModule,
            SharedModule = "Pilot.ClientSdk",
            Entries =
            {
                new() { Name = "Humanizer", AssemblyVersion = "2.14.*" },
                new() { Name = "SQLite.Interop", Modules = { "Reports" } },
                new() { Name = "Newtonsoft.Json", Rule = AssemblyRule.Pilot },
            },
        };
        spec.Normalize();

        var sdk = new[] { MainAssembly("113Pilot.ClientSdk.ext2"), Lib("Humanizer", "2.14.0.0") };
        var sdkCheck = AssemblyChecker.Check(spec, sdk, new[] { "0 - Общий модуль - (Pilot.ClientSdk) - (CICD)" });
        Assert.False(sdkCheck.HasProblems, sdkCheck.Details);

        var module = new[] { MainAssembly("114Test.ext2"), Lib("Humanizer", "2.14.0.0"), Lib("Other", "1.0.0.0"), Lib("Newtonsoft.Json", "13.0.0.0") };
        var check = AssemblyChecker.Check(spec, module, new[] { "Test" });
        Assert.Equal(AssemblyVerdict.SharedOnly, FileVerdict(check, "Humanizer"));
        Assert.Equal(AssemblyVerdict.SharedOnly, check.Items.Single(i => i.Assembly.Name == "Other").Verdict);
        Assert.Equal(AssemblyVerdict.ProvidedByPilot, check.Items.Single(i => i.Assembly.Name == "Newtonsoft.Json").Verdict);

        // Исключение: native-dll разрешена модулю Reports прямо в его zip.
        var reports = AssemblyChecker.Check(spec, new[] { MainAssembly("112Reports.ext2"), Lib("SQLite.Interop", null) }, new[] { "Reports" });
        Assert.False(reports.HasProblems, reports.Details);
    }

    [Fact]
    public void Shared_mode_checks_preload_and_references()
    {
        var spec = new AssemblySpec { Mode = AssemblySpecMode.SharedModule, Entries = { new() { Name = "NodaTime", Rule = AssemblyRule.Pilot } } };
        spec.Normalize();
        var refs = new List<AssemblyRef>
        {
            new("mscorlib", "4.0.0.0", "b77a5c561934e089"),
            new("PresentationFramework", "4.0.0.0", "31bf3856ad364e35"),
            new("Ascon.Pilot.SDK", "26.13.0.0", null),
            new("113Pilot.ClientSdk.ext2", "2.0.0.0", null),
            new("Humanizer", "2.13.0.0", "979442b78dfc278e"),
            new("NodaTime", "3.0.0.0", "4226afe0d9b296d1"),
            new("System.Reactive", "6.0.0.0", "94bc3704cddfc263"),
        };
        var module = new[] { MainAssembly("114Test.ext2") with { References = refs } };
        var sdk = new[] { MainAssembly("113Pilot.ClientSdk.ext2"), Lib("Humanizer", "2.14.0.0") };

        var inPreload = new SharedModuleState("0 - Общий - (Pilot.ClientSdk)", "2.1.0.0", "Preload", sdk);
        var check = AssemblyChecker.Check(spec, module, new[] { "Test" }, inPreload);
        var missing = check.Items.Where(i => i.Verdict == AssemblyVerdict.MissingReference).ToList();
        Assert.Single(missing);
        Assert.StartsWith("System.Reactive", missing[0].Subject);
        Assert.DoesNotContain(check.Items, i => i.Verdict == AssemblyVerdict.SharedModuleSetup);

        var inRoot = inPreload with { Folder = "" };
        Assert.Contains(AssemblyChecker.Check(spec, module, new[] { "Test" }, inRoot).Items, i => i.Verdict == AssemblyVerdict.SharedModuleSetup);
        var none = AssemblyChecker.Check(spec, module, new[] { "Test" }, SharedModuleState.Missing);
        Assert.Contains(none.Items, i => i.Verdict == AssemblyVerdict.SharedModuleSetup && i.Text.Contains("нет общего модуля"));
        Assert.DoesNotContain(none.Items, i => i.Verdict == AssemblyVerdict.MissingReference);

        // Сам общий модуль - только проверка Preload.
        var self = AssemblyChecker.Check(spec, sdk, new[] { "Pilot.ClientSdk" }, inRoot);
        Assert.Contains(self.Items, i => i.Verdict == AssemblyVerdict.SharedModuleSetup && i.Text.Contains("Preload"));
    }

    [Fact]
    public void Duplicate_single_import_exports_are_reported()
    {
        var main = MainAssembly("114Test.ext2") with
        {
            Exports = new[] { "IMenu<ObjectsViewContext>", "IMenu<ObjectsViewContext>", "IDataPlugin", "IDataPlugin", "IMenu<MainViewContext>" },
        };
        var check = AssemblyChecker.Check(new AssemblySpec(), new[] { main }, new[] { "Test" });
        var dup = Assert.Single(check.Items, i => i.Verdict == AssemblyVerdict.DuplicateExport);
        Assert.Contains("IMenu<ObjectsViewContext>", dup.Subject);
    }

    [Theory]
    [InlineData("Ascon.Pilot.SDK.IDataPlugin, Ascon.Pilot.SDK, Version=26.13.0.0, Culture=neutral, PublicKeyToken=null", "IDataPlugin")]
    [InlineData("Ascon.Pilot.SDK.Menu.IMenu`1[[Ascon.Pilot.SDK.ObjectsViewContext, Ascon.Pilot.SDK, Version=26.13.0.0, Culture=neutral, PublicKeyToken=null]], Ascon.Pilot.SDK, Version=26.13.0.0", "IMenu<ObjectsViewContext>")]
    [InlineData("Ascon.Pilot.SDK.Toolbar.IToolbar`1[[Ascon.Pilot.SDK.ObjectsViewContext, Ascon.Pilot.SDK]]", "IToolbar<ObjectsViewContext>")]
    public void Short_type_names(string serialized, string expected) =>
        Assert.Equal(expected, ModulePackage.ShortTypeName(serialized));

    [Fact]
    public void Seed_contains_pilot_client_assemblies()
    {
        var seed = AssemblySpecStore.Seed();
        Assert.Equal(AssemblyRule.Pilot, seed.Find("Newtonsoft.Json")!.Rule);
        Assert.Equal(AssemblyRule.Pilot, seed.Find("Ascon.Pilot.SDK")!.Rule);
        Assert.Equal(0, PilotClientAssemblies.AddTo(seed));
    }

    private static PackageAssembly MainAssembly(string name) => new(name + ".dll", 1, true, name, "1.0.0.0", null, true);

    private static PackageAssembly Lib(string name, string? version) => new(name + ".dll", 1, version is not null, name, version, null, false);

    private static AssemblyVerdict Verdict(AssemblyCheckResult r, string name) => FileVerdict(r, name);

    private static AssemblyVerdict FileVerdict(AssemblyCheckResult r, string name) =>
        r.Items.Single(i => i.IsFile && i.Assembly.Name == name).Verdict;

    /// <summary>Модуль: главная сборка (любая .NET-сборка под именем *.ext2.dll), сторонняя dll, native и чужой модуль.</summary>
    private static byte[] Zip()
    {
        var self = File.ReadAllBytes(typeof(ModuleAssembliesTests).Assembly.Location);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, byte[] bytes)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(bytes);
            }
            Add("114Test.ext2.dll", self);
            Add("xunit.assert.dll", File.ReadAllBytes(typeof(Assert).Assembly.Location));
            Add("x64/SQLite.Interop.dll", new byte[] { 0x4d, 0x5a, 1, 2, 3, 4, 5, 6 });
            Add("113Other.ext2.dll", File.ReadAllBytes(typeof(ModulePackage).Assembly.Location));
            Add("readme.txt", new byte[] { 1 });
        }
        return ms.ToArray();
    }
}
