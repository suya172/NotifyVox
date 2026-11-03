using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NotifyVox.Windows.Interop;

/// <summary>
/// Win32 RegisterHotKey によるグローバルホットキー管理ヘルパー (SPEC §4.2 F11, §6.1)
/// </summary>
public sealed partial class GlobalHotkeyManager : IDisposable
{
    public const int WmHotKey = 0x0312;
    private const int HotkeyId = 0x4E56; // 'NV'

    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private readonly ILogger<GlobalHotkeyManager> _logger;
    private nint _registeredHwnd;
    private bool _isRegistered;

    public GlobalHotkeyManager(ILogger<GlobalHotkeyManager> logger)
    {
        _logger = logger;
    }

    public event EventHandler? HotkeyPressed;

    public bool Register(nint hwnd, string? hotkeySpec)
    {
        Unregister();

        if (hwnd == nint.Zero || string.IsNullOrWhiteSpace(hotkeySpec))
        {
            return false;
        }

        if (!TryParseHotkey(hotkeySpec, out var modifiers, out var vk))
        {
            _logger.LogWarning("ホットキー文字列の解析に失敗しました: {Hotkey}", hotkeySpec);
            return false;
        }

        if (RegisterHotKey(hwnd, HotkeyId, modifiers | ModNoRepeat, vk))
        {
            _registeredHwnd = hwnd;
            _isRegistered = true;
            _logger.LogInformation("グローバルホットキーを登録しました: {Hotkey}", hotkeySpec);
            return true;
        }

        _logger.LogWarning("グローバルホットキー ({Hotkey}) の登録に失敗しました（他アプリと競合している可能性があります）。", hotkeySpec);
        return false;
    }

    public void Unregister()
    {
        if (_isRegistered && _registeredHwnd != nint.Zero)
        {
            UnregisterHotKey(_registeredHwnd, HotkeyId);
            _isRegistered = false;
            _registeredHwnd = nint.Zero;
        }
    }

    public bool ProcessWindowMessage(int msg, nint wParam)
    {
        if (msg == WmHotKey && wParam.ToInt32() == HotkeyId)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            return true;
        }

        return false;
    }

    public static bool TryParseHotkey(string hotkeySpec, out uint modifiers, out uint virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;

        if (string.IsNullOrWhiteSpace(hotkeySpec))
        {
            return false;
        }

        var parts = hotkeySpec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        foreach (var part in parts)
        {
            var upper = part.ToUpperInvariant();
            switch (upper)
            {
                case "CTRL" or "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!TryMapKey(upper, out virtualKey))
                    {
                        return false;
                    }
                    break;
            }
        }

        return modifiers != 0 && virtualKey != 0;
    }

    private static bool TryMapKey(string token, out uint vk)
    {
        vk = 0;

        // A-Z / 0-9
        if (token.Length == 1 && char.IsAsciiLetterOrDigit(token[0]))
        {
            vk = token[0];
            return true;
        }

        // F1-F12
        if (token.StartsWith('F') &&
            int.TryParse(token.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var fn) &&
            fn is >= 1 and <= 12)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        vk = token switch
        {
            "SPACE" => 0x20,
            "ESC" or "ESCAPE" => 0x1B,
            "PAUSE" => 0x13,
            _ => 0
        };

        return vk != 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hWnd, int id);

    public void Dispose()
    {
        Unregister();
    }
}
