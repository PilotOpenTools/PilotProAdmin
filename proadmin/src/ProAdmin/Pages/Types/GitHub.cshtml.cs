using System.IO.Compression;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProAdmin.Infrastructure;
using ProAdmin.Pilot;
using ProAdmin.Types;

namespace ProAdmin.Pages.Types;

public sealed class GitHubModel(IOptions<AppOptions> options, GitHubTypesClient github, TypesJobRunner jobs,
    TypesStore store, PilotGateway pilot) : AppPageModel
{
    [BindProperty] public long? PullRequest { get; set; }
    [BindProperty] public string? Token { get; set; }
    public string? Repository => GitHubTypesClient.RepositoryPath(options.Value.GitHubTypes.Repository);
    public string? Error { get; private set; }
    public TypesDeployment? Deployment => store.Deployment(TypesEnv.Test);
    public bool CanPublish => Repository is not null && TypesEnv.Find(AppSettings, TypesEnv.Test) is { HasServer: true } env
        && CurrentSession.LoginAccess.Any(a => a.Ok && a.Key == env.Key && a.PilotUrl == env.PilotUrl);

    public IActionResult OnGet()
    {
        ViewData["Section"] = "types";
        return options.Value.GitHubTypes.IsConfigured ? Page() : NotFound();
    }

    public IActionResult OnPost()
    {
        ViewData["Section"] = "types";
        if (!options.Value.GitHubTypes.IsConfigured)
            return NotFound();
        if (!CanPublish)
            return StatusCode(StatusCodes.Status403Forbidden);
        var token = Token?.Trim();
        Token = null;
        ModelState.Remove(nameof(Token));
        if (!ModelState.IsValid || PullRequest is not > 0 || token is null || token.Length is < 16 or > 512
            || !token.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            Error = "Укажите номер Pull Request и токен GitHub.";
            return Page();
        }

        try
        {
            var job = jobs.StartGitHubSync(AppSettings, github, Repository!, PullRequest.Value, token,
                Vault.GetPilotLogin(CurrentSession, TypesEnv.Test), CurrentSession.PilotLogin);
            return RedirectToPage("/Jobs/Details", new { id = job.Id });
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    public async Task<IActionResult> OnGetExportAsync(CancellationToken ct)
    {
        if (!options.Value.GitHubTypes.IsConfigured)
            return NotFound();
        if (!CanPublish)
            return StatusCode(StatusCodes.Status403Forbidden);
        ViewData["Section"] = "types";
        try
        {
            var url = TypesEnv.Find(AppSettings, TypesEnv.Test)!.PilotUrl;
            var metadata = await pilot.GetMetadataAsync(url, Vault.GetPilotLogin(CurrentSession, TypesEnv.Test), ct);
            var files = TypesExport.FromMetadata(metadata, "1.0.0").Render();
            files.Add(TypesCiTemplate.ValidatorFile, TypesCiTemplate.Validator);
            files.Add(".github/workflows/" + GitHubTypesClient.WorkflowFile, GitHubWorkflow);
            using var bytes = new MemoryStream();
            using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, leaveOpen: true))
                foreach (var (path, text) in files)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(path).Open(), new UTF8Encoding(false));
                    await writer.WriteAsync(text.AsMemory(), ct);
                }
            return File(bytes.ToArray(), "application/zip", "pilot-types-github.zip");
        }
        catch (PilotGatewayException ex)
        {
            Error = ex.Message;
            return Page();
        }
    }

    private const string GitHubWorkflow = """
        name: Проверка типов Pilot
        on:
          pull_request:
            branches: [dev]
          push:
            branches: [dev]
        permissions:
          contents: read
        jobs:
          validate:
            runs-on: ubuntu-latest
            steps:
              - uses: actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803 # v6
              - run: python3 ci/validate.py

        """;
}
