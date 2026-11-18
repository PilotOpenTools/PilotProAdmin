using System.IO.Compression;
using ProAdmin.Modules;

namespace ProAdmin.Pilot.ServerModules;

/// <summary>Проверка zip новой версии самого ServerHost перед записью в базу ($PilotServerExtension).</summary>
public static class ServerHostPackage
{
    public const string ExtensionName = "ServerHost";
    public const string MainAssembly = "ServerHost.servext.dll";

    /// <summary>Хост - десятки килобайт; архив читается в память целиком, поэтому верхняя граница жёсткая.</summary>
    public const long MaxSize = 50 * 1024 * 1024;

    /// <summary>Объект хоста среди серверных расширений базы: по имени, иначе по имени файла zip.</summary>
    public static PilotModule? Find(IEnumerable<PilotModule> extensions) =>
        extensions.FirstOrDefault(m => string.Equals(m.Name, ExtensionName, StringComparison.OrdinalIgnoreCase))
        ?? extensions.FirstOrDefault(m => m.FileName is { } f && string.Equals(Path.GetFileNameWithoutExtension(f), ExtensionName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Размер проверяется до чтения архива в память.</summary>
    public static void CheckSize(long length, string fileName)
    {
        if (length is <= 0 or > MaxSize)
            throw new InvalidDataException($"{fileName}: размер архива должен быть от 1 байта до {ModulePackage.FormatSize(MaxSize)}.");
    }

    /// <summary>
    /// Архив годится, если в его корне лежит <see cref="MainAssembly"/> (.NET-сборка). Возвращает описание пакета и признак
    /// понижения версии относительно <paramref name="currentVersion"/> (null или не разобрана - понижением не считается).
    /// </summary>
    public static (ModulePackageInfo Package, bool IsDowngrade) Check(byte[] zip, string fileName, string? currentVersion)
    {
        CheckSize(zip.Length, fileName);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"{fileName} - не zip-архив: {ex.Message}");
        }

        byte[] dll;
        using (archive)
        {
            var entry = archive.Entries.FirstOrDefault(e => string.Equals(e.FullName, MainAssembly, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"В корне архива {fileName} нет {MainAssembly} - это не ServerHost.");
            dll = ModulePackage.ReadEntry(entry);
        }

        var (name, version, product) = ModulePackage.ReadAssembly(dll, MainAssembly);
        var package = new ModulePackageInfo(zip, fileName, name, version, product);
        var downgrade = ModuleVersion.TryParse(version, out var next) && ModuleVersion.TryParse(currentVersion, out var current) && next! < current!;
        return (package, downgrade);
    }
}
