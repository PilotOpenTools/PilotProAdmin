using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ServerHost.Contract;

namespace ServerHost.Hosting;

/// <summary>Сведения об установленной версии модуля (module.json рядом с файлами).</summary>
internal sealed class ModuleManifest
{
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public DateTime InstalledAt { get; set; }
    public string? InstalledBy { get; set; }
    public string? Reason { get; set; }
    public string Sha256 { get; set; } = "";
    public string MainFile { get; set; } = "";
    public List<string> Files { get; set; } = new();
    /// <summary>Меняется при каждой операции над модулем (в том числе reload): по ней остальные узлы кластера видят, что модуль надо перезагрузить. Пусто - установлен прежней версией хоста.</summary>
    public string Revision { get; set; } = "";

    public static string NewRevision() => Guid.NewGuid().ToString("N");

    /// <summary>Тот же ли это установленный вариант модуля (версию не сравниваем: при загрузке она уточняется по файлу сборки).</summary>
    public bool SameState(ModuleManifest other) =>
        Revision == other.Revision && Enabled == other.Enabled && Sha256 == other.Sha256 && InstalledAt == other.InstalledAt;
}

/// <summary>Загрузка архива в staging/&lt;id&gt;/upload.json: её шаги могут выполнять разные узлы кластера.</summary>
internal sealed class UploadMeta
{
    public string FileName { get; set; } = "";
    public long Size { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Отчёт validate; по нему install решает, можно ли ставить распакованное.</summary>
    public ValidationReport? Report { get; set; }
}

/// <summary>
/// Папка модулей на постоянном томе сервера:
/// modules/&lt;Имя&gt;/current - рабочая версия, previous - предыдущая (для отката), staging/&lt;id&gt; - загрузки
/// (upload.json, archive.zip, module/ - распакованное после проверки), trash - удалённые модули.
/// Папка общая для всех узлов кластера и служит источником истины: межузловой замок (.lock) и метка поколения (generation) лежат в корне.
/// </summary>
internal sealed class ModuleStorage
{
    public const string EnvironmentVariable = "SERVER_MODULES_DIR";
    private const string ManifestFile = "module.json";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public ModuleStorage(string root, bool persistent)
    {
        Root = root;
        Persistent = persistent;
        Directory.CreateDirectory(ModulesDir);
    }

    public string Root { get; }
    public bool Persistent { get; }
    public string ModulesDir => Path.Combine(Root, "modules");
    public string StagingDir => Path.Combine(Root, "staging");
    public string TrashDir => Path.Combine(Root, "trash");

    /// <summary>
    /// Папка по умолчанию: переменная SERVER_MODULES_DIR, иначе том с базами Pilot в Docker
    /// (/usr/share/ascon/databases), иначе ProgramData. Папка рядом с сервером (/App) не годится - пропадёт при пересоздании контейнера.
    /// </summary>
    public static ModuleStorage OpenDefault()
    {
        var fromEnv = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return new ModuleStorage(fromEnv.Trim(), persistent: true);
        const string dockerDatabases = "/usr/share/ascon/databases";
        if (Directory.Exists(dockerDatabases))
            return new ModuleStorage(Path.Combine(dockerDatabases, ".server-modules"), persistent: true);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(programData))
            return new ModuleStorage(Path.Combine(programData, "ASCON", "ServerModules"), persistent: true);
        return new ModuleStorage(Path.Combine(AppContext.BaseDirectory, "ServerModules"), persistent: false);
    }

    internal const string LockFileName = ".lock";
    private const string GenerationFile = "generation";
    private const string UploadFile = "upload.json";
    private const string ArchiveName = "archive.zip";
    private const string ExtractedDirName = "module";

    /// <summary>Сколько ждать межузловой замок. Операция с пробной загрузкой модуля укладывается в секунды.</summary>
    internal TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Замок старше этого срока считается брошенным упавшим узлом и перехватывается.</summary>
    internal TimeSpan LockStaleAfter { get; set; } = TimeSpan.FromMinutes(2);

    public const int MaxNameLength = 100;

    /// <summary>
    /// Имя модуля - имя его папки: буквы, цифры, «_», «-» и «.» внутри имени; не пусто, не «.»/«..», без «..» и точки по краям.
    /// Имя приходит из имени файла в архиве и из параметра API, поэтому по нему нельзя выйти из папки modules.
    /// </summary>
    public static bool IsValidName(string? name) =>
        !string.IsNullOrEmpty(name) && name.Length <= MaxNameLength
        && name[0] != '.' && name[^1] != '.' && !name.Contains("..")
        && name.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');

