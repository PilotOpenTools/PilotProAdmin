using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Ascon.Pilot.SDK;
using Humanizer;
using Newtonsoft.Json;

namespace TestModule.Pilot
{
    /// <summary>
    /// Эксперимент «общие библиотеки»: в zip этого модуля нет ни одной сторонней dll.
    /// Newtonsoft.Json (13.0.3) должен прийти из папки клиента Pilot, Humanizer (собран против 2.13.14) -
    /// из модуля Pilot.ClientSdk (там лежит 2.14.1). Проверка - при загрузке модуля
    /// (второй IMenu&lt;ObjectsViewContext&gt; в модуле нельзя: клиент импортирует ровно один, при двух меню пропадает совсем). Результат пишется в %LocalAppData%\CICD\SharedLibsProbe.log.
    /// Без вызовов Pilot.ClientSdk: проба должна работать, даже если общий модуль ещё не загружен.
    /// </summary>
    internal static class SharedLibsProbe
    {
        private const string ClientSdkAssembly = "113Pilot.ClientSdk.ext2";

        public static string Run(string when)
        {
            var text = new StringBuilder();
            text.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {when}");
            var modules = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .Where(n => n.EndsWith(".ext2", StringComparison.OrdinalIgnoreCase))
                .ToList();
            text.AppendLine($"  Pilot.ClientSdk уже загружен: {(modules.Contains(ClientSdkAssembly) ? "да" : "НЕТ")}; модулей .ext2 в домене: {modules.Count}");
            text.AppendLine("  Newtonsoft.Json: " + Try(NewtonsoftCheck));
            text.AppendLine("  Humanizer: " + Try(HumanizerCheck));
            var result = text.ToString();
            Write(result);
            return result;
        }

        private static string Try(Func<string> check)
        {
            try
            {
                return "OK - " + check();
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null)
                    inner = inner.InnerException;
                return $"ОШИБКА {inner.GetType().Name}: {inner.Message}";
            }
        }

        // NoInlining: ссылка на тип библиотеки разрешается при JIT этого метода, а не вызывающего - ошибку ловит Try.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string NewtonsoftCheck()
        {
            var json = JsonConvert.SerializeObject(new { ok = true });
            return $"{json}; {Describe(typeof(JsonConvert))}";
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static string HumanizerCheck()
        {
            var text = "SharedLibrariesWork".Humanize();
            return $"«{text}»; {Describe(typeof(StringHumanizeExtensions))}";
        }

        private static string Describe(Type type)
        {
            var assembly = type.Assembly;
            return $"{assembly.GetName().Version} из {assembly.Location}";
        }

        private static void Write(string text)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CICD");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "SharedLibsProbe.log"), text + Environment.NewLine);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>Проба при загрузке модуля: работают ли общие библиотеки, если модуль загрузился раньше Pilot.ClientSdk.</summary>
    [Export(typeof(IDataPlugin))]
    public sealed class SharedLibsStartupProbe : IDataPlugin
    {
        public SharedLibsStartupProbe()
        {
            try
            {
                SharedLibsProbe.Run("при загрузке модуля");
            }
            catch (Exception)
            {
                // проба не должна мешать загрузке модуля
            }
        }
    }
}
