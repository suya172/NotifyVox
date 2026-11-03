using NotifyVox.Core.Models;

namespace NotifyVox.Core.Configuration;

/// <summary>
/// アプリ設定ルートモデル (SPEC §6.3 settings.json 準拠)
/// </summary>
public sealed record AppSettings
{
    public int Version { get; init; } = 1;
    public VoicevoxSettings Voicevox { get; init; } = new();
    public ReadingSettings Reading { get; init; } = new();
    public IReadOnlyList<AppRule> Apps { get; init; } = [];
    public MuteSettings Mute { get; init; } = new();
    public HistorySettings History { get; init; } = new();
}

public sealed record VoicevoxSettings
{
    public string Endpoint { get; init; } = "http://127.0.0.1:50021";
    public string EngineExePath { get; init; } = string.Empty;
    public bool AutoStartEngine { get; init; }
    public int DefaultStyleId { get; init; } = 3; // ずんだもん (ノーマル)
    public double SpeedScale { get; init; } = 1.05;
    public double PitchScale { get; init; }
    public double IntonationScale { get; init; } = 1.0;
    public float Volume { get; init; } = 0.8f;
    public string OutputDevice { get; init; } = string.Empty;

    /// <summary>
    /// SPEC §8: エンドポイントが localhost / ループバックアドレスかどうかを検証する。
    /// </summary>
    public static bool IsLoopbackEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback;
    }
}

public sealed record ReadingSettings
{
    public string Template { get; init; } = "{app}。{title}。{body}";
    public int MaxCharsPerNotification { get; init; } = 200;
    public bool AppendOmittedSuffix { get; init; } = true;
    public bool SpeakUrlAsWord { get; init; } = true;
    public bool ReadUnreadOnStartup { get; init; }
    public int MaxUnreadOnStartup { get; init; } = 3;
}

public sealed record AppRule
{
    public required string AppId { get; init; }
    public string AppName { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public int? StyleId { get; init; }
    public Priority Priority { get; init; } = Priority.Normal;
    public string? Template { get; init; }
    public IReadOnlyList<string> MuteKeywords { get; init; } = [];
}

public sealed record MuteSettings
{
    public IReadOnlyList<string> Keywords { get; init; } = [];
    public QuietHoursSettings QuietHours { get; init; } = new();
    public bool MuteWhenFullscreen { get; init; }
    public string Hotkey { get; init; } = "Ctrl+Alt+M";
}

public sealed record QuietHoursSettings
{
    public bool Enabled { get; init; }
    public string Start { get; init; } = "23:00";
    public string End { get; init; } = "07:00";
}

public sealed record HistorySettings
{
    public int RetentionDays { get; init; } = 7;
}
