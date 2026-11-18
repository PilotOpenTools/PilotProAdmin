using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Pilot.ClientSdk
{
    /// <summary>Встроенные ресурсы сборки модуля (иконки меню и т.п.). Замена IconLoader из NugetPackage.</summary>
    public static class EmbeddedResources
    {
        /// <summary>
        /// Байты ресурса по относительному пути (Resources/icon.svg). Build Action файла - EmbeddedResource.
        /// <code>builder.AddItem("open", 0).WithHeader("Открыть").WithIcon(EmbeddedResources.GetBytes("Resources/open.svg", typeof(StartPoint).Assembly));</code>
        /// </summary>
        public static byte[] GetBytes(string relativePath, Assembly assembly)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                throw new ArgumentException("Не указан путь ресурса.", nameof(relativePath));
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));

            var suffix = relativePath.TrimStart('/', '\\', '.').Replace('\\', '.').Replace('/', '.');
            var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException(
                           $"В сборке {assembly.GetName().Name} нет встроенного ресурса «{relativePath}». Проверьте Build Action = EmbeddedResource.");

            using (var stream = assembly.GetManifestResourceStream(name))
            using (var ms = new MemoryStream())
            {
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
    }
}
