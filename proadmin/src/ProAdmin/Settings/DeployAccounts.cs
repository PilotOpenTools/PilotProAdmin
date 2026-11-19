using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;

namespace ProAdmin.Settings;

public sealed record StoredDeployLogin(string Login, string ProtectedPassword);

public sealed class DeployAccountInput
{
    public string? Login { get; set; }
    public string? Password { get; set; }
    public bool Disable { get; set; }
}

public sealed class DeployAccounts(IDataProtectionProvider protection, IOptions<AppOptions> options)
{
    public static IReadOnlyList<string> Contours { get; } = ["test", "stage", "prod"];
    private readonly IDataProtector _protector = protection.CreateProtector("ProAdmin.CiDeployAccounts");

    public PilotLogin? Get(AdminSettings settings, string contour)
    {
        if (!settings.DeployLogins.TryGetValue(contour, out var saved))
            return options.Value.CiDeploy.Login(contour);
        if (string.IsNullOrEmpty(saved.ProtectedPassword))
            return null;
        try { return new PilotLogin(saved.Login, _protector.Unprotect(saved.ProtectedPassword)); }
        catch (CryptographicException) { return null; }
    }

    public List<DeployAccountInput> Prefill(AdminSettings settings) => Contours.Select(contour =>
        new DeployAccountInput { Login = Get(settings, contour)?.Login }).ToList();

    public async Task<string?> ApplyAsync(AdminSettings settings, IReadOnlyList<DeployAccountInput> inputs,
        PilotGateway pilot, CancellationToken ct)
    {
        if (inputs.Count != Contours.Count)
            return "Не удалось прочитать служебные учётные записи. Обновите страницу и повторите.";
        for (var i = 0; i < Contours.Count; i++)
        {
            var contour = Contours[i];
            var input = inputs[i];
            if (input.Disable)
            {
                settings.DeployLogins[contour] = new("", "");
                continue;
            }
            var previous = Get(settings, contour);
            var login = input.Login?.Trim();
            if (string.IsNullOrEmpty(input.Password) &&
                (string.IsNullOrEmpty(login) || login == previous?.Login))
                continue;
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(input.Password))
                return $"Pilot {contour}: при изменении учётной записи укажите логин и пароль.";
            var url = contour == "stage" ? settings.StagePilotUrl : settings.Find(contour)?.PilotUrl;
            if (string.IsNullOrWhiteSpace(url))
                return $"Pilot {contour}: сначала укажите адрес сервера.";
            try { await pilot.VerifyLoginAsync(url, new PilotLogin(login, input.Password), ct); }
            catch (PilotGatewayException ex) { return $"Pilot {contour}: {ex.Message}"; }
            settings.DeployLogins[contour] = new(login, _protector.Protect(input.Password));
        }
        return null;
    }
}
