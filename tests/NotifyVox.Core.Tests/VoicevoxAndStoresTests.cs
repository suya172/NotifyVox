using System.Buffers.Binary;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Audio;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Filtering;
using NotifyVox.Core.History;
using NotifyVox.Core.Models;
using NotifyVox.Core.Pipeline;
using NotifyVox.Core.Queueing;
using NotifyVox.Core.Voicevox;

namespace NotifyVox.Core.Tests;

public sealed class VoicevoxAndStoresTests
{
    private static byte[] CreateTestWavBytes(int sampleRate, short channels, short bitsPerSample, byte[] pcmPayload)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true);

        var byteRate = sampleRate * channels * (bitsPerSample / 8);
        var blockAlign = (short)(channels * (bitsPerSample / 8));

        bw.Write("RIFF"u8);
        bw.Write(36 + pcmPayload.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);
        bw.Write("data"u8);
        bw.Write(pcmPayload.Length);
        bw.Write(pcmPayload);
        bw.Flush();
        return ms.ToArray();
    }

    [Fact]
    public async Task WavHeaderParser_ParsesStandard44ByteWavAndPositionsAtPcmData()
    {
        var pcm = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var wav = CreateTestWavBytes(24000, 1, 16, pcm);

        using var ms = new MemoryStream(wav);
        var header = await WavHeaderParser.ReadHeaderAsync(ms, TestContext.Current.CancellationToken);

        Assert.Equal(24000, header.SampleRate);
        Assert.Equal(1, header.Channels);
        Assert.Equal(16, header.BitsPerSample);

        var remaining = new byte[4];
        var read = await ms.ReadAsync(remaining, TestContext.Current.CancellationToken);
        Assert.Equal(4, read);
        Assert.Equal(pcm, remaining);
    }

    [Fact]
    public async Task VoicevoxClient_FallsBackToStandardSynthesis_WhenStreamingReturns404()
    {
        var pcm = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var wavBytes = CreateTestWavBytes(24000, 1, 16, pcm);
        var requestedPaths = new List<string>();

        var handler = new FakeHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsolutePath;
            requestedPaths.Add(path);

            if (path == "/audio_query")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"speedScale":1.0,"pitchScale":0.0,"intonationScale":1.0}""", Encoding.UTF8, "application/json")
                };
            }

            if (path == "/streaming_synthesis")
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (path == "/synthesis")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(wavBytes)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.InternalServerError);
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var client = new VoicevoxClient(factory, NullLogger<VoicevoxClient>.Instance);
        byte[]? receivedPcm = null;
        WavHeader receivedHeader = default;

        await client.SynthesizeSentenceAsync(
            "http://127.0.0.1:50021",
            "テスト文です。",
            new VoicevoxSynthesisOptions(SpeakerId: 99, DefaultSpeakerId: 3, SpeedScale: 1.2),
            async (header, stream, ct) =>
            {
                receivedHeader = header;
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy, ct);
                receivedPcm = copy.ToArray();
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["/audio_query", "/streaming_synthesis", "/synthesis"], requestedPaths);
        Assert.Equal(24000, receivedHeader.SampleRate);
        Assert.Equal(pcm, receivedPcm);
    }

    [Fact]
    public async Task VoicevoxClient_FallsBackToDefaultSpeaker_After5ConsecutiveFailures()
    {
        var seenSpeakers = new List<string?>();
        var wavBytes = CreateTestWavBytes(24000, 1, 16, [1, 2]);

        var handler = new FakeHttpMessageHandler(req =>
        {
            var query = req.RequestUri!.Query;
            seenSpeakers.Add(query);

            if (query.Contains("speaker=999", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (req.RequestUri.AbsolutePath == "/audio_query")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"speedScale":1.0}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(wavBytes)
            };
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        var client = new VoicevoxClient(factory, NullLogger<VoicevoxClient>.Instance);
        var opts = new VoicevoxSynthesisOptions(SpeakerId: 999, DefaultSpeakerId: 3);

        for (var i = 0; i < 5; i++)
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.SynthesizeSentenceAsync(
                    "http://127.0.0.1:50021",
                    "失敗する話者",
                    opts,
                    (_, _, _) => Task.CompletedTask,
                    TestContext.Current.CancellationToken));
        }

        // 6回目は既定話者 (speaker=3) へ自動フォールバックして成功する (SPEC §7)
        await client.SynthesizeSentenceAsync(
            "http://127.0.0.1:50021",
            "フォールバック成功",
            opts,
            (_, _, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Contains(seenSpeakers, q => q is not null && q.Contains("speaker=3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SettingsAndHistoryStores_PersistPruneAndPipelineExecutesEndToEnd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NotifyVoxTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var settingsPath = Path.Combine(tempDir, "settings.json");
            var historyPath = Path.Combine(tempDir, "history.jsonl");
            var fakeTime = new FakeTimeProvider(new DateTimeOffset(2026, 11, 3, 10, 0, 0, TimeSpan.FromHours(9)));

            var settingsStore = new SettingsStore(NullLogger<SettingsStore>.Instance, settingsPath);
            settingsStore.RegisterDiscoveredApp("Test.App", "テストアプリ");
            Assert.Single(settingsStore.Current.Apps);
            Assert.False(settingsStore.Current.Apps[0].Enabled);

            // アプリを有効化
            settingsStore.Update(s => s with
            {
                Apps = [s.Apps[0] with { Enabled = true, StyleId = 3 }]
            });

            using var historyStore = new JsonLinesHistoryStore(NullLogger<JsonLinesHistoryStore>.Instance, historyPath, fakeTime);

            // 古い履歴を投入して Prune テスト
            await historyStore.AppendAsync(
                new HistoryEntry(fakeTime.GetUtcNow().AddDays(-10), "Old", "Old", "T", "B", true),
                TestContext.Current.CancellationToken);
            await historyStore.PruneExpiredAsync(7, TestContext.Current.CancellationToken);
            Assert.Empty(historyStore.GetRecent());

            // NotificationPipeline E2E テスト
            var vvClient = Substitute.For<IVoicevoxClient>();
            vvClient.GetVersionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("0.26.0");
            vvClient.GetSpeakersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns([]);
            vvClient.SynthesizeSentenceAsync(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<VoicevoxSynthesisOptions>(),
                Arg.Any<Func<WavHeader, Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var consumer = callInfo.ArgAt<Func<WavHeader, Stream, CancellationToken, Task>>(3);
                    var ct = callInfo.ArgAt<CancellationToken>(4);
                    using var pcm = new MemoryStream([1, 2, 3, 4]);
                    await consumer(new WavHeader(24000, 1, 16), pcm, ct);
                });

            var playedFinishedTcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var playedSentenceCount = 0;
            var audioPlayer = Substitute.For<IAudioPlayer>();
            audioPlayer.PlayPcmStreamAsync(
                Arg.Any<WavHeader>(),
                Arg.Any<Stream>(),
                Arg.Any<float>(),
                Arg.Any<string?>(),
                Arg.Any<CancellationToken>(),
                Arg.Any<CancellationToken>())
                .Returns(async callInfo =>
                {
                    var stream = callInfo.ArgAt<Stream>(1);
                    using var drain = new MemoryStream();
                    await stream.CopyToAsync(drain);
                    if (Interlocked.Increment(ref playedSentenceCount) >= 3)
                    {
                        playedFinishedTcs.TrySetResult(playedSentenceCount);
                    }
                });

            var filterPolicy = new FilterPolicy(fakeTime);
            using var speechQueue = new SpeechQueue(NullLogger<SpeechQueue>.Instance);
            using var engineManager = new VoicevoxEngineManager(vvClient, settingsStore, NullLogger<VoicevoxEngineManager>.Instance);
            await engineManager.StartAsync(TestContext.Current.CancellationToken);

            using var pipeline = new NotificationPipeline(
                settingsStore,
                filterPolicy,
                speechQueue,
                vvClient,
                engineManager,
                audioPlayer,
                historyStore,
                NullLogger<NotificationPipeline>.Instance);
            pipeline.Start();

            var ev = new NotificationEvent("Test.App", "テストアプリ", "件名", "一文目です。二文目です！", fakeTime.GetUtcNow());
            await pipeline.HandleNotificationAsync(ev, TestContext.Current.CancellationToken);

            // 「テストアプリ。」「件名。」「一文目です。」「二文目です！」 -> 4文のうち少なくとも3文以上再生完了を待機
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            await playedFinishedTcs.Task.WaitAsync(waitCts.Token);

            // 履歴記録完了まで少し待機
            for (var i = 0; i < 20 && historyStore.GetRecent().Count == 0; i++)
            {
                await Task.Delay(25, TestContext.Current.CancellationToken);
            }

            var recent = historyStore.GetRecent();
            Assert.Single(recent);
            Assert.True(recent[0].WasSpoken);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }
}
