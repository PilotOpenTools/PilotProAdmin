using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Types;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Types;

/// <summary>Коммит dev или ветка задачи модуля/автоматизации, с которым связывается изменение типов.</summary>
public sealed record TypesLinkCandidate(string? Sha, string? Branch, string Title, string? Author, DateTimeOffset? Date, string? WebUrl, bool InRelease)
{
    public string Value => Sha ?? "branch:" + Branch;
}

/// <summary>
/// Связать изменение типов с коммитом модуля или автоматизации: когда коммит уйдёт на стейдж (в release/…) и на прод,
/// изменение типов уйдёт туда же в той же операции, перед модулем.
/// </summary>
public class LinkModel : AppPageModel
{
    private readonly TypesStore _store;

    public LinkModel(TypesStore store) => _store = store;

    /// <summary>Коммит dev типов (sha) или ветка types/….</summary>
    [BindProperty(SupportsGet = true)] public string? Change { get; set; }
    [BindProperty(SupportsGet = true)] public string? Kind { get; set; }
    [BindProperty(SupportsGet = true)] public string? Project { get; set; }
    [BindProperty] public string? Target { get; set; }

    public TypesTargetKind? TargetKind => TypesTargets.Parse(Kind);
    public string? ChangeTitle { get; private set; }
    public string? ChangeBranch { get; private set; }
    public GitLabCommit? ChangeCommit { get; private set; }
    public List<TypesTargetKind> Kinds { get; } = new();
    public List<GitLabProject> Projects { get; private set; } = new();
    public GitLabProject? Selected { get; private set; }
    public List<TypesLinkCandidate> Candidates { get; } = new();
    public string? ActiveRelease { get; private set; }
    public List<TypesChangeLink> Existing { get; private set; } = new();
    public List<string> Errors { get; } = new();

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        await LoadAsync(ct);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        await LoadAsync(ct);
        if (Errors.Count > 0)
            return Page();
        var access = await GetGroupAccessAsync(TypesRepository.GroupOf(AppSettings.TypesProjectPath), fresh: false, ct);
        if (GitLabGroupAccess.CannotChange(access))
        {
            Errors.Add(ChangeDeniedText("типы", access, access?.GroupPath));
            return Page();
        }
        if (Selected is null || TargetKind is not { } kind || Candidates.FirstOrDefault(c => c.Value == Target) is not { } target)
        {
            Errors.Add("Выберите коммит или ветку задачи.");
            return Page();
        }
        if (Existing.Any(l => l.TargetKind == kind && string.Equals(l.TargetProject, Selected.PathWithNamespace, StringComparison.OrdinalIgnoreCase) &&
                              (l.TargetSha is not null && l.TargetSha == target.Sha || l.TargetBranch is not null && l.TargetBranch == target.Branch)))
        {
            Errors.Add("Такая связь уже есть.");
            return Page();
        }

