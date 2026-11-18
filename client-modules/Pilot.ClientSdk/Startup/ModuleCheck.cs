using System;
using System.Collections.Generic;
using System.Linq;

namespace Pilot.ClientSdk.Startup
{
    /// <summary>Модуль, назначенный пользователю на сервере: объект Extension в папке расширений.</summary>
    internal sealed class ExpectedModule
    {
        public ExpectedModule(Guid objectId, string name, string version, Guid fileId)
        {
            ObjectId = objectId;
            Name = name;
            Version = version;
            FileId = fileId;
        }

        public Guid ObjectId { get; }
        public string Name { get; }
        public string Version { get; }

        /// <summary>Body.Id zip-файла: клиент распаковывает модуль в Databases\{база}\{версия}\Plugins\{FileId}.</summary>
        public Guid FileId { get; }
    }

    /// <summary>Сборка *.ext2, загруженная в клиент.</summary>
    internal sealed class LoadedModule
    {
        public LoadedModule(string name, string title, string version, string directory, bool isOwn, bool implementsContract)
        {
            Name = name;
            Title = title;
            Version = version;
            Directory = directory;
            IsOwn = isOwn;
            ImplementsContract = implementsContract;
        }

        public string Name { get; }
        public string Title { get; }
        public string Version { get; }

        /// <summary>Папка, из которой загружена сборка (может быть пустой).</summary>
        public string Directory { get; }

        public bool IsOwn { get; }

        /// <summary>В сборке есть класс с <see cref="IModule"/> / <see cref="IModuleAsync"/>.</summary>
        public bool ImplementsContract { get; }
    }

    internal enum RowState
    {
        Waiting,
        Running,
        Ok,
        Warning,
        Error,
    }

    internal sealed class RowResult
    {
        public string Key { get; set; }
        public string Title { get; set; }
        public string Version { get; set; }
        public RowState State { get; set; }
        public string Status { get; set; }
        public long? DurationMs { get; set; }
        public string Details { get; set; }

        public bool IsDone => State != RowState.Waiting && State != RowState.Running;
    }

    /// <summary>
    /// Сводит три источника в строки окна загрузки:
    /// назначенные на сервере модули / скачаны ли (папка Plugins\{FileId}) / загружена ли сборка / как прошёл запуск (<see cref="StartupRunner"/>).
    /// Без обращения к Pilot и файловой системе - всё передаётся снаружи (так это проверяется тестами).
    /// </summary>
    internal static class ModuleCheck
    {
        /// <param name="expected">Модули с сервера; null - список получить не удалось, проверяем только загруженные модули.</param>
        /// <param name="pluginFolder">Папка скачанного модуля по FileId или null, если модуль не скачан.</param>
        /// <param name="dllNamesIn">Имена сборок *.ext2 в папке модуля (без .dll).</param>
        /// <param name="loaded">Загруженные в клиент сборки *.ext2.</param>
        /// <param name="reports">Запуски стартовых классов.</param>
        /// <param name="deadlinePassed">Время ожидания вышло: всё, что ещё ждём, считается проблемой.</param>
        public static List<RowResult> Evaluate(
            IReadOnlyList<ExpectedModule> expected,
            Func<Guid, string> pluginFolder,
            Func<string, IReadOnlyCollection<string>> dllNamesIn,
            IReadOnlyList<LoadedModule> loaded,
            IReadOnlyList<ModuleStartupReport> reports,
            bool deadlinePassed)
        {
            var rows = new List<RowResult>();
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var module in expected ?? Array.Empty<ExpectedModule>())
            {
                var row = new RowResult { Key = module.FileId.ToString(), Title = module.Name, Version = module.Version };
                rows.Add(row);

                var folder = pluginFolder(module.FileId);
                if (folder == null)
                {
                    Set(row, RowState.Error, "не скачан",
                        "Модуль назначен пользователю, но клиент его не скачал. Перезапустите Pilot; если не помогло - очистите кэш клиента.");
                    continue;
                }

                var names = dllNamesIn(folder) ?? Array.Empty<string>();
                var assembly = loaded.FirstOrDefault(a => SameDirectory(a.Directory, folder))
                               ?? loaded.FirstOrDefault(a => names.Contains(a.Name, StringComparer.OrdinalIgnoreCase));
                if (assembly == null)
                {
                    if (deadlinePassed)
                        Set(row, RowState.Error, "не загружен",
                            "Модуль скачан, но клиент не загрузил его сборку. Подробности - в логе клиента Pilot.");
                    else
                        Set(row, RowState.Waiting, "загрузка…", null);
                    continue;
                }

                matched.Add(assembly.Name);
                FillFromAssembly(row, assembly, reports, deadlinePassed);
            }

