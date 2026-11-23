using Ascon.Pilot.DataClasses;
using ProAdmin.GitLab;
using ProAdmin.Modules;
using ProAdmin.Pilot;
using ProAdmin.Types;
using Microsoft.AspNetCore.Mvc;

namespace ProAdmin.Pages.Types;

public sealed class AttributeRow
{
    public string? Name { get; set; }
    public string? Title { get; set; }
    public MAttrType Type { get; set; } = MAttrType.String;
    public bool Obligatory { get; set; }
    public int DisplaySortOrder { get; set; }
    public int DisplayHeight { get; set; } = 1;
    public bool ShowInTree { get; set; }
    public bool IsService { get; set; }
    public bool JoinWithPrevious { get; set; }
    public AttributeUniquenessType Uniqueness { get; set; }
    public string? GroupTitle { get; set; }
    public bool IgnoreIndex { get; set; }
    public string? Positions { get; set; }
    public string? Configuration { get; set; }
    public bool Remove { get; set; }

    public static AttributeRow From(AttributeDoc a) => new()
    {
        Name = a.Name, Title = a.Title, Type = a.Type, Obligatory = a.Obligatory, DisplaySortOrder = a.DisplaySortOrder,
        DisplayHeight = a.DisplayHeight, ShowInTree = a.ShowInTree, IsService = a.IsService, JoinWithPrevious = a.JoinWithPrevious,
        Uniqueness = a.Uniqueness, GroupTitle = a.GroupTitle, IgnoreIndex = a.IgnoreIndex,
        Positions = string.Join(", ", a.EditableForPositions), Configuration = a.Configuration,
    };

    public AttributeDoc ToDoc() => new()
    {
        Name = (Name ?? "").Trim(), Title = Blank(Title), Type = Type, Obligatory = Obligatory, DisplaySortOrder = DisplaySortOrder,
        DisplayHeight = DisplayHeight, ShowInTree = ShowInTree, IsService = IsService, JoinWithPrevious = JoinWithPrevious,
        Uniqueness = Uniqueness, GroupTitle = Blank(GroupTitle), IgnoreIndex = IgnoreIndex,
        EditableForPositions = EditForm.List(Positions), Configuration = EditForm.Config(Configuration),
    };

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}

public sealed class TransitionRow
{
    public string? From { get; set; }
    public string? To { get; set; }
    public string? DisplayName { get; set; }
    public string? Positions { get; set; }
}

public sealed class EditForm
{
    // Тип
    public string? Title { get; set; }
    public TypeKind TypeKind { get; set; } = TypeKind.User;
    public int Sort { get; set; }
    public bool HasFiles { get; set; }
    public bool IsMountable { get; set; }
    public bool IsService { get; set; }
    public bool IsProject { get; set; }
    public bool IsVisibleOnStorage { get; set; } = true;
    public string? StateMachine { get; set; }
    public List<string> Children { get; set; } = new();

    /// <summary>Родительские типы: в чьих «Дочерних типах» стоит этот тип (при сохранении меняются и они - тем же коммитом).</summary>
    public List<string> Parents { get; set; } = new();
    public string? Configuration { get; set; }
    public List<AttributeRow> Attributes { get; set; } = new();

    // Состояние
    public UserStateColors Color { get; set; }
    public bool IsCompletionState { get; set; }

    // Группа состояний
    public List<TransitionRow> Transitions { get; set; } = new();

