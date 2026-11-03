using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.History;

/// <summary>
/// JSON Lines 形式 (%LOCALAPPDATA%\NotifyVox\history.jsonl) による読み上げ履歴ストア (SPEC §4.7 F27-F28, §6.4, §13.5)
/// </summary>
public sealed class JsonLinesHistoryStore : IHistoryStore, IDisposable
{
    private const int MaxInMemoryEntries = 500;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _filePath;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<JsonLinesHistoryStore> _logger;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly Lock _cacheLock = new();
    private readonly LinkedList<HistoryEntry> _recentCache = new();

    public JsonLinesHistoryStore(
        ILogger<JsonLinesHistoryStore> logger,
        string? filePath = null,
        TimeProvider? timeProvider = null)
    {
        _logger = logger;
        _filePath = filePath ?? NotifyVoxPaths.DefaultHistoryFilePath;
        _timeProvider = timeProvider ?? TimeProvider.System;
        LoadInitialCache();
    }

    public event EventHandler<HistoryEntry>? EntryAdded;

    public async Task AppendAsync(HistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        lock (_cacheLock)
        {
            _recentCache.AddFirst(entry);
            while (_recentCache.Count > MaxInMemoryEntries)
            {
                _recentCache.RemoveLast();
            }
        }

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(_filePath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "履歴ファイルへの追記に失敗しました: {Path}", _filePath);
        }
        finally
        {
            _ioGate.Release();
        }

        EntryAdded?.Invoke(this, entry);
    }

    public IReadOnlyList<HistoryEntry> GetRecent(int maxCount = 200)
    {
        if (maxCount <= 0)
        {
            return [];
        }

        lock (_cacheLock)
        {
            var list = new List<HistoryEntry>(Math.Min(maxCount, _recentCache.Count));
            var node = _recentCache.First;
            while (node is not null && list.Count < maxCount)
            {
                list.Add(node.Value);
                node = node.Next;
            }

            return list;
        }
    }

    public async Task PruneExpiredAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0)
        {
            return;
        }

        var cutoff = _timeProvider.GetUtcNow() - TimeSpan.FromDays(retentionDays);

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(_filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var keptLines = new List<string>(lines.Length);
            var keptEntries = new List<HistoryEntry>(lines.Length);

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                    if (entry is not null && entry.Timestamp >= cutoff)
                    {
                        keptLines.Add(line);
                        keptEntries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // 破損行は破棄してクリーンアップ
                }
            }

            if (keptLines.Count != lines.Length)
            {
                var tempPath = _filePath + ".tmp";
                await File.WriteAllLinesAsync(tempPath, keptLines, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
                File.Move(tempPath, _filePath, overwrite: true);

                lock (_cacheLock)
                {
                    _recentCache.Clear();
                    for (var i = Math.Max(0, keptEntries.Count - MaxInMemoryEntries); i < keptEntries.Count; i++)
                    {
                        _recentCache.AddFirst(keptEntries[i]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "古い履歴の自動削除に失敗しました: {Path}", _filePath);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_cacheLock)
            {
                _recentCache.Clear();
            }

            if (File.Exists(_filePath))
            {
                File.Delete(_filePath);
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private void LoadInitialCache()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }

            foreach (var line in File.ReadLines(_filePath, Encoding.UTF8))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<HistoryEntry>(line, JsonOptions);
                    if (entry is not null)
                    {
                        _recentCache.AddFirst(entry);
                        while (_recentCache.Count > MaxInMemoryEntries)
                        {
                            _recentCache.RemoveLast();
                        }
                    }
                }
                catch (JsonException)
                {
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "履歴ファイルの初期読み込みに失敗しました: {Path}", _filePath);
        }
    }

    public void Dispose()
    {
        _ioGate.Dispose();
    }
}