            // Модули не с сервера (папка Development) и всё, что отчиталось о запуске, но не нашлось выше.
            foreach (var assembly in loaded.Where(a => a.IsOwn && !matched.Contains(a.Name)))
            {
                matched.Add(assembly.Name);
                var row = new RowResult
                {
                    Key = assembly.Name,
                    Title = assembly.Title,
                    Version = expected == null ? assembly.Version : assembly.Version + " (локально)",
                };
                FillFromAssembly(row, assembly, reports, deadlinePassed);
                rows.Add(row);
            }
            foreach (var group in reports.Where(r => !matched.Contains(r.Module)).GroupBy(r => r.Module, StringComparer.OrdinalIgnoreCase))
            {
                var row = new RowResult { Key = group.Key, Title = group.Key, Version = "" };
                FillFromReports(row, group.ToList(), deadlinePassed);
                rows.Add(row);
            }

            return rows.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static void FillFromAssembly(RowResult row, LoadedModule assembly, IReadOnlyList<ModuleStartupReport> reports, bool deadlinePassed)
        {
            if (!string.IsNullOrWhiteSpace(assembly.Title))
                row.Title = assembly.Title;
            if (string.IsNullOrWhiteSpace(row.Version))
                row.Version = assembly.Version;

            var own = reports.Where(r => string.Equals(r.Module, assembly.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (own.Count > 0)
            {
                FillFromReports(row, own, deadlinePassed);
                return;
            }

            if (!assembly.IsOwn)
                Set(row, RowState.Ok, "загружен", "Сторонний модуль: запуск не отслеживается.");
            else if (!assembly.ImplementsContract)
                Set(row, RowState.Warning, "загружен, без IModule",
                    "Стартовый класс модуля не реализует IModule - успех запуска и время не видны. См. README Pilot.ClientSdk, «Стартовый класс».");
            else if (deadlinePassed)
                Set(row, RowState.Warning, "нет отчёта о запуске",
                    "IModule реализован, но StartupRunner.Run(this) не вызван или конструктор стартового класса не выполнялся (ошибка MEF-импорта).");
            else
                Set(row, RowState.Waiting, "ожидание запуска…", null);
        }

        private static void FillFromReports(RowResult row, IReadOnlyList<ModuleStartupReport> reports, bool deadlinePassed)
        {
            row.DurationMs = reports.Sum(r => r.DurationMs);
            var failed = reports.Where(r => r.Status == ModuleStartupStatus.Error).ToList();
            if (failed.Count > 0)
            {
                var error = failed[0].Error;
                Set(row, RowState.Error, "ошибка запуска",
                    string.Join(Environment.NewLine, failed.Select(r => $"{r.Part}: {r.Error?.GetType().Name}: {r.Error?.Message}")));
                if (error is OperationCanceledException)
                    row.Status = "запуск отменён";
                return;
            }
            if (reports.Any(r => r.Status == ModuleStartupStatus.Running))
            {
                Set(row, RowState.Running, "запускается…", deadlinePassed ? "Инициализация модуля ещё идёт." : null);
                return;
            }
            Set(row, RowState.Ok, "запущен", null);
        }

        private static void Set(RowResult row, RowState state, string status, string details)
        {
            row.State = state;
            row.Status = status;
            row.Details = details;
        }

        private static bool SameDirectory(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            return string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }
    }
}
