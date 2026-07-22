using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows;

namespace CS2FocusGuard.App;

[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "WPF bindings require instance members.")]
public partial class UpdateAvailableWindow : Window, INotifyPropertyChanged
{
    private readonly Version _version;

    internal UpdateAvailableWindow(Version version)
    {
        _version = version;
        InitializeComponent();
        AppearanceManager.RegisterWindow(this);
        DataContext = this;
        Strings.LanguageChanged += OnLanguageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal bool ShouldUpdate { get; private set; }

    public string AppTitle => Strings.Get("AppTitle");

    public string TitleText => Strings.Get("UpdateAvailableTitle");

    public string MessageText =>
        Strings.Format("UpdateAvailableMessage", _version.ToString(3));

    public string UpdateNowText => Strings.Get("UpdateNow");

    public string LaterText => Strings.Get("UpdateLater");

    protected override void OnClosed(EventArgs e)
    {
        Strings.LanguageChanged -= OnLanguageChanged;
        base.OnClosed(e);
    }

    private void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        ShouldUpdate = true;
        Close();
    }

    private void Later_Click(object sender, RoutedEventArgs e) => Close();

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
        OnPropertyChanged(nameof(UpdateNowText));
        OnPropertyChanged(nameof(LaterText));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
