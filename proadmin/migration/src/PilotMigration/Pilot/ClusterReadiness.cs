using Ascon.Pilot.DataClasses;
using PilotMigration.Config;

namespace PilotMigration.Pilot;

internal static class ClusterReadiness
{
    internal static async Task RunAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        var config = builder.Configuration.GetSection("ClusterReadiness");
        var names = config["Databases"]?.Split(',', StringSplitOptions.TrimEntries);
        if (names is null || names.Any(name => name.Length == 0 || name.Contains('/'))
            || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            throw new InvalidOperationException("Укажите разные имена баз через запятую в ClusterReadiness__Databases.");

        var login = config["Login"];
        var password = config["Password"];
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Укажите учётную запись администратора Pilot для проверки готовности.");

        var node = new EnvConfig
        {
            Title = "Локальный узел кластера",
            PilotUrl = $"http://pilot:5545/{Uri.EscapeDataString(names[0])}",
            PilotAdminLogin = login,
            PilotAdminPassword = password
        };
        builder.Services.AddSingleton<PilotAdmin>();
        var app = builder.Build();
        app.MapGet("/health", () => Results.Text("ok"));
        app.MapGet("/ready", async (PilotAdmin pilot, CancellationToken ct) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var ready = await pilot.AreDatabasesLaunchedAsync(node, names, deadline.Token);
                return Results.StatusCode(ready ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex) when (ex is MigrationException or OperationCanceledException)
            {
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
        });
        await app.RunAsync();
    }

    internal static bool AllLaunched(IReadOnlyCollection<string> required, IEnumerable<AdminDatabaseInfo> databases)
    {
        var launched = databases.Where(db => db.State == DatabaseState.Launched).Select(db => db.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return required.Count > 0 && required.All(launched.Contains);
    }
}