    public static List<string> List(string? text) =>
        (text ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static string? Config(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Replace("\r\n", "\n").TrimEnd();
}

/// <summary>
/// Редактор типа (с атрибутами), состояния или группы состояний. Сохранение - коммит в ветку изменения types/… от dev
/// (выбранную или новую); на серверы ничего не пишется, пока ветка не влита в dev («Влить в тест» на вкладке «Типы»).
/// </summary>
public class EditModel(PilotGateway pilot) : AppPageModel
{
    [BindProperty(SupportsGet = true)] public string Kind { get; set; } = "type";
    [BindProperty(SupportsGet = true)] public string? Name { get; set; }
    [BindProperty(SupportsGet = true)] public string Mode { get; set; } = "form";

    /// <summary>Ветка изменения types/…, в которую сохраняется правка; пусто - создать новую (<see cref="NewChange"/>).</summary>
    [BindProperty(SupportsGet = true)] public string? Branch { get; set; }

    /// <summary>Название нового изменения: имя ветки types/KTN-…-opisanie и сообщение коммита при вливании в dev.</summary>
    [BindProperty] public string? NewChange { get; set; }

    /// <summary>Имя нового элемента (у существующего имя не меняется: переименование в Pilot = удаление + создание).</summary>
    [BindProperty] public string? NewName { get; set; }
    [BindProperty] public EditForm Form { get; set; } = new();
    [BindProperty] public string? Json { get; set; }
    [BindProperty] public string? CommitMessage { get; set; }

    public TypesSnapshot? Snapshot { get; private set; }
    public string? SourceRef { get; private set; }
    public bool IsNew => string.IsNullOrEmpty(Name);
    public bool IsDeleted { get; private set; }
    public List<string> Errors { get; } = new();
    public List<string> Warnings { get; } = new();
    public GitLabGroupAccess? Access { get; private set; }

    public string KindTitle => Kind switch { "state" => "Состояние", "machine" => "Группа состояний", _ => "Тип" };
    /// <summary>Ветки изменений в работе - куда можно сохранить правку.</summary>
    public List<GitLabBranch> Branches { get; private set; } = new();
    public IEnumerable<string> StateNames => (Snapshot?.States.Values.Where(s => !s.IsDeleted).Select(s => s.Name) ?? Enumerable.Empty<string>())
        .Prepend(TypesSnapshot.NoState);

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        Normalize();
        if (!await LoadAsync(ct))
            return Page();
        if (!IsNew && !Fill())
            return NotFound();
        if (IsNew && Kind == "type")
        {
            Form.Sort = await DefaultTypeOrderAsync(ct);
            if (Mode == "json")
                Json = TypesJson.Write(new TypeDoc { Sort = Form.Sort });
        }
        return Page();
    }

    private async Task<int> DefaultTypeOrderAsync(CancellationToken ct)
    {
        var orders = Snapshot!.Types.Values.Select(t => t.Sort).ToList();
        if (Branch is not null)
        {
            try
            {
                var dev = await TypesRepository.LoadAsync(CreateGitLabClient(), AppSettings.TypesProjectPath!, TypesBranches.Dev, ct);
                orders.AddRange(dev.Types.Values.Select(t => t.Sort));
            }
            catch (GitLabApiException ex)
            {
                Warnings.Add($"Порядок в dev не удалось проверить: {ex.Message}");
            }
        }

        if (TypesEnv.ForBranch(AppSettings, TypesBranches.Dev) is { HasServer: true } test)
        {
            try
            {
                var metadata = await pilot.GetMetadataAsync(test.PilotUrl, Vault.GetPilotLogin(CurrentSession, test.Key), ct);
                orders.AddRange(metadata.Types.Select(t => t.Sort));
            }
            catch (PilotGatewayException ex)
            {
                Warnings.Add($"Начальный порядок рассчитан по GitLab. Порядок в Pilot не удалось проверить: {ex.Message}");
            }
        }
        return TypesValidator.NextTypeOrder(orders);
    }

    public Task<IActionResult> OnPostAsync(CancellationToken ct) => SaveAsync(ct, delete: false);

    public Task<IActionResult> OnPostDeleteAsync(CancellationToken ct) => SaveAsync(ct, delete: true);

    private async Task<IActionResult> SaveAsync(CancellationToken ct, bool delete)
    {
        ViewData["Section"] = "types";
        if (!GitLabConnected)
            return RedirectToGitLab();
        Normalize();
        if (!await LoadAsync(ct))
            return Page();
        if (GitLabGroupAccess.CannotChange(Access))
        {
            Errors.Add(ChangeDeniedText("типы", Access, Access?.GroupPath));
            return Page();
        }

        var snap = Snapshot!;
        var actions = new List<GitLabCommitAction>();
        string label;
        try
        {
            label = Kind switch
            {
                "state" => SaveState(snap, delete, actions),
                "machine" => SaveMachine(snap, delete, actions),
                _ => SaveType(snap, delete, actions),
            };
        }
        catch (TypesFormatException ex)
        {
            Errors.Add(ex.Message);
            return Page();
        }
        if (Errors.Count > 0)
            return Page();

        var validation = TypesValidator.Validate(snap);
        if (!validation.Ok)
        {
            Errors.AddRange(validation.Errors);
            return Page();
        }
        if (actions.Count == 0)
        {
            TempData["Flash"] = "Изменений нет - коммит не нужен.";
            return RedirectToPage("/Types/Index", new { view = ViewOf(Kind) });
        }

        try
        {
            var git = CreateGitLabClient();
            var project = AppSettings.TypesProjectPath!;
            var message = string.IsNullOrWhiteSpace(CommitMessage) ? label : CommitMessage.Trim();
            var branch = Branch;
            if (branch is null)
            {
                var title = string.IsNullOrWhiteSpace(NewChange) ? message : NewChange.Trim();
                var task = ModuleBranches.ParseTask(title, AppSettings.TaskPattern);
                branch = TypesBranches.Task(task, task is null ? title : title.Replace(task, "", StringComparison.OrdinalIgnoreCase));
                if (branch == TypesBranches.TaskPrefix || Branches.Any(b => b.Name == branch))
                    branch += "-" + DateTime.Now.ToString("MMdd-HHmm");
            }
            var commit = await TypesRepository.CommitToDraftAsync(git, project, branch, message, actions, ct);
            TempData["Flash"] = $"Сохранено в ветку изменения {branch} (коммит {commit.ShortId}): {message}. На тест попадёт после «Влить в тест».";
            return RedirectToPage("/Types/Index", new { view = ViewOf(Kind), branch });
        }
        catch (GitLabApiException ex)
        {
            Errors.Add(ex.Message);
            return Page();
        }
    }

    // ------------------------------------------------------------------ тип

    private string SaveType(TypesSnapshot snap, bool delete, List<GitLabCommitAction> actions)
    {
        var existing = IsNew ? null : snap.Types.GetValueOrDefault(Name!) ?? throw new TypesFormatException($"Тип «{Name}» не найден в {SourceRef}.");
        TypeDoc doc;
        if (Mode == "json")
        {
            doc = TypesJson.Read<TypeDoc>(Json ?? "", "JSON");
            if (existing is not null && doc.Name != existing.Name)
                throw new TypesFormatException("Имя типа менять нельзя: в Pilot это удаление и создание нового типа (значения атрибутов не переносятся).");
        }
        else
        {
            var name = existing?.Name ?? (NewName ?? "").Trim();
            doc = new TypeDoc
            {
                // Вид и порядок в форме не меняются (новому типу порядок рассчитан при открытии), группа состояний задаётся
                // у атрибута «Состояние» - у типа сохраняется прежнее значение (правка - только в JSON).
                Name = name, Title = Text(Form.Title), Kind = existing?.Kind ?? TypeKind.User, Sort = existing?.Sort ?? Form.Sort,
                HasFiles = Form.HasFiles, IsMountable = Form.IsMountable,
                IsService = Form.IsService, IsProject = Form.IsProject, IsVisibleOnStorage = Form.IsVisibleOnStorage, IsDeleted = existing?.IsDeleted ?? false,
                StateMachine = existing?.StateMachine, Children = Form.Children.Where(c => c.Length > 0).Distinct().ToList(),
                Configuration = EditForm.Config(Form.Configuration), Icon = existing?.Icon,
                Attributes = Form.Attributes.Where(a => !a.Remove && !string.IsNullOrWhiteSpace(a.Name))
                    .Select(a => a.ToDoc()).OrderBy(a => a.DisplaySortOrder).ToList(),
            };
        }
        if (existing is null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(doc.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                Errors.Add("Имя нового типа - латиница, цифры и «_», начиная с буквы (например contract_doc).");
            if (snap.Types.ContainsKey(doc.Name))
                Errors.Add($"Тип «{doc.Name}» уже есть.");
            if (Errors.Count > 0)
                return "";
        }
        if (delete)
            doc.IsDeleted = true;
        foreach (var a in doc.Attributes.Where(a => !System.Text.RegularExpressions.Regex.IsMatch(a.Name, @"^[A-Za-z_][A-Za-z0-9_]*$")))
            if (existing?.Attributes.All(x => x.Name != a.Name) != false)
                Errors.Add($"Атрибут «{a.Name}»: имя - латиница, цифры и «_».");

        var parentsChanged = Mode == "json" || delete ? 0 : SaveParents(snap, doc.Name, actions);
        if (existing is not null && TypesJson.Same(existing, doc))
            return parentsChanged > 0 ? $"Тип {doc.Name}: родительские типы" : "";
        var path = existing is not null && snap.SourcePaths.TryGetValue(existing, out var p) ? p : TypesSnapshot.TypePath(doc.Name);
        snap.Types[doc.Name] = doc;
        actions.Add(existing is null ? GitLabCommitAction.Create(path, TypesJson.Write(doc)) : GitLabCommitAction.Update(path, TypesJson.Write(doc)));

        if (existing is not null)
        {
            var removed = existing.Attributes.Select(a => a.Name).Except(doc.Attributes.Select(a => a.Name)).ToList();
            if (removed.Count > 0)
                Warnings.Add($"Удаляются атрибуты: {string.Join(", ", removed)} - значения в объектах останутся скрытыми.");
        }
        return delete ? $"Тип {doc.Name}: удалён" : existing is null ? $"Тип {doc.Name}: создан" : $"Тип {doc.Name}: изменения";
    }

    // ------------------------------------------------------------------ состояние

    private string SaveState(TypesSnapshot snap, bool delete, List<GitLabCommitAction> actions)
    {
        var existing = IsNew ? null : snap.States.GetValueOrDefault(Name!) ?? throw new TypesFormatException($"Состояние «{Name}» не найдено в {SourceRef}.");
        if (existing?.IsSystem == true)
            throw new TypesFormatException("Системное состояние Pilot не редактируется.");
        StateDoc doc;
        if (Mode == "json")
        {
            doc = TypesJson.Read<StateDoc>(Json ?? "", "JSON");
            if (existing is not null && doc.Name != existing.Name)
                throw new TypesFormatException("Имя состояния менять нельзя.");
        }
        else
        {
            doc = new StateDoc
            {
                Id = existing?.Id ?? Guid.NewGuid(), Name = existing?.Name ?? (NewName ?? "").Trim(), Title = Text(Form.Title), Color = Form.Color,
                IsCompletionState = Form.IsCompletionState, IsDeleted = existing?.IsDeleted ?? false, Icon = existing?.Icon,
            };
        }
        if (existing is null)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(doc.Name, @"^[A-Za-z_][A-Za-z0-9_]*$"))
                Errors.Add("Имя нового состояния - латиница, цифры и «_» (например onApproval).");
            if (snap.States.ContainsKey(doc.Name))
                Errors.Add($"Состояние «{doc.Name}» уже есть.");
            if (Errors.Count > 0)
                return "";
        }
        if (delete)
            doc.IsDeleted = true;
        if (existing is not null && TypesJson.Same(existing, doc))
            return "";
        var path = existing is not null && snap.SourcePaths.TryGetValue(existing, out var p) ? p : TypesSnapshot.StatePath(doc.Name);
        snap.States[doc.Name] = doc;
        actions.Add(existing is null ? GitLabCommitAction.Create(path, TypesJson.Write(doc)) : GitLabCommitAction.Update(path, TypesJson.Write(doc)));
        return delete ? $"Состояние {doc.Name}: удалено" : existing is null ? $"Состояние {doc.Name}: создано" : $"Состояние {doc.Name}: изменения";
    }

