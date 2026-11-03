using System.Globalization;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Filtering;

public enum FilterAction
{
    Allow = 0,
    Summarize = 1,
    Reject = 2
}

public sealed record FilterDecision(
    FilterAction Action,
    int SpeakerId = 0,
    Priority Priority = Priority.Normal,
    string? AppTemplateOverride = null,
    string? SummaryText = null,
    string? RejectReason = null
)
{
    public bool ShouldSpeak => Action is FilterAction.Allow or FilterAction.Summarize;

    public static FilterDecision Allow(int speakerId, Priority priority, string? templateOverride) =>
        new(FilterAction.Allow, speakerId, priority, templateOverride);

    public static FilterDecision Summarize(string summaryText, int speakerId, Priority priority) =>
        new(FilterAction.Summarize, speakerId, priority, SummaryText: summaryText);

    public static FilterDecision Reject(string reason) =>
        new(FilterAction.Reject, RejectReason: reason);
}

/// <summary>
/// 通知の許可リスト・キーワード・重複排除・連投間引き・おやすみ時間・フルスクリーン判定を担うポリシー (SPEC §4.2 F6-F12)
/// </summary>
public sealed class FilterPolicy
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromSeconds(10); // SPEC F9
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(30);         // SPEC F10
    private static readonly int BurstThreshold = 4;                                  // SPEC F10: 30秒以内に4件以上
    private static readonly TimeSpan SummarySuppressionDuration = TimeSpan.FromSeconds(60); // SPEC F10: 以降60秒間要約モード

    private readonly TimeProvider _timeProvider;
    private readonly IFullscreenDetector? _fullscreenDetector;
    private readonly Lock _sync = new();

    private readonly Dictionary<DedupKey, DateTimeOffset> _recentDedup = [];
    private readonly Dictionary<string, Queue<DateTimeOffset>> _appBurstTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _appSummaryUntil = new(StringComparer.OrdinalIgnoreCase);
    private bool _isManuallyMuted;

    public FilterPolicy(TimeProvider? timeProvider = null, IFullscreenDetector? fullscreenDetector = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _fullscreenDetector = fullscreenDetector;
    }

    public bool IsManuallyMuted
    {
        get
        {
            lock (_sync)
            {
                return _isManuallyMuted;
            }
        }
        set
        {
            bool changed;
            lock (_sync)
            {
                changed = _isManuallyMuted != value;
                _isManuallyMuted = value;
            }

            if (changed)
            {
                ManualMuteChanged?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<bool>? ManualMuteChanged;

    public bool ToggleManualMute()
    {
        var next = !IsManuallyMuted;
        IsManuallyMuted = next;
        return next;
    }

    public FilterDecision Evaluate(NotificationEvent notification, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(settings);

        // SPEC §7: タイトル・本文が両方空なら読み上げない
        if (string.IsNullOrWhiteSpace(notification.Title) && string.IsNullOrWhiteSpace(notification.Body))
        {
            return FilterDecision.Reject("空の通知");
        }

        var now = _timeProvider.GetLocalNow();

        lock (_sync)
        {
            // SPEC F11: 手動ミュート
            if (_isManuallyMuted)
            {
                return FilterDecision.Reject("手動ミュート中");
            }

            // SPEC F11: おやすみモード（時間帯ミュート）
            if (IsInQuietHours(now.TimeOfDay, settings.Mute.QuietHours))
            {
                return FilterDecision.Reject("おやすみ時間帯");
            }

            // SPEC F12: フルスクリーン中ミュート
            if (settings.Mute.MuteWhenFullscreen && _fullscreenDetector?.IsFullscreenAppRunning() == true)
            {
                return FilterDecision.Reject("フルスクリーンアプリ実行中");
            }

            // SPEC F6: アプリ許可リスト方式（既定は全アプリ OFF）
            var appRule = FindAppRule(settings.Apps, notification.AppId);
            if (appRule is null || !appRule.Enabled)
            {
                return FilterDecision.Reject("許可リスト対象外");
            }

            // SPEC F8: キーワードミュート（全体＋アプリ単位の2階層）
            if (MatchesAnyKeyword(notification, settings.Mute.Keywords) ||
                MatchesAnyKeyword(notification, appRule.MuteKeywords))
            {
                return FilterDecision.Reject("ミュートキーワード一致");
            }

            // SPEC F9: 重複排除（同一アプリ・同一タイトル・同一本文が 10 秒以内に再着した場合は 1 回だけ読む）
            PruneDedupUnsafe(now);
            var dedupKey = new DedupKey(
                notification.AppId,
                notification.Title ?? string.Empty,
                notification.Body ?? string.Empty);

            if (_recentDedup.TryGetValue(dedupKey, out var lastSeen) &&
                now - lastSeen < DeduplicationWindow)
            {
                return FilterDecision.Reject("重複通知 (10秒以内)");
            }

            _recentDedup[dedupKey] = now;

            var speakerId = appRule.StyleId ?? settings.Voicevox.DefaultStyleId;
            var priority = appRule.Priority;

            // SPEC F10: 間引き（同一アプリから 30 秒以内に 4 件以上の通知が来た場合、以降 60 秒間は要約モード）
            if (_appSummaryUntil.TryGetValue(notification.AppId, out var summaryUntil))
            {
                if (now < summaryUntil)
                {
                    return FilterDecision.Reject("連投要約モード抑制中");
                }

                _appSummaryUntil.Remove(notification.AppId);
            }

            if (!_appBurstTimestamps.TryGetValue(notification.AppId, out var timestamps))
            {
                timestamps = new Queue<DateTimeOffset>();
                _appBurstTimestamps[notification.AppId] = timestamps;
            }

            while (timestamps.Count > 0 && now - timestamps.Peek() >= BurstWindow)
            {
                timestamps.Dequeue();
            }

            timestamps.Enqueue(now);

            if (timestamps.Count >= BurstThreshold)
            {
                var count = timestamps.Count;
                timestamps.Clear();
                _appSummaryUntil[notification.AppId] = now + SummarySuppressionDuration;
                var appName = string.IsNullOrWhiteSpace(notification.AppName) ? "アプリ" : notification.AppName;
                return FilterDecision.Summarize($"{appName}から{count}件の通知です", speakerId, priority);
            }

            return FilterDecision.Allow(speakerId, priority, appRule.Template);
        }
    }

    internal static bool IsInQuietHours(TimeSpan currentLocalTime, QuietHoursSettings quietHours)
    {
        if (!quietHours.Enabled)
        {
            return false;
        }

        if (!TimeOnly.TryParseExact(quietHours.Start, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) ||
            !TimeOnly.TryParseExact(quietHours.End, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            return false;
        }

        var current = TimeOnly.FromTimeSpan(currentLocalTime);

        if (start == end)
        {
            return false;
        }

        // 日中帯 (例: 13:00 - 15:00) vs 日跨ぎ (例: 23:00 - 07:00)
        return start < end
            ? current >= start && current < end
            : current >= start || current < end;
    }

    private static AppRule? FindAppRule(IReadOnlyList<AppRule> rules, string appId)
    {
        for (var i = 0; i < rules.Count; i++)
        {
            if (string.Equals(rules[i].AppId, appId, StringComparison.OrdinalIgnoreCase))
            {
                return rules[i];
            }
        }

        return null;
    }

    private static bool MatchesAnyKeyword(NotificationEvent notification, IReadOnlyList<string> keywords)
    {
        if (keywords.Count == 0)
        {
            return false;
        }

        var title = notification.Title ?? string.Empty;
        var body = notification.Body ?? string.Empty;

        for (var i = 0; i < keywords.Count; i++)
        {
            var kw = keywords[i];
            if (string.IsNullOrWhiteSpace(kw))
            {
                continue;
            }

            if (title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                body.Contains(kw, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void PruneDedupUnsafe(DateTimeOffset now)
    {
        if (_recentDedup.Count <= 64)
        {
            return;
        }

        List<DedupKey>? expired = null;
        foreach (var kv in _recentDedup)
        {
            if (now - kv.Value >= DeduplicationWindow)
            {
                expired ??= [];
                expired.Add(kv.Key);
            }
        }

        if (expired is not null)
        {
            foreach (var key in expired)
            {
                _recentDedup.Remove(key);
            }
        }
    }

    private readonly record struct DedupKey(string AppId, string Title, string Body);
}
