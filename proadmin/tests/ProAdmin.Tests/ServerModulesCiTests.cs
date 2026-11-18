using System.IO.Compression;
using ProAdmin.Modules;
using ProAdmin.Pilot.ServerModules;
using ProAdmin.Settings;
using Xunit;

namespace ProAdmin.Tests;

/// <summary>CI/CD серверных модулей: та же схема веток, что у клиентских, но *.servext.dll и выкладка в ServerHost.</summary>
public class ServerModulesCiTests
{
    private static AdminSettings Settings()
    {
        var s = new AdminSettings { ModulesGitLabGroupUrl = "http://gitlab/client-modules", ServerModulesGitLabGroupUrl = "http://gitlab/server-modules" };
        s.Normalize();
        return s;
    }

    [Fact]
    public void Server_template_builds_servext_and_deploys_to_host()
    {
        var yml = ModuleCiTemplate.Content(Settings(), ModuleKind.Server);

        Assert.Contains("*.servext.dll", yml);
        Assert.DoesNotContain(".ext2", yml);
        Assert.Contains(ModuleCiDeployEndpoint.ServerRoute, yml);
        Assert.DoesNotContain(ModuleCiDeployEndpoint.Route + "\"", yml);
        Assert.Contains(ModuleCiLibrariesEndpoint.ServerRoute, yml);
        Assert.DoesNotContain(ModuleCiLibrariesEndpoint.Route + "\"", yml);
        Assert.DoesNotContain("__", yml);
        // Схема веток и контуров - как у клиентских модулей.
        foreach (var part in new[] { "deploy_test:", "deploy_stage:", "deploy_prod:", "check_version:", "build_module:", "check_libraries:", "- job: check_libraries", "$CI_COMMIT_BRANCH == \"dev\"", "/^release\\//" })
            Assert.Contains(part, yml);
        // Тег и GitLab Release ставит веб-админка - job release в CI падал бы «already exists».
        Assert.DoesNotContain("tag_name:", yml);
    }

