using System.Text.Json;
using System.Text.Json.Serialization;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.Types;

/// <summary>Что и когда записано на сервер контура из репозитория типов.</summary>
public sealed class TypesDeployment
{
    public string Env { get; set; } = "";
    public string Reference { get; set; } = "";
    public string Sha { get; set; } = "";
    public string? Version { get; set; }
    public DateTimeOffset At { get; set; }
    public string By { get; set; } = "";
    public int Changes { get; set; }
}

/// <summary>Куда привязано изменение типов: клиентский/серверный модуль или автоматизация.</summary>
public enum TypesTargetKind
{
    Client,
    Server,
    Automation,
}

/// <summary>
/// Связь изменения типов (коммит dev или ветка types/…) с коммитом или веткой задачи модуля/автоматизации:
/// когда этот коммит модуля уходит на стейдж (в release/X.Y.Z) и на прод, изменение типов переносится в release / main
/// и записывается на сервер в той же операции, перед модулем.
/// </summary>
public sealed class TypesChangeLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Коммит изменения в dev репозитория типов (после squash-merge ветки).</summary>
    public string? TypesSha { get; set; }

    /// <summary>Ветка types/… - пока изменение не влито в dev; после вливания заполняется <see cref="TypesSha"/>.</summary>
    public string? TypesBranch { get; set; }

    public string TypesTitle { get; set; } = "";

    public TypesTargetKind TargetKind { get; set; }
    public string TargetProject { get; set; } = "";
    public string TargetName { get; set; } = "";

    /// <summary>Коммит модуля/автоматизации в dev.</summary>
    public string? TargetSha { get; set; }

    /// <summary>Ветка задачи модуля - пока не влита в dev; коммит находится по её влитому MR.</summary>
    public string? TargetBranch { get; set; }

    public string TargetTitle { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Изменение типов ушло на стейдж вместе с модулем (или уже было там).</summary>
    public DateTimeOffset? StageAt { get; set; }

    public DateTimeOffset? ProdAt { get; set; }
    public string? Error { get; set; }

    [JsonIgnore]
    public bool Done => ProdAt is not null;

    [JsonIgnore]
    public string TargetRef => TargetSha is { Length: > 8 } s ? s[..8] : TargetSha ?? TargetBranch ?? "";

    public bool IsChange(string? sha, string? branch) =>
        sha is not null && TypesSha is not null && (sha.StartsWith(TypesSha, StringComparison.Ordinal) || TypesSha.StartsWith(sha, StringComparison.Ordinal)) ||
        branch is not null && TypesBranch == branch && TypesSha is null;
}

/// <summary>data/types/state.json - что записано на серверы и связи изменений типов с модулями/автоматизациями.</summary>
public sealed class TypesStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly object _lock = new();
    private State _state;

    private sealed class State
    {
        public Dictionary<string, TypesDeployment> Deployments { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<TypesChangeLink> ChangeLinks { get; set; } = new();
    }

    public TypesStore(IOptions<AppOptions> options)
    {
        _dir = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "types");
        var path = Path.Combine(_dir, "state.json");
        _state = File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path), Json) ?? new State() : new State();
    }

    public TypesDeployment? Deployment(string env)
    {
        lock (_lock)
            return _state.Deployments.TryGetValue(env, out var d) ? d : null;
    }

    public void RecordDeployment(TypesDeployment deployment)
    {
        lock (_lock)
        {
            _state.Deployments[deployment.Env] = deployment;
            Save();
        }
    }

    public List<TypesChangeLink> ChangeLinks()
    {
        lock (_lock)
            return _state.ChangeLinks.OrderByDescending(l => l.CreatedAt).Select(Copy).ToList();
    }

    /// <summary>Невыпущенные связи модуля/автоматизации.</summary>
    public List<TypesChangeLink> OpenLinksFor(TypesTargetKind kind, string project)
    {
        lock (_lock)
            return _state.ChangeLinks.Where(l => !l.Done && l.TargetKind == kind &&
                                                 string.Equals(l.TargetProject, project, StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.CreatedAt).Select(Copy).ToList();
    }

    public void AddLink(TypesChangeLink link)
    {
        lock (_lock)
        {
            _state.ChangeLinks.Add(Copy(link));
            // Выпущенные связи старше 90 дней не нужны.
            _state.ChangeLinks.RemoveAll(l => l.Done && l.ProdAt < DateTimeOffset.UtcNow.AddDays(-90));
            Save();
        }
    }

    public bool RemoveLink(Guid id)
    {
        lock (_lock)
        {
            var removed = _state.ChangeLinks.RemoveAll(l => l.Id == id) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    public void UpdateLink(Guid id, Action<TypesChangeLink> change)
    {
        lock (_lock)
        {
            if (_state.ChangeLinks.FirstOrDefault(l => l.Id == id) is not { } link)
                return;
            change(link);
            Save();
        }
    }

    /// <summary>Ветка types/… влита в dev одним коммитом - её связи теперь указывают на этот коммит.</summary>
    public void BranchMerged(string branch, string sha, string title)
    {
        lock (_lock)
        {
            var changed = false;
            foreach (var l in _state.ChangeLinks.Where(l => l.TypesBranch == branch && l.TypesSha is null))
            {
                l.TypesSha = sha;
                l.TypesTitle = title;
                changed = true;
            }
            if (changed)
                Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "state.json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
        File.Move(tmp, path, overwrite: true);
    }

    private static TypesChangeLink Copy(TypesChangeLink l) =>
        JsonSerializer.Deserialize<TypesChangeLink>(JsonSerializer.Serialize(l, Json), Json)!;
}