    public static string RequireValidName(string? name) =>
        IsValidName(name) ? name! : throw new HostException(
            $"Недопустимое имя модуля «{name}»: допустимы буквы, цифры, «_», «-» и «.» внутри имени, не длиннее {MaxNameLength} символов.");

    public string ModuleDir(string name) => Path.Combine(ModulesDir, RequireValidName(name));
    public string CurrentDir(string name) => Path.Combine(ModuleDir(name), "current");
    public string PreviousDir(string name) => Path.Combine(ModuleDir(name), "previous");

    public IEnumerable<string> InstalledNames() =>
        Directory.Exists(ModulesDir)
            ? Directory.GetDirectories(ModulesDir).Select(d => Path.GetFileName(d)).Where(n => IsValidName(n) && File.Exists(Path.Combine(CurrentDir(n), ManifestFile)))
            : Enumerable.Empty<string>();

    public static ModuleManifest? ReadManifest(string dir)
    {
        var path = Path.Combine(dir, ManifestFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(path), Json) : null;
    }

    public static void WriteManifest(string dir, ModuleManifest manifest)
    {
        var path = Path.Combine(dir, ManifestFile);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(manifest, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>staging / current, прежняя current / previous (одна предыдущая версия).</summary>
    public void Promote(string name, string stagingDir)
    {
        Directory.CreateDirectory(ModuleDir(name));
        var current = CurrentDir(name);
        var previous = PreviousDir(name);
        if (Directory.Exists(current))
        {
            DeleteDirectory(previous);
            Directory.Move(current, previous);
        }
        Directory.Move(stagingDir, current);
    }

    /// <summary>Откат: current / previous.</summary>
    public void SwapWithPrevious(string name)
    {
        var current = CurrentDir(name);
        var previous = PreviousDir(name);
        var temp = Path.Combine(ModuleDir(name), "swap-" + Guid.NewGuid().ToString("N"));
        Directory.Move(current, temp);
        Directory.Move(previous, current);
        Directory.Move(temp, previous);
    }

    /// <summary>Удалённый модуль уходит в trash (последние 5), а не стирается сразу.</summary>
    public void MoveToTrash(string name)
    {
        Directory.CreateDirectory(TrashDir);
        var target = Path.Combine(TrashDir, $"{name}-{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.Move(ModuleDir(name), target);
        foreach (var old in new DirectoryInfo(TrashDir).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(5))
            DeleteDirectory(old.FullName);
    }

    // ------------------------------------------------------------------ загрузки на диске

    /// <summary>Идентификатор из API становится именем папки, поэтому только 32 шестнадцатеричных символа.</summary>
    public static bool IsValidUploadId(string? id) =>
        id is { Length: 32 } && id.All(Uri.IsHexDigit);

    public string UploadDir(string? uploadId) =>
        IsValidUploadId(uploadId) ? Path.Combine(StagingDir, uploadId!) : throw new HostException("Недопустимый идентификатор загрузки.");

    public string ArchivePath(string uploadId) => Path.Combine(UploadDir(uploadId), ArchiveName);
    public string ExtractedDir(string uploadId) => Path.Combine(UploadDir(uploadId), ExtractedDirName);

    public void CreateUpload(string uploadId, UploadMeta meta)
    {
        Directory.CreateDirectory(UploadDir(uploadId));
        WriteUpload(uploadId, meta);
    }

    /// <summary>null - загрузки нет (не начата, устарела и убрана или уже установлена).</summary>
    public UploadMeta? ReadUpload(string? uploadId)
    {
        var path = Path.Combine(UploadDir(uploadId), UploadFile);
        return File.Exists(path) ? JsonSerializer.Deserialize<UploadMeta>(File.ReadAllText(path), Json) : null;
    }

    public void WriteUpload(string uploadId, UploadMeta meta) =>
        WriteAtomic(Path.Combine(UploadDir(uploadId), UploadFile), JsonSerializer.Serialize(meta, Json));

    private static void WriteAtomic(string path, string text)
    {
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }

    // ------------------------------------------------------------------ кластер: замок и поколение

    /// <summary>Метка поколения каталога: меняется после каждой изменяющей операции любого узла. null - прочитать не удалось.</summary>
    public string? ReadGeneration()
    {
        try
        {
            return File.ReadAllText(Path.Combine(Root, GenerationFile)).Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // метку как раз заменяет другой узел (на Windows и SMB это ещё и «доступ запрещён») - сверка повторится на следующем обращении
        }
    }

    public string NewGeneration()
    {
        var generation = Guid.NewGuid().ToString("N");
        WriteAtomic(Path.Combine(Root, GenerationFile), generation);
        return generation;
    }

    /// <summary>Межузловой замок: файл в Root, создаваемый атомарно. null - не получен за <paramref name="wait"/>.</summary>
    public IDisposable? TryAcquireLock(TimeSpan wait)
    {
        var path = Path.Combine(Root, LockFileName);
        var token = Guid.NewGuid().ToString("N");
        var stamp = $"{token} {Environment.MachineName}:{Environment.ProcessId} {DateTime.UtcNow:O}";
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                    file.Write(Encoding.UTF8.GetBytes(stamp));
                return new RootLock(path, token, LockStaleAfter / 6);
            }
            catch (IOException) when (File.Exists(path)) { }
            catch (UnauthorizedAccessException) { } // Windows: файл замка как раз удаляется

            if (IsStaleLock(path))
            {
                // shortcut: два узла, одновременно перехватывающие один брошенный замок, могут снять свежий замок друг друга; окно - микросекунды, при нужде перейти на аренду с продлением.
                TakeOverLock(path);
                // Перехватить не удалось (чужой владелец файла, sticky-bit): ждём по таймауту, а не крутим цикл вхолостую.
                if (!File.Exists(path))
                    continue;
            }
            if (watch.Elapsed >= wait)
                return null;
            Thread.Sleep(50);
        }
    }

    public IDisposable AcquireLock() =>
        TryAcquireLock(LockTimeout) ?? throw new HostException(
            $"Каталог модулей занят другой операцией (замок {LockFileName}: {ReadLockOwner(Path.Combine(Root, LockFileName))}). " +
            $"Не удалось дождаться освобождения за {LockTimeout.TotalSeconds:0.#} с - повторите позже.");

    private bool IsStaleLock(string path)
    {
        // Одно обращение к файлу: для исчезнувшего файла GetLastWriteTimeUtc отдаёт 1601 год, то есть «брошен».
        var info = new FileInfo(path);
        return info.Exists && DateTime.UtcNow - info.LastWriteTimeUtc > LockStaleAfter;
    }

    private static void TakeOverLock(string path)
    {
        var aside = path + ".stale-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(path, aside);
            File.Delete(aside);
        }
        catch (IOException) { /* замок уже перехватил или снял другой узел */ }
        catch (UnauthorizedAccessException) { }
    }

