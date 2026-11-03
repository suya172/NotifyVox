using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace NotifyVox.App.Services;

public enum TrayVisualState
{
    Ready,
    Muted,
    EngineDisconnected,
    PermissionOrError
}

/// <summary>
/// タスクトレイ用の状態別アイコンを WPF DrawingVisual で描画し、
/// 32bit アルファ付きネイティブ System.Drawing.Icon としてキャッシュ生成するレンダラ
/// </summary>
internal static class TrayIconRenderer
{
    private static readonly ConcurrentDictionary<TrayVisualState, Icon> IconCache = new();

    public static Icon GetIcon(TrayVisualState state)
    {
        return IconCache.GetOrAdd(state, CreateNativeIcon);
    }

    private static Icon CreateNativeIcon(TrayVisualState state)
    {
        const int size = 32;
        var visual = new DrawingVisual();

        using (var dc = visual.RenderOpen())
        {
            var bgBrush = state switch
            {
                TrayVisualState.Ready => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),              // Green
                TrayVisualState.Muted => new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),              // Slate gray
                TrayVisualState.EngineDisconnected => new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x06)), // Amber
                TrayVisualState.PermissionOrError => new SolidColorBrush(Color.FromRgb(0xDC, 0x26, 0x26)),  // Red
                _ => System.Windows.Media.Brushes.DodgerBlue
            };
            bgBrush.Freeze();

            dc.DrawRoundedRectangle(bgBrush, null, new Rect(1, 1, 30, 30), 7, 7);

            var speakerGeo = new StreamGeometry();
            using (var ctx = speakerGeo.Open())
            {
                ctx.BeginFigure(new Point(7, 12), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(12, 12), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(18, 7), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(18, 25), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(12, 20), isStroked: false, isSmoothJoin: false);
                ctx.LineTo(new Point(7, 20), isStroked: false, isSmoothJoin: false);
            }
            speakerGeo.Freeze();
            dc.DrawGeometry(System.Windows.Media.Brushes.White, null, speakerGeo);

            var whitePen = new Pen(System.Windows.Media.Brushes.White, 2.4)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            whitePen.Freeze();

            if (state == TrayVisualState.Muted)
            {
                dc.DrawLine(whitePen, new Point(21, 12), new Point(27, 20));
                dc.DrawLine(whitePen, new Point(27, 12), new Point(21, 20));
            }
            else if (state is TrayVisualState.EngineDisconnected or TrayVisualState.PermissionOrError)
            {
                dc.DrawLine(whitePen, new Point(24, 9), new Point(24, 17));
                dc.DrawEllipse(System.Windows.Media.Brushes.White, null, new Point(24, 22), 1.6, 1.6);
            }
            else
            {
                var waveGeo = new StreamGeometry();
                using (var ctx = waveGeo.Open())
                {
                    ctx.BeginFigure(new Point(21, 11.5), isFilled: false, isClosed: false);
                    ctx.ArcTo(new Point(21, 20.5), new Size(5, 5), 0, false, SweepDirection.Clockwise, true, false);
                    ctx.BeginFigure(new Point(24, 8.5), isFilled: false, isClosed: false);
                    ctx.ArcTo(new Point(24, 23.5), new Size(8.5, 8.5), 0, false, SweepDirection.Clockwise, true, false);
                }
                waveGeo.Freeze();
                dc.DrawGeometry(null, whitePen, waveGeo);
            }
        }

        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var pngStream = new MemoryStream();
        encoder.Save(pngStream);
        var pngBytes = pngStream.ToArray();

        // PNG ペイロードを格納した標準 22 バイト ICO ヘッダを構築
        Span<byte> icoHeader = stackalloc byte[22];
        icoHeader.Clear();
        BinaryPrimitives.WriteUInt16LittleEndian(icoHeader[2..4], 1); // Type = ICO
        BinaryPrimitives.WriteUInt16LittleEndian(icoHeader[4..6], 1); // Count = 1
        icoHeader[6] = size;                                          // Width
        icoHeader[7] = size;                                          // Height
        BinaryPrimitives.WriteUInt16LittleEndian(icoHeader[10..12], 1);  // Planes
        BinaryPrimitives.WriteUInt16LittleEndian(icoHeader[12..14], 32); // BitCount
        BinaryPrimitives.WriteInt32LittleEndian(icoHeader[14..18], pngBytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(icoHeader[18..22], 22);

        using var icoStream = new MemoryStream(22 + pngBytes.Length);
        icoStream.Write(icoHeader);
        icoStream.Write(pngBytes);
        icoStream.Position = 0;

        return new Icon(icoStream);
    }
}
