using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProAdmin.Automations.Validation;

public sealed record LintIssue(string Level, int Line, string Message)
{
    public bool IsError => Level == "error";
}

/// <summary>
/// Проверка сценария автоматизации по справочнику Pilot (триггеры, действия, параметры, допустимые значения)
/// и - если передан <see cref="MetadataIndex"/> - по конфигурации базы: типы, атрибуты, состояния, C#-действия.
/// Ошибки означают, что сценарий заведомо не заработает; предупреждения - что стоит перепроверить.
/// </summary>
public static partial class AutomationLinter
{
    public static List<LintIssue> Lint(string triggersJson, string? csharp, string automationName, MetadataIndex? metadata,
        HostedActivities? hosted = null)
    {
        var issues = new List<LintIssue>();
        var root = JsonTree.Parse(triggersJson ?? "", out var syntaxError, out var syntaxLine);
        if (root is null)
        {
            issues.Add(new("error", syntaxLine, syntaxError!));
            return issues;
        }

        var ctx = new Context(issues, metadata, BuildActivityOwners(metadata, csharp, automationName), MetadataIndex.DeclaredActivities(csharp).ToHashSet(),
            hosted ?? HostedActivities.None);

        var triggers = root.IsArray ? root.Items : root.IsObject ? new List<JNode> { root } : null;
        if (triggers is null)
        {
            ctx.Error(root.Line, $"Сценарий должен быть массивом [ ... ] триггеров, а не {root.KindRu}.");
            return issues;
        }
        if (root.IsObject)
            ctx.Warn(root.Line, "Сценарий обычно оформляется массивом [ { \"when\": … } ]. Проверьте, что так задумано.");

        var triggerCount = 0;
        for (var i = 0; i < triggers.Count; i++)
        {
            var trigger = triggers[i];
            if (!trigger.IsObject)
            {
                ctx.Error(trigger.Line, $"Элемент сценария должен быть объектом {{ \"when\": …, \"then\": [ … ] }}, а не {trigger.KindRu}.");
                continue;
            }
            if (trigger.Properties.All(p => p.Name == "_comment_"))
            {
                // Клиент Pilot считает каждый элемент триггером и падает на элементе без "when"
                // (ArgumentNullException в AutomationEventHandler.RebuildSnapshot).
                if (i == 0 && root.IsArray && triggers.Skip(1).FirstOrDefault() is { IsObject: true })
                    ctx.Warn(trigger.Line, "Отдельная запись { \"_comment_\" } без \"when\" роняет клиент Pilot. При отправке версия будет перенесена в первый триггер (или нажмите «+patch»).");
                else
                    ctx.Error(trigger.Line, "Запись без \"when\" роняет клиент Pilot. Перенесите \"_comment_\" внутрь триггера: { \"_comment_\": \"Version: X.Y.Z\", \"when\": … }.");
                continue;
            }

            triggerCount++;
            LintTrigger(trigger, ctx);
        }

        if (triggerCount == 0)
            ctx.Warn(root.Line, "В сценарии нет ни одного триггера (\"when\") - автоматизация ничего не делает.");

        if (csharp is { Length: > 0 } && ctx.OwnActivities.Count == 0)
            ctx.Warn(1, "В C#-вставке не найдено объявление действия (public string Name => \"…\") - его нельзя будет вызвать из JSON.");
        foreach (var declared in ctx.OwnActivities.Where(a => !ctx.UsedActivities.Contains(a)))
            ctx.Info(1, $"Действие «{declared}» из C#-вставки не вызывается в JSON этой автоматизации (может использоваться другими).");

        static int Rank(string level) => level switch { "error" => 0, "warn" => 1, _ => 2 };
        issues.Sort((a, b) => a.Line != b.Line ? a.Line.CompareTo(b.Line) : Rank(a.Level).CompareTo(Rank(b.Level)));
        return issues;
    }

