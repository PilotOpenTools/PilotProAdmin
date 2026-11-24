using System.Text.RegularExpressions;

namespace ProAdmin.Tests;

/// <summary>
/// То, что по устройству стека живёт в нескольких местах: сервисы собираются порознь, и общий файл им не подключить.
/// Копии не должны расходиться. Тесты читают соседние папки монорепозитория (stack, proadmin, Серверные расширения);
/// вне него они падают с объяснением (см. <see cref="RepoCopiesFactAttribute"/>), а не молча проходят.
/// </summary>
public class RepoCopiesTests
{
    private static string Read(params string[] path) =>
        File.ReadAllText(Path.Combine(new[] { RepoCopies.Root() }.Concat(path).ToArray())).Replace("\r\n", "\n");

    private static string[] Sorted(IEnumerable<string> keys) => keys.Distinct().OrderBy(k => k, StringComparer.Ordinal).ToArray();

    [RepoCopiesFact]
    public void Server_setting_keys_match_in_wrapper_admin_and_migration()
    {
        var wrapper = Regex.Match(Read("stack", "Install", "wait-for-postgres.sh"), "SERVER_SETTINGS_KEYS=\"([^\"]+)\"").Groups[1].Value
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var admin = Regex.Matches(Read("proadmin", "src", "ProAdmin", "Servers", "ServerSettingCatalog.cs"),
            "new ServerSettingDefinition\\(\"([A-Za-z]+)\"").Select(m => m.Groups[1].Value);
        var migration = Regex.Matches(Read("proadmin", "migration", "src", "PilotMigration", "Pilot", "ServerSettingsCopy.cs"),
            "\\(\"([A-Z][A-Za-z]+)\", \"").Select(m => m.Groups[1].Value);

        Assert.NotEmpty(wrapper);
        Assert.Equal(Sorted(wrapper), Sorted(admin));
        Assert.Equal(Sorted(wrapper), Sorted(migration));
    }

    [RepoCopiesFact]
    public void Server_modules_contract_matches_host()
    {
        static string Code(string text) => string.Join("\n", text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("namespace ")));

        var host = Read("server-extensions", "ServerHost", "ServerHost", "Contract", "IServerModulesApi.cs");
        var admin = Read("proadmin", "src", "ProAdmin", "Pilot", "ServerModules", "ServerModulesContract.cs");
        Assert.Equal(Code(host), Code(admin));
    }
}
