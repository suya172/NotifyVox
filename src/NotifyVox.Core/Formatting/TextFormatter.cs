using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Formatting;

/// <summary>
/// 読み上げテンプレート展開・テキスト整形・文分割を行う純粋ロジッククラス (SPEC §4.3 F17-F18, §4.4 F19-F20, §4.5 F22-1)
/// </summary>
public static partial class TextFormatter
{
    [GeneratedRegex(@"https?://[^\s<>""'）)」】]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?:。\s*){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex DuplicatePeriodRegex();

    /// <summary>
    /// 通知イベントと設定から読み上げ用テキストを生成する。
    /// タイトル・本文がともに空の場合は空文字列を返す (SPEC §7)。
    /// </summary>
    public static string Format(
        NotificationEvent notification,
        ReadingSettings settings,
        string? appTemplateOverride = null)
    {
        ArgumentNullException.ThrowIfNull(notification);
        ArgumentNullException.ThrowIfNull(settings);

        var rawTitle = SanitizeSegment(notification.Title ?? string.Empty, settings.SpeakUrlAsWord).Trim();
        var rawBody = SanitizeSegment(notification.Body ?? string.Empty, settings.SpeakUrlAsWord).Trim();

        // SPEC §7: 両方空なら読み上げない
        if (string.IsNullOrWhiteSpace(rawTitle) && string.IsNullOrWhiteSpace(rawBody))
        {
            return string.Empty;
        }

        // SPEC F19: タイトルと本文が同一なら本文のみ（タイトル側を省略）
        var effectiveTitle = string.Equals(rawTitle, rawBody, StringComparison.Ordinal)
            ? string.Empty
            : rawTitle;
        var effectiveBody = rawBody;

        var template = !string.IsNullOrWhiteSpace(appTemplateOverride)
            ? appTemplateOverride
            : (!string.IsNullOrWhiteSpace(settings.Template) ? settings.Template : "{app}。{title}。{body}");

        var timeText = notification.ReceivedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        var appName = (notification.AppName ?? string.Empty).Trim();

        var expanded = template
            .Replace("{app}", appName, StringComparison.OrdinalIgnoreCase)
            .Replace("{アプリ名}", appName, StringComparison.Ordinal)
            .Replace("{title}", effectiveTitle, StringComparison.OrdinalIgnoreCase)
            .Replace("{タイトル}", effectiveTitle, StringComparison.Ordinal)
            .Replace("{body}", effectiveBody, StringComparison.OrdinalIgnoreCase)
            .Replace("{本文}", effectiveBody, StringComparison.Ordinal)
            .Replace("{time}", timeText, StringComparison.OrdinalIgnoreCase);

        // 空プレースホルダ展開で生じた連続句点「。。」や先頭・末尾の不要な句点を正規化
        expanded = DuplicatePeriodRegex().Replace(expanded, "。").Trim();
        while (expanded.StartsWith('。'))
        {
            expanded = expanded[1..].TrimStart();
        }
        while (expanded.EndsWith("。。", StringComparison.Ordinal))
        {
            expanded = expanded[..^1];
        }
        if (string.IsNullOrWhiteSpace(effectiveBody) && expanded.EndsWith('。'))
        {
            expanded = expanded[..^1].TrimEnd();
        }

        if (string.IsNullOrWhiteSpace(expanded))
        {
            return string.Empty;
        }

        // SPEC F17: 文字数上限（グラフェムクラスタ単位でサロゲートペア破壊を防止）
        if (settings.MaxCharsPerNotification > 0)
        {
            expanded = TruncateByTextElements(
                expanded,
                settings.MaxCharsPerNotification,
                settings.AppendOmittedSuffix);
        }

        return expanded;
    }

    /// <summary>
    /// 読み上げテキストを文単位（。！？!? および改行）に分割する (SPEC §4.5 F22-1)。
    /// 疑問文抑揚調整 (enable_interrogative_upspeak) を活かすため、句点・感嘆符・疑問符は文末に保持する。
    /// </summary>
    public static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\r' || ch == '\n')
            {
                FlushSentence(current, result);
                continue;
            }

            current.Append(ch);

            if (IsSentenceTerminator(ch))
            {
                // 「！？」「！！」等の連続終端記号は同一文にまとめる
                while (i + 1 < text.Length && IsSentenceTerminator(text[i + 1]))
                {
                    i++;
                    current.Append(text[i]);
                }

                FlushSentence(current, result);
            }
        }

        FlushSentence(current, result);
        return result;
    }

