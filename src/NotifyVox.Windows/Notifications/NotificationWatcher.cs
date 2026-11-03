using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Models;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace NotifyVox.Windows.Notifications;

/// <summary>
/// WinRT UserNotificationListener を用いて Windows アクションセンターの通知をリアルタイム取得する実装 (SPEC §4.1 F1-F5)
/// </summary>
public sealed class NotificationWatcher : INotificationWatcher
{
    private const int MaxSeenNotificationIds = 512;

    private readonly ISettingsStore _settingsStore;
    private readonly ILogger<NotificationWatcher> _logger;
    private readonly Lock _sync = new();
    private readonly HashSet<uint> _seenIds = [];
    private readonly Queue<uint> _seenOrder = new();

    private UserNotificationListener? _listener;
    private bool _subscribed;
    private NotificationAccessState _accessState = NotificationAccessState.Unknown;
    private string? _lastErrorMessage;

    public NotificationWatcher(ISettingsStore settingsStore, ILogger<NotificationWatcher> logger)
    {
        _settingsStore = settingsStore;
        _logger = logger;
    }

    public NotificationAccessState AccessState
    {
        get { lock (_sync) { return _accessState; } }
    }

    public string? LastErrorMessage
    {
        get { lock (_sync) { return _lastErrorMessage; } }
    }

    public event EventHandler<NotificationEvent>? NotificationReceived;
    public event EventHandler<NotificationAccessState>? AccessStateChanged;

    public async Task<NotificationAccessState> InitializeAndStartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            {
                SetState(NotificationAccessState.Error, "このバージョンの Windows は UserNotificationListener に対応していません。");
                return AccessState;
            }

