using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Text;
using System.Windows;
using Ascon.Pilot.SDK;
using Ascon.Pilot.SDK.Menu;
using Pilot.ClientSdk.Infrastructure;
using Pilot.ClientSdk.Startup;
using Pilot.ClientSdk.Telemetry;

namespace Pilot.ClientSdk.Pilot
{
    /// <summary>
    /// Пункты трея: «Модули версии» (какие модули загружены и в каких версиях - проверить, что пользователь
    /// получил новую версию после выкладки) и «Модули загрузка» (повторно открыть окно проверки загрузки модулей).
    /// </summary>
    [Export(typeof(IMenu<SystemTrayContext>))]
    public sealed class TrayMenu : IMenu<SystemTrayContext>
    {
        private const string ItemName = "ClientSdkVersions";
        private const string StartupItemName = "ClientSdkStartup";

        public void Build(IMenuBuilder builder, SystemTrayContext context)
        {
            if (!ModuleConfig.Current.TrayMenu)
                return;
            try
            {
                builder.AddItem(ItemName, builder.Count).WithHeader("Модули версии");
                builder.AddItem(StartupItemName, builder.Count).WithHeader("Модули загрузка");
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось добавить пункт в трей", ex);
            }
        }

        public void OnMenuItemClick(string name, SystemTrayContext context)
        {
            if (name == StartupItemName)
            {
                StartupMonitor.ShowReport();
                return;
            }
            if (name != ItemName)
                return;

            var sb = new StringBuilder();
            foreach (var assembly in ModuleStartup.LoadedModules())
            {
                var id = ModuleIdentity.Of(assembly);
                sb.AppendLine($"{id.Title}").AppendLine($"    {id.Module}  ·  {id.Version}");
            }
            if (sb.Length == 0)
                sb.AppendLine("Модулей не найдено.");

            var config = ModuleConfig.Current;
            sb.AppendLine().Append("Статистика: ").Append(config.StatsActive ? config.StatsUrl : "выключена");
            sb.AppendLine().Append("Настройки: ").Append(config.Source);
            MessageBox.Show(sb.ToString(), "Модули", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
