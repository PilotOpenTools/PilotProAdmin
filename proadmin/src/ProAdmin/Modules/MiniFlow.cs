using ProAdmin.Automations;
using ProAdmin.Reports;

namespace ProAdmin.Modules;

/// <summary>Состояние этапа в строке списка: работа здесь сейчас, пройден или пока ничего нет.</summary>
public enum MiniState
{
    Idle,
    Active,
    Done,
}

public sealed record MiniStage(string Title, MiniState State, string? Badge = null);

/// <summary>Схема пути в строке списка (модули, отчёты, автоматизации): где сейчас идёт работа и что дальше.</summary>
public sealed record MiniFlow(IReadOnlyList<MiniStage> Stages, NextStepKind Kind, string Note);

/// <summary>Строит схему CI/CD для строки списка только из данных каталога - без дополнительных запросов к GitLab.</summary>
public static class MiniFlows
{
    private static MiniFlow Build(IReadOnlyList<(string Title, bool Active, string? Badge)> steps, NextStepKind kind, string note)
    {
        // Ничего не в работе - все этапы пройдены; иначе подсвечены только те, где есть работа.
        var any = steps.Any(s => s.Active);
        return new MiniFlow(steps.Select(s => new MiniStage(s.Title, s.Active ? MiniState.Active : any ? MiniState.Idle : MiniState.Done, s.Badge)).ToList(),
            any ? kind : NextStepKind.Done, note);
    }

    public static MiniFlow ForModule(ModuleRow row)
    {
        var git = row.Git!;
        var test = row.Envs.GetValueOrDefault(ModuleEnv.Test)?.State;
        var prod = row.Envs.GetValueOrDefault(ModuleEnv.Prod)?.State;
        var stageState = row.Envs.GetValueOrDefault(ModuleEnv.Stage)?.State;
        var hasStage = row.Envs.ContainsKey(ModuleEnv.Stage);

        var tasks = git.TaskBranches.Count;
        var devAhead = git.ActiveRelease is null && git.DevVersion is { } d && git.MainVersion is { } m && d > m;
        var testActive = devAhead || !ModuleNextSteps.Ok(test);
        var stageActive = git.ActiveRelease is not null;
        var prodActive = git.HotfixBranches.Count > 0 || !ModuleNextSteps.Ok(prod);

        var steps = new List<(string, bool, string?)>
        {
            ("Задача", tasks > 0, tasks > 0 ? tasks.ToString() : null),
            ("Тест", testActive, null),
        };
        if (hasStage)
            steps.Add(("Стейдж", stageActive, git.ActiveReleaseVersion?.ToString()));
        steps.Add(("Прод", prodActive, git.HotfixBranches.Count > 0 ? "hotfix" : null));

        var (kind, note) =
            !git.HasCi || git.Dev is null ? (NextStepKind.Warn, "Репозиторий не готов к CI/CD")
            : git.HotfixBranches.Count > 0 ? (NextStepKind.Action, $"Hotfix ждёт выпуска в прод")
            : stageActive ? (NextStepKind.Action, !ModuleNextSteps.Ok(stageState)
                ? $"Релиз {git.ActiveReleaseVersion} - выложить на стейдж"
                : $"Релиз {git.ActiveReleaseVersion} на стейдже - проверить и выпустить")
            : tasks > 0 ? (NextStepKind.Action, $"В работе {ModuleNextSteps.Plural(tasks, "задача", "задачи", "задач")}")
            : testActive ? (NextStepKind.Action, ModuleNextSteps.Ok(test) ? "На тесте изменения - собрать релиз" : "Выложить dev на тест")
            : prodActive ? (NextStepKind.Warn, "На проде не голова main")
            : (NextStepKind.Done, git.MainVersion is { } v ? $"Всё выпущено: v{v}" : "Новых задач нет");

        if (!git.HasCi || git.Dev is null)
            return new MiniFlow(steps.Select(s => new MiniStage(s.Item1, MiniState.Idle)).ToList(), kind, note);
        return Build(steps, kind, note) with { Note = note };
    }

    public static MiniFlow ForReport(ReportRow row, bool hasMain)
    {
        var steps = new List<(string, bool, string?)>
        {
            ("Правка", false, null),
            ("Тест", row.PendingPromotion, null),
            ("Прод", row.PendingBackport || (hasMain && row.Repo is not null && row.Envs.Values.Any(e => e.State is SyncState.Differs or SyncState.OnlyGit)), null),
        };
        if (row.Repo is null)
            return new MiniFlow(steps.Select(s => new MiniStage(s.Item1, MiniState.Idle)).ToList(), NextStepKind.Warn, "Нет репозитория - не проходит тест и прод через GitLab");
        var (kind, note) =
            row.PendingBackport && row.RollbackTo is { } rt ? (NextStepKind.Action, $"Прод откачен на {rt} - dev ещё со старой версией: принять откат («В тест») или исправить в dev")
            : row.PendingBackport ? (NextStepKind.Action, "Правка на проде - вернуть в dev и на тест")
            : row.PendingPromotion ? (NextStepKind.Action, "Изменения на тесте - проверить и перенести на прод")
            : steps[2].Item2 ? (NextStepKind.Warn, "Серверы расходятся с ветками")
            : (NextStepKind.Done, "Тест и прод совпадают");
        return Build(steps, kind, note) with { Note = note };
    }

    public static MiniFlow ForAutomation(CatalogRow row, bool hasStage)
    {
        var offServer = row.Repo is not null && row.Envs.Values.Any(e => e.State is SyncState.Differs or SyncState.OnlyGit);
        var steps = new List<(string, bool, string?)>
        {
            ("Правка", false, null),
            ("Тест", row.PendingPromotion, row.DevVersion?.ToString()),
        };
        if (hasStage)
            steps.Add(("Стейдж", false, null));
        steps.Add(("Прод", row.MainAhead || offServer && !row.PendingPromotion, row.MainVersion?.ToString()));
        if (row.Repo is null)
            return new MiniFlow(steps.Select(s => new MiniStage(s.Item1, MiniState.Idle)).ToList(), NextStepKind.Warn, "Нет репозитория - не проходит тест и прод через GitLab");
        var (kind, note) =
            row.MainAhead && row.RollbackTo is { } rt ? (NextStepKind.Action, $"Прод откачен на {rt} - dev ещё со старой версией: принять откат или исправить в dev")
            : row.MainAhead ? (NextStepKind.Action, "Прод меняли мимо теста - вернуть в dev")
            : row.PendingPromotion ? (NextStepKind.Action, "Версия на тесте новее - проверить и перенести на прод")
            : offServer ? (NextStepKind.Warn, "Серверы расходятся с ветками")
            : (NextStepKind.Done, "Тест и прод совпадают");
        return Build(steps, kind, note) with { Note = note };
    }
}
