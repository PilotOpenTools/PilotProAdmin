using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using ServerHost.Hosting;

namespace ServerHost.Tests;

/// <summary>Подпись архивов модулей и доверенные ключи: без ключей (как раньше), с ключом, подмена, лишний файл, чужой ключ, битая подпись.</summary>
public sealed class ArchiveSignatureTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "host-tests", Guid.NewGuid().ToString("N"));
    private static readonly ServerAssemblies NoServerAssemblies = new(Array.Empty<(string, Version?)>());

    public void Dispose() => ModuleStorage.DeleteDirectory(_root);

    // ------------------------------------------------------------------ вспомогательное: независимая от хоста запись формата подписи

    private static ECDsa NewKey() => ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static TrustedKeys Trust(params ECDsa[] keys) => TrustedKeys.Parse(keys.Select(k => k.ExportSubjectPublicKeyInfoPem()).ToArray());

    private static byte[] ModuleZip(params (string Path, byte[] Data)[] extra) => BuildZip(
        new[] { ("Sample.servext.dll", SampleModule.Assembly), ("data/readme.txt", Encoding.UTF8.GetBytes("привет")) }.Concat(extra).ToArray());

    private static byte[] BuildZip(params (string Path, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (path, data) in files)
            {
                using var s = zip.CreateEntry(path).Open();
                s.Write(data);
            }
        return ms.ToArray();
    }

    /// <summary>Подписывает zip так, как это делает шаг CI: манифест «sha256␣␣путь», DER-подпись, файл signature.json.</summary>
    private static byte[] Sign(byte[] zip, ECDsa key, Action<Dictionary<string, object?>>? tamper = null, Func<string, string>? manifestOf = null)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        using (var source = new ZipArchive(new MemoryStream(zip)))
            foreach (var e in source.Entries.Where(e => e.Name.Length > 0 && e.FullName != ArchiveSignature.FileName))
            {
                using var s = e.Open();
                files[e.FullName] = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
            }
        var manifest = string.Concat(files.Select(f => $"{f.Value}  {f.Key}\n"));
        var spki = key.ExportSubjectPublicKeyInfo();
        var doc = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["algorithm"] = "ECDSA-P256-SHA256",
            ["keyId"] = Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
            ["publicKey"] = Convert.ToBase64String(spki),
            ["files"] = files,
            ["signature"] = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(manifestOf?.Invoke(manifest) ?? manifest),
                HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)),
        };
        tamper?.Invoke(doc);
        return WithEntry(zip, ArchiveSignature.FileName, JsonSerializer.SerializeToUtf8Bytes(doc));
    }

    private static byte[] WithEntry(byte[] zip, string path, byte[] data)
    {
        using var ms = new MemoryStream();
        ms.Write(zip);
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            using var s = archive.CreateEntry(path).Open();
            s.Write(data);
        }
        return ms.ToArray();
    }

    private static byte[] Replace(byte[] zip, string path, byte[] data)
    {
        using var source = new ZipArchive(new MemoryStream(zip));
        return BuildZip(source.Entries.Where(e => e.Name.Length > 0).Select(e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return (e.FullName, e.FullName == path ? data : ms.ToArray());
        }).ToArray());
    }

    private static byte[] Without(byte[] zip, string path)
    {
        using var source = new ZipArchive(new MemoryStream(zip));
        return BuildZip(source.Entries.Where(e => e.Name.Length > 0 && e.FullName != path).Select(e =>
        {
            using var s = e.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return (e.FullName, ms.ToArray());
        }).ToArray());
    }

    private static ArchiveInspector.Result Inspect(byte[] zip, TrustedKeys? trusted = null, bool strip = false) =>
        ArchiveInspector.Inspect(zip, "m.zip", NoServerAssemblies, strip, trusted: trusted);

    private static string KeyIdOf(ECDsa key) => ArchiveSignature.KeyId(key.ExportSubjectPublicKeyInfo());

    // ------------------------------------------------------------------ без доверенных ключей: поведение как раньше

    [Fact]
    public void NoKeys_UnsignedArchive_IsAcceptedWithoutWarnings()
    {
        var result = Inspect(ModuleZip(), TrustedKeys.None);

        Assert.Empty(result.Report.Errors);
        Assert.Empty(result.Report.Warnings);
        Assert.Null(result.SignedBy);
    }

    [Fact]
    public void NoKeys_ValidSignature_IsAcceptedWithAuthenticityWarning()
    {
        using var key = NewKey();

        var result = Inspect(Sign(ModuleZip(), key));

        Assert.Empty(result.Report.Errors);
        Assert.Equal(KeyIdOf(key), result.SignedBy);
        Assert.Contains(result.Report.Warnings, w => w.Contains("подлинность подписи не проверяется") && w.Contains(KeyIdOf(key)[..16]));
        Assert.DoesNotContain(ArchiveSignature.FileName, result.KeepEntries); // подпись в модуль не распаковывается
        Assert.DoesNotContain(result.Report.Files, f => f.Path == ArchiveSignature.FileName);
    }

    [Fact]
    public void NoKeys_TamperedSignedArchive_IsRejected()
    {
        using var key = NewKey();
        var signed = Sign(ModuleZip(), key);

        var result = Inspect(Replace(signed, "data/readme.txt", Encoding.UTF8.GetBytes("подмена")));

        Assert.Contains(result.Report.Errors, e => e.Contains("изменён после подписания") && e.Contains("data/readme.txt"));
        Assert.Null(result.SignedBy);
    }

    [Fact]
    public void NoKeys_BrokenSignatureJson_IsRejected()
    {
        var result = Inspect(WithEntry(ModuleZip(), ArchiveSignature.FileName, Encoding.UTF8.GetBytes("{ это не json")));

        Assert.Contains(result.Report.Errors, e => e.Contains("повреждён"));
    }

    // ------------------------------------------------------------------ с доверенным ключом

    [Fact]
    public void Keys_ValidSignature_IsAcceptedWithoutWarnings()
    {
        using var key = NewKey();

        var result = Inspect(Sign(ModuleZip(), key), Trust(key));

        Assert.Empty(result.Report.Errors);
        Assert.Empty(result.Report.Warnings);
        Assert.Equal(KeyIdOf(key), result.SignedBy);
        Assert.Equal("Sample", result.Report.ModuleName);
    }

    [Fact]
    public void Keys_OneOfSeveralTrustedKeys_IsEnough()
    {
        using var first = NewKey();
        using var second = NewKey();

        Assert.Empty(Inspect(Sign(ModuleZip(), second), Trust(first, second)).Report.Errors);
    }

    [Fact]
    public void Keys_UnsignedArchive_IsRejected()
    {
        using var key = NewKey();

        var result = Inspect(ModuleZip(), Trust(key));

        Assert.Contains(result.Report.Errors, e => e.Contains("Архив не подписан") && e.Contains("MODULE_SIGNING_KEY"));
        Assert.Equal(result.SignatureError, result.Report.Errors.First());
    }

    [Fact]
    public void Keys_FileChangedAfterSigning_IsRejected()
    {
        using var key = NewKey();
        var signed = Sign(ModuleZip(), key);

        var result = Inspect(Replace(signed, "Sample.servext.dll", SampleModule.Assembly.Append((byte)0).ToArray()), Trust(key));

        Assert.Contains(result.Report.Errors, e => e.Contains("изменён после подписания") && e.Contains("Sample.servext.dll"));
        Assert.Null(result.SignedBy);
    }

    [Fact]
    public void Keys_ExtraFile_IsRejected()
    {
        using var key = NewKey();
        var signed = Sign(ModuleZip(), key);

        var result = Inspect(WithEntry(signed, "evil.dll", new byte[] { 1, 2, 3 }), Trust(key));

        Assert.Contains(result.Report.Errors, e => e.Contains("добавлен после подписания") && e.Contains("evil.dll"));
    }

    [Fact]
    public void Keys_MissingFile_IsRejected()
    {
        using var key = NewKey();
        var signed = Sign(ModuleZip(), key);

        var result = Inspect(Without(signed, "data/readme.txt"), Trust(key));

        Assert.Contains(result.Report.Errors, e => e.Contains("удалён из архива после подписания") && e.Contains("data/readme.txt"));
    }

    [Fact]
    public void Keys_ForeignKey_IsRejected()
    {
        using var mine = NewKey();
        using var foreign = NewKey();

        var result = Inspect(Sign(ModuleZip(), foreign), Trust(mine));

        Assert.Contains(result.Report.Errors, e => e.Contains("неизвестным ключом") && e.Contains(KeyIdOf(foreign)[..16]));
        Assert.Null(result.SignedBy);
    }

    [Fact]
    public void Keys_ForeignKeyCannotBeBlessedByCopyingTrustedKeyId()
    {
        using var mine = NewKey();
        using var foreign = NewKey();

        // Злоумышленник подписывает своим ключом, но пишет в файле отпечаток доверенного.
        var forged = Sign(ModuleZip(), foreign, doc => doc["keyId"] = KeyIdOf(mine));

        var result = Inspect(forged, Trust(mine));

        Assert.Contains(result.Report.Errors, e => e.Contains("не соответствует публичному ключу"));
    }

    [Fact]
    public void Keys_TrustedKeyWithSubstitutedPublicKey_IsRejected()
    {
        using var mine = NewKey();
        using var foreign = NewKey();

        // Подпись чужая, а в файле - открытый ключ доверенного: подпись не сойдётся.
        var forged = Sign(ModuleZip(), foreign, doc =>
        {
            doc["publicKey"] = Convert.ToBase64String(mine.ExportSubjectPublicKeyInfo());
            doc["keyId"] = KeyIdOf(mine);
        });

        Assert.Contains(Inspect(forged, Trust(mine)).Report.Errors, e => e.Contains("подпись не сходится"));
    }

    [Fact]
    public void Keys_ManifestEditedInSignatureFile_IsRejected()
    {
        using var key = NewKey();

        // Список файлов в подписи дополнен файлом, которого в подписанном манифесте не было.
        var forged = Sign(ModuleZip(), key, doc => ((SortedDictionary<string, string>)doc["files"]!)["evil.dll"] = new string('0', 64));

        Assert.Contains(Inspect(forged, Trust(key)).Report.Errors, e => e.Contains("подпись не сходится"));
    }

    [Fact]
    public void Keys_SignatureBytesFlipped_IsRejected()
    {
        using var key = NewKey();
        var forged = Sign(ModuleZip(), key, doc =>
        {
            var sig = Convert.FromBase64String((string)doc["signature"]!);
            sig[^1] ^= 0xFF;
            doc["signature"] = Convert.ToBase64String(sig);
        });

        Assert.Contains(Inspect(forged, Trust(key)).Report.Errors, e => e.Contains("Подпись архива неверна"));
    }

    [Theory]
    [InlineData("{ это не json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"algorithm\":\"ECDSA-P256-SHA256\",\"publicKey\":\"%%%\",\"signature\":\"%%%\",\"files\":{}}")]
    [InlineData("{\"algorithm\":\"RSA\",\"publicKey\":\"AA==\",\"signature\":\"AA==\",\"files\":{}}")]
    public void Keys_BrokenSignatureFile_IsRejected(string json)
    {
        using var key = NewKey();

        var result = Inspect(WithEntry(ModuleZip(), ArchiveSignature.FileName, Encoding.UTF8.GetBytes(json)), Trust(key));

        Assert.NotEmpty(result.Report.Errors);
        Assert.Null(result.SignedBy);
    }

    [Fact]
    public void Keys_NotP256Key_IsRejected()
    {
        using var trustedKey = NewKey();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        var result = Inspect(Sign(ModuleZip(), p384), Trust(trustedKey));

        Assert.Contains(result.Report.Errors, e => e.Contains("P-256"));
    }

    [Fact]
    public void Keys_PathWithNewline_IsRejected()
    {
        using var key = NewKey();
        var signed = Sign(ModuleZip(("a\nb.txt", new byte[] { 1 })), key);

        Assert.Contains(Inspect(signed, Trust(key)).Report.Errors, e => e.Contains("недопустимый символ в пути"));
    }

    [Fact]
    public void Signed_DuplicateEntryPath_IsRejected()
    {
        using var key = NewKey();
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var data in new[] { new byte[] { 1 }, new byte[] { 2 } })
            {
                using var s = zip.CreateEntry("Sample.servext.dll").Open();
                s.Write(data);
            }

        // Манифест строится по пути, поэтому неоднозначный подписанный архив не принимается.
        var result = Inspect(WithEntry(ms.ToArray(), ArchiveSignature.FileName, Encoding.UTF8.GetBytes("{}")), Trust(key));

        Assert.Contains(result.Report.Errors, e => e.Contains("Путь повторяется"));
    }

    [Fact]
    public void Keys_ServerAssemblyStripping_DoesNotBreakSignature()
    {
        using var key = NewKey();
        var newtonsoft = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Newtonsoft.Json.dll"));
        var signed = Sign(ModuleZip(("Newtonsoft.Json.dll", newtonsoft)), key);
        var server = ServerAssemblies.Capture();

        // Подпись покрывает архив целиком, как он загружен; серверную сборку хост выбрасывает уже после проверки.
        var result = ArchiveInspector.Inspect(signed, "m.zip", server, stripServerAssemblies: true, trusted: Trust(key));

        Assert.Empty(result.Report.Errors);
        Assert.DoesNotContain("Newtonsoft.Json.dll", result.KeepEntries);
        Assert.Equal(KeyIdOf(key), result.SignedBy);
    }

    [Fact]
    public void Signature_RespectsUncompressedLimit()
    {
        using var key = NewKey();
        var signed = Sign(ModuleZip(("big.bin", new byte[3 * 1024 * 1024])), key);

        var result = ArchiveInspector.Inspect(signed, "m.zip", NoServerAssemblies, false, maxUncompressedBytes: 1024 * 1024, trusted: Trust(key));

        Assert.Contains(result.Report.Errors, e => e.Contains("Распакованный размер"));
    }

    // ------------------------------------------------------------------ доверенные ключи: откуда читаются и что считается ошибкой

    [Fact]
    public void TrustedKeys_NothingConfigured_IsDisabled()
    {
        var keys = TrustedKeys.Load(_root, fromEnvironment: null);

        Assert.False(keys.Enabled);
        Assert.Empty(keys.Ids);
        Assert.False(TrustedKeys.Load(_root, "  \n ").Enabled);
    }

    [Fact]
    public void TrustedKeys_LoadsFromFileAndEnvironmentTogether()
    {
        using var fromFile = NewKey();
        using var fromEnvironment = NewKey();
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, TrustedKeys.FileName), fromFile.ExportSubjectPublicKeyInfoPem());

        var keys = TrustedKeys.Load(_root, fromEnvironment.ExportSubjectPublicKeyInfoPem());

        Assert.True(keys.Enabled);
        Assert.Equal(new[] { KeyIdOf(fromFile), KeyIdOf(fromEnvironment) }.Order(), keys.Ids.Order());
        Assert.Empty(keys.Problems);
    }

    [Fact]
    public void TrustedKeys_EnvironmentValueWithEscapedNewlines_IsParsed()
    {
        using var key = NewKey();
        var oneLine = key.ExportSubjectPublicKeyInfoPem().Replace("\r\n", "\n").Replace("\n", "\\n");

        Assert.Equal(new[] { KeyIdOf(key) }, TrustedKeys.Parse(oneLine).Ids);
    }

    [Fact]
    public void TrustedKeys_PrivateKeyOrGarbage_LeavesProtectionOnWithoutKeys()
    {
        using var key = NewKey();

        var privateKey = TrustedKeys.Parse(key.ExportECPrivateKeyPem());
        Assert.True(privateKey.Enabled);
        Assert.Empty(privateKey.Ids);
        Assert.Contains(privateKey.Problems, p => p.Contains("PUBLIC KEY") && p.Contains("закрытый"));

        var garbage = TrustedKeys.Parse("просто текст");
        Assert.True(garbage.Enabled);
        Assert.Empty(garbage.Ids);
        Assert.NotEmpty(garbage.Problems);

        // Защита включена, но ни одного ключа: принимать нечего - иначе опечатка в ключе тихо отключала бы проверку.
        var result = Inspect(ModuleZip(), garbage);
        Assert.Contains(result.Report.Errors, e => e.Contains("ни один доверенный ключ не прочитан"));
    }

    [Fact]
    public void TrustedKeys_BadBlockNextToGoodOne_IsWarningOnly()
    {
        using var key = NewKey();
        var keys = TrustedKeys.Parse(key.ExportECPrivateKeyPem() + Environment.NewLine + key.ExportSubjectPublicKeyInfoPem());

        Assert.Equal(new[] { KeyIdOf(key) }, keys.Ids);
        var result = Inspect(Sign(ModuleZip(), key), keys);
        Assert.Empty(result.Report.Errors);
        Assert.Contains(result.Report.Warnings, w => w.StartsWith("Доверенные ключи:"));
    }

    [Fact]
    public void TrustedKeys_FileIsReadOnEveryValidation()
    {
        using var key = NewKey();
        var storage = new ModuleStorage(_root, persistent: true);
        Assert.False(TrustedKeys.Load(storage.Root, null).Enabled);

        File.WriteAllText(Path.Combine(storage.Root, TrustedKeys.FileName), key.ExportSubjectPublicKeyInfoPem());

        Assert.True(TrustedKeys.Load(storage.Root, null).Enabled);
    }

    // ------------------------------------------------------------------ хост целиком: validate / install

    private static async Task<ServerHost.Contract.ValidationReport> UploadAsync(ModuleHost host, byte[] zip)
    {
        var id = host.BeginUpload("m.zip", zip.Length);
        foreach (var chunk in zip.Chunk(10_000))
            host.AppendChunk(id, chunk);
        return await host.ValidateAsync(id, stripServerAssemblies: true);
    }

    private static byte[] HostZip() => BuildZip(
        ("Sample.servext.dll", SampleModule.Assembly),
        ("Newtonsoft.Json.dll", File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Newtonsoft.Json.dll"))));

    [Fact]
    public async Task Host_WithTrustedKey_InstallsSignedAndRejectsUnsigned()
    {
        using var key = NewKey();
        var storage = new ModuleStorage(_root, persistent: true);
        File.WriteAllText(Path.Combine(storage.Root, TrustedKeys.FileName), key.ExportSubjectPublicKeyInfoPem());
        var host = new ModuleHost(storage);

        var unsigned = await UploadAsync(host, HostZip());
        Assert.False(unsigned.CanInstall);
        Assert.Contains(unsigned.Errors, e => e.Contains("Архив не подписан"));
        Assert.False(Directory.Exists(storage.ExtractedDir(unsigned.UploadId))); // отвергнутое не распаковывается
        await Assert.ThrowsAsync<HostException>(() => host.InstallAsync(unsigned.UploadId, "test", null));
        Assert.Empty(host.GetState().Modules);

        var signed = await UploadAsync(host, Sign(HostZip(), key));
        Assert.True(signed.CanInstall, string.Join("; ", signed.Errors));
        var (module, _) = await host.InstallAsync(signed.UploadId, "test", "подписанный");
        Assert.True(module.Loaded, module.Error);
        Assert.Equal("1.2.3", module.Version);
        Assert.Contains(host.GetState().Journal, j => j.Text.Contains("подпись архива проверена") && j.Text.Contains(KeyIdOf(key)[..16]));
        Assert.Contains(host.GetState().Journal, j => j.Level == "warn" && j.Text.Contains("отвергнут проверкой подписи"));
    }

    [Fact]
    public async Task Host_WithTrustedKey_RejectsArchiveChangedAfterSigning()
    {
        using var key = NewKey();
        var storage = new ModuleStorage(_root, persistent: true);
        File.WriteAllText(Path.Combine(storage.Root, TrustedKeys.FileName), key.ExportSubjectPublicKeyInfoPem());
        var host = new ModuleHost(storage);

        var report = await UploadAsync(host, Replace(Sign(HostZip(), key), "Sample.servext.dll", new byte[] { 1 }));

        Assert.False(report.CanInstall);
        Assert.Contains(report.Errors, e => e.Contains("изменён после подписания"));
    }

    [Fact]
    public async Task Host_WithoutKeys_AcceptsBothAsBefore()
    {
        using var key = NewKey();
        var host = new ModuleHost(new ModuleStorage(_root, persistent: true));

        var unsigned = await UploadAsync(host, HostZip());
        Assert.True(unsigned.CanInstall, string.Join("; ", unsigned.Errors));
        Assert.Empty(unsigned.Warnings);

        var signed = await UploadAsync(host, Sign(HostZip(), key));
        Assert.True(signed.CanInstall, string.Join("; ", signed.Errors));
        Assert.Contains(signed.Warnings, w => w.Contains("подлинность подписи не проверяется"));
    }
}
