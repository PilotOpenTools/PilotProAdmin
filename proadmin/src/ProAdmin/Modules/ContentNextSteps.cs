using ProAdmin.Automations;
using ProAdmin.GitLab;

namespace ProAdmin.Modules;

/// <summary>Откат прода: на какой релиз откатили и какой релиз был на проде до отката (null - неизвестен).</summary>
public sealed record RollbackInfo(string Target, string? UndoTag);

/// <summary>Состояние отчёта или автоматизации, из которого выводится следующий шаг (собирается их страницей).</summary>
public sealed record ContentFlowInput
{
    /// <summary>«отчёт», «автоматизация» - в текстах шагов.</summary>
    public string Noun { get; init; } = "отчёт";
    public bool HasRepo { get; init; } = true;
    public bool HasDev { get; init; } = true;
    public bool HasMain { get; init; } = true;
    /// <summary>В dev есть изменения, которых нет в main.</summary>
    public bool DevAhead { get; init; }
    /// <summary>В main есть изменения, которых нет в dev (прод меняли мимо теста).</summary>
    public bool MainAhead { get; init; }
    /// <summary>Релиз, на который откатили main (по заголовку головного коммита); null - это не откат.</summary>
    public string? RollbackTo { get; init; }
    /// <summary>Ссылка «Отменить откат» (откат на релиз, что был перед ним).</summary>
    public string? UndoRollbackUrl { get; init; }
    public SyncState? Test { get; init; }
    public SyncState? Stage { get; init; }
    public SyncState? Prod { get; init; }
    /// <summary>Задан сервер стейджа: на прод - только через релиз.</summary>
    public bool HasStage { get; init; }
    /// <summary>Активный релиз (release/&lt;версия&gt;, ещё не на проде).</summary>
    public string? Release { get; init; }
    /// <summary>Почему релиз собрать нельзя (у автоматизации - версия в dev не выше main); null - можно.</summary>
    public string? ReleaseBlocked { get; init; }
    /// <summary>hotfix/… с коммитами сверх main и ссылка «Hotfix в прод».</summary>
    public string? Hotfix { get; init; }
    public string? HotfixUrl { get; init; }
    /// <summary>Ветка задачи с коммитами, ждёт «Влить / тест».</summary>
    public string? ReadyTask { get; init; }
    /// <summary>Как привести тест к dev, если они расходятся (у отчёта - отправить из Pilot, у автоматизации - сохранить в редакторе).</summary>
    public string TestDiffersHint { get; init; } = "";
    public string? LastRelease { get; init; }
    public string? CreateUrl { get; init; }
    public string? PromoteUrl { get; init; }
    public string? BackportUrl { get; init; }
    public string? CompareUrl { get; init; }
}

/// <summary>Следующий шаг отчёта/автоматизации: правка / dev (тест) / release (стейдж, если задан) / main (прод).</summary>
public static class ContentNextSteps
{
    /// <summary>Сервер совпадает с веткой (или сравнить не удалось - тогда не шумим).</summary>
    public static bool Ok(SyncState? s) => s is SyncState.Synced or SyncState.Unknown or null;