    private static void LintTrigger(JNode trigger, Context ctx)
    {
        CheckDuplicates(trigger, ctx);
        foreach (var p in trigger.Properties.Where(p => p.Name is not ("when" or "params" or "then" or "_comment_")))
            ctx.Warn(p.Line, $"Неизвестное поле триггера «{p.Name}»{Suggest(p.Name, new[] { "when", "params", "then" })}. Ожидаются when, params, then.");

        var when = trigger["when"];
        TriggerSpec? spec = null;
        if (when is null)
        {
            ctx.Error(trigger.Line, "У триггера нет поля \"when\" - не указано, на какое событие он срабатывает.");
        }
        else if (!when.IsString)
        {
            ctx.Error(when.Line, $"\"when\" должен быть строкой с именем триггера, а не {when.KindRu}.");
        }
        else if (!AutomationSchema.Triggers.TryGetValue(when.Text!, out spec))
        {
            var close = AutomationSchema.Closest(when.Text!, AutomationSchema.Triggers.Keys);
            if (close is not null)
                ctx.Error(when.Line, $"Неизвестный триггер «{when.Text}» - возможно, «{close}».");
            else
                ctx.Warn(when.Line, $"Триггера «{when.Text}» нет в справке Pilot. Известные: {string.Join(", ", AutomationSchema.Triggers.Keys)}.");
        }

        var parameters = trigger["params"];
        string[] triggerTypes = Array.Empty<string>();
        if (parameters is null)
        {
            ctx.Warn(trigger.Line, "У триггера нет \"params\" - будет использован triggerType по умолчанию. Лучше указать явно.");
        }
        else if (!parameters.IsObject)
        {
            ctx.Error(parameters.Line, $"\"params\" триггера должен быть объектом {{ … }}, а не {parameters.KindRu}.");
        }
        else
        {
            CheckDuplicates(parameters, ctx);
            var allowed = spec?.Params ?? Array.Empty<ParamSpec>();
            foreach (var p in parameters.Properties)
            {
                if (p.Name == "_comment_") continue;
                var ps = allowed.FirstOrDefault(a => a.Name == p.Name);
                if (ps is null)
                {
                    if (spec is not null)
                        ctx.Warn(p.Line, $"Параметр «{p.Name}» не описан для триггера {spec.Name}{Suggest(p.Name, allowed.Select(a => a.Name))}.");
                    continue;
                }
                CheckValue(ps, p.Value, $"«{p.Name}»", ctx);
            }

            var tt = parameters["triggerType"];
            if (tt is null)
                ctx.Warn(parameters.Line, "Не указан \"triggerType\" (\"Server\", \"Client\" или \"Server,Client\").");
            else if (tt.IsString)
                triggerTypes = tt.Text!.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

            if (spec?.OnlyTriggerType is { } only && tt is { IsString: true } && triggerTypes.Any(t => t != only))
                ctx.Error(tt.Line, $"Триггер {spec.Name} работает только с \"triggerType\": \"{only}\".");
        }

        var then = trigger["then"];
        if (then is null)
        {
            ctx.Error(trigger.Line, "У триггера нет \"then\" - не указано, какие действия выполнять.");
            return;
        }
        if (!then.IsArray)
        {
            ctx.Error(then.Line, $"\"then\" должен быть массивом действий [ {{ \"activity\": … }} ], а не {then.KindRu}.");
            return;
        }
        if (then.Items.Count == 0)
            ctx.Warn(then.Line, "Список действий \"then\" пуст.");

        foreach (var action in then.Items)
            LintActivity(action, triggerTypes, ctx);
    }

