using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Audio;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Voicevox;

/// <summary>
/// VOICEVOX エンジン (0.26.0+) との HTTP 通信・ストリーミング音声合成・フォールバックを担うクライアント (SPEC §4.5 F21-F24, §7)
/// </summary>
public sealed class VoicevoxClient : IVoicevoxClient
{
    public const int ConsecutiveFailureFallbackThreshold = 5; // SPEC §7

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<VoicevoxClient> _logger;
    private readonly TimeSpan _sentenceTimeout;

    // styleId -> SupportsStreaming ("streaming_talk" かどうか)
    private readonly ConcurrentDictionary<int, bool> _streamingSupportCache = new();
    private int _consecutiveFailures;
    private bool _fallbackSpeakerActive;

    public VoicevoxClient(
        IHttpClientFactory httpClientFactory,
        ILogger<VoicevoxClient> logger,
        TimeSpan? sentenceTimeout = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _sentenceTimeout = sentenceTimeout ?? TimeSpan.FromSeconds(10); // SPEC F24: 1文あたり10秒
    }

    public async Task<string?> GetVersionAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var baseUri = NormalizeEndpoint(endpoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        var client = _httpClientFactory.CreateClient(nameof(VoicevoxClient));
        using var response = await client.GetAsync(new Uri(baseUri, "version"), cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return raw.Trim().Trim('"');
    }

    public async Task<IReadOnlyList<VoicevoxSpeakerInfo>> GetSpeakersAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var baseUri = NormalizeEndpoint(endpoint);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        var client = _httpClientFactory.CreateClient(nameof(VoicevoxClient));
        using var response = await client.GetAsync(new Uri(baseUri, "speakers"), cts.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cts.Token).ConfigureAwait(false);

        var speakers = new List<VoicevoxSpeakerInfo>();
        foreach (var spElem in doc.RootElement.EnumerateArray())
        {
            var spName = spElem.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
            var spUuid = spElem.TryGetProperty("speaker_uuid", out var u) ? u.GetString() ?? string.Empty : string.Empty;
            var styles = new List<VoicevoxStyleInfo>();

            if (spElem.TryGetProperty("styles", out var stylesElem) && stylesElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var stElem in stylesElem.EnumerateArray())
                {
                    var id = stElem.GetProperty("id").GetInt32();
                    var stName = stElem.TryGetProperty("name", out var sn) ? sn.GetString() ?? string.Empty : string.Empty;
                    var stType = stElem.TryGetProperty("type", out var st) ? st.GetString() ?? "talk" : "talk";

                    var info = new VoicevoxStyleInfo(id, stName, stType);
                    styles.Add(info);
                    _streamingSupportCache[id] = info.SupportsStreaming;
                }
            }

            speakers.Add(new VoicevoxSpeakerInfo(spName, spUuid, styles));
        }

