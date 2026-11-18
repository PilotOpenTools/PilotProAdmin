using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using ServerHost.Contract;

namespace ServerHost.Hosting;

/// <summary>
/// Проверка архива модуля до загрузки: структура, главная сборка *.servext.dll и сборки, которые уже есть у сервера.
/// Сборку с тем же именем, что у сервера, модуль всё равно получит серверную (см. <see cref="ModuleLoadContext"/>),
/// поэтому такая же версия - лишний файл, другая - предупреждение: код модуля будет работать с серверной версией.
/// </summary>
internal static class ArchiveInspector
{
    public const string MainSuffix = ".servext.dll";

    /// <summary>Предел распакованного размера архива и числа файлов: защита от zip-бомбы (сжатый размер ограничен отдельно).</summary>
    public const long MaxUncompressedBytes = 512L * 1024 * 1024;
    public const int MaxEntries = 5000;

    /// <param name="SignedBy">Отпечаток ключа, которым подписан архив; null - подписи нет или она неверна.</param>
    /// <param name="SignatureError">Почему архив отвергнут проверкой подписи (для журнала хоста); null - подпись не причина.</param>
    public sealed record Result(ValidationReport Report, IReadOnlyList<string> KeepEntries, string? SignedBy = null, string? SignatureError = null);

    /// <param name="trusted">Доверенные ключи подписи; null - подлинность не проверяется (подпись, если она есть, сверяется с файлами всё равно).</param>
    public static Result Inspect(byte[] zip, string fileName, ServerAssemblies server, bool stripServerAssemblies,
        long maxUncompressedBytes = MaxUncompressedBytes, TrustedKeys? trusted = null)
    {
        trusted ??= TrustedKeys.None;
        var report = new ValidationReport
        {
            FileName = fileName,
            Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(),
        };
        var keep = new List<string>();
        (string? SignedBy, string? Error) signature;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
        }
        catch (InvalidDataException)
        {
            report.Errors.Add("Файл не является zip-архивом.");
            return new Result(report, keep);
        }

        using (archive)
        {
            var files = archive.Entries.Where(e => !e.FullName.EndsWith('/') && e.Name.Length > 0).ToList();
            if (files.Count == 0)
                report.Errors.Add("Архив пуст.");
            if (files.Count > MaxEntries)
            {
                report.Errors.Add($"В архиве {files.Count} файлов - больше допустимых {MaxEntries}.");
                return new Result(report, keep);
            }
            if (files.Sum(e => e.Length) > maxUncompressedBytes)
            {
                report.Errors.Add($"Распакованный размер архива больше допустимых {maxUncompressedBytes / 1024 / 1024} МБ.");
                return new Result(report, keep);
            }

            var remaining = maxUncompressedBytes; // суммарно фактически прочитанные байты, а не заявленные Length
            // Хеши файлов нужны только подписанному архиву и считаются в том же проходе, что и чтение сборок.
            var signatureEntry = files.FirstOrDefault(e => e.FullName.Replace('\\', '/') == ArchiveSignature.FileName);
            var hashes = signatureEntry is null ? null : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in files)
            {
                var path = entry.FullName.Replace('\\', '/');
                if (entry == signatureEntry)
                    continue; // сама подпись в манифест и в модуль не входит
                if (!IsSafePath(path))
                {
                    report.Errors.Add($"Недопустимый путь в архиве: {path}");
                    continue;
                }

                var file = new ArchiveFile { Path = path, Size = entry.Length };
                report.Files.Add(file);
                var atRoot = !path.Contains('/');
                var isDll = path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

                if (!isDll)
                {
                    file.Status = FileStatus.Other;
                    keep.Add(path);
                    if (hashes is not null)
                        AddHash(hashes, path, HashEntry(entry, ref remaining), report);
                    continue;
                }

                var identity = ReadIdentity(entry, ref remaining, out var hash);
                if (hashes is not null)
                    AddHash(hashes, path, hash, report);
                if (identity is null)
                {
                    file.Status = FileStatus.Native;
                    keep.Add(path);
                    continue;
                }

                file.AssemblyName = identity.Value.Name;
                file.Version = identity.Value.Version.ToString();

                if (atRoot && path.EndsWith(MainSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    file.Status = FileStatus.Main;
                    keep.Add(path);
                    continue;
                }

                if (server.TryGet(identity.Value.Name, out var serverVersion))
                {
                    file.ServerVersion = serverVersion?.ToString();
                    file.Stripped = true;
                    if (serverVersion is null || serverVersion == identity.Value.Version)
                    {
                        file.Status = FileStatus.Duplicate;
                        if (!stripServerAssemblies)
                            report.Errors.Add($"{path}: сборка {identity.Value.Name} {identity.Value.Version} уже есть на сервере - уберите её из архива.");
                    }
                    else
                    {
                        file.Status = FileStatus.VersionMismatch;
                        report.Warnings.Add($"{path}: на сервере {identity.Value.Name} версии {serverVersion}, в архиве {identity.Value.Version}. " +
                                            "Модуль будет работать с серверной версией - проверьте совместимость.");
                    }
                    continue;
                }

                file.Status = FileStatus.Private;
                keep.Add(path);
                if (!atRoot)
                    report.Warnings.Add($"{path}: сборка в подпапке не будет найдена загрузчиком - положите её в корень архива.");
            }

            var mains = report.Files.Where(f => f.Status == FileStatus.Main).ToList();
            if (mains.Count == 0)
                report.Errors.Add($"В корне архива нет главной сборки *{MainSuffix}.");
            else if (mains.Count > 1)
                report.Errors.Add($"В корне архива несколько главных сборок: {string.Join(", ", mains.Select(m => m.Path))}. Один архив - один модуль.");
            else
            {
                var name = ModuleName(mains[0].Path);
                if (!ModuleStorage.IsValidName(name))
                    report.Errors.Add($"Недопустимое имя модуля «{name}» (из имени {mains[0].Path}): допустимы буквы, цифры, «_», «-» и «.» внутри имени, " +
                                      $"не длиннее {ModuleStorage.MaxNameLength} символов.");
                else
                {
                    report.ModuleName = name;
                    report.Version = ModuleVersions.Display(mains[0].Version);
                }
            }

            signature = CheckSignature(report, signatureEntry, hashes, trusted, ref remaining);
        }

