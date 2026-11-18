namespace ProAdmin.Automations.Validation;

public enum ValueKind
{
    Any,
    String,
    StringArray,
    StringOrArray,
    Bool,
    Object,
}

/// <summary>С чем сверять значения параметра по метаданным базы.</summary>
public enum NameCheck
{
    None,
    Types,
    Attributes,
    States,

    /// <summary>Список выражений вида new.attr.executor.person.id или адресов.</summary>
    Expressions,

    /// <summary>Текст с подстановками {new.attr.title}.</summary>
    Template,

    /// <summary>stateToOrgUnitConfig: ключи - состояния, значения - атрибуты.</summary>
    StateToAttributes,
}

public sealed record ParamSpec(string Name, ValueKind Kind = ValueKind.Any, string[]? Allowed = null, NameCheck Check = NameCheck.None, bool Csv = false);

public sealed record TriggerSpec(string Name, string Description, ParamSpec[] Params, string? OnlyTriggerType = null);

public sealed record ActivitySpec(string Name, ParamSpec[] Params, string[] Required, string? RequiresTriggerType = null, bool OpenParams = false);

/// <summary>
/// Справочник триггеров и действий из справки Pilot-myAdmin (раздел «Автоматизация»).
/// Список триггеров предустановлен; действия могут дополняться C#-скриптами и серверными модулями.
/// </summary>
public static class AutomationSchema
{
    private static readonly string[] TriggerTypes = { "Server", "Client" };
    private static readonly string[] ChangeKinds = { "Create", "Modify" };

    private static readonly ParamSpec TriggerType = new("triggerType", ValueKind.String, TriggerTypes, Csv: true);
    private static readonly ParamSpec SourceTypes = new("sourceTypes", ValueKind.StringArray, Check: NameCheck.Types);
    private static readonly ParamSpec AttributeNames = new("attributeNames", ValueKind.StringArray, Check: NameCheck.Attributes);
    private static readonly ParamSpec ItemChangeKind = new("itemChangeKind", ValueKind.String, ChangeKinds, Csv: true);

    public static readonly IReadOnlyDictionary<string, TriggerSpec> Triggers = new[]
    {
        new TriggerSpec("AccessRecordChanged", "добавление или удаление записей в таблице прав", new[] { TriggerType, SourceTypes }),
        new TriggerSpec("AttributeChanged", "изменение атрибута", new[] { TriggerType, SourceTypes, AttributeNames, ItemChangeKind }),
        new TriggerSpec("DocumentOpened", "открытие документа", new[] { TriggerType, SourceTypes }, OnlyTriggerType: "Client"),
        new TriggerSpec("DocumentSignatureRequestChanged", "создание и удаление запросов на подпись", new[] { TriggerType, SourceTypes }),
        new TriggerSpec("DocumentSignatureRequestCreated", "добавление запросов на подпись", new[] { TriggerType, SourceTypes }),
        new TriggerSpec("DocumentSigned", "подписание документа", new[] { TriggerType, SourceTypes }),
        new TriggerSpec("ItemCreated", "создание элемента", new[] { TriggerType, SourceTypes }),
        new TriggerSpec("MovedToRecycleBin", "удаление объекта в корзину", new[] { TriggerType, SourceTypes }),
        new TriggerSpec("OrgUnitAttributeChanged", "смена типа организационной единицы в атрибуте", new[]
        {
            TriggerType, SourceTypes, AttributeNames, ItemChangeKind,
            new ParamSpec("oldOrgUnitKind", ValueKind.String, new[] { "Department", "Position", "Group" }),
            new ParamSpec("newOrgUnitKind", ValueKind.String, new[] { "Department", "Position", "Group" }),
        }),
        new TriggerSpec("RelationsChanged", "изменение, добавление, удаление связи", new[] { TriggerType, SourceTypes, ItemChangeKind }),
        new TriggerSpec("StateAttributeChanged", "изменение атрибута типа состояние", new[]
        {
            TriggerType, SourceTypes, AttributeNames, ItemChangeKind,
            new ParamSpec("statesFrom", ValueKind.StringArray, Check: NameCheck.States),
            new ParamSpec("statesTo", ValueKind.StringOrArray, Check: NameCheck.States),
        }),
        new TriggerSpec("VersionChanged", "изменение версии документа или файла", new[]
        {
            TriggerType, SourceTypes, ItemChangeKind,
            new ParamSpec("fileBodyChanged", ValueKind.Bool),
            new ParamSpec("fileExtensions", ValueKind.StringArray),
        }),
        new TriggerSpec("Finally", "завершение обработки изменений (используется в базовых сценариях)", new[] { TriggerType, SourceTypes }),
    }.ToDictionary(t => t.Name);