    [Fact]
    public void Server_template_signs_zip_only_when_key_is_set()
    {
        var yml = ModuleCiTemplate.Content(Settings(), ModuleKind.Server);

        Assert.Contains("MODULE_SIGNING_KEY", yml);
        Assert.Contains("signature.json", yml);
        // Ключа нет - предупреждение и сборка как раньше, а не ошибка.
        Assert.Contains("[ -z \"${MODULE_SIGNING_KEY:-}\" ]", yml);
        // Подпись ставится после сборки zip и до его вывода в журнал и артефактов.
        var begin = yml.IndexOf("# sign:begin", StringComparison.Ordinal);
        Assert.True(begin > yml.IndexOf("Compress-Archive", StringComparison.Ordinal));
        Assert.True(yml.IndexOf("# sign:end", StringComparison.Ordinal) < yml.IndexOf("-exec ls -la", StringComparison.Ordinal));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(yml, "# sign:begin"));
    }

    [Fact]
    public void Client_template_has_no_signing()
    {
        var yml = ModuleCiTemplate.Content(Settings(), ModuleKind.Client);

        Assert.DoesNotContain("MODULE_SIGNING_KEY", yml);
        Assert.DoesNotContain("signature", yml);
    }

    [Fact]
    public void Server_package_with_signature_file_is_still_analyzed()
    {
        var dll = File.ReadAllBytes(typeof(ModulePackage).Assembly.Location);
        var zip = Zip(("ProAdmin.servext.dll", dll), ("signature.json", System.Text.Encoding.UTF8.GetBytes("{}")));

        var info = ModulePackage.Analyze(zip, "ProAdmin.servext.zip", ModuleKind.Server);

        Assert.Equal("PilotProAdmin", info.AssemblyName);
        Assert.DoesNotContain(info.Assemblies, a => a.Path.Contains("signature"));
    }

    [Fact]
    public void Client_template_is_unchanged_by_kind()
    {
        var s = Settings();
        Assert.Equal(ModuleCiTemplate.Content(s), ModuleCiTemplate.Content(s, ModuleKind.Client));
        Assert.Contains(ModuleCiDeployEndpoint.Route, ModuleCiTemplate.Content(s));
    }

    [Theory]
    [InlineData("dev", "test")]
    [InlineData("release/2.1.0", "stage")]
    [InlineData("main", "prod")]
    [InlineData("feature/KTN-5", null)]
    public void Branch_maps_to_environment(string reference, string? env) =>
        Assert.Equal(env, ModuleCiDeployEndpoint.EnvForRef(reference));

    [Fact]
    public void Kind_selects_group_and_job_key()
    {
        var s = Settings();
        Assert.Equal("server-modules", ModuleKind.Server.GroupPath(s));
        Assert.Equal("client-modules", ModuleKind.Client.GroupPath(s));
        Assert.Equal("server-module:test", ModuleKind.Server.JobEnv("test"));
        Assert.Equal("module:test", ModuleKind.Client.JobEnv("test"));
        Assert.Equal(ModuleKind.Server, ModuleKinds.Parse("server"));
        Assert.Equal(ModuleKind.Client, ModuleKinds.Parse(null));
    }

    [Theory]
    [InlineData("087OpenStoradge.ext2", "OpenStoradge")]
    [InlineData("ApprovalDocumentVersionSaving.servext", "ApprovalDocumentVersionSaving")]
    [InlineData("Plain", "Plain")]
    public void Core_name_drops_kind_suffix(string assembly, string core) =>
        Assert.Equal(core, ModuleKinds.CoreName(assembly));

    [Fact]
    public void Server_package_requires_servext()
    {
        var dll = File.ReadAllBytes(typeof(ModulePackage).Assembly.Location);
        var zip = Zip(("ProAdmin.servext.dll", dll));

        var info = ModulePackage.Analyze(zip, "ProAdmin.servext.zip", ModuleKind.Server);
        Assert.Equal("PilotProAdmin", info.AssemblyName);

        var ex = Assert.Throws<InvalidDataException>(() => ModulePackage.Analyze(zip, "x.zip", ModuleKind.Client));
        Assert.Contains("ext2", ex.Message);
        Assert.Throws<InvalidDataException>(() => ModulePackage.Analyze(Zip(("A.ext2.dll", dll)), "a.zip", ModuleKind.Server));
    }

    [Fact]
    public void Server_module_id_is_stable_per_name()
    {
        Assert.Equal(ServerModuleTarget.IdFor("ApprovalDocumentVersionSaving"), ServerModuleTarget.IdFor("approvaldocumentversionsaving"));
        Assert.NotEqual(ServerModuleTarget.IdFor("A"), ServerModuleTarget.IdFor("B"));
    }

    /// <summary>
    /// Копии шаблона для репозиториев модулей: CICD/Серверные расширения/ci-template/.gitlab-ci.yml (серверные)
    /// и CICD/Клиентские модули/ci-template/.gitlab-ci.yml (клиентские).
    /// UPDATE_CI_TEMPLATES=1 - перезаписать её из кода.
    /// </summary>
    [RepoCopiesTheory]
    [InlineData(ModuleKind.Server, "Серверные расширения")]
    [InlineData(ModuleKind.Client, "Клиентские модули")]
    public void Template_copy_is_in_sync(ModuleKind kind, string folder)
    {
        var copy = Path.Combine(RepoCopies.Root(), folder, "ci-template", ".gitlab-ci.yml");
        // Переводы строк не сравниваются: .yml лежат в LF (.gitattributes), а исходник на Windows может быть в CRLF.
        var expected = ModuleCiTemplate.Content(new AdminSettings().Also(s => s.Normalize()), kind).Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("UPDATE_CI_TEMPLATES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.WriteAllText(copy, expected);
        }
        Assert.True(File.Exists(copy), $"Нет копии шаблона {copy} (UPDATE_CI_TEMPLATES=1 создаст её).");
        Assert.Equal(expected, File.ReadAllText(copy).Replace("\r\n", "\n"));
    }

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

internal static class TestObjectExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