        return new Result(report, keep, signature.SignedBy, signature.Error);
    }

    /// <summary>
    /// Подпись: с доверенными ключами архив обязан быть подписан одним из них; без ключей подпись, если она есть, всё равно сверяется
    /// с файлами (подлинность при этом не проверяется - о чём предупреждение).
    /// </summary>
    private static (string? SignedBy, string? Error) CheckSignature(ValidationReport report, ZipArchiveEntry? entry, Dictionary<string, string>? hashes,
        TrustedKeys trusted, ref long remaining)
    {
        var rejected = new List<string>();
        var noValidKeys = trusted.Enabled && trusted.Ids.Count == 0;
        if (noValidKeys)
            rejected.Add($"Проверка подписи включена ({TrustedKeys.EnvironmentVariable} или {TrustedKeys.FileName}), но ни один доверенный ключ не прочитан: " +
                              string.Join("; ", trusted.Problems.DefaultIfEmpty("список пуст")) + ".");
        else
            foreach (var problem in trusted.Problems)
                report.Warnings.Add("Доверенные ключи: " + problem + ".");

        if (entry is null)
        {
            if (trusted.Enabled && !noValidKeys)
                rejected.Add("Архив не подписан, а на сервере включена проверка подписи. Соберите модуль в CI с переменной группы " +
                             "MODULE_SIGNING_KEY (см. README ServerHost).");
            return Rejected(report, rejected);
        }
        if (entry.Length > ArchiveSignature.MaxFileBytes)
        {
            rejected.Add($"Файл подписи {ArchiveSignature.FileName} больше допустимых {ArchiveSignature.MaxFileBytes / 1024 / 1024} МБ.");
            return Rejected(report, rejected);
        }

        byte[] json;
        using (var stream = entry.Open())
        using (var buffer = new MemoryStream())
        {
            remaining -= CopyLimited(stream, buffer, Math.Min(remaining, ArchiveSignature.MaxFileBytes));
            json = buffer.ToArray();
        }
        var sig = ArchiveSignature.Parse(json, out var parseError);
        if (sig is null)
        {
            rejected.Add($"Файл подписи {ArchiveSignature.FileName} повреждён: {parseError}.");
            return Rejected(report, rejected);
        }

        var verdict = ArchiveSignature.Verify(sig, hashes!, trusted);
        rejected.AddRange(verdict.Errors);
        if (rejected.Count > 0)
            return Rejected(report, rejected);
        if (!trusted.Enabled)
            report.Warnings.Add($"Архив подписан ключом {ArchiveSignature.Short(verdict.KeyId!)}, целостность подтверждена. Доверенные ключи на сервере не заданы - " +
                                "подлинность подписи не проверяется (как включить: README ServerHost).");
        return (verdict.KeyId, null);
    }

    private static (string? SignedBy, string? Error) Rejected(ValidationReport report, List<string> errors)
    {
        report.Errors.AddRange(errors);
        return (null, errors.Count > 0 ? errors[0] : null);
    }

    private static void AddHash(Dictionary<string, string> hashes, string path, string hash, ValidationReport report)
    {
        if (!hashes.TryAdd(path, hash))
            report.Errors.Add($"Путь повторяется в архиве: {path}");
    }

    private static string HashEntry(ZipArchiveEntry entry, ref long remaining)
    {
        using var stream = entry.Open();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            remaining -= read;
            if (remaining < 0)
                throw new HostException($"Файл в архиве распаковывается больше допустимых {MaxUncompressedBytes / 1024 / 1024} МБ.");
            sha.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>TestServerAction.servext.dll / TestServerAction.</summary>
    public static string ModuleName(string mainPath)
    {
        var file = Path.GetFileName(mainPath);
        return file[..^MainSuffix.Length];
    }

    private static bool IsSafePath(string path) =>
        !Path.IsPathRooted(path) && !path.Contains(':') &&
        !path.Split('/').Any(part => part is ".." or "." || part.Length == 0);

    /// <summary>Копирование с пределом: заявленный в заголовке размер не доверяется.</summary>
    internal static long CopyLimited(Stream from, Stream to, long limit)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = from.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > limit)
                throw new HostException($"Файл в архиве распаковывается больше допустимых {limit / 1024 / 1024} МБ.");
            to.Write(buffer, 0, read);
        }
        return total;
    }

    private static (string Name, Version Version)? ReadIdentity(ZipArchiveEntry entry, ref long remaining, out string sha256)
    {
        sha256 = "";
        try
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            remaining -= CopyLimited(stream, buffer, remaining);
            sha256 = Convert.ToHexString(SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))).ToLowerInvariant();
            buffer.Position = 0;
            using var pe = new PEReader(buffer);
            if (!pe.HasMetadata)
                return null;
            var reader = pe.GetMetadataReader();
            if (!reader.IsAssembly)
                return null;
            var def = reader.GetAssemblyDefinition();
            return (reader.GetString(def.Name), def.Version);
        }
        catch (BadImageFormatException)
        {
            return null;
        }
    }
}