    // ------------------------------------------------------------------ группа состояний

    private string SaveMachine(TypesSnapshot snap, bool delete, List<GitLabCommitAction> actions)
    {
        var existing = IsNew ? null : snap.FindMachine(Name) ?? throw new TypesFormatException($"Группа состояний «{Name}» не найдена в {SourceRef}.");
        var oldPath = existing is null ? null : snap.MachinePath(existing);
        MachineDoc doc;
        if (Mode == "json")
        {
            doc = TypesJson.Read<MachineDoc>(Json ?? "", "JSON");
            doc.Id ??= existing?.Id;
        }
        else
        {
            doc = new MachineDoc { Id = existing?.Id ?? Guid.NewGuid(), Title = (Form.Title ?? "").Trim(), IsDeleted = existing?.IsDeleted ?? false };
            foreach (var row in Form.Transitions.Where(r => !string.IsNullOrWhiteSpace(r.From)))
            {
                var from = row.From!.Trim();
                var state = doc.States.FirstOrDefault(s => s.State == from);
                if (state is null)
                    doc.States.Add(state = new MachineStateDoc { State = from });
                if (!string.IsNullOrWhiteSpace(row.To))
                    state.Transitions.Add(new TransitionDoc { To = row.To.Trim(), DisplayName = Text(row.DisplayName), Positions = EditForm.List(row.Positions) });
            }
        }
        if (doc.Title.Length == 0)
        {
            Errors.Add("Укажите заголовок группы состояний.");
            return "";
        }
        if (snap.Machines.Any(m => !ReferenceEquals(m, existing) && m.Title == doc.Title))
        {
            Errors.Add($"Группа состояний «{doc.Title}» уже есть.");
            return "";
        }
        if (delete)
            doc.IsDeleted = true;
        if (existing is not null && TypesJson.Same(existing, doc))
            return "";

        if (existing is not null)
            snap.Machines[snap.Machines.IndexOf(existing)] = doc;
        else
            snap.Machines.Add(doc);
        var path = snap.MachinePath(doc);
        var text = TypesJson.Write(doc);
        actions.Add(existing is null ? GitLabCommitAction.Create(path, text)
            : oldPath == path ? GitLabCommitAction.Update(path, text)
            : GitLabCommitAction.Move(oldPath!, path, text));

        // Переименование: типы, ссылающиеся на группу по заголовку, меняются в том же коммите.
        if (existing is not null && existing.Title != doc.Title)
        {
            var oldRef = $"{{{{machine:{existing.Title}}}}}";
            var newRef = $"{{{{machine:{doc.Title}}}}}";
            foreach (var t in snap.Types.Values.ToList())
            {
                var json = TypesJson.Write(t);
                var changed = TypesJson.Read<TypeDoc>(json.Replace(oldRef, newRef), "тип");
                if (changed.StateMachine == existing.Title)
                    changed.StateMachine = doc.Title;
                if (TypesJson.Same(changed, t))
                    continue;
                var typePath = snap.SourcePaths.TryGetValue(t, out var tp) ? tp : TypesSnapshot.TypePath(t.Name);
                snap.Types[t.Name] = changed;
                actions.Add(GitLabCommitAction.Update(typePath, TypesJson.Write(changed)));
            }
        }
        return delete ? $"Группа состояний «{doc.Title}»: удалена"
            : existing is null ? $"Группа состояний «{doc.Title}»: создана"
            : existing.Title != doc.Title ? $"Группа состояний «{existing.Title}» / «{doc.Title}»" : $"Группа состояний «{doc.Title}»: изменения";
    }

