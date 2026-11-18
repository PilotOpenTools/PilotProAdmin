using System;
using System.IO;
using System.Text;

namespace Pilot.ClientSdk
{
    /// <summary>
    /// Файловый лог модуля: %AppData%\ASCON\Units Logs\Logs\{имя}.log - там же, где логи NugetPackage.
    /// Замена ModuleLogger из NugetPackage без NLog: модули живут в одном AppDomain клиента,
    /// и разные версии NLog у разных модулей конфликтуют.
    /// <code>
    /// private static readonly ModuleLogger Log = new ModuleLogger("OpenStoradge");
    /// Log.Info("Модуль запущен");
    /// Log.Error("Не удалось открыть хранилище", ex);
    /// </code>
    /// Потокобезопасен, никогда не бросает исключений. При превышении размера файл начинается заново.
    /// </summary>
    public sealed class ModuleLogger
    {
        private readonly object _sync = new object();
        private readonly long _maxSizeBytes;

        public ModuleLogger(string moduleName, long maxSizeBytes = 10 * 1024 * 1024)
        {
            if (string.IsNullOrWhiteSpace(moduleName))
                throw new ArgumentException("Не указано имя модуля.", nameof(moduleName));
            _maxSizeBytes = maxSizeBytes;
            FilePath = Path.Combine(Directory, SafeFileName(moduleName) + ".log");
        }

        /// <summary>Папка логов модулей: %AppData%\ASCON\Units Logs\Logs.</summary>
        public static string Directory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ASCON", "Units Logs", "Logs");

        public string FilePath { get; }

        public void Debug(string message) => Write("DEBUG", message, null);
        public void Info(string message) => Write("INFO", message, null);
        public void Warn(string message, Exception ex = null) => Write("WARN", message, ex);
        public void Error(string message, Exception ex = null) => Write("ERROR", message, ex);
        public void Fatal(string message, Exception ex = null) => Write("FATAL", message, ex);

        /// <summary>Очистить файл лога.</summary>
        public void Clear()
        {
            try
            {
                lock (_sync)
                    File.WriteAllText(FilePath, string.Empty);
            }
            catch
            {
                // лог не должен ронять модуль
            }
        }

        private void Write(string level, string message, Exception ex)
        {
            try
            {
                lock (_sync)
                {
                    System.IO.Directory.CreateDirectory(Directory);
                    var file = new FileInfo(FilePath);
                    if (file.Exists && file.Length > _maxSizeBytes)
                        file.Delete();

                    var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
                    if (ex != null)
                        line += Environment.NewLine + ex;
                    File.AppendAllText(FilePath, line + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // лог не должен ронять модуль
            }
        }

        private static string SafeFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (var c in name.Trim())
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }
    }
}
