using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using NotifyVox.App.Services;
using NotifyVox.Core.Abstractions;
using NotifyVox.Core.Configuration;
using NotifyVox.Core.Filtering;
using NotifyVox.Core.Models;
using NotifyVox.Core.Pipeline;
using NotifyVox.Core.Voicevox;

namespace NotifyVox.App.ViewModels;

public sealed record SpeakerStyleOption(int? StyleId, string DisplayName);

public sealed record PriorityOption(Priority Value, string DisplayName);

public sealed partial class AppRuleItemViewModel : ObservableObject
{
    private readonly Action _onChanged;
    private readonly Action<AppRuleItemViewModel> _onRemove;

    public AppRuleItemViewModel(
        AppRule model,
        Action onChanged,
        Action<AppRuleItemViewModel> onRemove)
    {
        _onChanged = onChanged;
        _onRemove = onRemove;
        AppId = model.AppId;
        _appName = string.IsNullOrWhiteSpace(model.AppName) ? model.AppId : model.AppName;
        _enabled = model.Enabled;
        _styleId = model.StyleId;
        _priority = model.Priority;
        _templateOverride = model.Template ?? string.Empty;
        _muteKeywordsCsv = string.Join(", ", model.MuteKeywords);
    }

    public string AppId { get; }

    [ObservableProperty]
    private string _appName;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private int? _styleId;

    [ObservableProperty]
    private Priority _priority;

    [ObservableProperty]
    private string _templateOverride;

    [ObservableProperty]
    private string _muteKeywordsCsv;

    partial void OnAppNameChanged(string value) => _onChanged();
    partial void OnEnabledChanged(bool value) => _onChanged();
    partial void OnStyleIdChanged(int? value) => _onChanged();
    partial void OnPriorityChanged(Priority value) => _onChanged();
    partial void OnTemplateOverrideChanged(string value) => _onChanged();
    partial void OnMuteKeywordsCsvChanged(string value) => _onChanged();

    [RelayCommand]
    private void Remove() => _onRemove(this);

