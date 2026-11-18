using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Startup;
using Pilot.ClientSdk.Telemetry;

namespace Pilot.ClientSdk.Pilot
{
    /// <summary>
    /// Точка входа. IDataPlugin клиент Pilot создаёт при старте: сервисы SDK сохраняются в <see cref="PilotApi"/>,
    /// запускается отправка статистики, а через минуту уходит список загруженных модулей с версиями
    /// (по нему в Grafana видно, какая версия модуля реально стоит у пользователей после выкладки).
    /// Когда откроется главное окно Pilot, показывается окно «Загрузка модулей» (<see cref="StartupMonitor"/>).
    /// </summary>
    [Export(typeof(IDataPlugin))]
    public sealed class ModuleStartup : IDataPlugin, IModule
    {
        private static readonly TimeSpan LoadedModulesDelay = TimeSpan.FromMinutes(1);

        private readonly IObjectsRepository _repository;
        private readonly IClientInfo _clientInfo;
        private readonly IDisposable _settingsSubscription;

        [ImportingConstructor]
        public ModuleStartup(
            IObjectsRepository repository,
            ISearchService searchService,
            IObjectModifier modifier,
            IFileProvider fileProvider,
            IPilotDialogService dialogs,
            ITabServiceProvider tabs,
            IClientInfo clientInfo,
            IPersonalSettings personalSettings,
            IAttributeFormatParser attributeFormatParser)
        {
            PilotApi.Init(repository, searchService, modifier, fileProvider, dialogs, tabs, clientInfo, personalSettings, attributeFormatParser);
            _repository = repository;
            _clientInfo = clientInfo;
            // Общие настройки Pilot (раздел «Модули») важнее файла: подписка живёт весь сеанс клиента.
            ModuleConfig.Changed += config => StatsSender.Default.Configure(config);
            _settingsSubscription = CommonSettings.Subscribe(personalSettings);
            // StartupRunner не пробрасывает исключение: модуль не должен мешать клиенту запуститься.
            StartupRunner.Run(this);
        }

        public void Initialize()
        {
            var config = ModuleConfig.Current;
            StatsSender.Default.Start(config, ClientEnvironment.Capture(_repository, _clientInfo));
            ModuleLog.Info($"Pilot.ClientSdk {ClientEnvironment.LibVersion} загружен. Настройки: {config.Source}. Статистика: " +
                           (config.StatsActive ? config.StatsUrl : "выключена"));

            // Настройки читаются через минуту: к этому времени общие настройки Pilot уже пришли.
            Task.Delay(LoadedModulesDelay).ContinueWith(_ =>
            {
                var current = ModuleConfig.Current;
                if (current.StatsActive && current.ReportLoadedModules)
                    ReportLoadedModules();
            }, TaskScheduler.Default);

            StartupMonitor.Start(_repository);
        }

        /// <summary>Модули (сборки *.ext2 с AssemblyCompany «CICD»), загруженные в клиент.</summary>
        internal static IReadOnlyList<Assembly> LoadedModules() =>
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && IsModule(a))
                .GroupBy(a => a.GetName().Name)
                .Select(g => g.First())
                .OrderBy(a => a.GetName().Name)
                .ToList();

        internal static bool IsModule(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? "";
            return name.EndsWith(".ext2", StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(ModuleIdentity.CompanyOf(assembly), "CICD", StringComparison.OrdinalIgnoreCase);
        }

        private static void ReportLoadedModules()
        {
            try
            {
                var modules = LoadedModules();
                foreach (var assembly in modules)
                    ModuleStats.For(assembly).Loaded();
                StatsSender.Default.FlushInBackground();
                ModuleLog.Info($"Загруженные модули ({modules.Count}): " +
                               string.Join(", ", modules.Select(a => $"{a.GetName().Name} {Telemetry.ModuleVersions.Display(a.GetName().Version)}")));
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось отправить список модулей", ex);
            }
        }
    }
}