    /// <summary>«откат на v2026.11.06» из заголовка коммита main / «v2026.11.06»; null, если это не откат.</summary>
    public static string? RollbackTarget(string? commitTitle) =>
        commitTitle is null ? null : System.Text.RegularExpressions.Regex.Match(commitTitle, @"откат на (v?\d[\w.\-]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase) is { Success: true } r ? r.Groups[1].Value : null;

    /// <summary>
    /// Релиз, на который откатили прод: тег на голове main называется «откат на vX» (так его создаёт «Откатить прод»).
    /// Заголовок коммита main у отката - «Merge branch 'webadmin/…-rollback/…'», поэтому смотрим на тег. null - голова main не откат.
    /// </summary>
    public static async Task<RollbackInfo?> RollbackAtMainAsync(GitLabClient git, string project, CancellationToken ct)
    {
        var head = (await git.FindBranchAsync(project, "main", ct))?.Commit?.Id;
        if (head is null)
            return null;
        var tags = await git.ListVersionTagsAsync(project, ct) ?? new();
        var tag = tags.FirstOrDefault(t => t.Commit?.Id == head);
        if (RollbackTarget(tag?.Message) is not { } target)
            return null;
        // «Отменить откат» - вернуть то, что было на проде перед ним: релиз, выпущенный раньше отката.
        var undo = tags.Where(t => t.Name != tag!.Name && t.Commit?.CommittedDate < tag.Commit?.CommittedDate)
            .OrderByDescending(t => t.Commit?.CommittedDate).FirstOrDefault()?.Name;
        return new RollbackInfo(target, undo);
    }

    public static ModuleNextStep For(ContentFlowInput m)
    {
        if (!m.HasRepo)
            return new(FlowStage.Task, NextStepKind.Warn, "Нет репозитория в GitLab",
                $"Без репозитория {m.Noun} не проходит тест и прод через GitLab - создайте его.", "Создать репозиторий", m.CreateUrl);
        if (!m.HasDev || !m.HasMain)
            return new(FlowStage.Task, NextStepKind.Warn, $"В репозитории нет ветки {(!m.HasDev ? "dev" : "main")}",
                "dev - тестовый сервер, main - продовый. Ветка создастся первой отправкой на этот контур.");

        if (m.Hotfix is { } hotfix)
            return new(FlowStage.Prod, NextStepKind.Action, $"Hotfix {hotfix} ждёт выпуска в прод",
                "«Hotfix в прод» перенесёт ветку в main и на продовый сервер (MR, pipeline), затем верните исправление в dev.", "Hotfix в прод /", m.HotfixUrl);

        if (m.MainAhead && m.RollbackTo is { } rolled)
            return new(FlowStage.Prod, NextStepKind.Action, $"Прод откачен на {rolled} - в dev всё ещё версия, от которой откатились",
                $"Выберите: «/ В тест» принимает откат (dev и тест станут как прод) либо исправьте {m.Noun} в dev и перенесите на прод новой версией. " +
                "Пока dev не изменён, «На прод» вернёт на прод ту самую версию, от которой откатывались.",
                "/ В тест (принять откат)", m.BackportUrl)
            { AltLabel = m.UndoRollbackUrl is null ? null : "Отменить откат", AltHref = m.UndoRollbackUrl };

        if (m.MainAhead)
            return new(FlowStage.Test, NextStepKind.Action, "Прод меняли мимо теста - верните изменения в dev",
                "В main есть правка, которой нет в dev: перенесите её в dev и на тест, иначе следующий перенос на прод её затрёт.",
                "/ В тест", m.BackportUrl);

        if (!Ok(m.Test))
            return new(FlowStage.Test, NextStepKind.Warn, m.Test == SyncState.OnlyGit ? "На тестовом сервере его нет" : "Тестовый сервер расходится с dev",
                m.TestDiffersHint, "Сравнить", m.CompareUrl);

        if (m.Release is { } release)
        {
            var v = release.StartsWith("release/", StringComparison.Ordinal) ? release["release/".Length..] : release;
            if (!Ok(m.Stage))
                return new(FlowStage.Stage, NextStepKind.Action, $"Выложите релиз {v} на стейдж",
                    "На сервере стейджа не то, что в ветке релиза.", "К релизу ", "#release");
            return new(FlowStage.Stage, NextStepKind.Action, $"Релиз {v} на стейдже - проверьте и выпустите в прод",
                "«На прод» перенесёт ветку релиза в main через Merge Request и pipeline и запишет на продовый сервер.", "На прод /", m.PromoteUrl);
        }

        if (m.ReadyTask is { } task)
            return new(FlowStage.Task, NextStepKind.Action, $"Ветка {task}: проверьте и влейте в dev",
                "«Влить / тест» создаст MR в dev, дождётся pipeline, запишет автоматизацию на тестовый сервер и удалит ветку.", "К веткам задач ", "#tasks");

        if (m.DevAhead && m.HasStage)
            return m.ReleaseBlocked is { } blocked
                ? new(FlowStage.Test, NextStepKind.Warn, "Изменения на тесте, но релиз собрать нельзя", blocked, "К релизу ", "#release")
                : new(FlowStage.Test, NextStepKind.Action, "Изменения на тесте - проверьте и соберите релиз",
                    "«Релиз / стейдж» создаст ветку release от dev и запишет её на сервер стейджа.", "Релиз / стейдж ", "#release");

        if (m.DevAhead)
            return new(FlowStage.Test, NextStepKind.Action, "Изменения на тесте - проверьте и перенесите на прод",
                "Стейдж не задан: «На прод» перенесёт dev в main через Merge Request и pipeline и запишет на продовый сервер.", "На прод /", m.PromoteUrl);

        if (!Ok(m.Prod))
            return new(FlowStage.Prod, NextStepKind.Warn, m.Prod == SyncState.OnlyGit ? "На продовом сервере его нет" : "Продовый сервер расходится с main",
                "На проде правили напрямую или запись не прошла. Сравните и перенесите на прод заново.", "Сравнить", m.CompareUrl);

        return new(FlowStage.Done, NextStepKind.Done,
            m.LastRelease is { } last ? $"Всё выпущено: {last} на проде" : "Тест и прод совпадают",
            "dev = main, серверы совпадают с ветками. Новая правка начинается на тесте.");
    }
}
