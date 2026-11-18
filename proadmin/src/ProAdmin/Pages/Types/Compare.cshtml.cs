using Ascon.Pilot.DataClasses;
using ProAdmin.Compare;
using ProAdmin.Pilot;
using ProAdmin.Settings;
using ProAdmin.Types;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Types;

/// <summary>Различия конфигурации двух баз: типы и атрибуты, состояния, группы состояний.</summary>
public class CompareModel : AppPageModel
{
    private readonly PilotGateway _pilot;

    public CompareModel(PilotGateway pilot) => _pilot = pilot;

    /// <summary>types - типы и атрибуты; states - состояния; machines - группы состояний.</summary>
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "types";
    [BindProperty(SupportsGet = true)] public string? A { get; set; }
    [BindProperty(SupportsGet = true)] public string? B { get; set; }

    public EnvironmentSettings? EnvA { get; private set; }
    public EnvironmentSettings? EnvB { get; private set; }
    public MetadataCompareResult? Result { get; private set; }
    public string? Problem { get; private set; }

    public IEnumerable<EnvironmentSettings> Servers => AppSettings.Environments.Where(e => e.HasServer);

    public List<MetaRow> Rows => Result is null ? new() : Kind switch
    {
        "states" => Result.States,
        "machines" => Result.StateMachines,
        _ => Result.Types,
    };

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();

        Kind = Kind is "states" or "machines" ? Kind : "types";
        var (defA, defB) = CompareService.DefaultPair(AppSettings);
        EnvA = AppSettings.Find(A) is { HasServer: true } a ? a : defA;
        EnvB = AppSettings.Find(B) is { HasServer: true } b ? b : defB;
        if (EnvA is null || EnvB is null || EnvA.Key == EnvB.Key)
        {
            Problem = "Для сравнения нужны два разных контура с серверами Pilot - проверьте настройки.";
            return Page();
        }

        string? errorA = null, errorB = null;
        var ta = Load(EnvA, m => errorA = m, ct);
        var tb = Load(EnvB, m => errorB = m, ct);
        await Task.WhenAll(ta, tb);

        Result = ta.Result is { } ma && tb.Result is { } mb ? MetadataCompare.Compare(ma, mb) : new MetadataCompareResult();
        Result.ErrorA = errorA;
        Result.ErrorB = errorB;
        return Page();
    }

    private async Task<DMetadata?> Load(EnvironmentSettings env, Action<string> onError, CancellationToken ct)
    {
        try
        {
            return await _pilot.GetMetadataAsync(env.PilotUrl, Vault.GetPilotLogin(CurrentSession, env.Key), ct);
        }
        catch (PilotGatewayException ex)
        {
            onError(ex.Message);
            return null;
        }
    }

    public int CountDiffs(List<MetaRow> rows) => rows.Count(r => r.State != CompareState.Same);

    public string Link(string? kind = null, string? a = null, string? b = null) =>
        $"/Types/Compare?kind={kind ?? Kind}&a={a ?? EnvA?.Key}&b={b ?? EnvB?.Key}";
}
