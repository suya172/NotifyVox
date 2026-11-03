using Microsoft.Extensions.Logging;
using NotifyVox.Core.Abstractions;
using Windows.ApplicationModel;

namespace NotifyVox.Windows.Startup;

/// <summary>
/// AppxManifest.xml で宣言された StartupTask ("NotifyVoxStartup") を制御するサービス (SPEC §4.6-4)
/// </summary>
public sealed class StartupTaskService : IStartupTaskService
{
    public const string TaskId = "NotifyVoxStartup";
    private readonly ILogger<StartupTaskService> _logger;

    public StartupTaskService(ILogger<StartupTaskService> logger)
    {
        _logger = logger;
    }

    public async Task<bool> IsEnabledAsync()
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            return task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "StartupTask 状態の取得に失敗しました（非パッケージ実行時の可能性）。");
            return false;
        }
    }

    public async Task<(bool Enabled, string? Message)> SetEnabledAsync(bool enable)
    {
        try
        {
            var task = await StartupTask.GetAsync(TaskId);
            if (enable)
            {
                var newState = await task.RequestEnableAsync();
                return newState switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy =>
                        (true, null),
                    StartupTaskState.DisabledByUser =>
                        (false, "Windows の「スタートアップ アプリ」設定で無効化されているため、Windows 設定から有効化してください。"),
                    StartupTaskState.DisabledByPolicy =>
                        (false, "グループ ポリシーにより自動起動が無効化されています。"),
                    _ =>
                        (false, "自動起動を有効化できませんでした。")
                };
            }

            task.Disable();
            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "StartupTask の切り替えに失敗しました。");
            return (false, "MSIX パッケージとして起動されている場合のみ自動起動を設定できます。");
        }
    }
}
