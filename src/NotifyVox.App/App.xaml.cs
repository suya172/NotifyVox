using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NotifyVox.App.Services;
using NotifyVox.App.ViewModels;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Filtering;
using NotifyVox.Core.History;
using NotifyVox.Core.Models;
using NotifyVox.Core.Pipeline;
using NotifyVox.Core.Queueing;
using NotifyVox.Core.Voicevox;
using NotifyVox.Windows.Audio;
using NotifyVox.Windows.Interop;
using NotifyVox.Windows.Notifications;
using NotifyVox.Windows.Startup;
using Serilog;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;

namespace NotifyVox.App;

/// <summary>
/// NotifyVox アプリケーションエントリポイント・DI構成・常駐ライフサイクル管理 (SPEC §3.1, §5)
/// </summary>
public partial class App : Application, IDisposable
{
    private const string SingleInstanceMutexName = @"Local\NotifyVox_SingleInstance_Mutex";
    private const string ShowSettingsEventName = @"Local\NotifyVox_ShowSettings_Event";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _showSettingsEvent;
    private CancellationTokenSource? _appLifetimeCts;
    private IHost? _host;
    private TrayController? _trayController;
    private MainWindow? _settingsWindow;
    private bool _isExiting;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 二重起動防止: 既に常駐している場合は既存プロセスの設定画面を前面に開いて終了する
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out var createdNew);
        if (!createdNew)
        {
            SignalExistingInstance();
            Shutdown(0);
            return;
        }

        ConfigureLogging();
        RegisterGlobalExceptionHandlers();

        _appLifetimeCts = new CancellationTokenSource();
        StartShowSettingsActivationListener(_appLifetimeCts.Token);

        try
        {
            _host = BuildHost();
            await _host.StartAsync(_appLifetimeCts.Token).ConfigureAwait(true);

            var services = _host.Services;
            var settingsStore = services.GetRequiredService<ISettingsStore>();
            var filterPolicy = services.GetRequiredService<FilterPolicy>();
            var watcher = services.GetRequiredService<INotificationWatcher>();
            var engineManager = services.GetRequiredService<VoicevoxEngineManager>();
            var pipeline = services.GetRequiredService<NotificationPipeline>();
            var historyStore = services.GetRequiredService<IHistoryStore>();
            var hotkeyManager = services.GetRequiredService<GlobalHotkeyManager>();

            watcher.NotificationReceived += (_, notification) =>
            {
                _ = pipeline.HandleNotificationAsync(notification);
            };

            _trayController = new TrayController(
                settingsStore,
                filterPolicy,
                watcher,
                engineManager,
                pipeline,
                hotkeyManager,
                openSettingsAction: OpenSettingsWindow,
                exitApplicationAction: ExitApplication,
                services.GetRequiredService<ILogger<TrayController>>());
            _trayController.Initialize();

            // 起動時の古い履歴削除と定期削除タスク開始 (SPEC F27)
            _ = RunHistoryMaintenanceLoopAsync(historyStore, settingsStore, _appLifetimeCts.Token);

            // パイプライン・VOICEVOX 監視・Windows 通知監視を開始
            pipeline.Start();
            await engineManager.StartAsync(_appLifetimeCts.Token).ConfigureAwait(true);
            var accessState = await watcher.InitializeAndStartAsync(_appLifetimeCts.Token).ConfigureAwait(true);
            _trayController.UpdateVisualState();

            // Windows サインイン時の自動起動 (StartupTask) や --minimized 指定時はトレイに静かに常駐し、
            // ユーザーが手動起動した場合や通知アクセス未許可時 (SPEC F1) は設定画面を表示する。
            var isStartupTask = IsLaunchedFromStartupTask();
            var isMinimizedArg = e.Args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase));
            if (accessState != NotificationAccessState.Allowed || (!isStartupTask && !isMinimizedArg))
            {
                OpenSettingsWindow();
            }
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "NotifyVox の起動処理中に致命的なエラーが発生しました。");
            MessageBox.Show(
                $"起動中にエラーが発生しました:\n{ex.Message}",
                "NotifyVox エラー",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            ExitApplication();
        }
    }

    private static IHost BuildHost()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddSerilog(dispose: false);

        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<ISettingsStore, SettingsStore>();
        builder.Services.AddSingleton<IHistoryStore, JsonLinesHistoryStore>();
        builder.Services.AddSingleton<IFullscreenDetector, FullscreenDetector>();
        builder.Services.AddSingleton<FilterPolicy>();
        builder.Services.AddSingleton<SpeechQueue>();

        builder.Services.AddHttpClient(nameof(VoicevoxClient));
        builder.Services.AddSingleton<IVoicevoxClient, VoicevoxClient>();
        builder.Services.AddSingleton<VoicevoxEngineManager>();

        builder.Services.AddSingleton<IAudioPlayer, WasapiAudioPlayer>();
        builder.Services.AddSingleton<INotificationWatcher, NotificationWatcher>();
        builder.Services.AddSingleton<IStartupTaskService, StartupTaskService>();
        builder.Services.AddSingleton<GlobalHotkeyManager>();
        builder.Services.AddSingleton<NotificationPipeline>();

        return builder.Build();
    }

    private static void ConfigureLogging()
    {
        var logDir = NotifyVoxPaths.DefaultLogsDirectory;
        Directory.CreateDirectory(logDir);
        var logPath = Path.Combine(logDir, "notifyvox-.log");

        // SPEC §5 / §8: %LOCALAPPDATA%\NotifyVox\logs、最大 5 MB × 3 世代ローテーション。
        // HttpClient のクエリ文字列 (audio_query?text=...) から通知本文がログへ漏洩しないよう Warning 以上に制限する。
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(
                path: logPath,
                formatProvider: CultureInfo.InvariantCulture,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 3,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        Log.Information("NotifyVox を起動しています。");
    }

    private void RegisterGlobalExceptionHandlers()
    {
        // SPEC §5: 例外でプロセスが落ちないよう捕捉してログ出力＋継続
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "UI スレッドで未処理例外が発生しました。");
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Warning(args.Exception, "バックグラウンド Task で未観測例外が発生しました。");
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            Log.Fatal(args.ExceptionObject as Exception, "AppDomain 未処理例外が発生しました (IsTerminating={IsTerminating})。", args.IsTerminating);
        };
    }

    private void OpenSettingsWindow()
    {
        Dispatcher.Invoke(() =>
        {
            if (_host is null || _trayController is null || _isExiting)
            {
                return;
            }

            if (_settingsWindow is { IsLoaded: true })
            {
                if (_settingsWindow.WindowState == WindowState.Minimized)
                {
                    _settingsWindow.WindowState = WindowState.Normal;
                }
                _settingsWindow.Activate();
                return;
            }

            var s = _host.Services;
            var vm = new MainViewModel(
                s.GetRequiredService<ISettingsStore>(),
                s.GetRequiredService<FilterPolicy>(),
                s.GetRequiredService<INotificationWatcher>(),
                s.GetRequiredService<VoicevoxEngineManager>(),
                s.GetRequiredService<NotificationPipeline>(),
                s.GetRequiredService<IAudioPlayer>(),
                s.GetRequiredService<IHistoryStore>(),
                s.GetRequiredService<IStartupTaskService>(),
                _trayController);

            _settingsWindow = new MainWindow(vm);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
            Log.Information("設定ウィンドウを表示しました (Title={Title})。", _settingsWindow.Title);
        });
    }

    private void StartShowSettingsActivationListener(CancellationToken cancellationToken)
    {
        _showSettingsEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        _ = Task.Run(() =>
        {
            var handles = new WaitHandle[] { _showSettingsEvent, cancellationToken.WaitHandle };
            while (!cancellationToken.IsCancellationRequested)
            {
                var signaled = WaitHandle.WaitAny(handles);
                if (signaled == 0)
                {
                    OpenSettingsWindow();
                }
                else
                {
                    break;
                }
            }
        }, CancellationToken.None);
    }

    private static bool IsLaunchedFromStartupTask()
    {
        try
        {
            var activatedArgs = AppInstance.GetActivatedEventArgs();
            return activatedArgs?.Kind == ActivationKind.StartupTask;
        }
        catch
        {
            return false;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShowSettingsEventName);
            ev.Set();
        }
        catch
        {
        }
    }

    private static async Task RunHistoryMaintenanceLoopAsync(
        IHistoryStore historyStore,
        ISettingsStore settingsStore,
        CancellationToken cancellationToken)
    {
        try
        {
            await historyStore.PruneExpiredAsync(settingsStore.Current.History.RetentionDays, cancellationToken)
                .ConfigureAwait(false);

            using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                await historyStore.PruneExpiredAsync(settingsStore.Current.History.RetentionDays, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        Log.Information("NotifyVox を終了しています。");

        try
        {
            _settingsWindow?.Close();
            _trayController?.Dispose();
            _trayController = null;

            if (_appLifetimeCts is not null)
            {
                await _appLifetimeCts.CancelAsync().ConfigureAwait(true);
                _appLifetimeCts.Dispose();
                _appLifetimeCts = null;
            }

            if (_host is not null)
            {
                await _host.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(true);
                _host.Dispose();
                _host = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "終了処理中に例外が発生しました。");
        }
        finally
        {
            _showSettingsEvent?.Dispose();
            if (_singleInstanceMutex is not null)
            {
                try { _singleInstanceMutex.ReleaseMutex(); } catch { }
                _singleInstanceMutex.Dispose();
            }

            await Log.CloseAndFlushAsync().ConfigureAwait(true);
            Shutdown(0);
        }
    }

    public void Dispose()
    {
        _trayController?.Dispose();
        _appLifetimeCts?.Dispose();
        _host?.Dispose();
        _showSettingsEvent?.Dispose();
        _singleInstanceMutex?.Dispose();
        GC.SuppressFinalize(this);
    }
}