    private static void LintActivity(JNode action, string[] triggerTypes, Context ctx)
    {
        if (!action.IsObject)
        {
            ctx.Error(action.Line, $"Действие должно быть объектом {{ \"activity\": …, \"params\": {{ … }} }}, а не {action.KindRu}.");
            return;
        }
        CheckDuplicates(action, ctx);
        foreach (var p in action.Properties.Where(p => p.Name is not ("activity" or "params" or "_comment_")))
            ctx.Warn(p.Line, $"Неизвестное поле действия «{p.Name}»{Suggest(p.Name, new[] { "activity", "params" })}. Ожидаются activity, params.");

        var name = action["activity"];
        if (name is null)
        {
            ctx.Error(action.Line, "У действия нет поля \"activity\" с именем действия.");
            return;
        }
        if (!name.IsString)
        {
            ctx.Error(name.Line, $"\"activity\" должно быть строкой, а не {name.KindRu}.");
            return;
        }

        var activity = name.Text!;
        ctx.UsedActivities.Add(activity);
        AutomationSchema.Activities.TryGetValue(activity, out var spec);
        var ownScript = ctx.OwnActivities.Contains(activity);
        var otherScript = ctx.ActivityOwners.TryGetValue(activity, out var owner);
        var fromModule = ctx.Hosted.Activities.TryGetValue(activity, out var module);

        if (spec is null && !ownScript && !otherScript && !fromModule)
        {
            var close = AutomationSchema.Closest(activity, AutomationSchema.Activities.Keys.Concat(ctx.OwnActivities)
                .Concat(ctx.ActivityOwners.Keys).Concat(ctx.Hosted.Activities.Keys));
            if (close is not null)
                ctx.Error(name.Line, $"Неизвестное действие «{activity}» - возможно, «{close}».");
            else if (ctx.Hosted.Available)
                ctx.Warn(name.Line, $"Действие «{activity}» не найдено ни среди стандартных, ни в C#-скриптах автоматизаций, ни в серверных модулях ServerHost" +
                                    (ctx.Hosted.InactiveModules.Count > 0 ? $" (выключены или не загрузились: {string.Join(", ", ctx.Hosted.InactiveModules)})" : "") +
                                    ". Если оно из штатного модуля расширения (.servext) - проверьте, что модуль установлен.");
            else if (ctx.Metadata is not null)
                ctx.Warn(name.Line, $"Действие «{activity}» не найдено ни среди стандартных, ни в C#-скриптах автоматизаций сервера. Если оно из серверного модуля - проверьте, что модуль установлен" +
                                    (ctx.Hosted.Error is { } hostError ? $" (список модулей ServerHost не получен: {hostError})" : "") + ".");
            else
                ctx.Warn(name.Line, $"Действие «{activity}» не стандартное и не объявлено в C#-вставке этой автоматизации.");
        }
        else if (spec is null && !ownScript && fromModule)
        {
            ctx.Info(name.Line, $"Действие «{activity}» из серверного модуля «{module!.Name}» {module.Version} (ServerHost).");
        }
        else if (spec is null && otherScript && !ownScript)
        {
            ctx.Info(name.Line, $"Действие «{activity}» объявлено в C#-скрипте автоматизации «{owner}».");
        }

        // Ограничения по triggerType.
        if (triggerTypes.Length > 0)
        {
            var required = spec?.RequiresTriggerType ?? (ownScript || otherScript || fromModule ? "Server" : null);
            if (required is not null && triggerTypes.Any(t => t != required))
                ctx.Warn(name.Line, spec is not null
                    ? $"Действие {activity} по справке вызывается триггером с \"triggerType\": \"{required}\", а здесь \"{string.Join(",", triggerTypes)}\"."
                    : $"Действия из C#-скриптов и серверных модулей вызываются только серверными триггерами (\"triggerType\": \"Server\"), а здесь \"{string.Join(",", triggerTypes)}\".");
        }

        var parameters = action["params"];
        if (parameters is null)
        {
            if (spec is not null && spec.Required.Length > 0)
                ctx.Warn(action.Line, $"У действия {activity} нет \"params\". Обязательные параметры: {string.Join(", ", spec.Required)}.");
            return;
        }
        if (!parameters.IsObject)
        {
            ctx.Error(parameters.Line, $"\"params\" действия должен быть объектом {{ … }}, а не {parameters.KindRu}.");
            return;
        }
        CheckDuplicates(parameters, ctx);

        if (spec is null)
        {
            // Пользовательское действие: проверяем только общие параметры, если они есть.
            foreach (var p in parameters.Properties)
                if (AutomationSchema.CommonActivityParams.FirstOrDefault(c => c.Name == p.Name) is { } common)
                    CheckValue(common, p.Value, $"«{p.Name}»", ctx);
            return;
        }

        var all = AutomationSchema.CommonActivityParams.Concat(spec.Params).ToList();
        foreach (var p in parameters.Properties)
        {
            if (p.Name == "_comment_") continue;
            var ps = all.FirstOrDefault(a => a.Name == p.Name);
            if (ps is null)
            {
                if (!spec.OpenParams)
                    ctx.Warn(p.Line, $"Параметр «{p.Name}» не описан для действия {activity}{Suggest(p.Name, all.Select(a => a.Name))}.");
                continue;
            }
            CheckValue(ps, p.Value, $"«{p.Name}»", ctx);
        }

        var missing = spec.Required.Where(r => parameters[r] is null).ToList();
        if (missing.Count > 0)
            ctx.Warn(parameters.Line, $"Для {activity} не указаны обязательные параметры: {string.Join(", ", missing)} (будут значения по умолчанию).");

        if (parameters["relationType"] is not null && parameters["target"] is { IsString: true } target && target.Text != "Relations")
            ctx.Warn(parameters["relationType"]!.Line, "\"relationType\" работает только вместе с \"target\": \"Relations\".");
    }

