using System;
using System.Reflection;
using Ascon.Pilot.SDK;
using Pilot.ClientSdk.Infrastructure;

namespace Pilot.ClientSdk.Telemetry
{
    /// <summary>Сведения о клиенте, которые прикладываются к каждому событию статистики.</summary>
    internal sealed class ClientEnvironment
    {
        public static string LibVersion { get; } = ModuleVersions.Display(typeof(ClientEnvironment).Assembly.GetName().Version);

        public string Login { get; set; }
        public string Person { get; set; }
        public string Server { get; set; }
        public string ClientType { get; set; }
        public string ClientVersion { get; set; }
        public string Machine { get; set; } = Environment.MachineName;

        public static ClientEnvironment Capture(IObjectsRepository repository, IClientInfo clientInfo)
        {
            var env = new ClientEnvironment { ClientVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() };

            try
            {
                var person = repository?.GetCurrentPerson();
                env.Login = person?.Login;
                env.Person = person?.DisplayName;
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось получить текущего пользователя", ex);
            }

            try
            {
                env.Server = clientInfo?.ConnectionString?.ToString();
                env.ClientType = clientInfo?.GetClientType().ToString();
            }
            catch (Exception ex)
            {
                ModuleLog.Warn("Не удалось получить сведения о подключении", ex);
            }

            return env;
        }

        public void Fill(ModuleEvent e)
        {
            e.Login = e.Login ?? Login;
            e.Person = e.Person ?? Person;
            e.Server = e.Server ?? Server;
            e.ClientType = e.ClientType ?? ClientType;
            e.ClientVersion = e.ClientVersion ?? ClientVersion;
            e.Machine = e.Machine ?? Machine;
            e.LibVersion = LibVersion;
        }
    }
}
