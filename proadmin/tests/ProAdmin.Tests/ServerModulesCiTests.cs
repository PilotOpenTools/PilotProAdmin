using System.IO.Compression;
using System.Text.RegularExpressions;
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
    /// Копии шаблона для репозиториев модулей: CICD/server-extensions/ci-template/.gitlab-ci.yml (серверные)
    /// и CICD/client-modules/ci-template/.gitlab-ci.yml (клиентские).
    /// UPDATE_CI_TEMPLATES=1 - перезаписать её из кода.
    /// </summary>
    [RepoCopiesTheory]
    [InlineData(ModuleKind.Server, "server-extensions")]
    [InlineData(ModuleKind.Client, "client-modules")]
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

    /// <summary>
    /// Резервная загрузка пакетов NuGet по внутреннему адресу GitLab: список пакетов закрытого проекта анонимно пуст,
    /// поэтому он запрашивается с токеном задания, а пустой или неполученный список - ошибка, а не тихий пропуск.
    /// </summary>
    [Theory]
    [InlineData(ModuleKind.Server)]
    [InlineData(ModuleKind.Client)]
    public void Package_list_of_private_feed_uses_job_token(ModuleKind kind)
    {
        var yml = ModuleCiTemplate.Content(new AdminSettings().Also(s => s.Normalize()), kind).Replace("\r\n", "\n");
        var list = yml.Split('\n').Where(l => l.Contains("/packages?package_type=nuget")).ToList();
        Assert.NotEmpty(list);
        Assert.Contains("JOB-TOKEN: $CI_JOB_TOKEN", list[0]);
        Assert.Contains("не получен или пуст", yml);
    }

    /// <summary>
    /// Runner выполняет script с set -eo pipefail: X=$(... | grep ...) без совпадений (например, в новом репозитории
    /// без тегов v*) молча завершает job с кодом 1. Такие подстановки обязаны иметь запасной вариант (|| true),
    /// кроме блоков, где сделано set +e.
    /// </summary>
    [Theory]
    [InlineData(ModuleKind.Server)]
    [InlineData(ModuleKind.Client)]
    public void Template_grep_substitutions_survive_pipefail(ModuleKind kind)
    {
        var lines = ModuleCiTemplate.Content(new AdminSettings().Also(s => s.Normalize()), kind).Replace("\r\n", "\n").Split('\n');
        var bad = new List<string>();
        int blockIndent = -1; var safe = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var indent = line.Length - line.TrimStart().Length;
            if (line.TrimStart() == "- |") { blockIndent = indent; safe = false; continue; }
            if (blockIndent < 0) continue;
            if (line.Trim().Length > 0 && indent <= blockIndent) { blockIndent = -1; continue; }
            if (line.Contains("set +e")) safe = true;
            if (safe || line.TrimStart().StartsWith('#')) continue;
            foreach (var sub in AssignedSubstitutions(line))
                if (Regex.IsMatch(sub, @"\|\s*grep\b") && !sub.Contains("||"))
                    bad.Add($"строка {i + 1}: {sub}");
        }
        Assert.True(bad.Count == 0, "grep в $(...) без || true:\n" + string.Join("\n", bad));
    }

    /// <summary>Тексты подстановок NAME=$(...) в строке (скобки внутри кавычек не считаются).</summary>
    private static IEnumerable<string> AssignedSubstitutions(string line)
    {
        foreach (Match m in Regex.Matches(line, @"[A-Za-z_][A-Za-z0-9_]*=\$\("))
        {
            int depth = 1, j = m.Index + m.Length; char quote = '\0';
            for (; j < line.Length && depth > 0; j++)
            {
                var c = line[j];
                if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
                if (c is '\'' or '"') quote = c;
                else if (c == '(') depth++;
                else if (c == ')') depth--;
            }
            yield return line[(m.Index + m.Length)..Math.Max(m.Index + m.Length, j - 1)];
        }
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