            var listener = UserNotificationListener.Current;
            var status = listener.GetAccessStatus();
            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                status = await listener.RequestAccessAsync().AsTask(cancellationToken).ConfigureAwait(false);
            }

            if (status != UserNotificationListenerAccessStatus.Allowed)
            {
                _logger.LogWarning("Windows 通知アクセスが許可されていません (Status={Status})。", status);
                SetState(NotificationAccessState.Denied, "Windows の通知アクセス許可が必要です。設定画面から許可を有効にしてください。");
                return AccessState;
            }

            // 既存の通知一覧を走査し、ID の既読登録・アプリ候補自動収集・任意の起動時未読読み上げ (SPEC F4) を実行
            await BaselineExistingNotificationsAsync(listener, cancellationToken).ConfigureAwait(false);

            lock (_sync)
            {
                if (!_subscribed)
                {
                    // 非パッケージ起動時はここで COMException (0x80070490 Element not found) が発生する
                    listener.NotificationChanged += OnNotificationChanged;
                    _listener = listener;
                    _subscribed = true;
                }
            }

            SetState(NotificationAccessState.Allowed, null);
            _logger.LogInformation("UserNotificationListener の購読を開始しました。");
            return AccessState;
        }
        catch (COMException ex)
        {
            var hint = ex.HResult == unchecked((int)0x80070490)
                ? "MSIX パッケージ ID が付与されていない状態で起動されました。scripts/register-dev.ps1 -Launch またはスタートメニューから起動してください。"
                : $"通知監視 API の初期化に失敗しました (0x{ex.HResult:X8})。";
            _logger.LogError(ex, "UserNotificationListener 初期化エラー: {Hint}", hint);
            SetState(NotificationAccessState.Error, hint);
            return AccessState;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "UserNotificationListener 初期化中に予期しない例外が発生しました。");
            SetState(NotificationAccessState.Error, ex.Message);
            return AccessState;
        }
    }

    private async Task BaselineExistingNotificationsAsync(
        UserNotificationListener listener,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<UserNotification> existing;
        try
        {
            existing = await listener.GetNotificationsAsync(NotificationKinds.Toast)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "既存通知一覧の取得に失敗しました。");
            return;
        }

        var startupCandidates = new List<NotificationEvent>();

        foreach (var item in existing)
        {
            MarkIdSeen(item.Id);

            var normalized = TryNormalize(item);
            if (normalized is not null)
            {
                // アクションセンターに残っている通知元アプリも候補リストへ事前登録 (SPEC §4.6-1)
                _settingsStore.RegisterDiscoveredApp(normalized.AppId, normalized.AppName);
                startupCandidates.Add(normalized);
            }
        }

        // SPEC F4: 設定で「起動時に未読を最大 N 件まで読む」が有効な場合のみ直近分を発火（既定: OFF）
        var reading = _settingsStore.Current.Reading;
        if (reading.ReadUnreadOnStartup && reading.MaxUnreadOnStartup > 0 && startupCandidates.Count > 0)
        {
            var toRead = startupCandidates
                .OrderByDescending(n => n.ReceivedAt)
                .Take(reading.MaxUnreadOnStartup)
                .Reverse()
                .ToList();

            foreach (var ev in toRead)
            {
                NotificationReceived?.Invoke(this, ev);
            }
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
    {
        if (args.ChangeKind != UserNotificationChangedKind.Added)
        {
            return;
        }

        if (!MarkIdSeen(args.UserNotificationId))
        {
            return;
        }

        try
        {
            var userNotification = sender.GetNotification(args.UserNotificationId);
            if (userNotification is null)
            {
                return;
            }

            var normalized = TryNormalize(userNotification);
            if (normalized is null)
            {
                return;
            }

            NotificationReceived?.Invoke(this, normalized);
        }
        catch (COMException ex)
        {
            // SPEC F5: 権限剥奪や API 例外時は自動リトライせず状態をエラーへ遷移して通知
            _logger.LogError(ex, "通知取得中に COMException が発生しました (0x{HResult:X8})。", ex.HResult);
            UnsubscribeSafe();
            SetState(NotificationAccessState.Error, "通知アクセス権限が失われたか API エラーが発生しました。再許可を確認してください。");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "個別通知 (Id={Id}) の正規化処理中に例外が発生しました。", args.UserNotificationId);
        }
    }

    private static NotificationEvent? TryNormalize(UserNotification userNotification)
    {
        var binding = userNotification.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric);
        if (binding is null)
        {
            return null;
        }

        var textElements = binding.GetTextElements();
        var title = textElements.Count > 0 ? textElements[0].Text ?? string.Empty : string.Empty;

        string body;
        if (textElements.Count <= 1)
        {
            body = string.Empty;
        }
        else if (textElements.Count == 2)
        {
            body = textElements[1].Text ?? string.Empty;
        }
        else
        {
            var sb = new StringBuilder();
            for (var i = 1; i < textElements.Count; i++)
            {
                var line = textElements[i].Text;
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }

                sb.Append(line);
            }

            body = sb.ToString();
        }

        string appId = string.Empty;
        string appName = "不明なアプリ";

        try
        {
            appId = userNotification.AppInfo?.AppUserModelId ?? userNotification.AppInfo?.Id ?? string.Empty;
            var displayName = userNotification.AppInfo?.DisplayInfo?.DisplayName;
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                appName = displayName;
            }
            else if (!string.IsNullOrWhiteSpace(appId))
            {
                appName = appId;
            }
        }
        catch
        {
            // 一部のシステム通知では AppInfo 参照が例外を投げる場合があるためフォールバック維持
        }

        if (string.IsNullOrWhiteSpace(appId))
        {
            appId = appName;
        }

        return new NotificationEvent(
            AppId: appId,
            AppName: appName,
            Title: title,
            Body: body,
            ReceivedAt: userNotification.CreationTime);
    }

    private bool MarkIdSeen(uint id)
    {
        lock (_sync)
        {
            if (!_seenIds.Add(id))
            {
                return false;
            }

            _seenOrder.Enqueue(id);
            while (_seenOrder.Count > MaxSeenNotificationIds)
            {
                var oldest = _seenOrder.Dequeue();
                _seenIds.Remove(oldest);
            }

            return true;
        }
    }

    private void SetState(NotificationAccessState state, string? errorMessage)
    {
        bool changed;
        lock (_sync)
        {
            changed = _accessState != state || _lastErrorMessage != errorMessage;
            _accessState = state;
            _lastErrorMessage = errorMessage;
        }

        if (changed)
        {
            AccessStateChanged?.Invoke(this, state);
        }
    }

    private void UnsubscribeSafe()
    {
        lock (_sync)
        {
            if (_subscribed && _listener is not null)
            {
                try
                {
                    _listener.NotificationChanged -= OnNotificationChanged;
                }
                catch
                {
                }

                _subscribed = false;
            }
        }
    }

    public void Dispose()
    {
        UnsubscribeSafe();
    }
}
