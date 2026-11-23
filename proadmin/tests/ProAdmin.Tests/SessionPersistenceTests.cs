using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ProAdmin.GitLab;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Sessions;
using ProAdmin.Settings;

namespace ProAdmin.Tests;

/// <summary>Сессии GitLab переживают перезапуск админки; сессии со входом в Pilot (с паролем) на диск не попадают.</summary>
public class SessionPersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pwa-sessions-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SessionVault NewVault()
    {
        var options = Options.Create(new AppOptions { DataDirectory = _dir });
        var protection = DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_dir, "keys")));
        var settings = new SettingsStore(options, NullLogger<SettingsStore>.Instance);
        return new SessionVault(protection, settings, new DeployAccounts(protection, options), options);
    }

    [Fact]
    public void GitLab_sessions_survive_a_restart_and_logout_removes_them()
    {
        var vault = NewVault();
        var token = new GitLabToken { AccessToken = "glpat-secret", BaseUrl = "http://gitlab:8080", RefreshToken = "r", IsOAuth = true };
        var session = vault.Create(token, new GitLabUser { Id = 7, Username = "root", IsAdmin = true });
        var pilot = vault.Create("http://pilot:5545", new PilotLogin("admin", "pilot-secret"));
        token.AccessToken = "glpat-refreshed"; // токен обновился на месте
        vault.Save();

        var file = File.ReadAllText(Path.Combine(_dir, "sessions.dat"));
        Assert.DoesNotContain("glpat", file); // на диске - только зашифрованное
        Assert.DoesNotContain("pilot-secret", file);

        var restarted = NewVault();
        var restored = restarted.Get(session.Id);
        Assert.NotNull(restored);
        Assert.Equal("glpat-refreshed", restored.GitLabToken!.AccessToken);
        Assert.Equal("r", restored.GitLabToken.RefreshToken);
        Assert.True(restored.GitLabToken.IsOAuth);
        Assert.Equal("root", restored.GitLabUser!.Username);
        Assert.True(restored.GitLabUser.IsAdmin);
        Assert.True(restored.UsesServiceAccounts);
        Assert.Null(restarted.Get(pilot.Id)); // вход в Pilot - только в памяти

        restarted.Remove(session.Id);
        Assert.Null(NewVault().Get(session.Id));
    }

    [Fact]
    public void Damaged_session_file_means_signing_in_again()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "sessions.dat"), "not protected");
        Assert.Null(NewVault().Get("any"));
    }
}
