using System;
using System.Linq;

namespace Pilot.ClientSdk.Telemetry
{
    /// <summary>Версия модуля - три числа X.Y.Z (как в ProAdmin и тегах vX.Y.Z).</summary>
    internal static class ModuleVersions
    {
        /// <summary>
        /// «2.1.0.0» (AssemblyVersion .NET) / «2.1.0». Ненулевая четвёртая часть остаётся видна -
        /// это другая сборка, а не та же версия.
        /// </summary>
        public static string Display(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return version ?? string.Empty;
            var v = version.Trim();
            return v.Count(c => c == '.') == 3 && v.EndsWith(".0", StringComparison.Ordinal) ? v.Substring(0, v.Length - 2) : v;
        }

        public static string Display(Version version) => Display(version?.ToString());
    }
}
