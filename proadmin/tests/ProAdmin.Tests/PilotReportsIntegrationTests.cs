using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Reports;
using Microsoft.Extensions.Logging.Abstractions;

namespace ProAdmin.Tests;

/// <summary>
/// Чтение отчётов с настоящего сервера Pilot (только чтение). Запускается, если заданы переменные:
/// PILOT_IT_URL (http://host:5545/база), PILOT_IT_LOGIN, PILOT_IT_PASSWORD, PILOT_IT_LICENSE (по умолчанию 100).
/// </summary>
public class PilotReportsIntegrationTests
{
    [RequiresEnvFact("PILOT_IT_URL", "PILOT_IT_LOGIN", "PILOT_IT_PASSWORD")]
    public async Task ListsAndDownloadsReportsFromRealServer()
    {
        var url = Environment.GetEnvironmentVariable("PILOT_IT_URL")!;

        var login = new PilotLogin(Environment.GetEnvironmentVariable("PILOT_IT_LOGIN")!, Environment.GetEnvironmentVariable("PILOT_IT_PASSWORD")!);
        var license = int.TryParse(Environment.GetEnvironmentVariable("PILOT_IT_LICENSE"), out var l) ? l : 100;
        var gateway = new PilotReportGateway(new NetworkAddressMapper(null), NullLogger<PilotReportGateway>.Instance);

        var list = await gateway.ListAsync(url, login, license, withContent: true, CancellationToken.None);
        Assert.NotEmpty(list.Reports);
        Assert.NotEmpty(list.Folders);
        Assert.All(list.Reports, r => Assert.False(string.IsNullOrWhiteSpace(r.Name)));
        Assert.All(list.Reports, r => Assert.Matches("^[0-9a-f]{40}$", r.Sha!));

        var first = list.Reports[0];
        var bytes = await gateway.DownloadAsync(url, login, license, first.Id, CancellationToken.None);
        Assert.NotNull(bytes);
        Assert.Equal(first.Sha, ReportFiles.BlobSha(bytes!));

        var output = Path.Combine(Path.GetTempPath(), "pilot-it-reports.txt");
        File.WriteAllLines(output, new[] { $"reports={list.Reports.Count} folders={list.Folders.Count}" }
            .Concat(list.Folders.Take(15).Select(f => "folder: " + f.Path))
            .Concat(list.Reports.Take(15).Select(r => $"report: [{r.Folder}] {r.Name} ({r.FileName}, {r.Size} b)")));
    }
}