    private static string ReadLockOwner(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return "владелец неизвестен"; }
    }

    private sealed class RootLock : IDisposable
    {
        private readonly string _path;
        private readonly string _token;
        private readonly Timer _renewal;
        private volatile bool _released;

        public RootLock(string path, string token, TimeSpan renewEvery)
        {
            _path = path;
            _token = token;
            _renewal = new Timer(_ => Renew(), null, renewEvery, renewEvery);
        }

        private bool IsOurs() => File.Exists(_path) && File.ReadAllText(_path).StartsWith(_token, StringComparison.Ordinal);

        /// <summary>Долгая операция (проверка архива, загрузка модулей) не должна выглядеть брошенным замком: пока держим, обновляем время файла.</summary>
        private void Renew()
        {
            try
            {
                if (!_released && IsOurs())
                    File.SetLastWriteTimeUtc(_path, DateTime.UtcNow);
            }
            catch (IOException) { } // не продлился - замок истечёт как обычно
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Чужой (перехваченный у нас) замок не трогаем; не снялся - истечёт сам через LockStaleAfter.</summary>
        public void Dispose()
        {
            _released = true;
            _renewal.Dispose();
            try
            {
                if (IsOurs())
                    File.Delete(_path);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void CleanupStaging(TimeSpan olderThan)
    {
        if (!Directory.Exists(StagingDir))
            return;
        foreach (var dir in new DirectoryInfo(StagingDir).GetDirectories().Where(d => UploadCreatedAt(d) < DateTime.UtcNow - olderThan))
            DeleteDirectory(dir.FullName);
    }

    /// <summary>Время начала загрузки из upload.json (время создания папки на сетевой ФС ненадёжно); нет файла - время папки.</summary>
    private static DateTime UploadCreatedAt(DirectoryInfo dir)
    {
        try
        {
            var path = Path.Combine(dir.FullName, UploadFile);
            if (File.Exists(path) && JsonSerializer.Deserialize<UploadMeta>(File.ReadAllText(path), Json) is { } meta)
                return meta.CreatedAt;
        }
        catch (Exception ex) when (ex is IOException or JsonException) { /* битая метка - судим по папке */ }
        return dir.CreationTimeUtc;
    }

    public static void DeleteDirectory(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        catch (IOException) { /* занято - удалится при следующей уборке */ }
        catch (UnauthorizedAccessException) { }
    }
}