    /// <summary>Общие для всех действий параметры.</summary>
    public static readonly ParamSpec[] CommonActivityParams =
    {
        SourceTypes,
        new("target", ValueKind.String, new[] { "Children", "Self", "Relations" }),
        new("targetTypes", ValueKind.StringOrArray, Check: NameCheck.Types),
        new("relationType", ValueKind.String, new[] { "SourceFiles", "Custom", "TaskAttachments" }),
        new("relationFilterByChangeKind", ValueKind.String, new[] { "Removed", "Added" }),
    };

    private static ParamSpec Attr(string name) => new(name, ValueKind.String, Check: NameCheck.Attributes);
    private static ParamSpec Attrs(string name) => new(name, ValueKind.StringOrArray, Check: NameCheck.Attributes);
    private static ParamSpec States(string name) => new(name, ValueKind.StringOrArray, Check: NameCheck.States);
    private static ParamSpec Flag(string name) => new(name, ValueKind.Any);
    private static ParamSpec Text(string name) => new(name, ValueKind.String, Check: NameCheck.Template);
    private static ParamSpec Exprs(string name) => new(name, ValueKind.StringOrArray, Check: NameCheck.Expressions);

    private static readonly ParamSpec[] StateChange = { Attr("attributeName"), States("initialStateNames"), States("toStateName") };
    private static readonly string[] StateChangeRequired = { "sourceTypes", "target", "targetTypes", "attributeName", "initialStateNames", "toStateName" };

    public static readonly IReadOnlyDictionary<string, ActivitySpec> Activities = new[]
    {
        new ActivitySpec("AddVirtualSignatureRequests", new[] { Attr("signingRoleAttributeName"), Attrs("orgUnitAttributeNames") },
            new[] { "sourceTypes", "target", "targetTypes", "orgUnitAttributeNames" }),
        new ActivitySpec("CleanDocumentSignaturesAndRequests", new[] { Attr("attributeName"), Flag("newVersionConfirmationNeeded"), States("initialStateNames") },
            new[] { "sourceTypes", "target", "attributeName" }, RequiresTriggerType: "Client"),
        new ActivitySpec("ClearAccessRecords", new[] { Flag("accessLevelFilter"), Flag("inheritableOnly") }, new[] { "accessLevelFilter" }),
        new ActivitySpec("ClearAttributeValue", new[] { Attr("attributeName") }, new[] { "sourceTypes", "attributeName", "target" }),
        new ActivitySpec("CopyAccessRights", new[] { Flag("accessLevelFilter"), Flag("inheritableFilter") }, new[] { "accessLevelFilter" }),
        new ActivitySpec("CopySignatureRequestsFromPreviousVersion", new[] { Flag("forbidAllSignedCopy"), Flag("copyIgnoringTaskState"), Flag("isVirtual") },
            new[] { "sourceTypes" }, RequiresTriggerType: "Client"),
        new ActivitySpec("CopySignatureRequestFromPreviousVersion", new[] { Flag("forbidAllSignedCopy"), Flag("copyIgnoringTaskState"), Flag("isVirtual") },
            new[] { "sourceTypes" }, RequiresTriggerType: "Client"),
        new ActivitySpec("CreateSignatureRequests", Array.Empty<ParamSpec>(), Array.Empty<string>(), RequiresTriggerType: "Client", OpenParams: true),
        new ActivitySpec("DeleteVirtualSignatureRequests", new[] { Attrs("orgUnitAttributeNames") },
            new[] { "sourceTypes", "target", "targetTypes", "orgUnitAttributeNames" }),
        new ActivitySpec("DocumentVersionsStateManager", new[]
        {
            States("intoProductionStateNames"), States("annulledStateNames"), Flag("intoProductionRole"),
            States("intoProductionStateName"), States("annuledStateName"),
        }, new[] { "intoProductionRole" }),
        new ActivitySpec("ManageTaskAndWorkflowState", new[] { new ParamSpec("completionStateConfig", ValueKind.Object) }, Array.Empty<string>(), OpenParams: true),
        new ActivitySpec("RecalculateWorkflowStates", new[] { Attr("attributeName") }, new[] { "sourceTypes", "target", "attributeName" }, RequiresTriggerType: "Server"),
        new ActivitySpec("ReplaceVirtualSignatureRequest", new[] { Attr("signingRoleAttributeName"), Attrs("orgUnitAttributeNames") },
            new[] { "sourceTypes", "target", "targetTypes", "orgUnitAttributeNames" }),
        new ActivitySpec("RevokeProcess", new[] { new ParamSpec("completionStateConfig", ValueKind.Object) }, Array.Empty<string>(), RequiresTriggerType: "Client"),
        new ActivitySpec("SendEmail", new[]
        {
            Exprs("recipients"), Exprs("ignoreList"), Text("subject"), Text("body"),
            new ParamSpec("host", ValueKind.String), new ParamSpec("port", ValueKind.Any), new ParamSpec("userName", ValueKind.String),
            new ParamSpec("password", ValueKind.String), new ParamSpec("fromEmail", ValueKind.String), new ParamSpec("fromDisplayName", ValueKind.String),
            Flag("isBodyHtml"), Flag("enableSsl"), Flag("checkCertificateRevocation"), Flag("сheckCertificateRevocation"),
            Flag("templates"), Flag("itemFormat"), Flag("separator"),
        }, new[] { "sourceTypes", "target", "host", "port", "userName", "password", "fromEmail", "recipients" }, RequiresTriggerType: "Server"),
        new ActivitySpec("SendNotifications", new[] { Exprs("subscribers"), Exprs("ignoreList"), Text("message"), new ParamSpec("notificationName", ValueKind.String) },
            Array.Empty<string>(), RequiresTriggerType: "Server"),
        new ActivitySpec("SetAccessLevel", new[] { Flag("accessLevel"), Flag("inheritable"), Attrs("orgUnitAttributeNames"), Flag("orgUnitKinds") },
            new[] { "sourceTypes", "target", "accessLevel", "inheritable", "orgUnitAttributeNames" }),
        new ActivitySpec("SetDateTimeAttributeValue", new[] { Attr("attributeName"), Attr("offsetAttributeName") },
            new[] { "sourceTypes", "target", "attributeName" }),
        new ActivitySpec("SetOrgUnitAttributeValue", new[] { Attr("attributeName"), Flag("orgUnitAttributeValue"), Flag("changeListKind"), Attr("sourceAttributeName") },
            new[] { "sourceTypes", "attributeName", "orgUnitAttributeValue" }),
        new ActivitySpec("SetOrgUnitAttributeValueBasedOnState", new[]
        {
            Attr("attributeName"), Attr("targetAttributeName"),
            new ParamSpec("stateToOrgUnitConfig", ValueKind.Object, Check: NameCheck.StateToAttributes),
        }, new[] { "attributeName", "stateToOrgUnitConfig", "targetAttributeName" }),
        new ActivitySpec("SetSignatureRequestRelatedObjectStateAttribute", StateChange, new[] { "sourceTypes", "attributeName", "initialStateNames", "toStateName" }),
        new ActivitySpec("SetStateAttributeValue", StateChange, StateChangeRequired),
        new ActivitySpec("SetStateAttributeValueIfDepartment", StateChange.Append(Attr("departmentAttributeName")).ToArray(),
            StateChangeRequired.Append("departmentAttributeName").ToArray()),
        new ActivitySpec("SetStateAttributeValueIfNotSigned", StateChange, StateChangeRequired),
        new ActivitySpec("SetStateAttributeValueIfSigned", StateChange, StateChangeRequired),
        new ActivitySpec("SetSystemStateValue", new[] { Flag("oldStates"), Flag("newState") }, new[] { "sourceTypes", "target", "oldStates", "newState" }),
        new ActivitySpec("UpdateSignatureRequests", Array.Empty<ParamSpec>(), Array.Empty<string>(), RequiresTriggerType: "Client", OpenParams: true),
    }.ToDictionary(a => a.Name);

