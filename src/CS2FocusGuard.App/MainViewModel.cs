using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CS2FocusGuard.Core;

namespace CS2FocusGuard.App;

[SuppressMessage(
    "Performance",
    "CA1822:Mark members as static",
    Justification = "These properties are instance-bound by WPF.")]
internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly AppRuntime _runtime;
    private bool _enabled;
    private bool _startWithWindows;
    private bool _closeToTray;
    private bool _useTraditionalChinese;
    private GuardStatus _status;
    private bool _updating;
    private bool _isLoadingApplications;
    private string _applicationSearch = string.Empty;

    internal MainViewModel(AppRuntime runtime)
    {
        _runtime = runtime;
        _enabled = runtime.Settings.Enabled;
        _startWithWindows = runtime.Settings.StartWithWindows;
        _closeToTray = runtime.Settings.CloseToTray;
        _useTraditionalChinese = Strings.UseTraditionalChinese;
        _status = runtime.Status;
        runtime.StatusChanged += OnStatusChanged;
        runtime.ApplicationsChanged += OnApplicationsChanged;
        Strings.LanguageChanged += OnLanguageChanged;
        ApplicationsView = CollectionViewSource.GetDefaultView(Applications);
        ApplicationsView.Filter = FilterApplication;
        RefreshApplicationsCommand = new AsyncCommand(LoadApplicationsAsync);
        _ = LoadApplicationsAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? CloseBehaviorChanged;

    public string AppTitle => Strings.Get("AppTitle");

    public string Subtitle => Strings.Get("Subtitle");

    public string EnabledLabel => Strings.Get("Enabled");

    public string EnabledDescription => Strings.Get("EnabledDescription");

    public string StartWithWindowsLabel => Strings.Get("StartWithWindows");

    public string StartDescription => Strings.Get("StartDescription");

    public string CloseToTrayLabel => Strings.Get("CloseToTray");

    public string CloseDescription => Strings.Get("CloseDescription");

    public string LanguageToggleLabel => Strings.Get("LanguageToggle");

    public string StatusLabel => Strings.Get("Status");

    public string StatusText => Strings.Status(_status);

    public string AudioAllowlistLabel => Strings.Get("AudioAllowlist");

    public string AudioAllowlistDescription => Strings.Get("AudioAllowlistDescription");

    public string Cs2AlwaysAllowedText => Strings.Get("Cs2AlwaysAllowed");

    public string SearchApplicationsLabel => Strings.Get("SearchApplications");

    public string RefreshLabel => Strings.Get("Refresh");

    public string LoadingApplicationsText => Strings.Get("LoadingApplications");

    public string NoApplicationsText => Strings.Get("NoApplications");

    public GuardState State => _status.State;

    public ObservableCollection<ApplicationAllowlistItem> Applications { get; } = [];

    public ICollectionView ApplicationsView { get; }

    public ICommand RefreshApplicationsCommand { get; }

    public bool IsLoadingApplications
    {
        get => _isLoadingApplications;
        private set
        {
            if (SetField(ref _isLoadingApplications, value))
            {
                OnPropertyChanged(nameof(ShowNoApplications));
            }
        }
    }

    public bool ShowNoApplications =>
        !IsLoadingApplications && ApplicationsView.IsEmpty;

    public string ApplicationSearch
    {
        get => _applicationSearch;
        set
        {
            if (!SetField(ref _applicationSearch, value))
            {
                return;
            }

            ApplicationsView.Refresh();
            OnPropertyChanged(nameof(ShowNoApplications));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetField(ref _enabled, value) || _updating)
            {
                return;
            }

            _ = UpdateEnabledAsync(value);
        }
    }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!SetField(ref _startWithWindows, value) || _updating)
            {
                return;
            }

            try
            {
                _runtime.SetStartWithWindows(value);
            }
            catch (Exception exception)
            {
                Revert(ref _startWithWindows, !value, nameof(StartWithWindows));
                ShowError(exception);
            }
        }
    }

    public bool CloseToTray
    {
        get => _closeToTray;
        set
        {
            if (!SetField(ref _closeToTray, value) || _updating)
            {
                return;
            }

            _runtime.SetCloseToTray(value);
            CloseBehaviorChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool UseTraditionalChinese
    {
        get => _useTraditionalChinese;
        set
        {
            if (!SetField(ref _useTraditionalChinese, value) || _updating)
            {
                return;
            }

            _runtime.SetUseTraditionalChinese(value);
        }
    }

    internal void Dispose()
    {
        _runtime.StatusChanged -= OnStatusChanged;
        _runtime.ApplicationsChanged -= OnApplicationsChanged;
        Strings.LanguageChanged -= OnLanguageChanged;
    }

    private async Task UpdateEnabledAsync(bool enabled)
    {
        try
        {
            await _runtime.SetEnabledAsync(enabled);
        }
        catch (Exception exception)
        {
            Revert(ref _enabled, !enabled, nameof(Enabled));
            ShowError(exception);
        }
    }

    private void OnStatusChanged(object? sender, GuardStatus status)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => OnStatusChanged(sender, status));
            return;
        }

        _status = status;
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(State));
    }

    private void OnApplicationsChanged(object? sender, EventArgs e)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => OnApplicationsChanged(sender, e));
            return;
        }

        _ = LoadApplicationsAsync();
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.Invoke(
                () => OnLanguageChanged(sender, e));
            return;
        }

        if (_useTraditionalChinese != Strings.UseTraditionalChinese)
        {
            _useTraditionalChinese = Strings.UseTraditionalChinese;
            OnPropertyChanged(nameof(UseTraditionalChinese));
        }

        OnPropertyChanged(nameof(AppTitle));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(EnabledLabel));
        OnPropertyChanged(nameof(EnabledDescription));
        OnPropertyChanged(nameof(StartWithWindowsLabel));
        OnPropertyChanged(nameof(StartDescription));
        OnPropertyChanged(nameof(CloseToTrayLabel));
        OnPropertyChanged(nameof(CloseDescription));
        OnPropertyChanged(nameof(LanguageToggleLabel));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(AudioAllowlistLabel));
        OnPropertyChanged(nameof(AudioAllowlistDescription));
        OnPropertyChanged(nameof(Cs2AlwaysAllowedText));
        OnPropertyChanged(nameof(SearchApplicationsLabel));
        OnPropertyChanged(nameof(RefreshLabel));
        OnPropertyChanged(nameof(LoadingApplicationsText));
        OnPropertyChanged(nameof(NoApplicationsText));
    }

    private async Task LoadApplicationsAsync()
    {
        IsLoadingApplications = true;
        try
        {
            var applications = await _runtime.GetApplicationsAsync();
            var allowed = (_runtime.Settings.AudioAllowlist ?? [])
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var items = await Task.Run(
                () =>
                    applications
                        .Select(
                            application => new ApplicationAllowlistItem(
                                application,
                                allowed.Contains(application.Id),
                                OnApplicationAllowedChanged))
                        .ToArray());
            Applications.Clear();
            foreach (var item in ApplicationAllowlistOrdering.Order(items))
            {
                Applications.Add(item);
            }

            ApplicationsView.Refresh();
            OnPropertyChanged(nameof(ShowNoApplications));
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
        finally
        {
            IsLoadingApplications = false;
        }
    }

    private void OnApplicationAllowedChanged(ApplicationAllowlistItem item)
    {
        ReorderApplications();
        var allowlist = Applications
            .Where(application => application.IsAllowed)
            .Select(application => application.Id)
            .ToArray();
        _ = SaveAudioAllowlistAsync(allowlist);
    }

    private void ReorderApplications()
    {
        var ordered = ApplicationAllowlistOrdering.Order(Applications).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var currentIndex = Applications.IndexOf(ordered[index]);
            if (currentIndex != index)
            {
                Applications.Move(currentIndex, index);
            }
        }

        ApplicationsView.Refresh();
        OnPropertyChanged(nameof(ShowNoApplications));
    }

    private async Task SaveAudioAllowlistAsync(string[] allowlist)
    {
        try
        {
            await _runtime.SetAudioAllowlistAsync(allowlist);
        }
        catch (Exception exception)
        {
            ShowError(exception);
        }
    }

    private static void ShowError(Exception exception) =>
        System.Windows.MessageBox.Show(
            exception.Message,
            Strings.Get("AppTitle"),
            MessageBoxButton.OK,
            MessageBoxImage.Error);

    private bool FilterApplication(object item)
    {
        if (item is not ApplicationAllowlistItem application ||
            string.IsNullOrWhiteSpace(ApplicationSearch))
        {
            return true;
        }

        return application.DisplayName.Contains(
                ApplicationSearch,
                StringComparison.CurrentCultureIgnoreCase) ||
            application.Id.Contains(
                ApplicationSearch,
                StringComparison.OrdinalIgnoreCase);
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void Revert<T>(ref T field, T value, string propertyName)
    {
        _updating = true;
        field = value;
        OnPropertyChanged(propertyName);
        _updating = false;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal static class ApplicationAllowlistOrdering
{
    internal static IOrderedEnumerable<ApplicationAllowlistItem> Order(
        IEnumerable<ApplicationAllowlistItem> applications) =>
        applications
            .OrderByDescending(application => application.IsAllowed)
            .ThenBy(
                application => application.DisplayName,
                StringComparer.CurrentCultureIgnoreCase);
}

internal sealed class ApplicationAllowlistItem : INotifyPropertyChanged
{
    private readonly Action<ApplicationAllowlistItem> _changed;
    private bool _isAllowed;

    internal ApplicationAllowlistItem(
        ApplicationDescriptor application,
        bool isAllowed,
        Action<ApplicationAllowlistItem> changed)
    {
        Id = application.Id;
        DisplayName = application.DisplayName;
        Icon = LoadIcon(application.IconPath);
        _isAllowed = isAllowed;
        _changed = changed;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Id { get; }

    public string DisplayName { get; }

    public string Initial => DisplayName[..1].ToUpperInvariant();

    public ImageSource? Icon { get; }

    public bool IsAllowed
    {
        get => _isAllowed;
        set
        {
            if (_isAllowed == value)
            {
                return;
            }

            _isAllowed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAllowed)));
            _changed(this);
        }
    }

    private static BitmapSource? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return null;
            }

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

internal sealed class AsyncCommand(Func<Task> execute) : ICommand
{
    private bool _isExecuting;

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting;

    public async void Execute(object? parameter)
    {
        if (_isExecuting)
        {
            return;
        }

        _isExecuting = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute();
        }
        finally
        {
            _isExecuting = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
