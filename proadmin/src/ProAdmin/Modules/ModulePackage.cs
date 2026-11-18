using System.Globalization;
using System.IO.Compression;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace ProAdmin.Modules;

/// <summary>Zip клиентского модуля и то, что из него видно: главная сборка, версия, имя для Pilot.</summary>
public sealed record ModulePackageInfo(
    byte[] Zip,
    string FileName,
    string AssemblyName,
    string Version,
    string? Product,
    IReadOnlyList<string>? PreviousNames = null)
{
    /// <summary>
    /// Прежние имена модуля из <c>[assembly: AssemblyMetadata("PreviousNames", "Имя;Сборка.ext2")]</c>:
    /// по ним выкладка находит на сервере объект переименованного модуля и обновляет его, а не создаёт второй.
    /// </summary>
    public IReadOnlyList<string> Previous => PreviousNames ?? Array.Empty<string>();

    /// <summary>Атрибут «Наименование» объекта Extension - как делает Pilot-myAdmin: AssemblyProduct, иначе имя сборки.</summary>
    public string ExtensionName => string.IsNullOrWhiteSpace(Product) ? AssemblyName : Product!.Trim();

    private IReadOnlyList<PackageAssembly>? _assemblies;

    /// <summary>Все dll/exe архива с версиями - для сверки со спецификацией сборок.</summary>
    public IReadOnlyList<PackageAssembly> Assemblies => _assemblies ??= ModulePackage.ListAssemblies(Zip, AssemblyName);

    /// <summary>Атрибут «Additional»: «Версия 1.2.1.0 - Размер 93 КБ».</summary>
    public string ExtensionAdditional => $"Версия {Version} - Размер {ModulePackage.FormatSize(Zip.LongLength)}";
}