    public AppRule ToModel()
    {
        var keywords = MuteKeywordsCsv
            .Split([',', '、', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new AppRule
        {
            AppId = AppId,
            AppName = AppName,
            Enabled = Enabled,
            StyleId = StyleId,
            Priority = Priority,
            Template = string.IsNullOrWhiteSpace(TemplateOverride) ? null : TemplateOverride.Trim(),
            MuteKeywords = keywords
        };
    }
}

public sealed record HistoryRowViewModel(
    string TimeDisplay,
    string AppName,
    string Title,
    string Body,
    string StatusDisplay,
    bool WasSpoken
)
{
    public static HistoryRowViewModel FromEntry(HistoryEntry e)
    {
        var local = e.Timestamp.ToLocalTime().ToString("MM/dd HH:mm:ss", CultureInfo.InvariantCulture);
        var status = e.WasSpoken
            ? "読み上げ済"
            : (string.IsNullOrWhiteSpace(e.Reason) ? "スキップ" : $"スキップ ({e.Reason})");

        return new HistoryRowViewModel(
            local,
            e.AppName,
            e.Title ?? string.Empty,
            e.Body,
            status,
            e.WasSpoken);
    }
}

/// <summary>
/// 設定ウィンドウ (4タブ: アプリ一覧 / 読み上げ / ミュート / 全般＋履歴) の ViewModel (SPEC §4.6, §4.7, F25)
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ISettingsStore _settingsStore;
    private readonly FilterPolicy _filterPolicy;
    private readonly INotificationWatcher _notificationWatcher;
    private readonly VoicevoxEngineManager _engineManager;
    private readonly NotificationPipeline _pipeline;
    private readonly IAudioPlayer _audioPlayer;
    private readonly IHistoryStore _historyStore;
    private readonly IStartupTaskService _startupTaskService;
    private readonly TrayController _trayController;

    private bool _suppressSave;

    public MainViewModel(
        ISettingsStore settingsStore,
        FilterPolicy filterPolicy,
        INotificationWatcher notificationWatcher,
        VoicevoxEngineManager engineManager,
        NotificationPipeline pipeline,
        IAudioPlayer audioPlayer,
        IHistoryStore historyStore,
        IStartupTaskService startupTaskService,
        TrayController trayController)
    {
        _settingsStore = settingsStore;
        _filterPolicy = filterPolicy;
        _notificationWatcher = notificationWatcher;
        _engineManager = engineManager;
        _pipeline = pipeline;
        _audioPlayer = audioPlayer;
        _historyStore = historyStore;
        _startupTaskService = startupTaskService;
        _trayController = trayController;

        PriorityOptions =
        [
            new PriorityOption(Priority.Normal, "通常"),
            new PriorityOption(Priority.High, "高 (割込み)")
        ];

        LoadFromSettings(_settingsStore.Current);
        RebuildSpeakerOptions(_engineManager.Speakers);
        RefreshOutputDevices();
        ReloadHistory();
        UpdateWatcherBanner();
        UpdateEngineBanner();
        UpdateHotkeyStatus();

        _isManuallyMuted = _filterPolicy.IsManuallyMuted;

        _settingsStore.SettingsChanged += OnExternalSettingsChanged;
        _filterPolicy.ManualMuteChanged += OnManualMuteChanged;
        _notificationWatcher.AccessStateChanged += OnAccessStateChanged;
        _engineManager.ConnectionStateChanged += OnEngineConnectionChanged;
        _engineManager.SpeakersUpdated += OnSpeakersUpdated;
        _historyStore.EntryAdded += OnHistoryEntryAdded;
        _trayController.HotkeyRegistrationChanged += OnHotkeyRegistrationChanged;

        _ = LoadStartupTaskStateAsync();
    }

    public IReadOnlyList<PriorityOption> PriorityOptions { get; }

    public ObservableCollection<AppRuleItemViewModel> AppRules { get; } = [];
    public ObservableCollection<SpeakerStyleOption> DefaultSpeakerOptions { get; } = [];
    public ObservableCollection<SpeakerStyleOption> PerAppSpeakerOptions { get; } = [];
    public ObservableCollection<AudioDeviceInfo> OutputDevices { get; } = [];
    public ObservableCollection<HistoryRowViewModel> RecentHistory { get; } = [];

    // --- 通知アクセスバナー (Tab 1) ---
    [ObservableProperty]
    private bool _isNotificationAccessWarningVisible;

    [ObservableProperty]
    private string _notificationAccessMessage = string.Empty;

    [ObservableProperty]
    private string _newAppId = string.Empty;

    [ObservableProperty]
    private string _newAppName = string.Empty;

    // --- 読み上げ設定 (Tab 2) ---
    [ObservableProperty]
    private string _engineStatusText = string.Empty;

    [ObservableProperty]
    private bool _isEngineConnected;

    [ObservableProperty]
    private int _defaultStyleId = 3;

    [ObservableProperty]
    private double _speedScale = 1.05;

    [ObservableProperty]
    private double _pitchScale;

    [ObservableProperty]
    private double _intonationScale = 1.0;

    [ObservableProperty]
    private double _volumePercent = 80;

    [ObservableProperty]
    private string _selectedOutputDeviceId = string.Empty;

    [ObservableProperty]
    private string _readingTemplate = "{app}。{title}。{body}";

    [ObservableProperty]
    private int _maxCharsPerNotification = 200;

    [ObservableProperty]
    private bool _appendOmittedSuffix = true;

    [ObservableProperty]
    private bool _speakUrlAsWord = true;

    // --- ミュート設定 (Tab 3) ---
    [ObservableProperty]
    private bool _isManuallyMuted;

    [ObservableProperty]
    private string _globalKeywordsText = string.Empty;

    [ObservableProperty]
    private bool _quietHoursEnabled;

    [ObservableProperty]
    private string _quietHoursStart = "23:00";

    [ObservableProperty]
    private string _quietHoursEnd = "07:00";

    [ObservableProperty]
    private bool _muteWhenFullscreen;

    [ObservableProperty]
    private string _hotkeyText = "Ctrl+Alt+M";

    [ObservableProperty]
    private string _hotkeyStatusText = string.Empty;

    // --- 全般・履歴設定 (Tab 4) ---
    [ObservableProperty]
    private bool _launchOnWindowsStartup;

    [ObservableProperty]
    private string _startupStatusMessage = string.Empty;

    [ObservableProperty]
    private bool _readUnreadOnStartup;

    [ObservableProperty]
    private int _maxUnreadOnStartup = 3;

    [ObservableProperty]
    private string _voicevoxEndpoint = "http://127.0.0.1:50021";

    [ObservableProperty]
    private bool _isNonLoopbackEndpointWarningVisible;

    [ObservableProperty]
    private string _engineExePath = string.Empty;

    [ObservableProperty]
    private bool _autoStartEngine;

    [ObservableProperty]
    private int _historyRetentionDays = 7;

    // --- Property Change Hooks (SPEC F25: 設定変更は再起動なしで即時反映) ---
    partial void OnDefaultStyleIdChanged(int value) => PersistIfInteractive();
    partial void OnSpeedScaleChanged(double value) => PersistIfInteractive();
    partial void OnPitchScaleChanged(double value) => PersistIfInteractive();
    partial void OnIntonationScaleChanged(double value) => PersistIfInteractive();
    partial void OnVolumePercentChanged(double value) => PersistIfInteractive();
    partial void OnSelectedOutputDeviceIdChanged(string value) => PersistIfInteractive();
    partial void OnReadingTemplateChanged(string value) => PersistIfInteractive();
    partial void OnMaxCharsPerNotificationChanged(int value) => PersistIfInteractive();
    partial void OnAppendOmittedSuffixChanged(bool value) => PersistIfInteractive();
    partial void OnSpeakUrlAsWordChanged(bool value) => PersistIfInteractive();
    partial void OnGlobalKeywordsTextChanged(string value) => PersistIfInteractive();
    partial void OnQuietHoursEnabledChanged(bool value) => PersistIfInteractive();
    partial void OnQuietHoursStartChanged(string value) => PersistIfInteractive();
    partial void OnQuietHoursEndChanged(string value) => PersistIfInteractive();
    partial void OnMuteWhenFullscreenChanged(bool value) => PersistIfInteractive();
    partial void OnHotkeyTextChanged(string value) => PersistIfInteractive();
    partial void OnReadUnreadOnStartupChanged(bool value) => PersistIfInteractive();
    partial void OnMaxUnreadOnStartupChanged(int value) => PersistIfInteractive();
    partial void OnEngineExePathChanged(string value) => PersistIfInteractive();
    partial void OnAutoStartEngineChanged(bool value) => PersistIfInteractive();
    partial void OnHistoryRetentionDaysChanged(int value) => PersistIfInteractive();

    partial void OnVoicevoxEndpointChanged(string value)
    {
        IsNonLoopbackEndpointWarningVisible = !VoicevoxSettings.IsLoopbackEndpoint(value);
        PersistIfInteractive();
    }

    partial void OnIsManuallyMutedChanged(bool value)
    {
        if (_filterPolicy.IsManuallyMuted != value)
        {
            _filterPolicy.IsManuallyMuted = value;
            if (value)
            {
                _pipeline.StopCurrentSpeech();
            }
        }
    }

    partial void OnLaunchOnWindowsStartupChanged(bool value)
    {
        if (_suppressSave)
        {
            return;
        }

        _ = ApplyStartupTaskToggleAsync(value);
    }

    // --- Commands ---

    [RelayCommand]
#pragma warning disable CA1822 // RelayCommand requires instance method for XAML property generation
    private void OpenWindowsNotificationSettings()
    {
        OpenShellUri("ms-settings:privacy-notifications");
    }
#pragma warning restore CA1822

    [RelayCommand]
    private async Task RecheckNotificationAccessAsync()
    {
        await _notificationWatcher.InitializeAndStartAsync().ConfigureAwait(true);
        UpdateWatcherBanner();
    }

    [RelayCommand]
    private void AddManualApp()
    {
        var id = NewAppId.Trim();
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var name = string.IsNullOrWhiteSpace(NewAppName) ? id : NewAppName.Trim();
        if (AppRules.Any(a => string.Equals(a.AppId, id, StringComparison.OrdinalIgnoreCase)))
        {
            NewAppId = string.Empty;
            NewAppName = string.Empty;
            return;
        }

        var rule = new AppRule { AppId = id, AppName = name, Enabled = true };
        AppRules.Add(new AppRuleItemViewModel(rule, PersistIfInteractive, RemoveAppRule));
        NewAppId = string.Empty;
        NewAppName = string.Empty;
        PersistIfInteractive();
    }

    [RelayCommand]
    private void EnableAllApps()
    {
        _suppressSave = true;
        foreach (var item in AppRules)
        {
            item.Enabled = true;
        }
        _suppressSave = false;
        PersistIfInteractive();
    }

    [RelayCommand]
    private void DisableAllApps()
    {
        _suppressSave = true;
        foreach (var item in AppRules)
        {
            item.Enabled = false;
        }
        _suppressSave = false;
        PersistIfInteractive();
    }

    [RelayCommand]
    private async Task RefreshVoicevoxAsync()
    {
        await _engineManager.RefreshNowAsync().ConfigureAwait(true);
        UpdateEngineBanner();
        RebuildSpeakerOptions(_engineManager.Speakers);
    }

    [RelayCommand]
    private async Task TestSpeechAsync()
    {
        var ok = await _pipeline.EnqueueTestSpeechAsync(styleIdOverride: DefaultStyleId).ConfigureAwait(true);
        if (!ok)
        {
            MessageBox.Show(
                "VOICEVOX エンジンへ接続できませんでした。\nVOICEVOX エディタまたはエンジン (0.26.0+) が起動しているか確認してください。",
                "NotifyVox — VOICEVOX 未接続",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private void StopSpeech() => _pipeline.StopCurrentSpeech();

    [RelayCommand]
    private void RefreshOutputDevices()
    {
        var currentDevice = SelectedOutputDeviceId;
        var devices = _audioPlayer.GetOutputDevices();

        _suppressSave = true;
        OutputDevices.Clear();
        foreach (var d in devices)
        {
            OutputDevices.Add(d);
        }

        if (!string.IsNullOrEmpty(currentDevice) &&
            !OutputDevices.Any(d => string.Equals(d.Id, currentDevice, StringComparison.Ordinal)))
        {
            OutputDevices.Add(new AudioDeviceInfo(currentDevice, $"(未接続デバイス) {currentDevice}"));
        }

        SelectedOutputDeviceId = currentDevice;
        _suppressSave = false;
    }

    [RelayCommand]
    private void BrowseEngineExe()
    {
        var dialog = new OpenFileDialog
        {
            Title = "VOICEVOX または VOICEVOX ENGINE (run.exe / VOICEVOX.exe) を選択",
            Filter = "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            EngineExePath = dialog.FileName;
        }
    }

    [RelayCommand]
#pragma warning disable CA1822
    private void OpenVoicevoxWebsite() => OpenShellUri("https://voicevox.hiroshiba.jp/");

    [RelayCommand]
    private void OpenLogsDirectory()
    {
        Directory.CreateDirectory(NotifyVoxPaths.DefaultLogsDirectory);
        OpenShellUri(NotifyVoxPaths.DefaultLogsDirectory);
    }

    [RelayCommand]
    private void OpenSettingsDirectory()
    {
        Directory.CreateDirectory(NotifyVoxPaths.SettingsDirectory);
        OpenShellUri(NotifyVoxPaths.SettingsDirectory);
    }
#pragma warning restore CA1822

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        await _historyStore.ClearAsync().ConfigureAwait(true);
        RecentHistory.Clear();
    }

    private void RemoveAppRule(AppRuleItemViewModel item)
    {
        if (AppRules.Remove(item))
        {
            PersistIfInteractive();
        }
    }

    private void LoadFromSettings(AppSettings s)
    {
        _suppressSave = true;
        try
        {
            DefaultStyleId = s.Voicevox.DefaultStyleId;
            SpeedScale = s.Voicevox.SpeedScale;
            PitchScale = s.Voicevox.PitchScale;
            IntonationScale = s.Voicevox.IntonationScale;
            VolumePercent = Math.Round(s.Voicevox.Volume * 100.0);
            SelectedOutputDeviceId = s.Voicevox.OutputDevice;
            VoicevoxEndpoint = s.Voicevox.Endpoint;
            IsNonLoopbackEndpointWarningVisible = !VoicevoxSettings.IsLoopbackEndpoint(s.Voicevox.Endpoint);
            EngineExePath = s.Voicevox.EngineExePath;
            AutoStartEngine = s.Voicevox.AutoStartEngine;

            ReadingTemplate = s.Reading.Template;
            MaxCharsPerNotification = s.Reading.MaxCharsPerNotification;
            AppendOmittedSuffix = s.Reading.AppendOmittedSuffix;
            SpeakUrlAsWord = s.Reading.SpeakUrlAsWord;
            ReadUnreadOnStartup = s.Reading.ReadUnreadOnStartup;
            MaxUnreadOnStartup = s.Reading.MaxUnreadOnStartup;

            GlobalKeywordsText = string.Join(Environment.NewLine, s.Mute.Keywords);
            QuietHoursEnabled = s.Mute.QuietHours.Enabled;
            QuietHoursStart = s.Mute.QuietHours.Start;
            QuietHoursEnd = s.Mute.QuietHours.End;
            MuteWhenFullscreen = s.Mute.MuteWhenFullscreen;
            HotkeyText = s.Mute.Hotkey;

            HistoryRetentionDays = s.History.RetentionDays;

            AppRules.Clear();
            foreach (var rule in s.Apps)
            {
                AppRules.Add(new AppRuleItemViewModel(rule, PersistIfInteractive, RemoveAppRule));
            }
        }
        finally
        {
            _suppressSave = false;
        }
    }

    private void PersistIfInteractive()
    {
        if (_suppressSave)
        {
            return;
        }

        var globalKeywords = GlobalKeywordsText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var updated = new AppSettings
        {
            Version = 1,
            Voicevox = new VoicevoxSettings
            {
                Endpoint = string.IsNullOrWhiteSpace(VoicevoxEndpoint) ? "http://127.0.0.1:50021" : VoicevoxEndpoint.Trim(),
                EngineExePath = EngineExePath.Trim(),
                AutoStartEngine = AutoStartEngine,
                DefaultStyleId = DefaultStyleId,
                SpeedScale = Math.Round(Math.Clamp(SpeedScale, 0.5, 2.0), 2),
                PitchScale = Math.Round(Math.Clamp(PitchScale, -0.15, 0.15), 2),
                IntonationScale = Math.Round(Math.Clamp(IntonationScale, 0.0, 2.0), 2),
                Volume = (float)Math.Round(Math.Clamp(VolumePercent / 100.0, 0.0, 1.5), 2),
                OutputDevice = SelectedOutputDeviceId ?? string.Empty
            },
            Reading = new ReadingSettings
            {
                Template = string.IsNullOrWhiteSpace(ReadingTemplate) ? "{app}。{title}。{body}" : ReadingTemplate,
                MaxCharsPerNotification = Math.Clamp(MaxCharsPerNotification, 10, 2000),
                AppendOmittedSuffix = AppendOmittedSuffix,
                SpeakUrlAsWord = SpeakUrlAsWord,
                ReadUnreadOnStartup = ReadUnreadOnStartup,
                MaxUnreadOnStartup = Math.Clamp(MaxUnreadOnStartup, 1, 20)
            },
            Apps = AppRules.Select(r => r.ToModel()).ToArray(),
            Mute = new MuteSettings
            {
                Keywords = globalKeywords,
                QuietHours = new QuietHoursSettings
                {
                    Enabled = QuietHoursEnabled,
                    Start = QuietHoursStart.Trim(),
                    End = QuietHoursEnd.Trim()
                },
                MuteWhenFullscreen = MuteWhenFullscreen,
                Hotkey = HotkeyText.Trim()
            },
            History = new HistorySettings
            {
                RetentionDays = Math.Clamp(HistoryRetentionDays, 1, 90)
            }
        };

        _settingsStore.Save(updated);
    }

    private void RebuildSpeakerOptions(IReadOnlyList<VoicevoxSpeakerInfo> speakers)
    {
        var savedDefault = DefaultStyleId;
        var savedPerApp = AppRules.ToDictionary(r => r, r => r.StyleId);

        _suppressSave = true;
        try
        {
            DefaultSpeakerOptions.Clear();
            PerAppSpeakerOptions.Clear();
            PerAppSpeakerOptions.Add(new SpeakerStyleOption(null, "(既定話者を使用)"));

            foreach (var sp in speakers)
            {
                foreach (var st in sp.Styles)
                {
                    var tag = st.SupportsStreaming ? "⚡streaming" : "standard";
                    var label = $"{sp.Name} — {st.Name} (ID: {st.Id}, {tag})";
                    DefaultSpeakerOptions.Add(new SpeakerStyleOption(st.Id, label));
                    PerAppSpeakerOptions.Add(new SpeakerStyleOption(st.Id, label));
                }
            }

            if (DefaultSpeakerOptions.All(o => o.StyleId != savedDefault))
            {
                var fallbackLabel = speakers.Count == 0
                    ? $"スタイル ID: {savedDefault} (エンジン接続後に話者名を表示)"
                    : $"スタイル ID: {savedDefault}";
                DefaultSpeakerOptions.Insert(0, new SpeakerStyleOption(savedDefault, fallbackLabel));
                PerAppSpeakerOptions.Add(new SpeakerStyleOption(savedDefault, fallbackLabel));
            }

            DefaultStyleId = savedDefault;
            foreach (var kv in savedPerApp)
            {
                if (kv.Value is int customId && PerAppSpeakerOptions.All(o => o.StyleId != customId))
                {
                    PerAppSpeakerOptions.Add(new SpeakerStyleOption(customId, $"スタイル ID: {customId}"));
                }
                kv.Key.StyleId = kv.Value;
            }
        }
        finally
        {
            _suppressSave = false;
        }
    }

    private void ReloadHistory()
    {
        RecentHistory.Clear();
        foreach (var entry in _historyStore.GetRecent(200))
        {
            RecentHistory.Add(HistoryRowViewModel.FromEntry(entry));
        }
    }

    private void UpdateWatcherBanner()
    {
        var state = _notificationWatcher.AccessState;
        IsNotificationAccessWarningVisible = state != NotificationAccessState.Allowed;
        NotificationAccessMessage = state switch
        {
            NotificationAccessState.Allowed => "Windows 通知アクセス: 許可済み（監視中）",
            NotificationAccessState.Denied => "Windows の通知アクセスが許可されていません。下のボタンから「プライバシーとセキュリティ > 通知」を開き、NotifyVox による通知へのアクセスを ON にした後「再チェック」を押してください。",
            NotificationAccessState.Error => _notificationWatcher.LastErrorMessage ?? "通知監視の初期化中にエラーが発生しました。",
            _ => "通知アクセス状態を確認しています..."
        };
    }

    private void UpdateEngineBanner()
    {
        IsEngineConnected = _engineManager.IsConnected;
        EngineStatusText = _engineManager.IsConnected
            ? $"接続済み (VOICEVOX ENGINE {_engineManager.EngineVersion})"
            : "未接続 — VOICEVOX エディタまたはエンジン (0.26.0+) を起動してください（30秒ごとに自動再接続）";
    }

    private void UpdateHotkeyStatus()
    {
        if (string.IsNullOrWhiteSpace(HotkeyText))
        {
            HotkeyStatusText = "ホットキー無効";
        }
        else
        {
            HotkeyStatusText = _trayController.IsHotkeyActive
                ? $"登録済み ({HotkeyText})"
                : $"未登録 ({HotkeyText} — 書式不正または他アプリと競合)";
        }
    }

    private async Task LoadStartupTaskStateAsync()
    {
        var enabled = await _startupTaskService.IsEnabledAsync().ConfigureAwait(true);
        _suppressSave = true;
        LaunchOnWindowsStartup = enabled;
        _suppressSave = false;
        StartupStatusMessage = enabled ? "有効 (Windows サインイン時に自動起動)" : "無効";
    }

    private async Task ApplyStartupTaskToggleAsync(bool enable)
    {
        var (actualEnabled, message) = await _startupTaskService.SetEnabledAsync(enable).ConfigureAwait(true);
        _suppressSave = true;
        LaunchOnWindowsStartup = actualEnabled;
        _suppressSave = false;
        StartupStatusMessage = message ?? (actualEnabled ? "有効 (Windows サインイン時に自動起動)" : "無効");
    }

    private void OnExternalSettingsChanged(object? sender, AppSettings settings)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // バックグラウンドで新規発見されたアプリ候補があれば UI リストへ差分追加する
            var existingIds = AppRules.Select(a => a.AppId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in settings.Apps)
            {
                if (!existingIds.Contains(rule.AppId))
                {
                    AppRules.Add(new AppRuleItemViewModel(rule, PersistIfInteractive, RemoveAppRule));
                }
            }
        });
    }

