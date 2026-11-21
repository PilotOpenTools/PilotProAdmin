using System.Diagnostics;
using System.Net;
using System.IO.Compression;
using System.Xml.Linq;
using ProAdmin.Modules;

namespace ProAdmin.Tests;

/// <summary>Каркас нового модуля: имена, проверка формы, файлы; сборка сгенерированного проекта - SCAFFOLD_BUILD=1.</summary>
public sealed class ModuleScaffoldTests
{
    private static ModuleScaffoldRequest Client(bool sdk = true) => new()
    {
        Kind = ModuleKind.Client, Code = "OpenStoradge", Title = "Открыть Storadge", Number = "087", Section = "01",
        UseClientSdk = sdk,
    };

    private static ModuleScaffoldRequest Server() => new()
    {
        Kind = ModuleKind.Server, Code = "SetReviewDate", Title = "Дата рассмотрения",
    };

    [Fact]
    public void NamesFollowStandConventions()
    {
        Assert.Equal("087OpenStoradge.ext2", ModuleScaffold.AssemblyName(Client()));
        Assert.Equal("01.087 - Открыть Storadge - (OpenStoradge) - (CICD)", ModuleScaffold.Product(Client()));
        Assert.Equal("SetReviewDate.servext", ModuleScaffold.AssemblyName(Server()));
        // Модуль на стенде находится по коду в скобках и имени сборки - как у модулей, выложенных CI.
        Assert.Equal("OpenStoradge", ModulePackage.CodeFromName(ModuleScaffold.Product(Client())));
        Assert.Equal("OpenStoradge", ModuleKinds.CoreName(ModuleScaffold.AssemblyName(Client())));
    }

    [Theory]
    [InlineData("1Bad", "Открыть", "087")]
    [InlineData("Bad-Name", "Открыть", "087")]
    [InlineData("Good", "", "087")]
    [InlineData("Good", "Со (скобкой)", "087")]
    [InlineData("Good", "Открыть", "87")]
    [InlineData("Good", "Открыть", "abc")]
    public void InvalidFormIsRejected(string code, string title, string number) =>
        Assert.NotEmpty(ModuleScaffold.Validate(Client() with { Code = code, Title = title, Number = number }));

    [Fact]
    public void ServerModuleNeedsNoNumber() => Assert.Empty(ModuleScaffold.Validate(Server()));

    [Fact]
    public void ClientFilesAreComplete()
    {
        var files = ModuleScaffold.Files(Client()).ToDictionary(f => f.Path, f => f.Content);
        Assert.Contains("OpenStoradge.csproj", files.Keys);
        Assert.Contains("OpenStoradge.sln", files.Keys);
        Assert.Contains("nuget.config", files.Keys);
        Assert.DoesNotContain(".gitlab-ci.yml", files.Keys); // шаблон CI кладёт отдельный коммит в dev
        var project = XDocument.Parse(files["OpenStoradge.csproj"]);
        Assert.Equal("087OpenStoradge.ext2", project.Descendants("AssemblyName").Single().Value);
        Assert.Equal("1.0.0", project.Descendants("Version").Single().Value);
        Assert.Contains(project.Descendants("PackageReference"), p => (string?)p.Attribute("Include") == "Pilot.ClientSdk");
        // Только nuget.org: там Pilot SDK и Pilot.ClientSdk; clear - источники машины разработчика не участвуют.
        var config = XDocument.Parse(files["nuget.config"]);
        Assert.Single(config.Descendants("clear"));
        Assert.Equal("https://api.nuget.org/v3/index.json", (string?)config.Descendants("add").Single().Attribute("value"));
        Assert.Contains("[assembly: AssemblyProduct(\"01.087 - Открыть Storadge - (OpenStoradge) - (CICD)\")]", files["AssemblyInfo.cs"]);
        Assert.Contains("## [1.0.0]", files["CHANGELOG.md"]);
    }

    [Fact]
    public void ClientWithoutSdkHasNoSdkReference()
    {
        var files = ModuleScaffold.Files(Client(sdk: false)).ToDictionary(f => f.Path, f => f.Content);
        Assert.DoesNotContain("Pilot.ClientSdk", files["OpenStoradge.csproj"]);
        Assert.DoesNotContain("Pilot.ClientSdk", files["Pilot/StartPoint.cs"]);
    }

