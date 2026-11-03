using NotifyVox.Core.Configuration;
using NotifyVox.Core.Formatting;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Tests;

public sealed class TextFormatterTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 3, 14, 35, 0, TimeSpan.FromHours(9));

    [Fact]
    public void Format_DefaultTemplate_ExpandsAppTitleAndBody()
    {
        var ev = new NotificationEvent("Discord", "Discord", "Alice", "こんにちは！", FixedTime);
        var result = TextFormatter.Format(ev, new ReadingSettings());

        Assert.Equal("Discord。Alice。こんにちは！", result);
    }

    [Fact]
    public void Format_EmptyTitle_OmitsTitleWithoutDoublePeriods()
    {
        var ev = new NotificationEvent("App1", "Slack", "", "ビルドが完了しました", FixedTime);
        var result = TextFormatter.Format(ev, new ReadingSettings());

        Assert.Equal("Slack。ビルドが完了しました", result);
    }

    [Fact]
    public void Format_EmptyBody_SpeaksTitleOnly()
    {
        var ev = new NotificationEvent("App1", "Timer", "5分経過しました", "   ", FixedTime);
        var result = TextFormatter.Format(ev, new ReadingSettings());

        Assert.Equal("Timer。5分経過しました", result);
    }

    [Fact]
    public void Format_BothTitleAndBodyEmpty_ReturnsEmptyString()
    {
        var ev = new NotificationEvent("App1", "Slack", null, "  ", FixedTime);
        var result = TextFormatter.Format(ev, new ReadingSettings());

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Format_IdenticalTitleAndBody_OmitsDuplicateTitle()
    {
        var ev = new NotificationEvent("App1", "LINE", "新しいメッセージがあります", "新しいメッセージがあります", FixedTime);
        var result = TextFormatter.Format(ev, new ReadingSettings());

        Assert.Equal("LINE。新しいメッセージがあります", result);
    }

    [Fact]
    public void Format_JapanesePlaceholdersAndAppOverride_ExpandsProperly()
    {
        var ev = new NotificationEvent("App1", "Teams", "会議連絡", "開始します", FixedTime);
        var result = TextFormatter.Format(ev, new ReadingSettings(), "{アプリ名}から連絡。{タイトル}。{本文}");

        Assert.Equal("Teamsから連絡。会議連絡。開始します", result);
    }

    [Fact]
    public void Format_ReplacesUrlWithWord_WhenEnabled()
    {
        var ev = new NotificationEvent(
            "Discord",
            "Discord",
            "Bob",
            "@alice 資料はこちら https://example.com/docs/spec?id=123&lang=ja を確認してください",
            FixedTime);

        var resultOn = TextFormatter.Format(ev, new ReadingSettings { SpeakUrlAsWord = true });
        Assert.Equal("Discord。Bob。@alice 資料はこちら URL を確認してください", resultOn);

        var resultOff = TextFormatter.Format(ev, new ReadingSettings { SpeakUrlAsWord = false });
        Assert.Contains("https://example.com/docs/spec?id=123&lang=ja", resultOff, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_RemovesConsecutiveEmojis_PreservesSingleEmojiAndMention()
    {
        var ev = new NotificationEvent(
            "Discord",
            "Discord",
            "Carol",
            "@here リリース完了🎉🎉🎉 お疲れ様👍",
            FixedTime);

        var result = TextFormatter.Format(ev, new ReadingSettings());
        Assert.Equal("Discord。Carol。@here リリース完了 お疲れ様👍", result);
    }

    [Fact]
    public void Format_TruncatesAtMaxChars_AndAppendsOmittedSuffix()
    {
        var longBody = new string('あ', 250);
        var ev = new NotificationEvent("App1", "App", "件名", longBody, FixedTime);
        var settings = new ReadingSettings
        {
            MaxCharsPerNotification = 20,
            AppendOmittedSuffix = true
        };

        var result = TextFormatter.Format(ev, settings);
        Assert.EndsWith("。以下省略", result, StringComparison.Ordinal);
        Assert.True(result.Length <= 25);
    }

    [Fact]
    public void SplitSentences_SplitsByPunctuationAndNewlines_KeepingInterrogativeMarks()
    {
        var input = "Discord。Aliceからの通知ですか？\r\nえっ！？今すぐ確認します！了解";
        var sentences = TextFormatter.SplitSentences(input);

        Assert.Equal(
            [
                "Discord。",
                "Aliceからの通知ですか？",
                "えっ！？",
                "今すぐ確認します！",
                "了解"
            ],
            sentences);
    }
}