    private void OnManualMuteChanged(object? sender, bool muted)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => IsManuallyMuted = muted);
    }

    private void OnAccessStateChanged(object? sender, NotificationAccessState state)
    {
        Application.Current?.Dispatcher.InvokeAsync(UpdateWatcherBanner);
    }

    private void OnEngineConnectionChanged(object? sender, bool connected)
    {
        Application.Current?.Dispatcher.InvokeAsync(UpdateEngineBanner);
    }

    private void OnSpeakersUpdated(object? sender, IReadOnlyList<VoicevoxSpeakerInfo> speakers)
    {
        Application.Current?.Dispatcher.InvokeAsync(() => RebuildSpeakerOptions(speakers));
    }

    private void OnHistoryEntryAdded(object? sender, HistoryEntry entry)
    {
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            RecentHistory.Insert(0, HistoryRowViewModel.FromEntry(entry));
            while (RecentHistory.Count > 200)
            {
                RecentHistory.RemoveAt(RecentHistory.Count - 1);
            }
        });
    }

    private void OnHotkeyRegistrationChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.InvokeAsync(UpdateHotkeyStatus);
    }

    private static void OpenShellUri(string uriOrPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uriOrPath,
                UseShellExecute = true
            });
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _settingsStore.SettingsChanged -= OnExternalSettingsChanged;
        _filterPolicy.ManualMuteChanged -= OnManualMuteChanged;
        _notificationWatcher.AccessStateChanged -= OnAccessStateChanged;
        _engineManager.ConnectionStateChanged -= OnEngineConnectionChanged;
        _engineManager.SpeakersUpdated -= OnSpeakersUpdated;
        _historyStore.EntryAdded -= OnHistoryEntryAdded;
        _trayController.HotkeyRegistrationChanged -= OnHotkeyRegistrationChanged;
    }
}
