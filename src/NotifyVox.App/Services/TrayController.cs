using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Filtering;
using NotifyVox.Core.Models;
using NotifyVox.Core.Pipeline;
using NotifyVox.Core.Voicevox;
using NotifyVox.Windows.Interop;

namespace NotifyVox.App.Services;

/// <summary>
/// タスクトレイアイコン・コンテキストメニュー・グローバルホットキーメッセージ受信を管理するコントローラ (SPEC §4.6 F26, §6.1)
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly FilterPolicy _filterPolicy;
    private readonly INotificationWatcher _notificationWatcher;
    private readonly VoicevoxEngineManager _engineManager;
    private readonly NotificationPipeline _pipeline;
    private readonly GlobalHotkeyManager _hotkeyManager;
    private readonly Action _openSettingsAction;
    private readonly Action _exitApplicationAction;
    private readonly ILogger<TrayController> _logger;

    private TaskbarIcon? _taskbarIcon;
    private MenuItem? _muteMenuItem;
    private HwndSource? _messageHwndSource;
    private string _registeredHotkeySpec = string.Empty;
    private bool _isHotkeyRegistered;

    public TrayController(
        ISettingsStore settingsStore,
        FilterPolicy filterPolicy,
        INotificationWatcher notificationWatcher,
        VoicevoxEngineManager engineManager,
        NotificationPipeline pipeline,
        GlobalHotkeyManager hotkeyManager,
        Action openSettingsAction,
        Action exitApplicationAction,
        ILogger<TrayController> logger)
    {
        _settingsStore = settingsStore;
        _filterPolicy = filterPolicy;
        _notificationWatcher = notificationWatcher;
        _engineManager = engineManager;
        _pipeline = pipeline;
        _hotkeyManager = hotkeyManager;
        _openSettingsAction = openSettingsAction;
        _exitApplicationAction = exitApplicationAction;
        _logger = logger;
    }

    public bool IsHotkeyActive => _isHotkeyRegistered;

    public event EventHandler? HotkeyRegistrationChanged;

    public void Initialize()
    {
        // メッセージ専用ウィンドウ (HWND_MESSAGE = -3) を作成してグローバルホットキーを常駐待ち受けする
        var parameters = new HwndSourceParameters("NotifyVoxHotkeySink")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = new nint(-3)
        };
        _messageHwndSource = new HwndSource(parameters);
        _messageHwndSource.AddHook(WndProc);

        _hotkeyManager.HotkeyPressed += OnHotkeyPressed;
        ApplyHotkey(_settingsStore.Current.Mute.Hotkey);

        _muteMenuItem = new MenuItem
        {
            Header = "ミュート (_M)",
            IsCheckable = true,
            IsChecked = _filterPolicy.IsManuallyMuted
        };
        _muteMenuItem.Click += (_, _) => ToggleMuteFromUi();

        var stopCurrentItem = new MenuItem { Header = "現在の読み上げを停止 (_S)" };
        stopCurrentItem.Click += (_, _) => _pipeline.StopCurrentSpeech();

        var clearQueueItem = new MenuItem { Header = "キューを全消去 (_C)" };
        clearQueueItem.Click += (_, _) => _pipeline.ClearAllSpeech();

        var testSpeakItem = new MenuItem { Header = "読み上げテスト (_T)" };
        testSpeakItem.Click += async (_, _) =>
        {
            var ok = await _pipeline.EnqueueTestSpeechAsync().ConfigureAwait(true);
            if (!ok)
            {
                ShowBalloon("VOICEVOX 未接続", "VOICEVOX エディタまたはエンジンを起動してください。", NotificationIcon.Warning);
            }
        };

        var settingsItem = new MenuItem
        {
            Header = "設定を開く (_O)...",
            FontWeight = FontWeights.SemiBold
        };
        settingsItem.Click += (_, _) => _openSettingsAction();

        var exitItem = new MenuItem { Header = "終了 (_X)" };
        exitItem.Click += (_, _) => _exitApplicationAction();

        var menu = new ContextMenu();
        menu.Items.Add(_muteMenuItem);
        menu.Items.Add(stopCurrentItem);
        menu.Items.Add(clearQueueItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(testSpeakItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new Separator());
        menu.Items.Add(exitItem);

        _taskbarIcon = new TaskbarIcon
        {
            ContextMenu = menu,
            DoubleClickCommand = new RelayCommand(_openSettingsAction),
            NoLeftClickDelay = false
        };

        UpdateVisualState();
        // EcoQoS (Efficiency Mode) による音声・通知遅延を防ぐため false を指定
        _taskbarIcon.ForceCreate(enablesEfficiencyMode: false);

        _filterPolicy.ManualMuteChanged += OnManualMuteChanged;
        _engineManager.ConnectionStateChanged += OnEngineConnectionChanged;
        _engineManager.ManualStartGuidanceRequested += OnManualStartGuidanceRequested;
        _notificationWatcher.AccessStateChanged += OnAccessStateChanged;
        _settingsStore.SettingsChanged += OnSettingsChanged;
    }

    public void ShowBalloon(string title, string message, NotificationIcon icon = NotificationIcon.Info)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            try
            {
                _taskbarIcon?.ShowNotification(title, message, icon);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "トレイバルーン通知の表示に失敗しました。");
            }
        });
    }

    public void UpdateVisualState()
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (_taskbarIcon is null)
            {
                return;
            }

            if (_muteMenuItem is not null)
            {
                _muteMenuItem.IsChecked = _filterPolicy.IsManuallyMuted;
            }

            TrayVisualState visualState;
            string statusLine;

            if (_notificationWatcher.AccessState is NotificationAccessState.Denied or NotificationAccessState.Error)
            {
                visualState = TrayVisualState.PermissionOrError;
                statusLine = _notificationWatcher.AccessState == NotificationAccessState.Denied
                    ? "通知アクセス未許可"
                    : "通知取得エラー";
            }
            else if (_filterPolicy.IsManuallyMuted)
            {
                visualState = TrayVisualState.Muted;
                statusLine = "ミュート中";
            }
            else if (!_engineManager.IsConnected)
            {
                visualState = TrayVisualState.EngineDisconnected;
                statusLine = "VOICEVOX 未接続";
            }
            else
            {
                visualState = TrayVisualState.Ready;
                statusLine = $"待機中 (VOICEVOX {_engineManager.EngineVersion})";
            }

            _taskbarIcon.Icon = TrayIconRenderer.GetIcon(visualState);
            _taskbarIcon.ToolTipText = $"NotifyVox — {statusLine}";
        });
    }

    private void ToggleMuteFromUi()
    {
        var muted = _filterPolicy.ToggleManualMute();
        if (muted)
        {
            // ミュートにした瞬間、再生中の音声も止める (SPEC U4: 会議・配信時に即座に黙らせる)
            _pipeline.StopCurrentSpeech();
        }
        UpdateVisualState();
    }

    private void OnHotkeyPressed(object? sender, EventArgs e)
    {
        var muted = _filterPolicy.ToggleManualMute();
        if (muted)
        {
            _pipeline.StopCurrentSpeech();
        }
        UpdateVisualState();
    }

    private void OnManualMuteChanged(object? sender, bool muted) => UpdateVisualState();

    private void OnEngineConnectionChanged(object? sender, bool connected) => UpdateVisualState();

    private void OnManualStartGuidanceRequested(object? sender, EventArgs e)
    {
        ShowBalloon(
            "VOICEVOX エンジンが起動していません",
            "読み上げを行うには VOICEVOX を起動してください（設定画面から自動起動も設定可能です）。",
            NotificationIcon.Warning);
    }

    private void OnAccessStateChanged(object? sender, NotificationAccessState state)
    {
        UpdateVisualState();
        if (state == NotificationAccessState.Denied)
        {
            ShowBalloon(
                "通知アクセス許可が必要です",
                "NotifyVox の設定画面を開き、Windows の通知アクセスを許可してください。",
                NotificationIcon.Warning);
        }
        else if (state == NotificationAccessState.Error && !string.IsNullOrWhiteSpace(_notificationWatcher.LastErrorMessage))
        {
            ShowBalloon(
                "通知監視を初期化できませんでした",
                _notificationWatcher.LastErrorMessage,
                NotificationIcon.Error);
        }
    }

    private void OnSettingsChanged(object? sender, AppSettings settings)
    {
        var desired = settings.Mute.Hotkey ?? string.Empty;
        if (!string.Equals(desired, _registeredHotkeySpec, StringComparison.OrdinalIgnoreCase))
        {
            Application.Current?.Dispatcher.InvokeAsync(() => ApplyHotkey(desired));
        }
    }

    private void ApplyHotkey(string hotkeySpec)
    {
        _registeredHotkeySpec = hotkeySpec;
        if (_messageHwndSource is null)
        {
            _isHotkeyRegistered = false;
            return;
        }

        _isHotkeyRegistered = string.IsNullOrWhiteSpace(hotkeySpec)
            ? false
            : _hotkeyManager.Register(_messageHwndSource.Handle, hotkeySpec);

        HotkeyRegistrationChanged?.Invoke(this, EventArgs.Empty);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (_hotkeyManager.ProcessWindowMessage(msg, wParam))
        {
            handled = true;
        }

        return nint.Zero;
    }

    public void Dispose()
    {
        _filterPolicy.ManualMuteChanged -= OnManualMuteChanged;
        _engineManager.ConnectionStateChanged -= OnEngineConnectionChanged;
        _engineManager.ManualStartGuidanceRequested -= OnManualStartGuidanceRequested;
        _notificationWatcher.AccessStateChanged -= OnAccessStateChanged;
        _settingsStore.SettingsChanged -= OnSettingsChanged;
        _hotkeyManager.HotkeyPressed -= OnHotkeyPressed;

        _hotkeyManager.Dispose();

        if (_messageHwndSource is not null)
        {
            _messageHwndSource.RemoveHook(WndProc);
            _messageHwndSource.Dispose();
            _messageHwndSource = null;
        }

        _taskbarIcon?.Dispose();
        _taskbarIcon = null;
    }
}
