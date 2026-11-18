namespace ProAdmin.Modules;

/// <summary>Этап пути задачи на странице модуля: ветка / тест / стейдж / прод.</summary>
public enum FlowStage
{
    Task,
    Test,
    Stage,
    Prod,
    /// <summary>Всё выпущено: тест, стейдж и прод на последнем релизе.</summary>
    Done,
}

public enum NextStepKind
{
    /// <summary>Нужно действие пользователя.</summary>
    Action,
    /// <summary>Работает CI или ждём другого человека (ревью).</summary>
    Wait,
    Done,
    Warn,
}

/// <summary>Что сейчас с модулем и какой шаг следующий. Href - якорь на странице (#release) или внешняя ссылка.</summary>
public sealed record ModuleNextStep(FlowStage Stage, NextStepKind Kind, string Title, string? Text = null, string? ActionLabel = null, string? Href = null)
{
    public bool External => Href is not null && !Href.StartsWith('#');

    /// <summary>Вторая кнопка рядом с основной (например, «Отменить откат»).</summary>
    public string? AltLabel { get; init; }
    public string? AltHref { get; init; }
}

/// <summary>Этап на карточке «где сейчас»: название, ветка, подпись (версия на сервере, сколько задач).</summary>
public sealed record FlowStep(string Title, string Branch, string Caption);

/// <summary>
/// Карточка «где сейчас и что дальше» (Pages/Shared/_FlowCard): этапы, следующий шаг, активные ветки.
/// Current - номер текущего этапа в Steps (Steps.Count - всё пройдено).
/// </summary>
public sealed record FlowCard(IReadOnlyList<FlowStep> Steps, int Current, ModuleNextStep Next, IReadOnlyList<ProAdmin.GitLab.ActiveBranch> Branches, string RepoUrl,
    IReadOnlyList<FlowLane>? Lanes = null)
{
    /// <summary>Номер этапа в схеме без стейджа (Правка / Тест / Прод).</summary>
    public static int WithoutStage(FlowStage stage) => stage switch
    {
        FlowStage.Task => 0,
        FlowStage.Test => 1,
        FlowStage.Stage or FlowStage.Prod => 2,
        _ => 3,
    };
}

/// <summary>
/// Задача (ветка или коммит) на карточке «где сейчас»: своя линия от первого этапа до этапа, где она сейчас.
/// Column - номер этапа в <see cref="FlowCard.Steps"/>.
/// </summary>
public sealed record FlowLane(string Title, string Ref, string? Url, int Column, string? Note = null);

/// <summary>Линии задач: ветки в работе, коммиты dev, которых нет в main (в релизе - на стейдже), и багфиксы релиза.</summary>
public static class FlowLanes
{
    public static List<FlowLane> Build(string repoUrl, IEnumerable<(ProAdmin.GitLab.GitLabBranch Branch, string? Note)> branches,
        IEnumerable<ProAdmin.GitLab.GitLabCommit> devOnly, IReadOnlyCollection<ProAdmin.GitLab.GitLabCommit> release, int testColumn, int stageColumn)
    {
        var lanes = branches.Select(b => new FlowLane(b.Branch.Commit?.Title ?? b.Branch.Name, b.Branch.Name,
            $"{repoUrl}/-/commits/{Uri.EscapeDataString(b.Branch.Name)}", 0, b.Note)).ToList();
        var matched = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in devOnly)
        {
            var inRelease = release.FirstOrDefault(r => r.Id == c.Id || r.Title == c.Title ||
                                                        ProAdmin.Types.TypesRepository.PickedFrom(r.Message).Any(p => c.Id.StartsWith(p, StringComparison.Ordinal)));
            if (inRelease is not null)
                matched.Add(inRelease.Id);
            lanes.Add(new FlowLane(c.Title ?? c.ShortId, c.ShortId, c.WebUrl, inRelease is null ? testColumn : stageColumn));
        }
        lanes.AddRange(release.Where(r => !matched.Contains(r.Id))
            .Select(r => new FlowLane(r.Title ?? r.ShortId, r.ShortId, r.WebUrl, stageColumn, "только в релизе")));
        return lanes;
    }

    /// <summary>«2 из 3 задач»: сколько линий дошло до этапа <paramref name="column"/> из тех, что прошли тест.</summary>
    public static string Reached(IReadOnlyCollection<FlowLane> lanes, int column, int from, string one, string few, string many)
    {
        var total = lanes.Count(l => l.Column >= from);
        return $"{lanes.Count(l => l.Column >= column)} из {ModuleNextSteps.Plural(total, one, few, many)}";
    }
}

