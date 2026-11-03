using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Filtering;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Tests;

public sealed class FilterPolicyTests
{
    private static AppSettings CreateSettingsWithDiscordEnabled(
        IReadOnlyList<string>? globalKeywords = null,
        IReadOnlyList<string>? appKeywords = null,
        QuietHoursSettings? quietHours = null,
        bool muteFullscreen = false)
    {
        return new AppSettings
        {
            Voicevox = new VoicevoxSettings { DefaultStyleId = 3 },
            Apps =
            [
                new AppRule
                {
                    AppId = "com.squirrel.Discord.Discord",
                    AppName = "Discord",
                    Enabled = true,
                    StyleId = 8,
                    Priority = Priority.High,
                    MuteKeywords = appKeywords ?? []
                }
            ],
            Mute = new MuteSettings
            {
                Keywords = globalKeywords ?? [],
                QuietHours = quietHours ?? new QuietHoursSettings { Enabled = false },
                MuteWhenFullscreen = muteFullscreen
            }
        };
    }

    [Fact]
    public void Evaluate_AllowlistDefaultOff_RejectsUnregisteredAndDisabledApps()
    {
        var policy = new FilterPolicy();
        var settings = CreateSettingsWithDiscordEnabled();

        var unlisted = new NotificationEvent("Other.App", "Other", "Title", "Body", DateTimeOffset.UtcNow);
        var decision1 = policy.Evaluate(unlisted, settings);
        Assert.False(decision1.ShouldSpeak);

        var allowed = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Alice", "Hello", DateTimeOffset.UtcNow);
        var decision2 = policy.Evaluate(allowed, settings);
        Assert.Equal(FilterAction.Allow, decision2.Action);
        Assert.Equal(8, decision2.SpeakerId);
        Assert.Equal(Priority.High, decision2.Priority);
    }

    [Fact]
    public void Evaluate_KeywordMute_RejectsGlobalAndPerAppKeywords_CaseInsensitively()
    {
        var policy = new FilterPolicy();
        var settings = CreateSettingsWithDiscordEnabled(
            globalKeywords: ["ネタバレ"],
            appKeywords: ["MutedChannel"]);

        var evGlobal = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "一般", "これはネタバレ注意", DateTimeOffset.UtcNow);
        Assert.Equal(FilterAction.Reject, policy.Evaluate(evGlobal, settings).Action);

        var evApp = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "#mutedchannel", "雑談", DateTimeOffset.UtcNow);
        Assert.Equal(FilterAction.Reject, policy.Evaluate(evApp, settings).Action);
    }

    [Fact]
    public void Evaluate_Deduplication_SuppressesIdenticalNotificationWithin10Seconds()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 12, 0, 0, TimeSpan.FromHours(9)));
        var policy = new FilterPolicy(fakeTime);
        var settings = CreateSettingsWithDiscordEnabled();

        var ev = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Alice", "同じ本文", fakeTime.GetUtcNow());
        Assert.Equal(FilterAction.Allow, policy.Evaluate(ev, settings).Action);

        fakeTime.Advance(TimeSpan.FromSeconds(9.9));
        var dup = policy.Evaluate(ev, settings);
        Assert.Equal(FilterAction.Reject, dup.Action);

        fakeTime.Advance(TimeSpan.FromSeconds(0.2));
        var afterWindow = policy.Evaluate(ev, settings);
        Assert.Equal(FilterAction.Allow, afterWindow.Action);
    }

    [Fact]
    public void Evaluate_BurstThrottling_Enters60SecondSummaryModeOn4thNotificationWithin30Seconds()
    {
        var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 15, 0, 0, TimeSpan.FromHours(9)));
        var policy = new FilterPolicy(fakeTime);
        var settings = CreateSettingsWithDiscordEnabled();

        for (var i = 1; i <= 3; i++)
        {
            var ev = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Group", $"連投 {i}", fakeTime.GetUtcNow());
            Assert.Equal(FilterAction.Allow, policy.Evaluate(ev, settings).Action);
            fakeTime.Advance(TimeSpan.FromSeconds(5));
        }

        // 15秒経過時点での4件目 -> 要約モード遷移
        var fourth = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Group", "連投 4", fakeTime.GetUtcNow());
        var summaryDecision = policy.Evaluate(fourth, settings);
        Assert.Equal(FilterAction.Summarize, summaryDecision.Action);
        Assert.Equal("Discordから4件の通知です", summaryDecision.SummaryText);

        // 以降 60 秒間は抑制される
        fakeTime.Advance(TimeSpan.FromSeconds(30));
        var suppressed = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Group", "連投 5", fakeTime.GetUtcNow());
        Assert.Equal(FilterAction.Reject, policy.Evaluate(suppressed, settings).Action);

        // 合計 61 秒経過後は通常モードへ復帰
        fakeTime.Advance(TimeSpan.FromSeconds(31));
        var resumed = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Group", "復帰後", fakeTime.GetUtcNow());
        Assert.Equal(FilterAction.Allow, policy.Evaluate(resumed, settings).Action);
    }

    [Theory]
    [InlineData("23:00", "07:00", 23, 30, true)]
    [InlineData("23:00", "07:00", 2, 15, true)]
    [InlineData("23:00", "07:00", 7, 0, false)]
    [InlineData("23:00", "07:00", 14, 0, false)]
    [InlineData("13:00", "15:00", 14, 0, true)]
    [InlineData("13:00", "15:00", 16, 0, false)]
    public void IsInQuietHours_HandlesOvernightAndDaytimeRanges(
        string start,
        string end,
        int hour,
        int minute,
        bool expectedMuted)
    {
        var qh = new QuietHoursSettings { Enabled = true, Start = start, End = end };
        var result = FilterPolicy.IsInQuietHours(new TimeSpan(hour, minute, 0), qh);

        Assert.Equal(expectedMuted, result);
    }

    [Fact]
    public void Evaluate_ManualMuteAndFullscreenMute_WorkProperly()
    {
        var fullscreen = Substitute.For<IFullscreenDetector>();
        fullscreen.IsFullscreenAppRunning().Returns(true);

        var policy = new FilterPolicy(fullscreenDetector: fullscreen);
        var settings = CreateSettingsWithDiscordEnabled(muteFullscreen: true);
        var ev = new NotificationEvent("com.squirrel.Discord.Discord", "Discord", "Alice", "Game ping", DateTimeOffset.UtcNow);

        Assert.Equal(FilterAction.Reject, policy.Evaluate(ev, settings).Action);

        fullscreen.IsFullscreenAppRunning().Returns(false);
        policy.ToggleManualMute();
        Assert.True(policy.IsManuallyMuted);
        Assert.Equal(FilterAction.Reject, policy.Evaluate(ev, settings).Action);
    }
}