        return speakers;
    }

    public async Task SynthesizeSentenceAsync(
        string endpoint,
        string sentence,
        VoicevoxSynthesisOptions options,
        Func<WavHeader, Stream, CancellationToken, Task> streamConsumer,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sentence);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(streamConsumer);

        var baseUri = NormalizeEndpoint(endpoint);
        var speakerId = ResolveEffectiveSpeakerId(options);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_sentenceTimeout);
        var ct = timeoutCts.Token;

        try
        {
            var client = _httpClientFactory.CreateClient(nameof(VoicevoxClient));

            // 1. POST /audio_query
            var queryJson = await CreateAudioQueryAsync(client, baseUri, sentence, speakerId, options, ct)
                .ConfigureAwait(false);

            // 2. ストリーミング合成優先 → 非対応なら通常 /synthesis にフォールバック (SPEC F22-2, F22-4)
            var tryStreaming = !_streamingSupportCache.TryGetValue(speakerId, out var supportsStreaming) || supportsStreaming;
            HttpResponseMessage? synthResponse = null;

            try
            {
                if (tryStreaming)
                {
                    synthResponse = await PostSynthesisRequestAsync(
                        client,
                        baseUri,
                        path: "streaming_synthesis",
                        speakerId,
                        queryJson,
                        includeSegmentLength: true,
                        ct).ConfigureAwait(false);

                    if (!synthResponse.IsSuccessStatusCode && ShouldFallbackToStandardSynthesis(synthResponse.StatusCode))
                    {
                        _logger.LogInformation(
                            "話者スタイル {SpeakerId} で /streaming_synthesis が {StatusCode} を返したため /synthesis へフォールバックします。",
                            speakerId,
                            (int)synthResponse.StatusCode);
                        _streamingSupportCache[speakerId] = false;
                        synthResponse.Dispose();
                        synthResponse = null;
                    }
                }

                synthResponse ??= await PostSynthesisRequestAsync(
                    client,
                    baseUri,
                    path: "synthesis",
                    speakerId,
                    queryJson,
                    includeSegmentLength: false,
                    ct).ConfigureAwait(false);

                synthResponse.EnsureSuccessStatusCode();

                await using var wavStream = await synthResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var header = await WavHeaderParser.ReadHeaderAsync(wavStream, ct).ConfigureAwait(false);

                await streamConsumer(header, wavStream, ct).ConfigureAwait(false);

                // 成功時は連続失敗カウンタをリセット
                Interlocked.Exchange(ref _consecutiveFailures, 0);
                if (options.SpeakerId == speakerId)
                {
                    _fallbackSpeakerActive = false;
                }
            }
            finally
            {
                synthResponse?.Dispose();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            RecordFailure(options);
            throw new TimeoutException($"VOICEVOX 合成リクエストがタイムアウト ({_sentenceTimeout.TotalSeconds:0.#} 秒) しました。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            RecordFailure(options);
            throw;
        }
    }

    private int ResolveEffectiveSpeakerId(VoicevoxSynthesisOptions options)
    {
        if (_fallbackSpeakerActive && options.SpeakerId != options.DefaultSpeakerId)
        {
            return options.DefaultSpeakerId;
        }

        return options.SpeakerId;
    }

    private void RecordFailure(VoicevoxSynthesisOptions options)
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        if (count >= ConsecutiveFailureFallbackThreshold &&
            !_fallbackSpeakerActive &&
            options.SpeakerId != options.DefaultSpeakerId)
        {
            _fallbackSpeakerActive = true;
            _logger.LogWarning(
                "音声合成が連続 {Count} 回失敗したため、既定話者スタイル ({DefaultSpeakerId}) へフォールバックします (SPEC §7)。",
                count,
                options.DefaultSpeakerId);
        }
    }

    private static async Task<string> CreateAudioQueryAsync(
        HttpClient client,
        Uri baseUri,
        string sentence,
        int speakerId,
        VoicevoxSynthesisOptions options,
        CancellationToken ct)
    {
        var queryUri = new Uri(
            baseUri,
            $"audio_query?text={Uri.EscapeDataString(sentence)}&speaker={speakerId.ToString(CultureInfo.InvariantCulture)}");

        using var req = new HttpRequestMessage(HttpMethod.Post, queryUri);
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var rawJson = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        var node = JsonNode.Parse(rawJson)?.AsObject()
            ?? throw new InvalidDataException("VOICEVOX audio_query の応答 JSON が不正です。");

        // SPEC F23: 話速・音高・抑揚を適用
        node["speedScale"] = options.SpeedScale;
        node["pitchScale"] = options.PitchScale;
        node["intonationScale"] = options.IntonationScale;

        return node.ToJsonString();
    }

    private static Task<HttpResponseMessage> PostSynthesisRequestAsync(
        HttpClient client,
        Uri baseUri,
        string path,
        int speakerId,
        string queryJson,
        bool includeSegmentLength,
        CancellationToken ct)
    {
        var uriPath = includeSegmentLength
            ? $"{path}?speaker={speakerId.ToString(CultureInfo.InvariantCulture)}&segment_length=0.3&enable_interrogative_upspeak=true"
            : $"{path}?speaker={speakerId.ToString(CultureInfo.InvariantCulture)}&enable_interrogative_upspeak=true";

        var req = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, uriPath))
        {
            Content = new StringContent(queryJson, Encoding.UTF8, "application/json")
        };

        return client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static bool ShouldFallbackToStandardSynthesis(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.NotFound
            or HttpStatusCode.BadRequest
            or HttpStatusCode.MethodNotAllowed
            or HttpStatusCode.UnprocessableEntity
            or HttpStatusCode.NotImplemented;

    private static Uri NormalizeEndpoint(string endpoint)
    {
        var trimmed = string.IsNullOrWhiteSpace(endpoint)
            ? "http://127.0.0.1:50021/"
            : (endpoint.EndsWith('/') ? endpoint : endpoint + "/");

        return new Uri(trimmed, UriKind.Absolute);
    }
}
