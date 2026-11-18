using ProAdmin.GitLab;
using ProAdmin.Jobs;
using ProAdmin.Modules;
using ProAdmin.Pilot;

namespace ProAdmin.Types;

/// <summary>Всё, что нужно операции модуля или автоматизации, чтобы выпустить вместе с собой связанные изменения типов.</summary>
public sealed class TypesFollowDeps
{
    public required TypesContext Ctx { get; init; }
    public required TypesStore Store { get; init; }
    public required PilotGateway Pilot { get; init; }

    public TypesFollower For(GitLabClient git, PublishJob job) => new(Pilot, Store, git, Ctx, job);
}

/// <summary>Подготовленный выпуск связанных изменений типов на прод: MR в main с пройденным pipeline.</summary>
public sealed class TypesFollowRun
{
    internal TypesPromoteWorkflow? Promote { get; init; }
    internal TypesPromoteWorkflow.Prepared? Prepared { get; init; }
    internal TypesPlan? Plan { get; set; }
    public required List<TypesChangeLink> Links { get; init; }
}

/// <summary>
/// Изменения типов едут вместе с модулем (автоматизацией): связь «коммит модуля / изменение типов в dev»
/// срабатывает, когда этот коммит модуля оказывается в ветке, которая выкладывается на стейдж или прод.
/// Стейдж: изменение переносится dev / release и пишется на стейдж до модуля.
/// Прод: перенос release / main готовится (MR, pipeline), типы пишутся на прод до модуля, MR вливается после него;
/// модуль не выпустился - типы откатываются.
/// </summary>
public sealed class TypesFollower
{
    private readonly PilotGateway _pilot;
    private readonly TypesStore _store;
    private readonly GitLabClient _git;
    private readonly TypesContext _ctx;
    private readonly PublishJob _job;

    public TypesFollower(PilotGateway pilot, TypesStore store, GitLabClient git, TypesContext ctx, PublishJob job)
    {
        _pilot = pilot;
        _store = store;
        _git = git;
        _ctx = ctx;
        _job = job;
    }

    private string Project => _ctx.ProjectPath;

