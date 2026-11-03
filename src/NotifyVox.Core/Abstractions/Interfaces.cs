using NotifyVox.Core.Configuration;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Abstractions;

/// <summary>
/// Windows 通知監視インターフェース (SPEC §4.1, §6.1)
/// </summary>
public interface INotificationWatcher : IDisposable
{
    NotificationAccessState AccessState { get; }
    string? LastErrorMessage { get; }

    event EventHandler<NotificationEvent>? NotificationReceived;
    event EventHandler<NotificationAccessState>? AccessStateChanged;

    Task<NotificationAccessState> InitializeAndStartAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// 音声チャンク逐次再生インターフェース (SPEC §3.1, §6.1)
/// </summary>
public interface IAudioPlayer : IDisposable
{
    /// <summary>
    /// 利用可能な出力デバイス一覧を取得する（空 ID は既定デバイスを表す）。
    /// </summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>
    /// WAV ヘッダと後続の PCM ストリームを受け取り、1 チャンク目から逐次再生する。
    /// <paramref name="interruptAtChunkToken"/> が発火された場合、現在チャンクの投入境界で速やかに中断する (SPEC F14)。
    /// <paramref name="abortToken"/> が発火された場合、再生中バッファも含めて即時停止する (SPEC F16, §6.5)。
    /// </summary>
    Task PlayPcmStreamAsync(
        WavHeader header,
        Stream pcmStream,
        float volume,
        string? outputDeviceId,
        CancellationToken interruptAtChunkToken,
        CancellationToken abortToken);

    /// <summary>
    /// 現在再生中の音声を即時停止する。
    /// </summary>
    void StopPlayback();
}

/// <summary>
/// VOICEVOX エンジン HTTP クライアントインターフェース (SPEC §4.5, §6.1)
/// </summary>
public interface IVoicevoxClient
{
    Task<string?> GetVersionAsync(string endpoint, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<VoicevoxSpeakerInfo>> GetSpeakersAsync(string endpoint, CancellationToken cancellationToken = default);

    /// <summary>
    /// 1 文の音声合成（ストリーミング優先、非対応時は一括合成へ自動フォールバック）を開始し、
    /// WAV ヘッダ解析済みの PCM ストリームを <paramref name="streamConsumer"/> に渡す。
    /// </summary>
    Task SynthesizeSentenceAsync(
        string endpoint,
        string sentence,
        VoicevoxSynthesisOptions options,
        Func<WavHeader, Stream, CancellationToken, Task> streamConsumer,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// フルスクリーンアプリ実行判定インターフェース (SPEC §4.2 F12)
/// </summary>
public interface IFullscreenDetector
{
    bool IsFullscreenAppRunning();
}

/// <summary>
/// 設定永続化・変更通知ストアインターフェース (SPEC §6.1, §6.3, F25)
/// </summary>
public interface ISettingsStore
{
    AppSettings Current { get; }
    event EventHandler<AppSettings>? SettingsChanged;

    void Save(AppSettings settings);
    void Update(Func<AppSettings, AppSettings> mutator);
    void RegisterDiscoveredApp(string appId, string appName);
}

/// <summary>
/// 読み上げ履歴ストアインターフェース (SPEC §4.7 F27, §6.1)
/// </summary>
public interface IHistoryStore
{
    event EventHandler<HistoryEntry>? EntryAdded;

    Task AppendAsync(HistoryEntry entry, CancellationToken cancellationToken = default);
    IReadOnlyList<HistoryEntry> GetRecent(int maxCount = 200);
    Task PruneExpiredAsync(int retentionDays, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Windows 起動時自動起動制御インターフェース (SPEC §4.6-4)
/// </summary>
public interface IStartupTaskService
{
    Task<bool> IsEnabledAsync();
    Task<(bool Enabled, string? Message)> SetEnabledAsync(bool enable);
}
