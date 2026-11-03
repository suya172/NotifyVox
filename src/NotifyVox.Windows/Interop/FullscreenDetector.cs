using System.Runtime.InteropServices;
using System.Text;
using NotifyVox.Core.Abstractions;

namespace NotifyVox.Windows.Interop;

/// <summary>
/// Win32 API によりフルスクリーンアプリ（ゲーム・動画全画面等）が実行中かを判定する実装 (SPEC §4.2 F12)
/// </summary>
public sealed partial class FullscreenDetector : IFullscreenDetector
{
    private const int MonitorDefaultToNearest = 2;

    public bool IsFullscreenAppRunning()
    {
        try
        {
            // Direct3D 排他フルスクリーン等を含む OS 標準判定
            if (SHQueryUserNotificationState(out var state) == 0)
            {
                if (state is QueryUserNotificationState.Busy
                    or QueryUserNotificationState.RunningD3dFullScreen
                    or QueryUserNotificationState.PresentationMode)
                {
                    // ただしデスクトップ自体やエクスプローラーではなく、前面ウィンドウがモニタ全面を覆っているかも確認
                    if (state is QueryUserNotificationState.RunningD3dFullScreen or QueryUserNotificationState.PresentationMode)
                    {
                        return true;
                    }
                }
            }

            var hwnd = GetForegroundWindow();
            if (hwnd == nint.Zero || hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
            {
                return false;
            }

            // デスクトップ背景クラス (Progman / WorkerW) は除外
            var className = GetWindowClassName(hwnd);
            if (className is "Progman" or "WorkerW" or "Shell_TrayWnd")
            {
                return false;
            }

            if (!GetWindowRect(hwnd, out var winRect))
            {
                return false;
            }

            var hMonitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (hMonitor == nint.Zero)
            {
                return false;
            }

            var monInfo = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(hMonitor, ref monInfo))
            {
                return false;
            }

            return winRect.Left <= monInfo.rcMonitor.Left &&
                   winRect.Top <= monInfo.rcMonitor.Top &&
                   winRect.Right >= monInfo.rcMonitor.Right &&
                   winRect.Bottom >= monInfo.rcMonitor.Bottom;
        }
        catch
        {
            return false;
        }
    }

    private static string GetWindowClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[64];
        var len = GetClassNameW(hwnd, buffer, buffer.Length);
        return len > 0 ? new string(buffer[..len]) : string.Empty;
    }

    private enum QueryUserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        WindowsStoreApp = 7
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public uint dwFlags;
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out QueryUserNotificationState pquns);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hWnd, out Rect lpRect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint hMonitor, ref MonitorInfo lpmi);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassNameW(nint hWnd, Span<char> lpClassName, int nMaxCount);
}
