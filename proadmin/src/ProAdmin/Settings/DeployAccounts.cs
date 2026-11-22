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

    /// <summary>Пароль уже сохранён (в настройках или в .env установщика) - пустое поле его не меняет.</summary>
    public bool HasPassword { get; set; }
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
        Get(settings, contour) is { } login
            ? new DeployAccountInput { Login = login.Login, HasPassword = !string.IsNullOrEmpty(login.Password) }
            : new DeployAccountInput()).ToList();

    /// <summary>Есть ли в форме изменения: введён пароль или другой логин. Без изменений сохранение учёток не трогает Pilot.</summary>
    public bool HasChanges(AdminSettings settings, IReadOnlyList<DeployAccountInput> inputs) =>
        inputs.Count == Contours.Count && inputs.Select((input, i) =>
            !string.IsNullOrEmpty(input.Password) ||
            (!string.IsNullOrWhiteSpace(input.Login) && input.Login.Trim() != Get(settings, Contours[i])?.Login)).Any(c => c);

    /// <param name="notes">Что сделано в базах (создан служебный пользователь) и предупреждения по учётным записям,
    /// которые не менялись: их проблемы сохранение не останавливают.</param>
    public async Task<string?> ApplyAsync(AdminSettings settings, IReadOnlyList<DeployAccountInput> inputs,
        PilotGateway pilot, CancellationToken ct, List<string>? notes = null)
    {
        if (inputs.Count != Contours.Count)
            return "Не удалось прочитать служебные учётные записи. Обновите страницу и повторите.";
        for (var i = 0; i < Contours.Count; i++)
        {
            var contour = Contours[i];
            var input = inputs[i];
            var previous = Get(settings, contour);
            var login = input.Login?.Trim();
            var url = contour == "stage" ? settings.StagePilotUrl : settings.Find(contour)?.PilotUrl;
            if (string.IsNullOrEmpty(input.Password) &&
                (string.IsNullOrEmpty(login) || login == previous?.Login))
            {
                // Учётная запись не менялась (в том числе заданная установщиком в .env): пользователь базы
                // заводится и здесь, но сбой (сервер недоступен) только предупреждение.
                if (previous is not null && !string.IsNullOrWhiteSpace(url))
                {
                    try
                    {
                        if (await pilot.EnsureDatabaseUserAsync(url, previous, settings.ReportsLicenseType, ct) is { } done)
                            notes?.Add($"Pilot {contour}: {done}.");
                    }
                    catch (PilotGatewayException ex) { notes?.Add($"Pilot {contour}: служебная учётная запись не проверена - {ex.Message}"); }
                }
                continue;
            }
            if (string.IsNullOrEmpty(login) || string.IsNullOrEmpty(input.Password))
                return $"Pilot {contour}: при изменении учётной записи укажите логин и пароль.";
            if (string.IsNullOrWhiteSpace(url))
                return $"Pilot {contour}: сначала укажите адрес сервера.";
            var account = new PilotLogin(login, input.Password);
            try
            {
                await pilot.VerifyLoginAsync(url, account, ct);
                if (await pilot.EnsureDatabaseUserAsync(url, account, settings.ReportsLicenseType, ct) is { } done)
                    notes?.Add($"Pilot {contour}: {done}.");
            }
            catch (PilotGatewayException ex) { return $"Pilot {contour}: {ex.Message}"; }
            settings.DeployLogins[contour] = new(login, _protector.Protect(input.Password));
        }
        return null;
    }
}
