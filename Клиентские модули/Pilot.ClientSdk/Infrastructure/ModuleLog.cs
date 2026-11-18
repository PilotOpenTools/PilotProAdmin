namespace Pilot.ClientSdk.Infrastructure
{
    /// <summary>Лог самого Pilot.ClientSdk: %AppData%\ASCON\Units Logs\Logs\Pilot.ClientSdk.log.</summary>
    internal static class ModuleLog
    {
        private static readonly ModuleLogger Logger = new ModuleLogger("Pilot.ClientSdk", 5 * 1024 * 1024);

        public static void Info(string message) => Logger.Info(message);

        public static void Warn(string message, System.Exception ex = null) => Logger.Warn(message, ex);

        public static void Error(string message, System.Exception ex = null) => Logger.Error(message, ex);
    }
}
