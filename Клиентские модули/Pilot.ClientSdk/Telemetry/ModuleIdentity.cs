using System;
using System.Reflection;

namespace Pilot.ClientSdk.Telemetry
{
    /// <summary>
    /// Какой модуль прислал событие - берётся из атрибутов его сборки:
    /// <see cref="Module"/> - имя сборки (087OpenStoradge.ext2), <see cref="Title"/> - AssemblyProduct
    /// (так модуль называется в Pilot-myAdmin), <see cref="Version"/> - AssemblyVersion в виде X.Y.Z.
    /// </summary>
    public sealed class ModuleIdentity
    {
        public ModuleIdentity(string module, string title, string version)
        {
            Module = string.IsNullOrWhiteSpace(module) ? "<без имени>" : module.Trim();
            Title = string.IsNullOrWhiteSpace(title) ? Module : title.Trim();
            Version = ModuleVersions.Display(version); // 2.1.0.0 / 2.1.0
        }

        public string Module { get; }
        public string Title { get; }
        public string Version { get; }

        public static ModuleIdentity Of(Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));
            var name = assembly.GetName();
            string title = null;
            try
            {
                title = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product
                        ?? assembly.GetCustomAttribute<AssemblyTitleAttribute>()?.Title;
            }
            catch
            {
                // атрибуты недоступны (динамическая сборка и т.п.) - хватит имени
            }
            return new ModuleIdentity(name.Name, title, name.Version?.ToString());
        }

        /// <summary>Компания сборки (AssemblyCompany) или null.</summary>
        internal static string CompanyOf(Assembly assembly)
        {
            try { return assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company; }
            catch { return null; }
        }
    }
}
