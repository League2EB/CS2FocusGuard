using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CS2FocusGuard.App;

[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "WPF bindings require instance members.")]
public partial class UpdateFailureWindow : Window, INotifyPropertyChanged
{
    private readonly string _operationId;
    private readonly Version _version;
    private readonly string _failureKey;
    private readonly Uri _releasePageUri;
    private readonly string _logPath;

    internal UpdateFailureWindow(
        string operationId,
        Version version,
        string failureKey,
        Uri releasePageUri,
        string logPath)
    {
        _operationId = operationId;
        _version = version;
        _failureKey = failureKey;
        _releasePageUri = releasePageUri;
        _logPath = logPath;
        InitializeComponent();
        AppearanceManager.RegisterWindow(this);
        DataContext = this;
        Strings.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool ShouldRetry { get; private set; }

    public string AppTitle => Strings.Get("AppTitle");

    public string TitleText => Strings.Get("UpdateFailedTitle");

    public string MessageText =>
        Strings.Format("UpdateFailedMessage", Strings.Get(_failureKey));

    public string FallbackText =>
        Strings.Format("UpdateFallbackMessage", _version.ToString(3));

    public string LogText =>
        Strings.Format("UpdateLogLocation", _logPath);

    public string RetryText => Strings.Get("Retry");

    public string OpenReleasePageText => Strings.Get("OpenReleasePage");

    public string CloseText => Strings.Get("Close");

    protected override void OnClosed(EventArgs e)
    {
        Strings.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ShouldRetry = true;
        Close();
    }

    private void OpenReleasePage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            ReleasePageLauncher.Open(_releasePageUri);
            UpdateDiagnostics.Write(
                _operationId,
                "fallback-page-opened",
                _version,
                _releasePageUri.AbsoluteUri);
        }
        catch (Exception exception)
        {
            UpdateDiagnostics.Write(
                _operationId,
                "fallback-page-failed",
                _version,
                exception: exception);
            System.Windows.MessageBox.Show(
                Strings.Get("UpdateFailureBrowser"),
                Strings.Get("UpdateFailedTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnLanguageChanged(sender, e));
            return;
        }

        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(TitleText));
        OnPropertyChanged(nameof(MessageText));
        OnPropertyChanged(nameof(FallbackText));
        OnPropertyChanged(nameof(LogText));
        OnPropertyChanged(nameof(RetryText));
        OnPropertyChanged(nameof(OpenReleasePageText));
        OnPropertyChanged(nameof(CloseText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