/// <summary>MR задачи в dev - для выбора следующего шага.</summary>
public sealed record NextStepMr(long Iid, string? Title, bool Draft, bool Reviewed, string? WebUrl, string? PipelineStatus);

/// <summary>Состояние модуля, из которого выводится следующий шаг (собирается страницей модуля).</summary>
public sealed record NextStepInput
{
    public bool Ready { get; init; } = true;
    public bool NeedReview { get; init; }
    public bool HasStage { get; init; } = true;
    public string? Release { get; init; }
    public int ReleaseMrs { get; init; }
    public IReadOnlyList<NextStepMr> DevMrs { get; init; } = Array.Empty<NextStepMr>();
    public ModuleSync? Test { get; init; }
    public ModuleSync? Stage { get; init; }
    public ModuleSync? Prod { get; init; }
    /// <summary>Pipeline, который сейчас выкладывает голову dev / release (ссылка).</summary>
    public string? DeployingTest { get; init; }
    public string? DeployingStage { get; init; }
    public int Unreleased { get; init; }
    public ModuleVersion? LastProd { get; init; }
    /// <summary>hotfix/… с коммитами сверх main.</summary>
    public string? Hotfix { get; init; }
    /// <summary>Ветка задачи с коммитами, но без MR, и ссылка «создать MR».</summary>
    public string? BranchWithoutMr { get; init; }
    public string? NewMrUrl { get; init; }
    /// <summary>Только что созданная ветка задачи: коммитов и MR ещё нет.</summary>
    public string? EmptyBranch { get; init; }
}

public static class ModuleNextSteps
{
    /// <summary>Сервер на голове своей ветки (или выложен вручную той же версии).</summary>
    public static bool Ok(ModuleSync? s) => s is ModuleSync.Synced or ModuleSync.SameVersion or ModuleSync.Released or ModuleSync.Disabled or ModuleSync.Unknown or null;

