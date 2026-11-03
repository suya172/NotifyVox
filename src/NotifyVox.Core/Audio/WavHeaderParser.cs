using System.Buffers.Binary;
using System.Text;
using NotifyVox.Core.Models;

namespace NotifyVox.Core.Audio;

/// <summary>
/// VOICEVOX が返却する WAV ストリームの先頭から PCM フォーマット情報を抽出し、
/// ストリーム位置を data チャンク先頭（生 PCM データの開始位置）まで進めるパーサー。
/// </summary>
public static class WavHeaderParser
{
    public static async Task<WavHeader> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var riffBuf = new byte[12];
        await ReadExactAsync(stream, riffBuf, cancellationToken).ConfigureAwait(false);

        if (Encoding.ASCII.GetString(riffBuf, 0, 4) != "RIFF" ||
            Encoding.ASCII.GetString(riffBuf, 8, 4) != "WAVE")
        {
            throw new InvalidDataException("有効な RIFF/WAVE ヘッダではありません。");
        }

        int channels = 0;
        int sampleRate = 0;
        int bitsPerSample = 0;
        bool foundFmt = false;

        var chunkHeader = new byte[8];
        while (true)
        {
            await ReadExactAsync(stream, chunkHeader, cancellationToken).ConfigureAwait(false);
            var chunkId = Encoding.ASCII.GetString(chunkHeader, 0, 4);
            var chunkSize = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(4, 4));
            if (chunkSize < 0 || chunkSize > 64 * 1024 * 1024 && chunkId != "data")
            {
                throw new InvalidDataException($"不正な WAV チャンクサイズです: {chunkId} ({chunkSize})");
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("WAV fmt チャンクが 16 バイト未満です。");
                }

                var fmtBuf = new byte[chunkSize];
                await ReadExactAsync(stream, fmtBuf, cancellationToken).ConfigureAwait(false);

                var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.AsSpan(0, 2));
                if (audioFormat != 1) // 1 = Linear PCM
                {
                    throw new InvalidDataException($"未対応の WAV フォーマット種別です: {audioFormat}");
                }

                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.AsSpan(2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmtBuf.AsSpan(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmtBuf.AsSpan(14, 2));
                foundFmt = true;
            }
            else if (chunkId == "data")
            {
                if (!foundFmt)
                {
                    throw new InvalidDataException("fmt チャンクより先に data チャンクが出現しました。");
                }

                // ストリーム位置はちょうど PCM サンプル先頭を指している状態
                return new WavHeader(sampleRate, channels, bitsPerSample);
            }
            else
            {
                // 未知のサブチャンク（JUNK, LIST 等）を読み飛ばす（奇数長はワード境界パディング考慮）
                var skipBytes = chunkSize + (chunkSize & 1);
                await SkipExactAsync(stream, skipBytes, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("WAV ヘッダの読み取り中にストリームが終了しました。");
            }

            offset += read;
        }
    }

    private static async Task SkipExactAsync(Stream stream, int count, CancellationToken cancellationToken)
    {
        var scratch = new byte[Math.Min(count, 1024)];
        var remaining = count;
        while (remaining > 0)
        {
            var toRead = Math.Min(remaining, scratch.Length);
            var read = await stream.ReadAsync(scratch.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("WAV サブチャンクのスキップ中にストリームが終了しました。");
            }

            remaining -= read;
        }
    }
}
