using System.Buffers;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Models;

namespace NotifyVox.Windows.Audio;

/// <summary>
/// NAudio 3.3.0 (WasapiPlayer + BufferedWaveProvider) による低遅延ストリーミング再生実装 (SPEC §3.1, §6.1)
/// </summary>
public sealed class WasapiAudioPlayer : IAudioPlayer
{
    private const int LatencyMs = 60;

    private readonly ILogger<WasapiAudioPlayer> _logger;
    private readonly Lock _playerLock = new();
    private WasapiPlayer? _activePlayer;

    public WasapiAudioPlayer(ILogger<WasapiAudioPlayer> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>
        {
            new(string.Empty, "既定の出力デバイス (Windows 既定)")
        };

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (endpoint)
                {
                    devices.Add(new AudioDeviceInfo(endpoint.ID, endpoint.FriendlyName));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "オーディオ出力デバイス一覧の取得に失敗しました。");
        }

        return devices;
    }

    public async Task PlayPcmStreamAsync(
        WavHeader header,
        Stream pcmStream,
        float volume,
        string? outputDeviceId,
        CancellationToken interruptAtChunkToken,
        CancellationToken abortToken)
    {
        ArgumentNullException.ThrowIfNull(pcmStream);

        var waveFormat = new WaveFormat(header.SampleRate, header.BitsPerSample, header.Channels);
        var bufferedProvider = new BufferedWaveProvider(waveFormat, TimeSpan.FromSeconds(60))
        {
            ReadFully = true,
            DiscardOnBufferOverflow = false
        };

        MMDevice? selectedDevice = null;
        WasapiPlayer? player = null;

        try
        {
            player = await BuildPlayerAsync(outputDeviceId, d => selectedDevice = d).ConfigureAwait(false);
            player.Init(bufferedProvider);

            lock (_playerLock)
            {
                _activePlayer = player;
            }

            var blockAlign = Math.Max(1, waveFormat.BlockAlign);
            var clampedVolume = Math.Clamp(volume, 0f, 2f);
            var startedPlayback = false;

            var readBuf = ArrayPool<byte>.Shared.Rent(8192);
            var carryBuf = new byte[blockAlign];
            var carryCount = 0;

            try
            {
                while (!abortToken.IsCancellationRequested && !interruptAtChunkToken.IsCancellationRequested)
                {
                    // 先行合成でバッファが十分に溜まっている場合は過剰メモリ消費を防ぐため軽くバックプレッシャをかける
                    while (bufferedProvider.BufferedDuration > TimeSpan.FromSeconds(20))
                    {
                        await Task.Delay(30, abortToken).ConfigureAwait(false);
                        if (interruptAtChunkToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }

                    if (interruptAtChunkToken.IsCancellationRequested)
                    {
                        break;
                    }

                    if (carryCount > 0)
                    {
                        Buffer.BlockCopy(carryBuf, 0, readBuf, 0, carryCount);
                    }

                    var read = await pcmStream.ReadAsync(
                        readBuf.AsMemory(carryCount, 8192 - carryCount),
                        abortToken).ConfigureAwait(false);

                    if (read == 0)
                    {
                        break;
                    }

                    var totalBytes = carryCount + read;
                    var alignedBytes = totalBytes - (totalBytes % blockAlign);
                    carryCount = totalBytes - alignedBytes;
                    if (carryCount > 0)
                    {
                        Buffer.BlockCopy(readBuf, alignedBytes, carryBuf, 0, carryCount);
                    }

                    if (alignedBytes <= 0)
                    {
                        continue;
                    }

                    var sampleSpan = readBuf.AsSpan(0, alignedBytes);
                    if (header.BitsPerSample == 16 && Math.Abs(clampedVolume - 1.0f) > 0.001f)
                    {
                        ApplyPcm16Volume(sampleSpan, clampedVolume);
                    }

                    bufferedProvider.AddSamples(sampleSpan);

                    // 1 チャンク目が届いた瞬間に即座に再生を開始する (超低遅延発話)
                    if (!startedPlayback)
                    {
                        player.Play();
                        startedPlayback = true;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(readBuf);
            }

            if (abortToken.IsCancellationRequested)
            {
                player.Stop();
                bufferedProvider.ClearBuffer();
                abortToken.ThrowIfCancellationRequested();
            }

            // チャンク境界割込み (SPEC F14) が発生した場合は現在投入済みのわずかなチャンク再生完了のみ待つか即停止
            if (startedPlayback && !interruptAtChunkToken.IsCancellationRequested)
            {
                while (bufferedProvider.BufferedBytes > 0 && !abortToken.IsCancellationRequested && !interruptAtChunkToken.IsCancellationRequested)
                {
                    await Task.Delay(15, abortToken).ConfigureAwait(false);
                }

                // ハードウェアバッファ残量分だけ待機して語尾切れを防ぐ
                if (!abortToken.IsCancellationRequested && !interruptAtChunkToken.IsCancellationRequested)
                {
                    var tailWaitMs = Math.Clamp(player.LatencyMilliseconds + 20, 30, 150);
                    await Task.Delay(tailWaitMs, abortToken).ConfigureAwait(false);
                }
            }

            player.Stop();
        }
        finally
        {
            lock (_playerLock)
            {
                if (ReferenceEquals(_activePlayer, player))
                {
                    _activePlayer = null;
                }
            }

            player?.Dispose();
            selectedDevice?.Dispose();
        }
    }

    public void StopPlayback()
    {
        lock (_playerLock)
        {
            try
            {
                _activePlayer?.Stop();
            }
            catch
            {
            }
        }
    }

    private async Task<WasapiPlayer> BuildPlayerAsync(string? outputDeviceId, Action<MMDevice> retainDevice)
    {
        var builder = new WasapiPlayerBuilder()
            .WithSharedMode()
            .WithEventSync()
            .WithLatency(LatencyMs);

        if (!string.IsNullOrWhiteSpace(outputDeviceId))
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                var device = enumerator.GetDevice(outputDeviceId);
                retainDevice(device);
                return await builder.WithDevice(device).BuildAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "指定された出力デバイス ({DeviceId}) が利用できないため既定デバイスへフォールバックします。", outputDeviceId);
            }
        }

        return await builder.WithDefaultDeviceStreamRouting().BuildAsync().ConfigureAwait(false);
    }

    private static void ApplyPcm16Volume(Span<byte> pcmBytes, float volume)
    {
        var samples = MemoryMarshal.Cast<byte, short>(pcmBytes);
        for (var i = 0; i < samples.Length; i++)
        {
            var scaled = (int)MathF.Round(samples[i] * volume);
            samples[i] = (short)Math.Clamp(scaled, short.MinValue, short.MaxValue);
        }
    }

    public void Dispose()
    {
        StopPlayback();
    }
}
