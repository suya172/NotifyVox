using System.Windows;
using NotifyVox.App.ViewModels;

namespace NotifyVox.App;

/// <summary>
/// 設定ウィンドウ (SPEC §4.6)
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }
}