    /// <summary>Типы, у которых <paramref name="name"/> в дочерних.</summary>
    public static List<string> ParentsOf(TypesSnapshot snap, string name) =>
        snap.Types.Values.Where(t => t.Name != name && t.Children.Contains(name)).Select(t => t.Name).ToList();

    /// <summary>Родительские типы из формы: добавить тип в «Дочерние» выбранных и убрать из остальных. Возвращает число изменённых типов.</summary>
    private int SaveParents(TypesSnapshot snap, string name, List<GitLabCommitAction> actions)
    {
        var wanted = Form.Parents.Where(x => x.Length > 0 && x != name).ToHashSet();
        var changed = 0;
        foreach (var t in snap.Types.Values.Where(t => t.Name != name).ToList())
        {
            var has = t.Children.Contains(name);
            if (has == wanted.Contains(t.Name))
                continue;
            var copy = TypesJson.Read<TypeDoc>(TypesJson.Write(t), "тип");
            if (has)
                copy.Children.RemoveAll(c => c == name);
            else
                copy.Children.Add(name);
            var typePath = snap.SourcePaths.TryGetValue(t, out var tp) ? tp : TypesSnapshot.TypePath(t.Name);
            snap.Types[t.Name] = copy;
            actions.Add(GitLabCommitAction.Update(typePath, TypesJson.Write(copy)));
            changed++;
        }
        return changed;
    }