public static partial class ModulePackage
{
    /// <summary>
    /// Разбирает zip модуля. Главная сборка - *.ext2.dll (серверный модуль - *.servext.dll); если их несколько - та,
    /// чьё имя совпадает с началом имени zip, иначе самая большая. Версия и AssemblyProduct читаются из метаданных,
    /// сборка не загружается.
    /// </summary>
    public static ModulePackageInfo Analyze(byte[] zip, string fileName, ModuleKind kind = ModuleKind.Client)
    {
        var mainSuffix = kind.AssemblySuffix() + ".dll";
        using var archive = OpenZip(zip, fileName);
        var dlls = archive.Entries
            .Where(e => e.Name.EndsWith(mainSuffix, StringComparison.OrdinalIgnoreCase) && !e.FullName.Contains('/') && !e.FullName.Contains('\\'))
            .ToList();
        if (dlls.Count == 0)
            dlls = archive.Entries.Where(e => e.Name.EndsWith(mainSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (dlls.Count == 0)
            throw new InvalidDataException(kind == ModuleKind.Server
                ? $"В архиве {fileName} нет сборки *.servext.dll - это не серверный модуль Pilot."
                : $"В архиве {fileName} нет сборки *.ext2.dll - это не клиентский модуль Pilot.");

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var main = dlls.FirstOrDefault(e => baseName.StartsWith(Path.GetFileNameWithoutExtension(e.Name), StringComparison.OrdinalIgnoreCase))
                   ?? dlls.OrderByDescending(e => e.Length).First();

        var mainBytes = ReadEntry(main);
        var (name, version, product) = ReadAssembly(mainBytes, main.Name);
        return new ModulePackageInfo(zip, fileName, name, version, product, ReadPreviousNames(mainBytes));
    }

    /// <summary>
    /// Zip модуля из архива артефактов job GitLab: первый вложенный *.zip (bin/Имя-1.2.3.zip).
    /// Если вложенного zip нет, но в артефактах лежит сама сборка - модуль собирается из файлов её папки.
    /// </summary>
    public static (byte[] Zip, string FileName) FromArtifacts(byte[] artifacts)
    {
        using var archive = OpenZip(artifacts, "artifacts.zip");
        var inner = archive.Entries
            .Where(e => e.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) && e.Length > 0)
            .OrderByDescending(e => e.Name.Contains(".ext2", StringComparison.OrdinalIgnoreCase) || e.Name.Contains(".servext", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(e => e.Length)
            .FirstOrDefault();
        if (inner is null)
            throw new InvalidDataException("В артефактах job нет zip модуля (ожидается bin/*.zip - см. шаблон .gitlab-ci.yml для модулей).");

        return (ReadEntry(inner), inner.Name);
    }

    internal static (string Name, string Version, string? Product) ReadAssembly(byte[] dll, string fileName)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(dll, writable: false));
            if (!pe.HasMetadata)
                throw new InvalidDataException($"{fileName} - не .NET-сборка.");
            var md = pe.GetMetadataReader();
            var def = md.GetAssemblyDefinition();
            var name = md.GetString(def.Name);
            var version = ModuleVersion.Display(def.Version.ToString())!; // 2.1.0.0 / 2.1.0
            string? product = null;
            foreach (var handle in def.GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(handle);
                if (AttributeTypeName(md, attr) != "System.Reflection.AssemblyProductAttribute")
                    continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() == 1) // пролог
                    product = blob.ReadSerializedString();
            }
            return (name, version, product);
        }
        catch (BadImageFormatException ex)
        {
            throw new InvalidDataException($"{fileName} - не .NET-сборка: {ex.Message}");
        }
    }

    /// <summary>Атрибут сборки, по которому модуль сообщает свои прежние имена (переименование модуля).</summary>
    public const string PreviousNamesKey = "PreviousNames";

    /// <summary>Значение <c>AssemblyMetadata("PreviousNames", "А;Б")</c> - список имён; нет атрибута - пусто.</summary>
    internal static IReadOnlyList<string> ReadPreviousNames(byte[] dll)
    {
        try
        {
            using var pe = new PEReader(new MemoryStream(dll, writable: false));
            if (!pe.HasMetadata)
                return Array.Empty<string>();
            var md = pe.GetMetadataReader();
            foreach (var handle in md.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attr = md.GetCustomAttribute(handle);
                if (AttributeTypeName(md, attr) != "System.Reflection.AssemblyMetadataAttribute")
                    continue;
                var blob = md.GetBlobReader(attr.Value);
                if (blob.ReadUInt16() != 1 || blob.ReadSerializedString() != PreviousNamesKey)
                    continue;
                return (blob.ReadSerializedString() ?? "")
                    .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }
        catch (BadImageFormatException)
        {
        }
        return Array.Empty<string>();
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attr)
    {
        EntityHandle type;
        if (attr.Constructor.Kind == HandleKind.MemberReference)
            type = md.GetMemberReference((MemberReferenceHandle)attr.Constructor).Parent;
        else if (attr.Constructor.Kind == HandleKind.MethodDefinition)
            type = md.GetMethodDefinition((MethodDefinitionHandle)attr.Constructor).GetDeclaringType();
        else
            return null;

        return type.Kind switch
        {
            HandleKind.TypeReference => Full(md.GetTypeReference((TypeReferenceHandle)type).Namespace, md.GetTypeReference((TypeReferenceHandle)type).Name),
            HandleKind.TypeDefinition => Full(md.GetTypeDefinition((TypeDefinitionHandle)type).Namespace, md.GetTypeDefinition((TypeDefinitionHandle)type).Name),
            _ => null,
        };

        string Full(StringHandle ns, StringHandle n) => md.GetString(ns) + "." + md.GetString(n);
    }

    /// <summary>Предел размера одной распакованной записи zip (сборки модуля - единицы мегабайт).</summary>
    public const long MaxEntryBytes = 256L * 1024 * 1024;

    /// <summary>Предел суммарного распакованного размера zip: защита памяти процесса от zip-бомбы.</summary>
    public const long MaxUnpackedBytes = 1024L * 1024 * 1024;

    private static ZipArchive OpenZip(byte[] zip, string fileName)
    {
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{fileName} - не zip-архив: {ex.Message}");
        }
        if (archive.Entries.Sum(e => e.Length) > MaxUnpackedBytes)
        {
            archive.Dispose();
            throw new InvalidDataException($"{fileName}: распакованный размер больше {FormatSize(MaxUnpackedBytes)} - архив не принимается.");
        }
        return archive;
    }

    /// <summary>Запись zip целиком в память, но не больше <see cref="MaxEntryBytes"/>: заявленный в архиве размер проверяется по факту чтения.</summary>
    public static byte[] ReadEntry(ZipArchiveEntry entry)
    {
        if (entry.Length > MaxEntryBytes)
            throw new InvalidDataException($"{entry.FullName}: распакованный размер больше {FormatSize(MaxEntryBytes)}.");
        using var source = entry.Open();
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (ms.Length + read > MaxEntryBytes)
                throw new InvalidDataException($"{entry.FullName}: распакованный размер больше {FormatSize(MaxEntryBytes)}.");
            ms.Write(buffer, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>Размер как в Pilot-myAdmin: «93 КБ», «1,8 МБ».</summary>
    public static string FormatSize(long bytes)
    {
        var ru = CultureInfo.GetCultureInfo("ru-RU");
        if (bytes < 1024 * 1024)
            return $"{Math.Max(1, (long)Math.Round(bytes / 1024.0)).ToString(ru)} КБ";
        return $"{(bytes / 1024.0 / 1024.0).ToString("0.#", ru)} МБ";
    }

    /// <summary>«Версия 1.2.1.0 - Размер 93 КБ» / «1.2.1.0».</summary>
    public static string? VersionFromAdditional(string? additional) =>
        additional is not null && VersionRegex().Match(additional) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"Верси[яи]\s+([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex VersionRegex();

    /// <summary>Код модуля в скобках: «01.087 - Открыть Storadge - (OpenStoradge) - (CICD)» / «OpenStoradge».</summary>
    public static string? CodeFromName(string? extensionName)
    {
        if (string.IsNullOrWhiteSpace(extensionName))
            return null;
        return CodeRegex().Matches(extensionName).Select(m => m.Groups[1].Value.Trim())
            .FirstOrDefault(c => c.Length > 0 && !string.Equals(c, "CICD", StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\(([^()]+)\)")]
    private static partial Regex CodeRegex();
}
