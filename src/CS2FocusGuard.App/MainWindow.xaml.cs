using System.ComponentModel;
using System.Windows;

namespace CS2FocusGuard.App;

public partial class MainWindow : System.Windows.Window
{
    private readonly MainViewModel _viewModel;
    private bool _allowClose;

    internal MainWindow(AppRuntime runtime)
    {
        InitializeComponent();
        AppearanceManager.RegisterWindow(this);
        _viewModel = new MainViewModel(runtime);
        DataContext = _viewModel;
    }

    internal void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    internal void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        if (_viewModel.CloseToTray)
        {
            Hide();
        }
        else
        {
            _ = ((App)System.Windows.Application.Current).RequestExitAsync();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }
}
