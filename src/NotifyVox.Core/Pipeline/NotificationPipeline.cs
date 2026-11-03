using System.Buffers;
using System.Net.Http;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Filtering;
using NotifyVox.Core.Formatting;
using NotifyVox.Core.Models;
using NotifyVox.Core.Queueing;
using NotifyVox.Core.Voicevox;

namespace NotifyVox.Core.Pipeline;

/// <summary>
/// 通知受信からフィルタ判定・テキスト整形・キュー投入・文単位パイプライン合成再生・履歴記録までを統括するオーケストレータ (SPEC §6.5)
/// </summary>
public sealed class NotificationPipeline : IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly FilterPolicy _filterPolicy;
    private readonly SpeechQueue _speechQueue;
    private readonly IVoicevoxClient _voicevoxClient;
    private readonly VoicevoxEngineManager _engineManager;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IHistoryStore _historyStore;
    private readonly ILogger<NotificationPipeline> _logger;

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;

    public NotificationPipeline(
        ISettingsStore settingsStore,
        FilterPolicy filterPolicy,
        SpeechQueue speechQueue,
        IVoicevoxClient voicevoxClient,
        VoicevoxEngineManager engineManager,
        IAudioPlayer audioPlayer,
        IHistoryStore historyStore,
        ILogger<NotificationPipeline> logger)
    {
        _settingsStore = settingsStore;
        _filterPolicy = filterPolicy;
        _speechQueue = speechQueue;
        _voicevoxClient = voicevoxClient;
        _engineManager = engineManager;
        _audioPlayer = audioPlayer;
        _historyStore = historyStore;
        _logger = logger;
    }

    public void Start()
    {
        if (_workerTask is not null)
        {
            return;
        }

        _workerCts = new CancellationTokenSource();
        _workerTask = Task.Run(() => WorkerLoopAsync(_workerCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// 着信した通知イベントを処理パイプラインに投入する (SPEC §6.5)。
    /// </summary>
    public async Task HandleNotificationAsync(NotificationEvent notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);

        try
        {
            // SPEC §4.6-1: 通知を発行したアプリを候補一覧へ自動収集
            _settingsStore.RegisterDiscoveredApp(notification.AppId, notification.AppName);

            var settings = _settingsStore.Current;
            var decision = _filterPolicy.Evaluate(notification, settings);

            if (!decision.ShouldSpeak)
            {
                await RecordHistorySafeAsync(notification, wasSpoken: false, decision.RejectReason, cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            // SPEC F21: エンジン未接続時はキューに保持せず破棄し、履歴にのみ残す
            if (!_engineManager.IsConnected)
            {
                _logger.LogInformation(
                    "VOICEVOX エンジン未接続のため通知読み上げをスキップしました: App={AppName}, Title={Title}",
                    notification.AppName,
                    notification.Title ?? "(なし)");
                await RecordHistorySafeAsync(notification, wasSpoken: false, "VOICEVOX 未接続", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var formattedText = decision.Action == FilterAction.Summarize
                ? decision.SummaryText ?? string.Empty
                : TextFormatter.Format(notification, settings.Reading, decision.AppTemplateOverride);

            if (string.IsNullOrWhiteSpace(formattedText))
            {
                await RecordHistorySafeAsync(notification, wasSpoken: false, "整形後テキスト空", cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            var request = new SpeechRequest(
                notification,
                formattedText,
                decision.SpeakerId,
                decision.Priority);

            _speechQueue.Enqueue(request);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // SPEC §5: パイプライン各段は例外を捕捉しログ＋継続（本文は記録しない）
            _logger.LogError(ex, "通知受信処理中に例外が発生しました: App={AppName}", notification.AppName);
        }
    }

    /// <summary>
    /// トレイメニューや設定画面からの読み上げテストを実行する (SPEC F26)。
    /// </summary>
    public async Task<bool> EnqueueTestSpeechAsync(string? customText = null, int? styleIdOverride = null)
    {
        if (!_engineManager.IsConnected)
        {
            var connected = await _engineManager.RefreshNowAsync().ConfigureAwait(false);
            if (!connected)
            {
                return false;
            }
        }

        var settings = _settingsStore.Current;
        var text = string.IsNullOrWhiteSpace(customText)
            ? "NotifyVox。読み上げテストです。通知のストリーミング再生が正常に動作しています。"
            : customText;

        var source = new NotificationEvent(
            AppId: "NotifyVox.Test",
            AppName: "NotifyVox",
            Title: "読み上げテスト",
            Body: text,
            ReceivedAt: DateTimeOffset.Now);

        _speechQueue.Enqueue(new SpeechRequest(
            source,
            text,
            styleIdOverride ?? settings.Voicevox.DefaultStyleId,
            Priority.High,
            IsTest: true));

        return true;
    }

    public void StopCurrentSpeech()
    {
        _speechQueue.StopCurrent();
        _audioPlayer.StopPlayback();
    }

    public void ClearAllSpeech()
    {
        _speechQueue.ClearAll();
        _audioPlayer.StopPlayback();
    }

    private async Task WorkerLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            SpeechRequest request;
            try
            {
                request = await _speechQueue.DequeueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await ProcessSpeechRequestAsync(request, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessSpeechRequestAsync(SpeechRequest request, CancellationToken stoppingToken)
    {
        using var scope = _speechQueue.BeginActiveSpeech(request.Priority, stoppingToken);
        var sentences = TextFormatter.SplitSentences(request.Text);
        if (sentences.Count == 0)
        {
            return;
        }

        var settings = _settingsStore.Current;
        var synthOptions = new VoicevoxSynthesisOptions(
            SpeakerId: request.SpeakerId,
            DefaultSpeakerId: settings.Voicevox.DefaultStyleId,
            SpeedScale: settings.Voicevox.SpeedScale,
            PitchScale: settings.Voicevox.PitchScale,
            IntonationScale: settings.Voicevox.IntonationScale);

        var anySentencePlayed = false;
        PreparedSentence? prefetchedNext = null;

        try
        {
            // SPEC F22-3: 文単位パイプライン処理（現在の文を再生しながら次の文を先行合成する）
            var currentPrepared = StartSentenceSynthesis(
                sentences[0],
                settings.Voicevox.Endpoint,
                synthOptions,
                scope.AbortToken);

            for (var i = 0; i < sentences.Count; i++)
            {
                if (scope.AbortToken.IsCancellationRequested || scope.InterruptAtChunkToken.IsCancellationRequested)
                {
                    currentPrepared.CancelAndDispose();
                    break;
                }

                // 次の文があれば、現在の文の再生と並行して合成を開始しておく
                if (i + 1 < sentences.Count && !scope.InterruptAtChunkToken.IsCancellationRequested)
                {
                    prefetchedNext = StartSentenceSynthesis(
                        sentences[i + 1],
                        settings.Voicevox.Endpoint,
                        synthOptions,
                        scope.AbortToken);
                }

                try
                {
                    var header = await currentPrepared.HeaderTask.ConfigureAwait(false);
                    if (i == 0)
                    {
                        var startupLatencyMs = (DateTimeOffset.Now - request.Source.ReceivedAt).TotalMilliseconds;
                        _logger.LogInformation(
                            "ストリーミング発話開始: App={AppName}, Speaker={SpeakerId}, Sentences={SentenceCount}, LatencyMs={LatencyMs:F0}",
                            request.Source.AppName,
                            request.SpeakerId,
                            sentences.Count,
                            startupLatencyMs);
                    }
                    await using var pcmStream = new ChannelChunkStream(currentPrepared.Chunks.Reader);

                    await _audioPlayer.PlayPcmStreamAsync(
                        header,
                        pcmStream,
                        settings.Voicevox.Volume,
                        settings.Voicevox.OutputDevice,
                        scope.InterruptAtChunkToken,
                        scope.AbortToken).ConfigureAwait(false);

                    await currentPrepared.PumpTask.ConfigureAwait(false);
                    anySentencePlayed = true;
                }
                catch (OperationCanceledException) when (scope.AbortToken.IsCancellationRequested || scope.InterruptAtChunkToken.IsCancellationRequested)
                {
                    currentPrepared.CancelAndDispose();
                    prefetchedNext?.CancelAndDispose();
                    prefetchedNext = null;
                    break;
                }
                catch (HttpRequestException ex)
                {
                    // エンジンが停止・切断された場合は接続状態を更新し残りの文も中断 (SPEC §7)
                    _logger.LogWarning(ex, "VOICEVOX エンジン通信エラーのため発話を中断しました: App={AppName}", request.Source.AppName);
                    _engineManager.ReportCommunicationFailure();
                    currentPrepared.CancelAndDispose();
                    prefetchedNext?.CancelAndDispose();
                    prefetchedNext = null;
                    break;
                }
                catch (Exception ex)
                {
                    // SPEC F24 / §7: タイムアウトや話者エラーの文はスキップして次の文へ進む
                    _logger.LogWarning(
                        ex,
                        "文 ({Index}/{Total}) の合成・再生に失敗したためスキップします: App={AppName}",
                        i + 1,
                        sentences.Count,
                        request.Source.AppName);
                    currentPrepared.CancelAndDispose();
                }

                if (prefetchedNext is not null)
                {
                    currentPrepared = prefetchedNext;
                    prefetchedNext = null;
                }
            }
        }
        finally
        {
            prefetchedNext?.CancelAndDispose();
        }

        if (!request.IsTest)
        {
            await RecordHistorySafeAsync(
                request.Source,
                wasSpoken: anySentencePlayed,
                reason: anySentencePlayed ? null : "合成または再生中断",
                stoppingToken).ConfigureAwait(false);
        }
    }

    private PreparedSentence StartSentenceSynthesis(
        string sentence,
        string endpoint,
        VoicevoxSynthesisOptions options,
        CancellationToken abortToken)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(abortToken);
        var headerTcs = new TaskCompletionSource<WavHeader>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 1 チャンク約 4KB〜16KB のため容量 64 で十分な先行バッファを確保しつつメモリ上限を抑える
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(64)
        {
            SingleWriter = true,
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        var pumpTask = Task.Run(async () =>
        {
            try
            {
                await _voicevoxClient.SynthesizeSentenceAsync(
                    endpoint,
                    sentence,
                    options,
                    async (header, wavStream, synthCt) =>
                    {
                        headerTcs.TrySetResult(header);

                        var rentBuf = ArrayPool<byte>.Shared.Rent(8192);
                        try
                        {
                            while (true)
                            {
                                var read = await wavStream.ReadAsync(rentBuf.AsMemory(0, 8192), synthCt).ConfigureAwait(false);
                                if (read == 0)
                                {
                                    break;
                                }

                                var chunk = new byte[read];
                                Buffer.BlockCopy(rentBuf, 0, chunk, 0, read);
                                await channel.Writer.WriteAsync(chunk, synthCt).ConfigureAwait(false);
                            }
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(rentBuf);
                        }
                    },
                    cts.Token).ConfigureAwait(false);

                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                headerTcs.TrySetException(ex);
                channel.Writer.TryComplete(ex);
                throw;
            }
        }, CancellationToken.None);

        return new PreparedSentence(cts, headerTcs.Task, channel, pumpTask);
    }

    private async Task RecordHistorySafeAsync(
        NotificationEvent notification,
        bool wasSpoken,
        string? reason,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = new HistoryEntry(
                notification.ReceivedAt,
                notification.AppId,
                notification.AppName,
                notification.Title,
                notification.Body,
                wasSpoken,
                reason);
            await _historyStore.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "履歴保存中にエラーが発生しました。");
        }
    }

    public void Dispose()
    {
        if (_workerCts is not null)
        {
            try { _workerCts.Cancel(); } catch (ObjectDisposedException) { }
            _workerCts.Dispose();
        }
    }

    private sealed class PreparedSentence(
        CancellationTokenSource cts,
        Task<WavHeader> headerTask,
        Channel<byte[]> chunks,
        Task pumpTask)
    {
        public Task<WavHeader> HeaderTask { get; } = headerTask;
        public Channel<byte[]> Chunks { get; } = chunks;
        public Task PumpTask { get; } = pumpTask;

        public void CancelAndDispose()
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            cts.Dispose();
        }
    }

    /// <summary>
    /// ChannelReader&lt;byte[]&gt; を逐次読み取り Stream としてアダプトする軽量ストリーム。
    /// </summary>
    private sealed class ChannelChunkStream(ChannelReader<byte[]> reader) : Stream
    {
        private byte[]? _currentChunk;
        private int _currentOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.Length == 0)
            {
                return 0;
            }

            while (_currentChunk is null || _currentOffset >= _currentChunk.Length)
            {
                if (!await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return 0;
                }

                if (!reader.TryRead(out _currentChunk))
                {
                    continue;
                }

                _currentOffset = 0;
            }

            var available = _currentChunk.Length - _currentOffset;
            var toCopy = Math.Min(available, buffer.Length);
            _currentChunk.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
            _currentOffset += toCopy;
            return toCopy;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