    /// <summary>
    /// URL 読み替えと連続絵文字除去を適用する (SPEC F18)。メンション記号 '@' は保持する。
    /// </summary>
    public static string SanitizeSegment(string input, bool speakUrlAsWord = true)
    {
        if (string.IsNullOrEmpty(input))
        {
            return string.Empty;
        }

        var working = speakUrlAsWord
            ? UrlRegex().Replace(input, "URL")
            : input;

        return RemoveConsecutiveEmojis(working);
    }

    private static string RemoveConsecutiveEmojis(string text)
    {
        var elements = new List<(string Text, bool IsEmoji)>();
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            elements.Add((element, IsEmojiTextElement(element)));
        }

        var sb = new StringBuilder(text.Length);
        var idx = 0;
        while (idx < elements.Count)
        {
            if (!elements[idx].IsEmoji)
            {
                sb.Append(elements[idx].Text);
                idx++;
                continue;
            }

            var runEnd = idx + 1;
            while (runEnd < elements.Count && elements[runEnd].IsEmoji)
            {
                runEnd++;
            }

            var emojiRunCount = runEnd - idx;
            if (emojiRunCount == 1)
            {
                sb.Append(elements[idx].Text);
            }
            // emojiRunCount >= 2 の連続絵文字は除去 (SPEC F18)
            idx = runEnd;
        }

        return sb.ToString();
    }

    private static bool IsEmojiTextElement(string element)
    {
        foreach (var rune in element.EnumerateRunes())
        {
            var v = rune.Value;
            // SMP 絵文字・記号ブロック (U+1F000..U+1FAFF)
            if (v is >= 0x1F000 and <= 0x1FAFF)
            {
                return true;
            }
            // BMP 装飾・絵文字記号 (U+2600..U+27BF, U+231A..U+23F3, U+2B50, U+2B55)
            if (v is (>= 0x2600 and <= 0x27BF) or (>= 0x231A and <= 0x23F3) or 0x2B50 or 0x2B55)
            {
                return true;
            }
            // 異体字セレクタ U+FE0F を伴う絵文字表現 (例: ❤️, ✨, ⚠️, 1️⃣)
            if (v == 0xFE0F && element.Length > 1)
            {
                return true;
            }
        }

        return false;
    }

    private static string TruncateByTextElements(string text, int maxElements, bool appendSuffix)
    {
        var info = new StringInfo(text);
        if (info.LengthInTextElements <= maxElements)
        {
            return text;
        }

        var truncated = info.SubstringByTextElements(0, maxElements).TrimEnd();
        while (truncated.EndsWith('。'))
        {
            truncated = truncated[..^1].TrimEnd();
        }

        return appendSuffix ? $"{truncated}。以下省略" : truncated;
    }

    private static bool IsSentenceTerminator(char ch) =>
        ch is '。' or '！' or '？' or '!' or '?';

    private static void FlushSentence(StringBuilder current, List<string> target)
    {
        if (current.Length == 0)
        {
            return;
        }

        var sentence = current.ToString().Trim();
        current.Clear();

        if (string.IsNullOrWhiteSpace(sentence))
        {
            return;
        }

        // 句読点のみで構成される断片は除外
        var hasContent = false;
        foreach (var c in sentence)
        {
            if (!IsSentenceTerminator(c) && !char.IsWhiteSpace(c))
            {
                hasContent = true;
                break;
            }
        }

        if (hasContent)
        {
            target.Add(sentence);
        }
    }
}
