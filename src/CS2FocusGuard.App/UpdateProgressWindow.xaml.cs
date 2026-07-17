using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CS2FocusGuard.App;

[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "WPF bindings require instance members.")]
public partial class UpdateProgressWindow : Window, INotifyPropertyChanged
{
    private readonly CancellationTokenSource _cancellationSource;
    private string _statusText;
    private double _progressPercent;
    private bool _canCancel = true;
    private bool _allowClose;

    internal UpdateProgressWindow(CancellationTokenSource cancellationSource)
    {
        _cancellationSource = cancellationSource;
        _statusText = Strings.Get("UpdatePreparing");
        InitializeComponent();
        DataContext = this;
        Strings.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string AppTitle => Strings.Get("AppTitle");

    public string TitleText => Strings.Get("UpdateDownloadingTitle");

    public string CancelText => Strings.Get("Cancel");

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (_statusText == value)
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set
        {
            if (Math.Abs(_progressPercent - value) < double.Epsilon)
            {
                return;
            }

            _progressPercent = value;
            OnPropertyChanged();
        }
    }

    public bool CanCancel
    {
        get => _canCancel;
        private set
        {
            if (_canCancel == value)
            {
                return;
            }

            _canCancel = value;
            OnPropertyChanged();
        }
    }

    internal void ReportProgress(UpdateDownloadProgress progress)
    {
        if (progress.IsVerifying)
        {
            ShowVerification();
            return;
        }

        if (progress.TotalBytes is { } totalBytes && totalBytes > 0)
        {
            ProgressPercent = Math.Min(100, progress.BytesReceived * 100d / totalBytes);
            StatusText = Strings.Format(
                "UpdateDownloadingProgress",
                FormatBytes(progress.BytesReceived),
                FormatBytes(totalBytes));
            return;
        }

        StatusText = Strings.Format(
            "UpdateDownloadingUnknownProgress",
            FormatBytes(progress.BytesReceived));
    }

    internal void ShowVerification()
    {
        CanCancel = false;
        ProgressPercent = 100;
        StatusText = Strings.Get("UpdateVerifying");
    }

    internal void ShowInstalling()
    {
        CanCancel = false;
        StatusText = Strings.Get("UpdateInstalling");
    }

    internal void AllowClose() => _allowClose = true;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_allowClose)
        {
            base.OnClosing(e);
            return;
        }

        if (CanCancel)
        {
            _cancellationSource.Cancel();
        }
        else
        {
            e.Cancel = true;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        Strings.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cancellationSource.Cancel();
        Close();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnLanguageChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(CancelText));
    }

    private static string FormatBytes(long bytes)
    {
        const double bytesPerMegabyte = 1024d * 1024;
        return $"{bytes / bytesPerMegabyte:0.0} MB";
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