    // ------------------------------------------------------------------

    private void Normalize()
    {
        Kind = Kind is "state" or "machine" ? Kind : "type";
        Mode = Mode == "json" ? "json" : "form";
    }

    private static string ViewOf(string kind) => kind switch { "state" => "states", "machine" => "machines", _ => "types" };

    private static string? Text(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    /// <summary>Содержимое выбранной ветки изменения или dev.</summary>
    private async Task<bool> LoadAsync(CancellationToken ct)
    {
        var project = AppSettings.TypesProjectPath;
        if (project is null)
        {
            Errors.Add("В настройках не указан репозиторий типов.");
            return false;
        }
        Access = await GetGroupAccessAsync(TypesRepository.GroupOf(project), fresh: false, ct);
        var git = CreateGitLabClient();
        try
        {
            Branches = (await git.ListBranchesAsync(project, null, ct) ?? new()).Where(b => TypesBranches.IsTask(b.Name))
                .OrderByDescending(b => b.Commit?.CommittedDate).ToList();
            if (Branch is not null && Branches.All(b => b.Name != Branch))
                Branch = null;
            SourceRef = Branch ?? TypesBranches.Dev;
            Snapshot = await TypesRepository.LoadAsync(git, project, SourceRef, ct);
            return true;
        }
        catch (GitLabApiException ex)
        {
            Errors.Add(ex.Message);
            return false;
        }
    }

    /// <summary>Заполнить форму и JSON из репозитория. false - элемента нет.</summary>
    private bool Fill()
    {
        var snap = Snapshot!;
        switch (Kind)
        {
            case "state":
                if (snap.States.GetValueOrDefault(Name!) is not { } s)
                    return false;
                Form = new EditForm { Title = s.Title, Color = s.Color, IsCompletionState = s.IsCompletionState };
                Json = TypesJson.Write(s);
                IsDeleted = s.IsDeleted;
                return true;
            case "machine":
                if (snap.FindMachine(Name) is not { } m)
                    return false;
                Form = new EditForm
                {
                    Title = m.Title,
                    Transitions = m.States.SelectMany(st => st.Transitions.Count == 0
                        ? new[] { new TransitionRow { From = st.State } }
                        : st.Transitions.Select(t => new TransitionRow { From = st.State, To = t.To, DisplayName = t.DisplayName, Positions = string.Join(", ", t.Positions) })).ToList(),
                };
                Json = TypesJson.Write(m);
                IsDeleted = m.IsDeleted;
                return true;
            default:
                if (snap.Types.GetValueOrDefault(Name!) is not { } t)
                    return false;
                Form = new EditForm
                {
                    Title = t.Title, TypeKind = t.Kind, Sort = t.Sort, HasFiles = t.HasFiles, IsMountable = t.IsMountable, IsService = t.IsService,
                    IsProject = t.IsProject, IsVisibleOnStorage = t.IsVisibleOnStorage, StateMachine = t.StateMachine, Children = t.Children.ToList(),
                    Parents = ParentsOf(snap, t.Name),
                    Configuration = t.Configuration, Attributes = t.Attributes.Select(AttributeRow.From).ToList(),
                };
                Json = TypesJson.Write(t);
                IsDeleted = t.IsDeleted;
                return true;
        }
    }
}
