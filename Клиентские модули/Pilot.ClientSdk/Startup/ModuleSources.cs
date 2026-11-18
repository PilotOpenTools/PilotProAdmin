using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Pilot;
using Pilot.ClientSdk.Telemetry;

namespace Pilot.ClientSdk.Startup
{
    /// <summary>Откуда <see cref="ModuleCheck"/> берёт данные: сервер Pilot, папка Plugins клиента, AppDomain.</summary>
    internal sealed class ModuleSources
    {
        private const string ExtensionTypeName = "Extension";
        private const string ExtensionFolderTypeName = "Extension_folder";
        private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(10);

        private readonly ConcurrentDictionary<string, bool> _contract = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, IReadOnlyCollection<string>> _dllNames = new ConcurrentDictionary<string, IReadOnlyCollection<string>>(StringComparer.OrdinalIgnoreCase);
        private readonly IObjectsRepository _repository;
        private IReadOnlyList<string> _pluginRoots;

        public ModuleSources(IObjectsRepository repository) => _repository = repository;

        /// <summary>
        /// Модули, назначенные пользователю: живые объекты Extension в папке расширений, которые ему видны.
        /// null - список получить не удалось (нет связи, нет прав на папку расширений).
        /// </summary>
        public async Task<IReadOnlyList<ExpectedModule>> LoadExpectedAsync()
        {
            try
            {
                var loader = new Loader(_repository);
                var root = await loader.LoadAsync(SystemObjectIds.ExtensionRootObjectId, LoadTimeout).ConfigureAwait(false);
                if (root == null)
                {
                    ModuleLog.Warn("Проверка модулей: папка расширений недоступна, список модулей с сервера не получен");
                    return null;
                }

                var result = new List<ExpectedModule>();
                var seen = new HashSet<Guid> { root.Id };
                var level = root.Children.ToList();
                while (level.Count > 0)
                {
                    var objects = await loader.LoadAsync(level.Where(seen.Add), LoadTimeout).ConfigureAwait(false);
                    level = new List<Guid>();
                    foreach (var obj in objects.Where(o => !o.IsRemoved()))
                    {
                        var typeName = obj.Type?.Name;
                        if (typeName == ExtensionFolderTypeName)
                            level.AddRange(obj.Children);
                        else if (typeName == ExtensionTypeName && obj.Files.Count > 0)
                            result.Add(new ExpectedModule(obj.Id, NameOf(obj), VersionOf(obj), obj.Files[0].Id));
                    }
                }
                return result;
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Проверка модулей: не удалось получить список модулей с сервера", ex);
                return null;
            }
        }

        /// <summary>Папка скачанного модуля или null.</summary>
        public string PluginFolder(Guid fileId)
        {
            foreach (var root in PluginRoots())
            {
                var dir = Path.Combine(root, fileId.ToString());
                if (Directory.Exists(dir))
                    return dir;
            }
            return null;
        }

        public IReadOnlyCollection<string> DllNamesIn(string folder) =>
            _dllNames.GetOrAdd(folder, f =>
            {
                try
                {
                    return Directory.GetFiles(f, "*.ext2.dll").Select(Path.GetFileNameWithoutExtension).ToList();
                }
                catch
                {
                    return Array.Empty<string>();
                }
            });

        /// <summary>Загруженные в клиент сборки *.ext2 (модули Pilot).</summary>
        public IReadOnlyList<LoadedModule> Loaded()
        {
            var result = new List<LoadedModule>();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.IsDynamic)
                        continue;
                    var name = assembly.GetName().Name ?? "";
                    if (!name.EndsWith(".ext2", StringComparison.OrdinalIgnoreCase) || result.Any(r => r.Name == name))
                        continue;
                    var id = ModuleIdentity.Of(assembly);
                    result.Add(new LoadedModule(name, id.Title, id.Version, DirectoryOf(assembly),
                        ModuleStartup.IsModule(assembly), ImplementsContract(assembly)));
                }
                catch
                {
                    // одна странная сборка не должна ломать проверку
                }
            }
            return result;
        }

        private bool ImplementsContract(Assembly assembly) =>
            _contract.GetOrAdd(assembly.FullName, _ =>
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { return false; }
                return types.Any(t => t.IsClass && !t.IsAbstract &&
                                      (typeof(IModule).IsAssignableFrom(t) || typeof(IModuleAsync).IsAssignableFrom(t)));
            });

        /// <summary>
        /// Папки Plugins текущей базы: …\Databases\{база}\{версия клиента}\Plugins.
        /// Сначала - по уже загруженным модулям (так берётся именно текущая версия клиента), иначе - поиск по базе.
        /// </summary>
        private IReadOnlyList<string> PluginRoots()
        {
            if (_pluginRoots != null)
                return _pluginRoots;

            var roots = AppDomain.CurrentDomain.GetAssemblies()
                .Select(DirectoryOf)
                .Where(d => !string.IsNullOrEmpty(d) && Guid.TryParse(Path.GetFileName(d), out _))
                .Select(Path.GetDirectoryName)
                .Where(d => d != null && string.Equals(Path.GetFileName(d), "Plugins", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (roots.Count == 0)
            {
                try
                {
                    var ascon = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ASCON");
                    var dbId = _repository.GetDatabaseId().ToString();
                    roots = Directory.Exists(ascon)
                        ? Directory.GetDirectories(ascon)
                            .Select(p => Path.Combine(p, "Databases", dbId))
                            .Where(Directory.Exists)
                            .SelectMany(Directory.GetDirectories)
                            .Select(v => Path.Combine(v, "Plugins"))
                            .Where(Directory.Exists)
                            .OrderByDescending(Directory.GetLastWriteTimeUtc)
                            .ToList()
                        : new List<string>();
                }
                catch (Exception ex)
                {
                    ModuleLog.Warn("Проверка модулей: не найдена папка Plugins клиента", ex);
                }
            }
            return _pluginRoots = roots;
        }

        private static string DirectoryOf(Assembly assembly)
        {
            try
            {
                return assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) ? null : Path.GetDirectoryName(assembly.Location);
            }
            catch
            {
                return null;
            }
        }

        private static string NameOf(IDataObject obj) =>
            Attr(obj, "Extension_name") ?? obj.DisplayName ?? obj.Id.ToString();

        /// <summary>«Версия 1.2.1 - Размер 93 КБ» / «1.2.1» (старые «1.2.1.0» - тоже «1.2.1»).</summary>
        internal static string VersionOf(IDataObject obj) => VersionFromAdditional(Attr(obj, "Extension_additional"));

        internal static string VersionFromAdditional(string additional)
        {
            if (string.IsNullOrWhiteSpace(additional))
                return "";
            var m = System.Text.RegularExpressions.Regex.Match(additional, @"Верси[яи]\s+([0-9]+(?:\.[0-9]+){1,3})");
            return m.Success ? Telemetry.ModuleVersions.Display(m.Groups[1].Value) : additional.Trim();
        }

        // Имя атрибута в базе - «Extension_name 53522556-…», ищем по префиксу.
        private static string Attr(IDataObject obj, string prefix)
        {
            foreach (var pair in obj.Attributes)
            {
                if (pair.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && pair.Value is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
            return null;
        }
    }
}
