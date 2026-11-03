using Microsoft.Extensions.Logging;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Queueing;

/// <summary>
/// 読み上げリクエストの FIFO キュー・高優先度割込み・上限破棄・中断制御を行うクラス (SPEC §4.3 F13-F16, §6.1)
/// </summary>
public sealed class SpeechQueue : IDisposable
{
    public const int DefaultMaxCapacity = 10; // SPEC F15

    private readonly int _maxCapacity;
    private readonly ILogger<SpeechQueue> _logger;
    private readonly Lock _sync = new();
    private readonly LinkedList<SpeechRequest> _items = new();
    private readonly SemaphoreSlim _signal = new(0);

    private Priority? _activePriority;
    private CancellationTokenSource? _activeAbortCts;
    private CancellationTokenSource? _activeChunkInterruptCts;
    private bool _disposed;

    public SpeechQueue(ILogger<SpeechQueue> logger, int maxCapacity = DefaultMaxCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCapacity, 1);
        _logger = logger;
        _maxCapacity = maxCapacity;
    }

    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// リクエストをキューに投入する。
    /// - 上限超過時は最も古い通常優先度（無ければ最古アイテム）を破棄してログ記録 (SPEC F15)。
    /// - 高優先度は既存の通常優先度より前（高優先度グループの末尾）へ挿入し、現在通常優先度を発話中ならチャンク境界割込みを発火する (SPEC F14)。
    /// </summary>
    public void Enqueue(SpeechRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        CancellationTokenSource? interruptToTrigger = null;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_items.Count >= _maxCapacity)
            {
                EvictOldestUnsafe();
            }

            if (request.Priority == Priority.High)
            {
                // 既存の High の直後（最初の Normal の直前）に挿入
                var node = _items.First;
                while (node is not null && node.Value.Priority == Priority.High)
                {
                    node = node.Next;
                }

                if (node is not null)
                {
                    _items.AddBefore(node, request);
                }
                else
                {
                    _items.AddLast(request);
                }

                // 現在発話中のアイテムが Normal ならチャンク境界で割り込む (SPEC F14)
                if (_activePriority == Priority.Normal && _activeChunkInterruptCts is { IsCancellationRequested: false })
                {
                    interruptToTrigger = _activeChunkInterruptCts;
                }
            }
            else
            {
                _items.AddLast(request);
            }

            _signal.Release();
        }

        if (interruptToTrigger is not null)
        {
            _logger.LogInformation("高優先度通知の着信により現在の発話へチャンク境界割込みを要求します ({AppName})", request.Source.AppName);
            TryCancel(interruptToTrigger);
        }
    }

    /// <summary>
    /// 次の読み上げリクエストを取り出す（アイテムが来るまで非同期待機）。
    /// </summary>
    public async Task<SpeechRequest> DequeueAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (_items.First is not null)
                {
                    var value = _items.First.Value;
                    _items.RemoveFirst();
                    return value;
                }
            }
        }
    }

    /// <summary>
    /// 発話開始時に呼び出し、即時中断トークンおよびチャンク境界割込みトークンを発行する。
    /// </summary>
    public ActiveSpeechScope BeginActiveSpeech(Priority priority, CancellationToken parentToken)
    {
        lock (_sync)
        {
            _activeAbortCts?.Dispose();
            _activeChunkInterruptCts?.Dispose();

            _activePriority = priority;
            _activeAbortCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
            _activeChunkInterruptCts = CancellationTokenSource.CreateLinkedTokenSource(_activeAbortCts.Token);

            // 既にキュー先頭に High が待機しており、今回が Normal なら即割込み状態にする
            if (priority == Priority.Normal && _items.First?.Value.Priority == Priority.High)
            {
                _activeChunkInterruptCts.Cancel();
            }

            return new ActiveSpeechScope(this, _activeAbortCts.Token, _activeChunkInterruptCts.Token);
        }
    }

    /// <summary>
    /// SPEC F16: 現在の読み上げのみを即時停止する（後続のキューは維持）。
    /// </summary>
    public void StopCurrent()
    {
        CancellationTokenSource? abortCts;
        lock (_sync)
        {
            abortCts = _activeAbortCts;
        }

        TryCancel(abortCts);
    }

    /// <summary>
    /// SPEC F16: キューを全消去し、現在の読み上げも即時停止する。
    /// </summary>
    public int ClearAll()
    {
        int removedCount;
        CancellationTokenSource? abortCts;

        lock (_sync)
        {
            removedCount = _items.Count;
            _items.Clear();
            abortCts = _activeAbortCts;

            // 余剰セマフォカウントをドレイン
            while (_signal.Wait(0))
            {
            }
        }

        TryCancel(abortCts);
        return removedCount;
    }

    private void EndActiveSpeech()
    {
        lock (_sync)
        {
            _activePriority = null;
            _activeChunkInterruptCts?.Dispose();
            _activeChunkInterruptCts = null;
            _activeAbortCts?.Dispose();
            _activeAbortCts = null;
        }
    }

    private void EvictOldestUnsafe()
    {
        // 古い Normal を優先して探す (SPEC F15)
        var target = _items.First;
        while (target is not null && target.Value.Priority != Priority.Normal)
        {
            target = target.Next;
        }

        target ??= _items.First;
        if (target is null)
        {
            return;
        }

        var dropped = target.Value;
        _items.Remove(target);

        // セマフォカウントを1つ減算してアイテム数と整合させる
        _signal.Wait(0);

        // SPEC §5: ログに本文は記録せずアプリ名とタイトルのみ記録
        _logger.LogWarning(
            "読み上げキュー上限 ({MaxCapacity} 件) 超過のため古い通知を破棄しました: App={AppName}, Priority={Priority}, Title={Title}",
            _maxCapacity,
            dropped.Source.AppName,
            dropped.Priority,
            dropped.Source.Title ?? "(なし)");
    }

    private static void TryCancel(CancellationTokenSource? cts)
    {
        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _items.Clear();
            TryCancel(_activeAbortCts);
            _activeChunkInterruptCts?.Dispose();
            _activeAbortCts?.Dispose();
        }

        _signal.Dispose();
    }

    public readonly struct ActiveSpeechScope : IDisposable
    {
        private readonly SpeechQueue _owner;
        public CancellationToken AbortToken { get; }
        public CancellationToken InterruptAtChunkToken { get; }

        internal ActiveSpeechScope(SpeechQueue owner, CancellationToken abortToken, CancellationToken interruptAtChunkToken)
        {
            _owner = owner;
            AbortToken = abortToken;
            InterruptAtChunkToken = interruptAtChunkToken;
        }

        public void Dispose() => _owner.EndActiveSpeech();
    }
}
