using ProAdmin.Pilot;
using ProAdmin.Servers;
using ProAdmin.Settings;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Servers;

public class IndexModel : AppPageModel
{
    public const string Keep = "keep";

    private readonly ServerSettingsStore _store;
    private readonly PilotGateway _pilot;

    public IndexModel(ServerSettingsStore store, PilotGateway pilot)
    {
        _store = store;
        _pilot = pilot;
    }

    public sealed record EnvView(string Key, string Title, string PilotUrl, ServerSettingsState State);

    public List<EnvView> Envs { get; } = new();

    public bool Configured => _store.IsConfigured;

    /// <summary>Контур, чьи настройки только что сохранены, - для заметного предупреждения о перезапуске.</summary>
    [TempData] public string? SavedEnv { get; set; }

    public void OnGet() => Load();

    public async Task<IActionResult> OnPostAsync(string env, Dictionary<string, string> values, CancellationToken ct)
    {
        var target = Contours().FirstOrDefault(e => e.Key == env);
        if (target.Key is null)
            return NotFound();
        if (!_store.IsConfigured || !_store.Read(env).Available)
        {
            TempData["FlashError"] = $"{target.Title}: каталог настроек сервера не подключён к админке.";
            return RedirectToPage();
        }

        // Менять настройки сервера может только его администратор: проверяем вход на этот сервер.
        if (string.IsNullOrWhiteSpace(target.PilotUrl))
        {
            TempData["FlashError"] = $"{target.Title}: адрес сервера не задан в «Настройках» - нельзя проверить, что вы его администратор.";
            return RedirectToPage();
        }
        try
        {
            await _pilot.VerifyLoginAsync(target.PilotUrl, Vault.GetPilotLogin(CurrentSession, env), ct);
        }
        catch (PilotGatewayException ex)
        {
            TempData["FlashError"] = $"{target.Title}: изменять настройки может только администратор этого сервера Pilot. Проверка входа: {ex.Message}";
            return RedirectToPage();
        }

        var desired = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var def in ServerSettingCatalog.All)
        {
            var value = values.TryGetValue(def.Key, out var v) ? v : Keep;
            if (value is "true" or "false")
                desired[def.Key] = value == "true";
        }

        try
        {
            _store.Write(env, desired, CurrentSession.PilotLogin);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            TempData["FlashError"] = $"{target.Title}: не удалось записать настройки - {ex.Message}";
            return RedirectToPage();
        }

        SavedEnv = env;
        TempData["Flash"] = SavedFlash(target.Title, env, _store.Read(env));
        return RedirectToPage();
    }

    internal static string SavedFlash(string title, string env, ServerSettingsState state) =>
        state.IsCluster
            ? $"{title}: настройки сохранены. {ServerSettingsStore.ClusterRestartHint}"
            : state.RestartRequired
                ? $"{title}: настройки сохранены. Они вступят в силу только после перезапуска контейнера {ServerSettingsStore.ContainerName(env)}."
                : $"{title}: настройки сохранены, сервер уже работает с этими значениями - перезапуск не нужен.";

    private void Load()
    {
        ViewData["Section"] = "servers";
        foreach (var (key, title, url) in Contours())
            Envs.Add(new EnvView(key, title, url, _store.Read(key)));
    }

    private IEnumerable<(string Key, string Title, string PilotUrl)> Contours()
    {
        var test = AppSettings.Find(AdminSettings.Test);
        var prod = AppSettings.Find(AdminSettings.Prod);
        yield return (AdminSettings.Test, test?.Title ?? "Тест", test?.PilotUrl ?? "");
        yield return ("stage", "Стейдж", AppSettings.StagePilotUrl);
        yield return (AdminSettings.Prod, prod?.Title ?? "Прод", prod?.PilotUrl ?? "");
    }
}
