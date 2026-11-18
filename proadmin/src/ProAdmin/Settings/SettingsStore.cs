using System.Text.Json;
using ProAdmin.Infrastructure;
using Microsoft.Extensions.Options;

namespace ProAdmin.Settings;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly ILogger<SettingsStore> _logger;
    private readonly DefaultSettingsOptions _defaults;
    private readonly object _lock = new();
    private AdminSettings _current;

    public SettingsStore(IOptions<AppOptions> options, ILogger<SettingsStore> logger)
    {
        _logger = logger;
        _defaults = options.Value.Defaults;
        _path = Path.Combine(Path.GetFullPath(options.Value.DataDirectory), "settings.json");
        _current = Load();
    }

    /// <summary>Первый запуск: общие настройки из переменных окружения (.env), сразу сохраняются в файл.</summary>
    private AdminSettings Seed()
    {
        var settings = new AdminSettings();
        settings.Normalize();
        void Apply(string key, string? url)
        {
            if (!string.IsNullOrWhiteSpace(url))
                settings.Find(key)!.PilotUrl = url.Trim();
        }
        Apply(AdminSettings.Test, _defaults.TestPilotUrl);
        Apply(AdminSettings.Prod, _defaults.ProdPilotUrl);
        if (!string.IsNullOrWhiteSpace(_defaults.GitLabGroupUrl))
            settings.GitLabGroupUrl = _defaults.GitLabGroupUrl.Trim();
        if (!string.IsNullOrWhiteSpace(_defaults.ReportsGitLabGroupUrl))
            settings.ReportsGitLabGroupUrl = _defaults.ReportsGitLabGroupUrl.Trim();
        if (!string.IsNullOrWhiteSpace(_defaults.ModulesGitLabGroupUrl))
            settings.ModulesGitLabGroupUrl = _defaults.ModulesGitLabGroupUrl.Trim();
        if (!string.IsNullOrWhiteSpace(_defaults.ServerModulesGitLabGroupUrl))
            settings.ServerModulesGitLabGroupUrl = _defaults.ServerModulesGitLabGroupUrl.Trim();
        if (!string.IsNullOrWhiteSpace(_defaults.TypesGitLabProjectUrl))
            settings.TypesGitLabProjectUrl = _defaults.TypesGitLabProjectUrl.Trim();
        if (!string.IsNullOrWhiteSpace(_defaults.StagePilotUrl))
            settings.StagePilotUrl = _defaults.StagePilotUrl.Trim();
        settings.UpdatedBy = "начальные настройки (.env)";
        settings.UpdatedAt = DateTimeOffset.UtcNow;

        try
        {
            Save(settings);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось записать {Path}", _path);
        }
        return settings;
    }

    public AdminSettings Current
    {
        get { lock (_lock) return _current.Clone(); }
    }

    public void Save(AdminSettings settings)
    {
        settings.Normalize();
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
            File.Move(tmp, _path, overwrite: true);
            _current = settings.Clone();
        }
    }

    private AdminSettings Load()
    {
        if (!File.Exists(_path))
            return Seed();

        AdminSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AdminSettings>(File.ReadAllText(_path)) ?? new AdminSettings();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось прочитать {Path}, используются настройки по умолчанию", _path);
            settings = new AdminSettings();
        }
        settings.Normalize();
        return settings;
    }
}
