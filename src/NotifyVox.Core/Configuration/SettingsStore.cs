using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;

namespace NotifyVox.Core.Configuration;

/// <summary>
/// NotifyVox の永続化パス解決ヘルパー (SPEC §6.4)
/// </summary>
public static class NotifyVoxPaths
{
    public static string SettingsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NotifyVox");

    public static string LocalDataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NotifyVox");

    public static string DefaultSettingsFilePath =>
        Path.Combine(SettingsDirectory, "settings.json");

    public static string DefaultHistoryFilePath =>
        Path.Combine(LocalDataDirectory, "history.jsonl");

    public static string DefaultLogsDirectory =>
        Path.Combine(LocalDataDirectory, "logs");
}

/// <summary>
/// settings.json のスレッドセーフな読み書きと即時反映通知を担うストア (SPEC §6.1, §6.3, F25)
/// </summary>
public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly string _filePath;
    private readonly ILogger<SettingsStore> _logger;
    private readonly Lock _sync = new();
    private AppSettings _current;

    public SettingsStore(ILogger<SettingsStore> logger, string? filePath = null)
    {
        _logger = logger;
        _filePath = filePath ?? NotifyVoxPaths.DefaultSettingsFilePath;
        _current = LoadOrCreateDefault();
    }

    public AppSettings Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    public event EventHandler<AppSettings>? SettingsChanged;

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_sync)
        {
            _current = settings;
            PersistUnsafe(_current);
        }

        SettingsChanged?.Invoke(this, settings);
    }

    public void Update(Func<AppSettings, AppSettings> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);

        AppSettings updated;
        lock (_sync)
        {
            updated = mutator(_current);
            if (Equals(_current, updated))
            {
                return;
            }

            _current = updated;
            PersistUnsafe(_current);
        }

        SettingsChanged?.Invoke(this, updated);
    }

    /// <summary>
    /// SPEC §4.6-1: 通知を受け取ったアプリを自動収集し、未登録であれば無効状態 (Enabled=false) で候補リストに追加する。
    /// 表示名が変わった場合は表示名のみ更新する。
    /// </summary>
    public void RegisterDiscoveredApp(string appId, string appName)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return;
        }

        AppSettings? changed = null;
        lock (_sync)
        {
            var existingIndex = -1;
            for (var i = 0; i < _current.Apps.Count; i++)
            {
                if (string.Equals(_current.Apps[i].AppId, appId, StringComparison.OrdinalIgnoreCase))
                {
                    existingIndex = i;
                    break;
                }
            }

            if (existingIndex < 0)
            {
                var nextApps = _current.Apps.ToList();
                nextApps.Add(new AppRule
                {
                    AppId = appId,
                    AppName = appName,
                    Enabled = false
                });
                _current = _current with { Apps = nextApps };
                PersistUnsafe(_current);
                changed = _current;
                _logger.LogInformation("新規通知元アプリを候補登録しました: {AppName} ({AppId})", appName, appId);
            }
            else if (!string.IsNullOrWhiteSpace(appName) &&
                     !string.Equals(_current.Apps[existingIndex].AppName, appName, StringComparison.Ordinal))
            {
                var nextApps = _current.Apps.ToList();
                nextApps[existingIndex] = nextApps[existingIndex] with { AppName = appName };
                _current = _current with { Apps = nextApps };
                PersistUnsafe(_current);
                changed = _current;
            }
        }

        if (changed is not null)
        {
            SettingsChanged?.Invoke(this, changed);
        }
    }

    private AppSettings LoadOrCreateDefault()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (!File.Exists(_filePath))
            {
                var defaults = new AppSettings();
                PersistUnsafe(defaults);
                return defaults;
            }

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (loaded is not null)
            {
                return loaded;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "設定ファイルの読み込みに失敗したため既定値で復旧します: {Path}", _filePath);
            TryBackupCorruptedFile();
        }

        var fallback = new AppSettings();
        PersistUnsafe(fallback);
        return fallback;
    }

    private void PersistUnsafe(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "設定ファイルの保存に失敗しました: {Path}", _filePath);
        }
    }

    private void TryBackupCorruptedFile()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var backup = $"{_filePath}.corrupt.{DateTimeOffset.Now:yyyyMMddHHmmss}.bak";
                File.Copy(_filePath, backup, overwrite: true);
            }
        }
        catch
        {
            // バックアップ失敗でもプロセスは落とさない (SPEC §5)
        }
    }
}
