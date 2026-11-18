using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Xunit;
using ServerHost.Hosting;

namespace ServerHost.Tests;

/// <summary>
/// Шаг подписи из ci-template: берётся прямо из .gitlab-ci.yml, запускается в контейнере образа SDK (как в CI), а подписанный им zip проверяет код хоста.
/// Нужен Docker и образ SDK (TEST_SDK_IMAGE, по умолчанию mcr.microsoft.com/dotnet/sdk:10.0), сам образ тест не скачивает.
/// SKIP_DOCKER_TESTS=1 - пропустить явно.
/// </summary>
public sealed class SigningCiStepTests : IDisposable
{
    private static readonly string Image = Environment.GetEnvironmentVariable("TEST_SDK_IMAGE") is { Length: > 0 } image
        ? image : "mcr.microsoft.com/dotnet/sdk:10.0";

    private readonly string _work = Path.Combine(Path.GetTempPath(), "sign-tests", Guid.NewGuid().ToString("N"));

    public SigningCiStepTests() => Directory.CreateDirectory(Path.Combine(_work, "bin"));

    public void Dispose() => ModuleStorage.DeleteDirectory(_work);

    private static (int Code, string Output) Docker(IEnumerable<string> args, IDictionary<string, string>? env = null)
    {
        var info = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        foreach (var (name, value) in env ?? new Dictionary<string, string>())
            info.Environment[name] = value;
        using var process = Process.Start(info)!;
        var stderr = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output + stderr.Result);
    }

    private static bool DockerReady()
    {
        try
        {
            return Environment.GetEnvironmentVariable("SKIP_DOCKER_TESTS") != "1" && Docker(new[] { "image", "inspect", Image }).Code == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false; // docker не установлен
        }
    }

    /// <summary>Строки между # sign:begin и # sign:end из шаблона, без отступа блока YAML (6 пробелов).</summary>
    private static string StepScript()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "ci-template")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var lines = File.ReadAllText(Path.Combine(dir!, "ci-template", ".gitlab-ci.yml")).Replace("\r\n", "\n").Split('\n');
        var begin = Array.FindIndex(lines, l => l.Contains("# sign:begin"));
        var end = Array.FindIndex(lines, l => l.Contains("# sign:end"));
        Assert.True(begin >= 0 && end > begin, "в ci-template нет шага подписи");
        return string.Join("\n", lines[begin..(end + 1)].Select(l => l.Length >= 6 ? l[6..] : l)) + "\n";
    }

    private (int Code, string Output) RunStep(IDictionary<string, string>? env, params string[] dockerEnvNames)
    {
        File.WriteAllText(Path.Combine(_work, "step.sh"), StepScript());
        var args = new List<string> { "run", "--rm", "--name", "zz7-sign-" + Guid.NewGuid().ToString("N")[..8], "-v", _work + ":/work", "-w", "/work" };
        foreach (var name in dockerEnvNames)
            args.AddRange(new[] { "-e", name });
        args.AddRange(new[] { Image, "bash", "-e", "/work/step.sh" });
        return Docker(args, env);
    }

    private (string Private, string Public) NewKeyPair()
    {
        var (code, output) = Docker(new[] { "run", "--rm", "--name", "zz7-sign-" + Guid.NewGuid().ToString("N")[..8], "-v", _work + ":/work", "-w", "/work", Image, "sh", "-c",
            "openssl ecparam -name prime256v1 -genkey -noout -out key.pem && openssl ec -in key.pem -pubout -out pub.pem" });
        Assert.True(code == 0, output);
        return (File.ReadAllText(Path.Combine(_work, "key.pem")), File.ReadAllText(Path.Combine(_work, "pub.pem")));
    }

    private string ZipPath => Path.Combine(_work, "bin", "Sample.zip");

    private byte[] WriteModuleZip()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in new[] { ("Sample.servext.dll", SampleModule.Assembly), ("data/справка.txt", Encoding.UTF8.GetBytes("привет")), ("empty.txt", Array.Empty<byte>()) })
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(data);
            }
        File.WriteAllBytes(ZipPath, ms.ToArray());
        return ms.ToArray();
    }

    private static ArchiveInspector.Result Inspect(byte[] zip, string publicPem) =>
        ArchiveInspector.Inspect(zip, "m.zip", new ServerAssemblies(Array.Empty<(string, Version?)>()), false, trusted: TrustedKeys.Parse(publicPem));

    [Fact]
    public void Ci_step_signs_zip_and_host_accepts_it_for_every_key_form()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");
        var (privatePem, publicPem) = NewKeyPair();
        var expectedKeyId = ArchiveSignature.KeyId(ECDsa.Create().Also(k => k.ImportFromPem(publicPem)).ExportSubjectPublicKeyInfo());

        var forms = new Dictionary<string, string>
        {
            ["PEM в переменной"] = privatePem,
            ["файл (тип File)"] = "/work/key.pem",
            ["PEM в base64 (Masked)"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(privatePem)),
        };
        foreach (var (form, value) in forms)
        {
            WriteModuleZip();
            var (code, output) = RunStep(new Dictionary<string, string> { ["MODULE_SIGNING_KEY"] = value }, "MODULE_SIGNING_KEY");
            Assert.True(code == 0, $"{form}: {output}");
            Assert.Contains("Подписан Sample.zip", output);

            var result = Inspect(File.ReadAllBytes(ZipPath), publicPem);
            Assert.True(result.Report.Errors.Count == 0, $"{form}: " + string.Join("; ", result.Report.Errors));
            Assert.Equal(expectedKeyId, result.SignedBy);
            Assert.Equal("Sample", result.Report.ModuleName);
        }
    }

    [Fact]
    public void Ci_step_signs_only_module_zip_and_leaves_other_zips_in_bin()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");
        var (privatePem, publicPem) = NewKeyPair();
        WriteModuleZip();
        // Посторонний zip крупнее модуля, но без .servext/.ext2 в имени: выбор тот же, что у админки, - подписан должен быть только модуль.
        var otherPath = Path.Combine(_work, "bin", "data-pack.zip");
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            using (var s = zip.CreateEntry("blob.bin").Open())
                s.Write(new byte[200_000]);
            File.WriteAllBytes(otherPath, ms.ToArray());
        }
        var otherBefore = File.ReadAllBytes(otherPath);

        var (code, output) = RunStep(new Dictionary<string, string> { ["MODULE_SIGNING_KEY"] = privatePem }, "MODULE_SIGNING_KEY");

        Assert.True(code == 0, output);
        Assert.Equal(otherBefore, File.ReadAllBytes(otherPath));
        using (var other = ZipFile.OpenRead(otherPath))
            Assert.Null(other.GetEntry("signature.json"));
        var result = Inspect(File.ReadAllBytes(ZipPath), publicPem);
        Assert.True(result.Report.Errors.Count == 0, string.Join("; ", result.Report.Errors));
        Assert.NotNull(result.SignedBy);
    }

    [Fact]
    public void Ci_step_signing_twice_replaces_signature()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");
        var (privatePem, publicPem) = NewKeyPair();
        WriteModuleZip();
        var env = new Dictionary<string, string> { ["MODULE_SIGNING_KEY"] = privatePem };

        Assert.Equal(0, RunStep(env, "MODULE_SIGNING_KEY").Code);
        Assert.Equal(0, RunStep(env, "MODULE_SIGNING_KEY").Code);

        using (var zip = ZipFile.OpenRead(ZipPath))
            Assert.Single(zip.Entries, e => e.FullName == ArchiveSignature.FileName);
        Assert.Empty(Inspect(File.ReadAllBytes(ZipPath), publicPem).Report.Errors);
    }

    [Fact]
    public void Ci_step_without_key_warns_and_leaves_zip_unchanged()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");
        var original = WriteModuleZip();

        var (code, output) = RunStep(null);

        Assert.Equal(0, code);
        Assert.Contains("MODULE_SIGNING_KEY не задан", output);
        Assert.Equal(original, File.ReadAllBytes(ZipPath));
    }

    [Fact]
    public void Ci_step_with_unusable_key_fails_the_job_and_does_not_publish_unsigned_zip_silently()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");
        WriteModuleZip();

        var (code, output) = RunStep(new Dictionary<string, string> { ["MODULE_SIGNING_KEY"] = "BEGIN это не ключ" }, "MODULE_SIGNING_KEY");

        Assert.NotEqual(0, code);
        Assert.Contains("Ошибка подписи модуля", output);
    }

    [Fact]
    public void Ci_step_rejects_key_of_another_curve()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");
        WriteModuleZip();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        var (code, output) = RunStep(new Dictionary<string, string> { ["MODULE_SIGNING_KEY"] = p384.ExportECPrivateKeyPem() }, "MODULE_SIGNING_KEY");

        Assert.NotEqual(0, code);
        Assert.Contains("P-256", output);
    }

    [Fact]
    public void Key_made_by_readme_command_is_a_valid_trusted_key()
    {
        Assert.SkipUnless(DockerReady(), $"нет Docker или образа {Image} (TEST_SDK_IMAGE)");

        var (_, publicPem) = NewKeyPair();

        var keys = TrustedKeys.Parse(publicPem);
        Assert.True(keys.Enabled);
        Assert.Single(keys.Ids);
        Assert.Empty(keys.Problems);
    }
}

internal static class SigningTestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