    private static void CheckValue(ParamSpec spec, JNode value, string label, Context ctx)
    {
        switch (spec.Kind)
        {
            case ValueKind.String when !value.IsString:
                ctx.Error(value.Line, $"{label} должен быть строкой в кавычках, а не {value.KindRu}.");
                return;
            case ValueKind.StringArray when !value.IsArray:
                ctx.Error(value.Line, value.IsString
                    ? $"{label} должен быть массивом: [\"{value.Text}\"], а не строкой."
                    : $"{label} должен быть массивом строк, а не {value.KindRu}.");
                return;
            case ValueKind.StringOrArray when !value.IsString && !value.IsArray:
                ctx.Error(value.Line, $"{label} должен быть строкой или массивом строк, а не {value.KindRu}.");
                return;
            case ValueKind.Bool when value.Kind is not (JsonValueKind.True or JsonValueKind.False):
                ctx.Warn(value.Line, $"{label} ожидается true или false (без кавычек), а не {value.KindRu}.");
                return;
            case ValueKind.Object when !value.IsObject:
                ctx.Error(value.Line, $"{label} должен быть объектом {{ … }}, а не {value.KindRu}.");
                return;
        }

        if (value.IsArray)
            foreach (var item in value.Items.Where(i => !i.IsString))
                ctx.Error(item.Line, $"Элементы {label} должны быть строками в кавычках, а не {item.KindRu}.");

        if (spec.Allowed is not null && value.IsString)
        {
            var parts = spec.Csv ? value.Text!.Split(',', StringSplitOptions.TrimEntries) : new[] { value.Text! };
            foreach (var part in parts.Where(p => !spec.Allowed.Contains(p)))
            {
                var close = AutomationSchema.Closest(part, spec.Allowed, 4);
                ctx.Error(value.Line, $"Недопустимое значение {label}: «{part}»" +
                                      (close is not null ? $" - возможно, «{close}»" : "") +
                                      $". Допустимо: {string.Join(", ", spec.Allowed.Select(a => $"\"{a}\""))}" +
                                      (spec.Csv ? " (через запятую)." : "."));
            }
        }

        if (ctx.Metadata is not { } meta)
            return;

        switch (spec.Check)
        {
            case NameCheck.Types:
                foreach (var (name, line) in value.Strings())
                    CheckName(name, line, meta.Types, "тип", ctx);
                break;
            case NameCheck.Attributes:
                foreach (var (name, line) in value.Strings())
                    CheckName(name, line, meta.Attributes, "атрибут", ctx);
                break;
            case NameCheck.States:
                foreach (var (name, line) in value.Strings())
                    CheckName(name, line, meta.States, "состояние", ctx);
                break;
            case NameCheck.Expressions:
                foreach (var (expr, line) in value.Strings())
                    CheckExpression(expr, line, ctx, meta);
                break;
            case NameCheck.Template:
                foreach (Match m in TemplateExpr().Matches(value.Text ?? ""))
                    CheckExpression(m.Groups[1].Value, value.Line, ctx, meta);
                break;
            case NameCheck.StateToAttributes when value.IsObject:
                foreach (var p in value.Properties)
                {
                    CheckName(p.Name, p.Line, meta.States, "состояние", ctx);
                    foreach (var (attr, line) in p.Value.Strings())
                        CheckName(attr, line, meta.Attributes, "атрибут", ctx);
                }
                break;
        }
    }

    private static void CheckName(string name, int line, HashSet<string> known, string what, Context ctx)
    {
        if (name.Length == 0 || known.Contains(name))
            return;
        var close = AutomationSchema.Closest(name, known);
        ctx.Warn(line, $"В базе нет {WhatGenitive(what)} «{name}»" + (close is not null ? $" - возможно, «{close}»" : "") + ".");
    }

    private static string WhatGenitive(string what) => what switch
    {
        "тип" => "типа",
        "атрибут" => "атрибута",
        "состояние" => "состояния",
        _ => what,
    };

    /// <summary>Выражения вида new.attr.executor.person.id: корень и имена атрибутов после «attr».</summary>
    private static void CheckExpression(string expression, int line, Context ctx, MetadataIndex meta)
    {
        var expr = expression.Trim();
        if (expr.Length == 0 || expr.Contains('@') || expr.Contains(' ') || !expr.Contains('.'))
            return;

        var parts = expr.Split('.');
        if (!AutomationSchema.ExpressionRoots.Contains(parts[0]))
        {
            var close = AutomationSchema.Closest(parts[0], AutomationSchema.ExpressionRoots);
            ctx.Warn(line, $"Выражение «{expr}»: начало должно быть old, new или initiatingPerson" + (close is not null ? $" - возможно, «{close}»" : "") + ".");
            return;
        }
        foreach (var attribute in AutomationSchema.ExpressionAttributes(expr))
            CheckName(attribute, line, meta.Attributes, "атрибут", ctx);
    }

    private static void CheckDuplicates(JNode node, Context ctx)
    {
        foreach (var g in node.Properties.GroupBy(p => p.Name).Where(g => g.Count() > 1 && g.Key != "_comment_"))
            ctx.Warn(g.Last().Line, $"Поле «{g.Key}» указано несколько раз - будет использовано последнее значение.");
    }

    private static string Suggest(string name, IEnumerable<string> candidates) =>
        AutomationSchema.Closest(name, candidates) is { } c ? $" - возможно, «{c}»" : "";

    /// <summary>C#-действия других автоматизаций сервера (действия этой автоматизации берутся из редактора).</summary>
    private static Dictionary<string, string> BuildActivityOwners(MetadataIndex? metadata, string? csharp, string automationName) =>
        metadata?.ScriptActivities.Where(kv => kv.Value != automationName).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        ?? new Dictionary<string, string>(StringComparer.Ordinal);

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9_.]*)(?::[^}]*)?\}")]
    internal static partial Regex TemplateExpr();

    private sealed class Context
    {
        public Context(List<LintIssue> issues, MetadataIndex? metadata, Dictionary<string, string> owners, HashSet<string> own, HostedActivities hosted)
        {
            Hosted = hosted;
            Issues = issues;
            Metadata = metadata;
            ActivityOwners = owners;
            OwnActivities = own;
        }

        public List<LintIssue> Issues { get; }
        public MetadataIndex? Metadata { get; }
        public Dictionary<string, string> ActivityOwners { get; }
        public HashSet<string> OwnActivities { get; }
        public HostedActivities Hosted { get; }
        public HashSet<string> UsedActivities { get; } = new(StringComparer.Ordinal);

        public void Error(int line, string message) => Issues.Add(new("error", line, message));
        public void Warn(int line, string message) => Issues.Add(new("warn", line, message));
        public void Info(int line, string message) => Issues.Add(new("info", line, message));
    }
}
