using System.Windows;
using NotifyVox.App.ViewModels;

namespace NotifyVox.App;

/// <summary>
/// 設定ウィンドウ (SPEC §4.6)
/// カスタムタイトルバー操作 (ドラッグ・最小化・最大化・閉じる) とウィンドウ状態管理
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        StateChanged += MainWindow_StateChanged;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // 最大化時はウィンドウ枠が画面外にはみ出さないようパディングを付与し、外枠ボーダーを非表示にする
            WindowBorder.BorderThickness = new Thickness(0);
            WindowBorder.Padding = new Thickness(8);
            MaximizeButton.Content = "\uE923"; // ChromeRestore
            MaximizeButton.ToolTip = "元に戻す";
        }
        else
        {
            WindowBorder.BorderThickness = new Thickness(1);
            WindowBorder.Padding = new Thickness(0);
            MaximizeButton.Content = "\uE922"; // ChromeMaximize
            MaximizeButton.ToolTip = "最大化";
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // SPEC §5: 設定画面を閉じてもタスクトレイ常駐を維持する
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        StateChanged -= MainWindow_StateChanged;

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
