using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace Pilot.ClientSdk.Infrastructure
{
    /// <summary>
    /// Настройки модуля. Откуда берутся (по убыванию приоритета):
    /// 1) общие настройки Pilot, раздел «Модули» (<see cref="Pilot.CommonSettings"/>) - задаёт администратор, действуют на всех;
    /// 2) Pilot.ClientSdk.config.json рядом с dll модуля - значения по умолчанию, пока общие настройки не заданы.
    /// Адрес сервиса статистики можно переопределить переменной окружения CLIENTSDK_STATS_URL
    /// (удобно на машине разработчика).
    /// </summary>
    [DataContract]
    internal sealed class ModuleConfig
    {
        public const string FileName = "Pilot.ClientSdk.config.json";
        public const int DefaultFlushSeconds = 15;
        public const int DefaultStartupLockSeconds = 15;

        public const string WindowAlways = "always";
        public const string WindowOnProblems = "problems";
        public const string WindowNever = "never";

        [DataMember(Name = "statsEnabled", Order = 1)]
        public bool StatsEnabled { get; set; } = true;

        /// <summary>POST-адрес сервиса статистики: http://host:5310/api/module-events.</summary>
        [DataMember(Name = "statsUrl", Order = 2)]
        public string StatsUrl { get; set; }

        [DataMember(Name = "apiKey", Order = 3)]
        public string ApiKey { get; set; }

        /// <summary>Как часто отправлять накопленные события (секунды).</summary>
        [DataMember(Name = "flushSeconds", Order = 4)]
        public int FlushSeconds { get; set; } = DefaultFlushSeconds;

        /// <summary>При старте клиента отправить список загруженных модулей и их версии.</summary>
        [DataMember(Name = "reportLoadedModules", Order = 5)]
        public bool ReportLoadedModules { get; set; } = true;

        /// <summary>
        /// Окно «Загрузка модулей»: <see cref="WindowAlways"/> - при каждом запуске,
        /// <see cref="WindowOnProblems"/> - только если модуль не скачан / не загружен / упал при запуске,
        /// <see cref="WindowNever"/> - не показывать (проверка только в лог и Grafana).
        /// Не задано - по старому флагу <see cref="StartupWindow"/>.
        /// </summary>
        [DataMember(Name = "startupWindowMode", Order = 6, EmitDefaultValue = false)]
        public string StartupWindowMode { get; set; }

        /// <summary>Старый флаг окна загрузки (до startupWindowMode): false = «never».</summary>
        [DataMember(Name = "startupWindow", Order = 7)]
        public bool StartupWindow { get; set; } = true;

        /// <summary>Сколько секунд окно загрузки нельзя закрыть, пока модули ещё грузятся (и сколько ждём модули).</summary>
        [DataMember(Name = "startupLockSeconds", Order = 8)]
        public int StartupLockSeconds { get; set; } = DefaultStartupLockSeconds;

        /// <summary>Закрыть окно загрузки самому, если все модули запустились без ошибок.</summary>
        [DataMember(Name = "startupAutoClose", Order = 9)]
        public bool StartupAutoClose { get; set; } = true;

        /// <summary>Модули, которые не проверять при старте (часть названия модуля или имени сборки, без учёта регистра).</summary>
        [DataMember(Name = "ignoredModules", Order = 10)]
        public string[] IgnoredModules { get; set; } = Array.Empty<string>();

        /// <summary>Пункты «Модули …» в меню трея.</summary>
        [DataMember(Name = "trayMenu", Order = 11)]
        public bool TrayMenu { get; set; } = true;

        /// <summary>Откуда взяты настройки (для трея и лога).</summary>
        public string Source { get; private set; } = "значения по умолчанию";

        public bool StatsActive => StatsEnabled && !string.IsNullOrWhiteSpace(StatsUrl);

        /// <summary>Режим окна загрузки с учётом старого флага startupWindow.</summary>
        public string WindowMode =>
            StartupWindowMode == WindowAlways || StartupWindowMode == WindowOnProblems || StartupWindowMode == WindowNever
                ? StartupWindowMode
                : StartupWindow ? WindowAlways : WindowNever;

        /// <summary>Модуль исключён из проверки при старте.</summary>
        public bool IsIgnored(params string[] names) =>
            IgnoredModules != null && IgnoredModules.Any(i => !string.IsNullOrWhiteSpace(i) &&
                names.Any(n => n != null && n.IndexOf(i.Trim(), StringComparison.OrdinalIgnoreCase) >= 0));

        private static readonly object Sync = new object();
        private static ModuleConfig _file;
        private static ModuleConfig _current;

        /// <summary>Действующие настройки: общие настройки Pilot, если заданы, иначе файл из папки модуля.</summary>
        public static ModuleConfig Current
        {
            get
            {
                lock (Sync)
                    return _current ?? (_current = File);
            }
        }

        /// <summary>Настройки из файла рядом с dll (читаются один раз).</summary>
        public static ModuleConfig File
        {
            get
            {
                lock (Sync)
                    return _file ?? (_file = Load(Path.GetDirectoryName(typeof(ModuleConfig).Assembly.Location)));
            }
        }

        /// <summary>Действующие настройки поменялись (пришли общие настройки Pilot).</summary>
        public static event Action<ModuleConfig> Changed;

        /// <summary>
        /// Значение общих настроек Pilot (JSON). Пусто - действуют настройки из файла.
        /// Невалидный JSON пишется в лог и не меняет действующие настройки.
        /// </summary>
        public static void ApplyCommon(string json)
        {
            ModuleConfig next;
            if (string.IsNullOrWhiteSpace(json))
                next = File;
            else
            {
                try
                {
                    next = Normalize(Parse(json));
                    next.Source = "общие настройки Pilot";
                }
                catch (Exception ex)
                {
                    ModuleLog.Error("Общие настройки «Модули» не разобраны - действуют прежние", ex);
                    return;
                }
            }

            lock (Sync)
            {
                if (ReferenceEquals(_current, next) || (_current != null && _current.ToJson() == next.ToJson() && _current.Source == next.Source))
                    return;
                _current = next;
            }
            ModuleLog.Info($"Настройки Pilot.ClientSdk: {next.Source}. Статистика: {(next.StatsActive ? next.StatsUrl : "выключена")}, окно загрузки: {next.WindowMode}");
            try
            {
                Changed?.Invoke(next);
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось применить новые настройки", ex);
            }
        }

        public static ModuleConfig Load(string directory)
        {
            var path = Path.Combine(directory ?? string.Empty, FileName);
            var config = TryRead(path);
            if (config != null)
                config.Source = "файл " + FileName;
            return Normalize(config ?? new ModuleConfig());
        }

        private static ModuleConfig Normalize(ModuleConfig config)
        {
            var envUrl = Environment.GetEnvironmentVariable("CLIENTSDK_STATS_URL");
            if (!string.IsNullOrWhiteSpace(envUrl))
                config.StatsUrl = envUrl.Trim();
            config.StatsUrl = string.IsNullOrWhiteSpace(config.StatsUrl) ? null : config.StatsUrl.Trim();
            if (config.FlushSeconds < 1)
                config.FlushSeconds = DefaultFlushSeconds;
            if (config.StartupLockSeconds < 0)
                config.StartupLockSeconds = DefaultStartupLockSeconds;
            config.IgnoredModules = (config.IgnoredModules ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray();
            return config;
        }

        internal static ModuleConfig Parse(string json)
        {
            var serializer = new DataContractJsonSerializer(typeof(ModuleConfig));
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                return (ModuleConfig)serializer.ReadObject(stream);
        }

        /// <summary>JSON для общих настроек Pilot (с отступами - его видно в окне настроек как текст).</summary>
        public string ToJson()
        {
            var copy = (ModuleConfig)MemberwiseClone();
            copy.StartupWindowMode = WindowMode;
            copy.StartupWindow = copy.StartupWindowMode != WindowNever;
            var serializer = new DataContractJsonSerializer(typeof(ModuleConfig));
            using (var stream = new MemoryStream())
            {
                using (var writer = JsonReaderWriterFactory.CreateJsonWriter(stream, Encoding.UTF8, false, true, "  "))
                {
                    serializer.WriteObject(writer, copy);
                    writer.Flush();
                }
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }

        public ModuleConfig Clone()
        {
            var copy = (ModuleConfig)MemberwiseClone();
            copy.IgnoredModules = (IgnoredModules ?? Array.Empty<string>()).ToArray();
            return copy;
        }

        /// <summary>Для тестов: сбросить действующие настройки.</summary>
        internal static void Reset(ModuleConfig file = null)
        {
            lock (Sync)
            {
                _file = file;
                _current = null;
            }
            Changed = null;
        }

        private static ModuleConfig TryRead(string path)
        {
            try
            {
                return System.IO.File.Exists(path) ? Parse(System.IO.File.ReadAllText(path, Encoding.UTF8)) : null;
            }
            catch (Exception ex)
            {
                ModuleLog.Error("Не удалось прочитать " + path, ex);
                return null;
            }
        }

        // DataContractJsonSerializer не вызывает инициализаторы свойств - выставляем значения по умолчанию сами.
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            StatsEnabled = true;
            FlushSeconds = DefaultFlushSeconds;
            ReportLoadedModules = true;
            StartupWindow = true;
            StartupLockSeconds = DefaultStartupLockSeconds;
            StartupAutoClose = true;
            IgnoredModules = Array.Empty<string>();
            TrayMenu = true;
            Source = "значения по умолчанию";
        }
    }
}