        _store.AddLink(new TypesChangeLink
        {
            TypesSha = ChangeCommit?.Id,
            TypesBranch = ChangeBranch,
            TypesTitle = ChangeTitle ?? "",
            TargetKind = kind,
            TargetProject = Selected.PathWithNamespace,
            TargetName = Selected.Name,
            TargetSha = target.Sha,
            TargetBranch = target.Branch,
            TargetTitle = target.Title,
            CreatedBy = CurrentSession.Actor,
        });
        TempData["Flash"] = $"«{ChangeTitle}» поедет на стейдж и прод вместе с {kind.Title().ToLowerInvariant()} «{Selected.Name}» " +
                            $"({(target.Sha is not null ? ModuleDeploy.Short(target.Sha) : target.Branch)} {target.Title}).";
        return Redirect("/Types#changes");
    }

    private async Task LoadAsync(CancellationToken ct)
    {
        var typesProject = AppSettings.TypesProjectPath;
        if (typesProject is null || string.IsNullOrWhiteSpace(Change))
        {
            Errors.Add("Не указано изменение типов.");
            return;
        }
        foreach (var k in new[] { TypesTargetKind.Client, TypesTargetKind.Server, TypesTargetKind.Automation })
            if (k.GroupPath(AppSettings) is not null)
                Kinds.Add(k);
        Kind ??= Kinds.Count > 0 ? Kinds[0].Key() : null;

        var git = CreateGitLabClient();
        try
        {
            if (TypesBranches.IsTask(Change))
            {
                var branch = await git.FindBranchAsync(typesProject, Change, ct);
                if (branch is null)
                {
                    Errors.Add($"Ветки {Change} нет в репозитории типов.");
                    return;
                }
                ChangeBranch = branch.Name;
                ChangeTitle = branch.Commit?.Title ?? branch.Name;
            }
            else
            {
                ChangeCommit = await git.FindCommitAsync(typesProject, Change, ct);
                if (ChangeCommit is null)
                {
                    Errors.Add($"Коммита {Change} нет в репозитории типов.");
                    return;
                }
                ChangeTitle = ChangeCommit.Title;
                var state = await TypesRepoState.LoadAsync(git, typesProject, ct);
                if (state.StageOf(ChangeCommit.Id) == TypesChangeStage.Prod)
                    Errors.Add("Это изменение уже на проде - связывать незачем.");
            }
            Existing = _store.ChangeLinks().Where(l => l.IsChange(ChangeCommit?.Id, ChangeBranch)).ToList();

            if (TargetKind is not { } kind || kind.GroupPath(AppSettings) is not { } group)
                return;
            Projects = (await git.ListGroupProjectsAsync(group, null, ct) ?? new()).OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            Selected = Projects.FirstOrDefault(p => string.Equals(p.PathWithNamespace, Project, StringComparison.OrdinalIgnoreCase));
            if (Selected is null)
                return;
            await LoadCandidatesAsync(git, kind, Selected.PathWithNamespace, ct);
        }
        catch (GitLabApiException ex)
        {
            Errors.Add(ex.Message);
        }
    }

    /// <summary>Задачи dev, которых нет в main (свежие сверху), и ветки задач модуля - их MR потом найдётся в dev.</summary>
    private async Task LoadCandidatesAsync(GitLabClient git, TypesTargetKind kind, string project, CancellationToken ct)
    {
        var branches = await git.ListBranchesAsync(project, null, ct) ?? new();
        ActiveRelease = branches.Where(b => ModuleBranches.IsRelease(b.Name)).Select(b => b.Name).OrderByDescending(n => n, StringComparer.Ordinal).FirstOrDefault();
        var inRelease = new HashSet<string>(StringComparer.Ordinal);
        if (ActiveRelease is not null)
            foreach (var c in await git.CompareCommitsAsync(project, ModuleBranches.Main, ActiveRelease, ct))
            {
                inRelease.Add(c.Id);
                foreach (var p in TypesRepository.PickedFrom(c.Message))
                    inRelease.Add(p);
            }

        if (branches.Any(b => b.Name == ModuleBranches.Dev) && branches.Any(b => b.Name == ModuleBranches.Main))
        {
            var commits = (await git.CompareCommitsAsync(project, ModuleBranches.Main, ModuleBranches.Dev, ct))
                .Where(c => c.ParentIds is not { Count: > 1 } && !ModuleWorkflowBase.IsServiceCommit(c.Title))
                .Reverse();
            foreach (var c in commits)
                Candidates.Add(new TypesLinkCandidate(c.Id, null, c.Title ?? c.ShortId, c.AuthorName, c.CommittedDate, c.WebUrl,
                    inRelease.Any(r => c.Id.StartsWith(r, StringComparison.Ordinal))));
        }
        // У автоматизаций ветка задачи вливается в dev новым коммитом, а не её MR, - связываются только коммиты dev.
        if (kind != TypesTargetKind.Automation)
            foreach (var b in branches.Where(b => ModuleBranches.IsTaskBranch(b.Name)).OrderByDescending(b => b.Commit?.CommittedDate))
                Candidates.Add(new TypesLinkCandidate(null, b.Name, b.Commit?.Title ?? b.Name, b.Commit?.AuthorName, b.Commit?.CommittedDate, null, false));
    }
}