    /// <summary>Перед выкладкой <paramref name="reference"/> на стейдж: связанные изменения / release / сервер стейджа.</summary>
    public async Task StageAsync(TypesTargetKind kind, string targetProject, string reference, string notSaved, CancellationToken ct)
    {
        var found = await FindAsync(kind, targetProject, reference, l => l.StageAt is null, notSaved, ct);
        if (found.Count == 0)
            return;
        await ToReleaseAsync(found, ct);
        Mark(found, l => l.StageAt ??= DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Перед выпуском <paramref name="reference"/> в прод: связанные изменения, которых ещё нет на стейдже, сначала уходят туда,
    /// затем готовится перенос release / main. null - связанных изменений нет.
    /// </summary>
    public async Task<TypesFollowRun?> PrepareProdAsync(TypesTargetKind kind, string targetProject, string reference, string? task,
        string notSaved, CancellationToken ct)
    {
        var found = await FindAsync(kind, targetProject, reference, l => l.ProdAt is null, notSaved, ct);
        if (found.Count == 0)
            return null;
        await ToReleaseAsync(found, ct);
        Mark(found, l => l.StageAt ??= DateTimeOffset.UtcNow);

        var links = found.Select(f => f.Link).ToList();
        var pending = await TypesRepository.PendingAsync(_git, Project, TypesBranches.Release, TypesBranches.Main, ct);
        var chosen = pending.Where(p => TypesRepository.PickedFrom(p.Commit.Message).Any(x => found.Any(f => f.Sha.StartsWith(x, StringComparison.Ordinal))))
            .Select(p => p.Commit).ToList();
        if (chosen.Count == 0)
        {
            _job.Add("ok", "Связанные изменения типов уже на проде.");
            return new TypesFollowRun { Links = links };
        }

        var config = await _git.FindFileAsync(Project, TypesSnapshot.ConfigFile, TypesBranches.Main, ct);
        var current = config is null ? null : TypesJson.Read<TypesConfigInfo>(config.Text, TypesSnapshot.ConfigFile).Version;
        var version = ModuleVersion.TryParse(current, out var v) ? v!.NextMinor() : new ModuleVersion(1, 1, 0);
        var notes = chosen.Select(c => c.Title ?? c.ShortId).ToList();
        _job.Add("info", $"Типы {version}: выпуск вместе с модулем - {ModuleNextSteps.Plural(chosen.Count, "изменение", "изменения", "изменений")}.");

        var promote = new TypesPromoteWorkflow(_pilot, _git, _job, _ctx, _store, TypesBranches.Main, chosen.Select(c => c.Id).ToList(), version, notes, task);
        var prepared = await promote.PrepareAsync(ct);
        return new TypesFollowRun { Promote = promote, Prepared = prepared, Links = links };
    }

    /// <summary>Записать подготовленные типы на прод (перед модулем).</summary>
    public async Task ApplyProdAsync(TypesFollowRun run, CancellationToken ct)
    {
        if (run.Promote is not null)
            run.Plan = await run.Promote.ApplyPreparedAsync(run.Prepared!, ct);
    }

    /// <summary>Модуль выпущен: MR типов / main, тег. Не влилось - типы остаются на проде, MR вливается вручную.</summary>
    public async Task CompleteProdAsync(TypesFollowRun run, CancellationToken ct)
    {
        if (run.Promote is not null)
        {
            try
            {
                await run.Promote.MergePreparedAsync(run.Prepared!, run.Plan!, revertOnFail: false, ct);
            }
            catch (WorkflowException ex)
            {
                _job.Add("warn", ex.Message, run.Prepared!.Mr.WebUrl);
                foreach (var l in run.Links)
                    _store.UpdateLink(l.Id, x => x.Error = ex.Message);
            }
        }
        foreach (var l in run.Links)
            _store.UpdateLink(l.Id, x => x.ProdAt ??= DateTimeOffset.UtcNow);
        _job.Add("ok", $"Связанные изменения типов на проде: {string.Join("; ", run.Links.Select(l => l.TypesTitle))}.");
    }

    /// <summary>Модуль не выпустился: вернуть типы прода, закрыть MR типов.</summary>
    public async Task AbortProdAsync(TypesFollowRun run)
    {
        if (run.Promote is null)
            return;
        await run.Promote.AbortAsync(run.Prepared!, run.Plan);
        if (run.Plan is { HasChanges: true })
            _job.Add("info", "Связанные изменения типов не выпущены: прод возвращён, Merge Request типов закрыт.");
    }

    /// <summary>Связи модуля, чей коммит есть в <paramref name="reference"/>, и коммиты их изменений в dev типов.</summary>
    private async Task<List<(TypesChangeLink Link, string Sha)>> FindAsync(TypesTargetKind kind, string targetProject, string reference,
        Func<TypesChangeLink, bool> open, string notSaved, CancellationToken ct)
    {
        var result = new List<(TypesChangeLink Link, string Sha)>();
        foreach (var l in _store.OpenLinksFor(kind, targetProject).Where(open))
        {
            var target = l.TargetSha;
            if (target is null && l.TargetBranch is { } branch)
            {
                target = await TypesTargets.MergedShaAsync(_git, targetProject, branch, ct);
                if (target is null)
                    continue; // ветка задачи ещё не влита в dev - в этот релиз она не входит
                _store.UpdateLink(l.Id, x => x.TargetSha = target);
            }
            if (target is null || !await TypesTargets.ContainsAsync(_git, targetProject, reference, target, ct))
                continue;

            var sha = l.TypesSha ?? (l.TypesBranch is { } tb ? await TypesTargets.MergedShaAsync(_git, Project, tb, ct) : null);
            if (sha is null)
                throw new WorkflowException($"С коммитом {ModuleDeploy.Short(target)} связано изменение типов «{l.TypesTitle}» (ветка {l.TypesBranch}), " +
                                            $"но оно ещё не влито в dev типов. Влейте его на вкладке «Типы» или удалите связь. {notSaved}");
            if (l.TypesSha is null)
                _store.UpdateLink(l.Id, x => x.TypesSha = sha);
            result.Add((l, sha));
        }
        if (result.Count > 0)
            _job.Add("info", $"С выпускаемыми коммитами связаны изменения типов: {result.Count}.",
                details: string.Join("\n", result.Select(r => $"{ModuleDeploy.Short(r.Sha)} {r.Link.TypesTitle} / {r.Link.TargetRef} {r.Link.TargetTitle}")));
        return result;
    }

    /// <summary>Изменения, которых ещё нет в release, переносятся туда и пишутся на стейдж.</summary>
    private async Task ToReleaseAsync(List<(TypesChangeLink Link, string Sha)> found, CancellationToken ct)
    {
        var state = await TypesRepoState.LoadAsync(_git, Project, ct);
        var need = found.Where(f => state.StageOf(f.Sha) == TypesChangeStage.Test).Select(f => f.Sha).ToList();
        if (need.Count == 0)
            return;
        var pending = await TypesRepository.PendingAsync(_git, Project, TypesBranches.Dev, TypesBranches.Release, ct);
        var ids = pending.Where(p => need.Any(n => n.StartsWith(p.Commit.Id, StringComparison.Ordinal) || p.Commit.Id.StartsWith(n, StringComparison.Ordinal)))
            .Select(p => p.Commit.Id).ToList();
        if (ids.Count == 0)
            return;
        _job.Add("info", $"Типы / стейдж: {ModuleNextSteps.Plural(ids.Count, "изменение", "изменения", "изменений")} из dev переносятся в release.");
        await new TypesPromoteWorkflow(_pilot, _git, _job, _ctx, _store, TypesBranches.Release, ids, null, Array.Empty<string>(), null).RunAsync(ct);
    }

    private void Mark(List<(TypesChangeLink Link, string Sha)> found, Action<TypesChangeLink> change)
    {
        foreach (var (l, _) in found)
            _store.UpdateLink(l.Id, x =>
            {
                change(x);
                x.Error = null;
            });
    }
}