    [Fact]
    public void ServerFilesAreComplete()
    {
        var files = ModuleScaffold.Files(Server()).ToDictionary(f => f.Path, f => f.Content);
        Assert.Contains("SetReviewDateActivity.cs", files.Keys);
        Assert.Contains("Properties/launchSettings.json", files.Keys);
        Assert.Contains("public string Name => \"SetReviewDate\";", files["SetReviewDateActivity.cs"]);
        System.Text.Json.JsonDocument.Parse(files["Properties/launchSettings.json"]);
        Assert.Equal("SetReviewDate.servext", XDocument.Parse(files["SetReviewDate.csproj"]).Descendants("AssemblyName").Single().Value);
    }

    [Fact]
    public void QuotesInTitleDoNotBreakCode()
    {
        var r = Client() with { Title = "Отчёт «по» проекту" };
        Assert.Empty(ModuleScaffold.Validate(r));
        Assert.Contains("Отчёт «по» проекту", ModuleScaffold.Files(r).Single(f => f.Path == "AssemblyInfo.cs").Content);
        // Кавычки и обратная косая черта сломали бы строковые литералы в коде модуля - их форма не пропускает.
        Assert.NotEmpty(ModuleScaffold.Validate(Client() with { Title = "С \"кавычками\"" }));
        Assert.NotEmpty(ModuleScaffold.Validate(Client() with { Title = "С \\ чертой" }));
    }

    /// <summary>Сгенерированные проекты собираются, а zip - тот, что примет админка. Нужен nuget.org; Pilot.ClientSdk - из SCAFFOLD_CLIENTSDK_FEED.</summary>
    [RequiresEnvFact("SCAFFOLD_BUILD")]
    public void GeneratedProjectsBuild()
    {
        var feed = Environment.GetEnvironmentVariable("SCAFFOLD_CLIENTSDK_FEED");
        var cases = new List<ModuleScaffoldRequest> { Server(), Client(sdk: false) };
        if (!string.IsNullOrEmpty(feed))
            cases.Add(Client());
        foreach (var request in cases)
        {
            var dir = Path.Combine(Path.GetTempPath(), "scaffold-" + Guid.NewGuid().ToString("N")[..8]);
            try
            {
                foreach (var (path, content) in ModuleScaffold.Files(request))
                {
                    var file = Path.Combine(dir, path);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    File.WriteAllText(file, content);
                }
                // Pilot.ClientSdk ещё может не дойти до nuget.org: тогда пакет берётся из локальной папки SCAFFOLD_CLIENTSDK_FEED.
                if (request.UseClientSdk && !string.IsNullOrEmpty(feed))
                    File.WriteAllText(Path.Combine(dir, "nuget.config"), ModuleScaffold.NuGetConfig.Replace(
                        "<add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />",
                        $"<add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />\n    <add key=\"local\" value=\"{feed}\" />"));

                var psi = new ProcessStartInfo("dotnet", "build -c Release --nologo -p:CopyToPilot=false") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
                using var process = Process.Start(psi)!;
                var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                Assert.True(process.ExitCode == 0, $"{request.Code}: {output}");

                var zip = Directory.GetFiles(Path.Combine(dir, "bin"), "*.zip").Single();
                var package = ModulePackage.Analyze(File.ReadAllBytes(zip), Path.GetFileName(zip), request.Kind);
                Assert.Equal(ModuleScaffold.AssemblyName(request), package.AssemblyName);
                Assert.Equal(ModuleScaffold.Product(request), package.ExtensionName);
                Assert.Equal("1.0.0", ModuleVersion.Display(package.Version));
                using var archive = ZipFile.OpenRead(zip);
                Assert.DoesNotContain(archive.Entries, e => e.Name.StartsWith("Ascon.Pilot", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
            }
        }
    }
}

public sealed partial class PagesTests
{
    [Theory]
    [InlineData("/Modules/Create")]
    [InlineData("/Modules/Create?kind=server")]
    public async Task CreateModuleNeedsProvenGitLabRights(string url)
    {
        var client = ClientFor(_factory.NewSession(withGitLab: true));
        var response = await client.GetAsync(url);
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        var post = await PostForm(client, url.Contains("server") ? "/Modules?kind=server" : "/Modules", url, ("Code", "Demo"), ("Title", "Демо"), ("Number", "101"));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.DoesNotContain("/Modules/Module", post.Headers.Location!.OriginalString);
    }
}
