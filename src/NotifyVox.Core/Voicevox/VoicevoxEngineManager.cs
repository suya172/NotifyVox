using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Voicevox;

/// <summary>
/// VOICEVOX エンジンの接続状態監視・起動時自動起動・30秒周期の再接続試行を担うマネージャ (SPEC §4.5 F21, §7)
/// </summary>
public sealed class VoicevoxEngineManager : IDisposable
{
    private static readonly TimeSpan ReconnectInterval = TimeSpan.FromSeconds(30); // SPEC §7

    private readonly IVoicevoxClient _client;
    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<VoicevoxEngineManager> _logger;
    private readonly Lock _sync = new();

    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _isConnected;
    private string? _engineVersion;
    private string _lastCheckedEndpoint = string.Empty;
    private IReadOnlyList<VoicevoxSpeakerInfo> _speakers = [];
    private bool _startupNoticeRaised;

    public VoicevoxEngineManager(
        IVoicevoxClient client,
        ISettingsStore settingsStore,
        ILogger<VoicevoxEngineManager> logger)
    {
        _client = client;
        _settingsStore = settingsStore;
        _logger = logger;
        _settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public bool IsConnected
    {
        get { lock (_sync) { return _isConnected; } }
    }

    public string? EngineVersion
    {
        get { lock (_sync) { return _engineVersion; } }
    }

    public IReadOnlyList<VoicevoxSpeakerInfo> Speakers
    {
        get { lock (_sync) { return _speakers; } }
    }

    public event EventHandler<bool>? ConnectionStateChanged;
    public event EventHandler<IReadOnlyList<VoicevoxSpeakerInfo>>? SpeakersUpdated;

    /// <summary>
    /// 起動時にエンジン未接続かつ自動起動できない場合に1回発火し、UI / トレイで導入・起動案内を出す (SPEC F21, §9)。
    /// </summary>
    public event EventHandler? ManualStartGuidanceRequested;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var connected = await CheckConnectionOnceAsync(cancellationToken).ConfigureAwait(false);
        if (!connected)
        {
            var vv = _settingsStore.Current.Voicevox;
            if (vv.AutoStartEngine && !string.IsNullOrWhiteSpace(vv.EngineExePath) && File.Exists(vv.EngineExePath))
            {
                connected = await TryAutoStartEngineAsync(vv.EngineExePath, cancellationToken).ConfigureAwait(false);
            }

            if (!connected && !_startupNoticeRaised)
            {
                _startupNoticeRaised = true;
                ManualStartGuidanceRequested?.Invoke(this, EventArgs.Empty);
            }
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token), CancellationToken.None);
    }

    public async Task<bool> RefreshNowAsync(CancellationToken cancellationToken = default)
    {
        return await CheckConnectionOnceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// パイプライン実行中に通信失敗した際、即座に未接続状態へ遷移させるために呼び出す。
    /// </summary>
    public void ReportCommunicationFailure()
    {
        UpdateConnectionState(false, null);
    }

    private async Task<bool> TryAutoStartEngineAsync(string exePath, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("VOICEVOX エンジンの自動起動を試みます: {ExePath}", exePath);
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? string.Empty,
                UseShellExecute = true
            });

            // 最大 15 秒間起動完了を待機
            for (var i = 0; i < 15; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (await CheckConnectionOnceAsync(cancellationToken).ConfigureAwait(false))
                {
                    _logger.LogInformation("VOICEVOX エンジンの自動起動と接続に成功しました。");
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "VOICEVOX エンジンの自動起動に失敗しました: {ExePath}", exePath);
        }

        return false;
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(ReconnectInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await CheckConnectionOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task<bool> CheckConnectionOnceAsync(CancellationToken cancellationToken)
    {
        var endpoint = _settingsStore.Current.Voicevox.Endpoint;
        lock (_sync)
        {
            _lastCheckedEndpoint = endpoint;
        }
        try
        {
            var version = await _client.GetVersionAsync(endpoint, cancellationToken).ConfigureAwait(false);
            var wasConnected = IsConnected;
            UpdateConnectionState(true, version);

            if (!wasConnected || Speakers.Count == 0)
            {
                var speakers = await _client.GetSpeakersAsync(endpoint, cancellationToken).ConfigureAwait(false);
                lock (_sync)
                {
                    _speakers = speakers;
                }
                SpeakersUpdated?.Invoke(this, speakers);
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (IsConnected)
            {
                _logger.LogWarning(ex, "VOICEVOX エンジンとの接続が失われました ({Endpoint})。30秒ごとに再接続を試行します。", endpoint);
            }
            UpdateConnectionState(false, null);
            return false;
        }
    }

    private void UpdateConnectionState(bool connected, string? version)
    {
        bool changed;
        lock (_sync)
        {
            changed = _isConnected != connected;
            _isConnected = connected;
            _engineVersion = version;
        }

        if (changed)
        {
            if (connected)
            {
                _logger.LogInformation("VOICEVOX エンジンへ接続しました (Version: {Version})", version);
            }
            ConnectionStateChanged?.Invoke(this, connected);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        bool endpointChanged;
        lock (_sync)
        {
            endpointChanged = !string.Equals(_lastCheckedEndpoint, settings.Voicevox.Endpoint, StringComparison.OrdinalIgnoreCase);
        }

        if (endpointChanged)
        {
            _ = Task.Run(() => CheckConnectionOnceAsync(CancellationToken.None));
        }
    }

    public void Dispose()
    {
        _settingsStore.SettingsChanged -= OnSettingsChanged;
        if (_monitorCts is not null)
        {
            try { _monitorCts.Cancel(); } catch (ObjectDisposedException) { }
            _monitorCts.Dispose();
        }
    }
}
