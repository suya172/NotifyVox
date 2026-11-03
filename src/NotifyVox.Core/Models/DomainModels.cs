namespace NotifyVox.Core.Models;

/// <summary>
/// 読み上げ優先度 (SPEC §4.2 F7, §4.3 F14, §6.2)
/// </summary>
public enum Priority
{
    Normal = 0,
    High = 1
}

/// <summary>
/// Windows 通知アクセス許可の状態 (SPEC §4.1 F1, F5)
/// </summary>
public enum NotificationAccessState
{
    Unknown = 0,
    Allowed = 1,
    Denied = 2,
    Error = 3
}

/// <summary>
/// 正規化された通知イベント (SPEC §6.2)
/// </summary>
public sealed record NotificationEvent(
    string AppId,
    string AppName,
    string? Title,
    string Body,
    DateTimeOffset ReceivedAt
);

/// <summary>
/// キューへ投入される読み上げリクエスト (SPEC §6.2)
/// </summary>
public sealed record SpeechRequest(
    NotificationEvent Source,
    string Text,
    int SpeakerId,
    Priority Priority,
    bool IsTest = false
);

/// <summary>
/// 読み上げ履歴エントリ (SPEC §4.7 F27)
/// </summary>
public sealed record HistoryEntry(
    DateTimeOffset Timestamp,
    string AppId,
    string AppName,
    string? Title,
    string Body,
    bool WasSpoken,
    string? Reason = null
);

/// <summary>
/// WAV ストリーム先頭 44 バイトから解析した PCM フォーマット情報
/// </summary>
public readonly record struct WavHeader(
    int SampleRate,
    int Channels,
    int BitsPerSample
);

/// <summary>
/// VOICEVOX 話者スタイル情報
/// </summary>
public sealed record VoicevoxStyleInfo(
    int Id,
    string Name,
    string Type
)
{
    public bool SupportsStreaming => string.Equals(Type, "streaming_talk", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// VOICEVOX キャラクター（話者）情報
/// </summary>
public sealed record VoicevoxSpeakerInfo(
    string Name,
    string SpeakerUuid,
    IReadOnlyList<VoicevoxStyleInfo> Styles
);

/// <summary>
/// 音声出力デバイス情報 (SPEC §4.6-2)
/// </summary>
public sealed record AudioDeviceInfo(
    string Id,
    string Name
);

/// <summary>
/// VOICEVOX 合成オプション (SPEC §4.5 F23)
/// </summary>
public sealed record VoicevoxSynthesisOptions(
    int SpeakerId,
    int DefaultSpeakerId,
    double SpeedScale = 1.05,
    double PitchScale = 0.0,
    double IntonationScale = 1.0
);