    public static readonly string[] ExpressionRoots = { "old", "new", "initiatingPerson" };

    /// <summary>Атрибуты из выражения вида new.attr.executor.person.id.</summary>
    public static IEnumerable<string> ExpressionAttributes(string expression)
    {
        var parts = expression.Trim().Split('.');
        if (parts.Any(p => p.Contains('@') || p.Contains(' ')) || !ExpressionRoots.Contains(parts[0]))
            yield break;
        for (var i = 0; i < parts.Length - 1; i++)
            if (parts[i] == "attr")
                yield return parts[i + 1];
    }

    /// <summary>Ближайшее известное имя (для «возможно, вы имели в виду»). null - ничего похожего.</summary>
    public static string? Closest(string value, IEnumerable<string> candidates, int maxDistance = 3)
    {
        var list = candidates.ToList();

        // Недописанное или дописанное имя: toState / toStateName.
        if (value.Length >= 4)
        {
            var prefixed = list.Where(c => c.StartsWith(value, StringComparison.OrdinalIgnoreCase) || value.StartsWith(c, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => Math.Abs(c.Length - value.Length)).FirstOrDefault();
            if (prefixed is not null && !string.Equals(prefixed, value, StringComparison.Ordinal))
                return prefixed;
        }

        string? best = null;
        var bestDistance = int.MaxValue;
        foreach (var c in list)
        {
            var d = Distance(value.ToLowerInvariant(), c.ToLowerInvariant());
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }
        }
        var limit = Math.Min(maxDistance, Math.Max(1, value.Length / 3));
        return bestDistance <= limit ? best : null;
    }

    public static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