    public static ModuleNextStep For(NextStepInput m)
    {
        if (!m.Ready)
            return new(FlowStage.Task, NextStepKind.Warn, "Репозиторий не готов к CI/CD",
                "Нет ветки dev или .gitlab-ci.yml - подключите их кнопкой в предупреждении выше.");

        if (m.Hotfix is { } hotfix)
            return new(FlowStage.Prod, NextStepKind.Action, $"Hotfix {hotfix} ждёт выпуска в прод",
                "Проверьте исправление (можно на стейдже) и нажмите «Hotfix в прод»: поднимется патч-версия, исправление вольётся в dev.",
                "К hotfix ", "#hotfix");

        if (m.Release is { } release)
        {
            var v = ModuleBranches.ReleaseVersion(release)?.ToString() ?? release;
            if (m.ReleaseMrs > 0)
                return new(FlowStage.Stage, NextStepKind.Action, $"В релиз {v} ждут влития {Plural(m.ReleaseMrs, "MR", "MR", "MR")}",
                    "Code Review в GitLab, затем «Влить / стейдж». Пока они не влиты, выпуск в прод заблокирован.",
                    "К MR релиза ", "#release");
            if (m.DeployingStage is { } stagePipeline)
                return new(FlowStage.Stage, NextStepKind.Wait, $"Релиз {v} выкладывается на стейдж",
                    "CI собирает и выкладывает голову релиза. Обновите страницу, когда pipeline закончится.",
                    "Pipeline", stagePipeline);
            if (!Ok(m.Stage))
                return new(FlowStage.Stage, NextStepKind.Action, $"Выложите релиз {v} на стейдж",
                    "На стейдже не голова релиза.", "К релизу ", "#release");
            return new(FlowStage.Stage, NextStepKind.Action, $"Релиз {v} на стейдже - проверьте и выпустите в прод",
                "После проверки на стейдже: «Выпустить в прод» выложит тот же артефакт на прод, вольёт релиз в main, поставит тег и Release.",
                "Выпустить в прод ", "#release-prod");
        }

        var ready = m.DevMrs.Where(mr => !mr.Draft).ToList();
        if (ready.FirstOrDefault(mr => !ProAdmin.GitLab.GitLabPipeline.Blocks(mr.PipelineStatus) && (mr.Reviewed || !m.NeedReview)) is { } toMerge)
            return new(FlowStage.Task, NextStepKind.Action, $"Влейте !{toMerge.Iid} в dev",
                $"«{toMerge.Title}»{PipelineNote(toMerge.PipelineStatus)}. «Влить / тест» сделает squash merge и выложит dev на тест.",
                "К MR задач ", "#tasks");
        if (ready.FirstOrDefault(mr => ProAdmin.GitLab.GitLabPipeline.Blocks(mr.PipelineStatus)) is { } failed)
            return new(FlowStage.Task, NextStepKind.Action, $"У !{failed.Iid} не прошёл pipeline",
                $"«{failed.Title}». Посмотрите ошибку в pipeline: при временном сбое запустите его заново, иначе исправьте ветку и запушьте её.",
                "К MR задач ", "#tasks");
        if (ready.FirstOrDefault() is { } waiting)
            return new(FlowStage.Task, NextStepKind.Wait, $"!{waiting.Iid} ждёт Approve ревьюера",
                $"«{waiting.Title}»{PipelineNote(waiting.PipelineStatus)}. Ревьюер (не автор) нажимает Approve в GitLab, затем здесь - «Влить / тест».",
                "MR в GitLab", waiting.WebUrl);
        if (m.DevMrs.FirstOrDefault() is { } draft)
            return new(FlowStage.Task, NextStepKind.Wait, $"!{draft.Iid} - черновик (Draft)",
                "Снимите Draft в GitLab, когда задача готова к ревью.", "MR в GitLab", draft.WebUrl);

        if (m.DeployingTest is { } testPipeline)
            return new(FlowStage.Test, NextStepKind.Wait, "dev выкладывается на тест",
                "CI собирает и выкладывает голову dev. Обновите страницу, когда pipeline закончится.", "Pipeline", testPipeline);
        if (!Ok(m.Test))
            return new(FlowStage.Test, NextStepKind.Action, "Выложите dev на тест",
                "На тестовом сервере не голова dev - нажмите «Выложить dev» в карточке теста.", "К серверам ", "#envs");

        if (m.Unreleased > 0)
        {
            var since = m.LastProd is { } lp ? $" сверх v{lp}" : "";
            return new(FlowStage.Test, NextStepKind.Action, $"На тесте {Plural(m.Unreleased, "задача", "задачи", "задач")}{since} - соберите релиз",
                m.HasStage ? "Проверьте задачи на тесте и нажмите «Релиз / стейдж»: создастся release/X.Y.Z и выложится на стейдж."
                           : "Укажите сервер стейджа в настройках, чтобы собрать релиз.",
                "Релиз / стейдж ", "#release");
        }

        if (m.BranchWithoutMr is { } branch)
            return new(FlowStage.Task, NextStepKind.Action, $"Ветка {branch}: создайте MR в dev",
                "В ветке есть коммиты, но Merge Request нет - без него задача не попадёт на тест.", "Создать MR", m.NewMrUrl);

        if (m.EmptyBranch is { } empty)
            return new(FlowStage.Task, NextStepKind.Wait, $"Ветка {empty} создана - коммитов пока нет",
                $"git fetch && git switch {empty}, закоммитьте и запушьте изменения - затем здесь появится «Создать MR».");

        if (!Ok(m.Prod))
            return new(FlowStage.Prod, NextStepKind.Warn, "На проде не голова main",
                "Прод откатили или выложили вручную. Отменить откат: «Релизы на проде» / выберите последний релиз. Либо выпустите исправление релизом или hotfix.", "К релизам ", "#releases");

        return new(FlowStage.Done, NextStepKind.Done,
            m.LastProd is { } last ? $"Всё выпущено: v{last} на проде" : "Новых задач нет",
            "Тест, стейдж и прод совпадают. Новая работа начинается с ветки задачи от dev.",
            "Создать ветку задачи ", "#tasks");
    }

    private static string PipelineNote(string? status) => status switch
    {
        "running" or "pending" or "created" => " · pipeline ещё идёт",
        _ => "",
    };

    public static string Plural(int n, string one, string few, string many)
    {
        var word = (n % 100) is >= 11 and <= 14 ? many : (n % 10) switch { 1 => one, >= 2 and <= 4 => few, _ => many };
        return $"{n} {word}";
    }
}
