using CS2FocusGuard.Core;

namespace CS2FocusGuard.App;

internal sealed class AppRuntime : IAsyncDisposable
{
    private static readonly TimeSpan RuntimeShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly string _dataDirectory;
    private readonly AppSettingsStore _settingsStore;
    private readonly Func<IManagedAudioSessionController> _audioControllerFactory;
    private readonly TimeSpan _audioInitializationTimeout;
    private readonly IGameProcessProbe _processProbe;
    private QuietHoursNotificationController? _notificationController;
    private GuardCoordinator? _coordinator;
    private IManagedAudioSessionController? _audioController;
    private AudioGuardCoordinator? _audioCoordinator;
    private ApplicationCatalog _applicationCatalog;
    private GuardMonitor? _monitor;
    private string? _initializationError;
    private bool _initialized;
    private bool _disposed;

    internal AppRuntime()
        : this(
            AppDataPaths.DataDirectory,
            static () => new WindowsAudioSessionController(),
            TimeSpan.FromSeconds(10),
            new Cs2ProcessProbe())
    {
    }

    internal AppRuntime(
        string dataDirectory,
        Func<IManagedAudioSessionController> audioControllerFactory,
        TimeSpan audioInitializationTimeout,
        IGameProcessProbe processProbe)
    {
        _dataDirectory = dataDirectory;
        _settingsStore = new AppSettingsStore(dataDirectory);
        _audioControllerFactory = audioControllerFactory;
        _audioInitializationTimeout = audioInitializationTimeout;
        _processProbe = processProbe;
        _applicationCatalog = new ApplicationCatalog(
            UnavailableAudioSessionController.Instance);
        LoadSettings();
    }

    internal event EventHandler<GuardStatus>? StatusChanged;

    internal event EventHandler? ApplicationsChanged;

    internal AppSettings Settings { get; private set; } = new();

    internal GuardStatus Status
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_initializationError))
            {
                return new GuardStatus(GuardState.Error, _initializationError);
            }

            if (!string.IsNullOrWhiteSpace(_audioCoordinator?.LastError))
            {
                return new GuardStatus(GuardState.Error, _audioCoordinator.LastError);
            }

            return _coordinator?.Status ??
                new GuardStatus(
                    Settings.Enabled ? GuardState.Waiting : GuardState.Disabled);
        }
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _notificationController = new QuietHoursNotificationController();
        _coordinator = new GuardCoordinator(
            _notificationController,
            new JsonGuardJournalStore(_dataDirectory));
        _coordinator.StatusChanged += OnStatusChanged;

        var components = new List<IGuardComponent> { _coordinator };
        try
        {
            _audioController = await AudioControllerLoader.CreateAsync(
                _audioControllerFactory,
                _audioInitializationTimeout,
                cancellationToken);
            _audioCoordinator = new AudioGuardCoordinator(
                _audioController,
                new JsonAudioGuardJournalStore(_dataDirectory));
            _audioCoordinator.ConfigureAllowlist(Settings.AudioAllowlist ?? []);
            _audioCoordinator.ErrorChanged += OnAudioErrorChanged;
            _applicationCatalog = new ApplicationCatalog(_audioController);
            ApplicationsChanged?.Invoke(this, EventArgs.Empty);
            components.Add(_audioCoordinator);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            _initializationError = exception.Message;
            StartupErrorLog.Write(
                "Audio initialization",
                exception,
                _dataDirectory);
        }

        _monitor = new GuardMonitor(
            components,
            _processProbe);
        await _monitor.StartAsync(Settings.Enabled, cancellationToken);
        StatusChanged?.Invoke(this, Status);
    }

    internal void ReportInitializationError(Exception exception)
    {
        _initializationError = exception.Message;
        StartupErrorLog.Write(
            "Runtime initialization",
            exception,
            _dataDirectory);
        StatusChanged?.Invoke(this, Status);
    }

    internal async Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        Settings = Settings with { Enabled = enabled };
        _settingsStore.Save(Settings);
        if (_monitor is not null)
        {
            await _monitor.SetEnabledAsync(enabled, cancellationToken);
        }
    }

    internal void SetStartWithWindows(bool enabled)
    {
        WindowsStartupRegistration.SetEnabled(enabled);
        Settings = Settings with { StartWithWindows = enabled };
        _settingsStore.Save(Settings);
    }

    internal void SetCloseToTray(bool enabled)
    {
        Settings = Settings with { CloseToTray = enabled };
        _settingsStore.Save(Settings);
    }

    internal void SetUseTraditionalChinese(bool useTraditionalChinese)
    {
        Settings = Settings with { UseTraditionalChinese = useTraditionalChinese };
        _settingsStore.Save(Settings);
        Strings.SetUseTraditionalChinese(useTraditionalChinese);
    }

    internal void SetUseLargeInterface(bool useLargeInterface)
    {
        Settings = Settings with { UseLargeInterface = useLargeInterface };
        _settingsStore.Save(Settings);
        AppearanceManager.ApplyInterfaceMetrics(useLargeInterface);
    }

    internal void SetUseDarkTheme(bool useDarkTheme)
    {
        Settings = Settings with { UseDarkTheme = useDarkTheme };
        _settingsStore.Save(Settings);
        AppearanceManager.ApplyTheme(useDarkTheme);
    }

    internal Task<IReadOnlyList<ApplicationDescriptor>> GetApplicationsAsync(
        CancellationToken cancellationToken = default) =>
        _applicationCatalog.GetApplicationsAsync(
            Settings.AudioAllowlist ?? [],
            cancellationToken);

    internal async Task SetAudioAllowlistAsync(
        IEnumerable<string> applicationIds,
        CancellationToken cancellationToken = default)
    {
        var allowlist = AudioAllowlistSettings.Normalize(applicationIds);
        Settings = Settings with { AudioAllowlist = allowlist };
        _settingsStore.Save(Settings);
        if (_audioCoordinator is not null)
        {
            await _audioCoordinator.UpdateAllowlistAsync(
                allowlist,
                cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_coordinator is not null)
        {
            _coordinator.StatusChanged -= OnStatusChanged;
        }

        if (_audioCoordinator is not null)
        {
            _audioCoordinator.ErrorChanged -= OnAudioErrorChanged;
        }

        var monitorStopped = true;
        if (_monitor is not null)
        {
            try
            {
                await _monitor.DisposeAsync()
                    .AsTask()
                    .WaitAsync(RuntimeShutdownTimeout);
            }
            catch (TimeoutException exception)
            {
                monitorStopped = false;
                StartupErrorLog.Write(
                    "Runtime shutdown",
                    exception,
                    _dataDirectory);
            }
        }

        if (monitorStopped)
        {
            _audioCoordinator?.Dispose();
        }

        _audioController?.Dispose();
        _coordinator?.Dispose();
        _notificationController?.Dispose();
    }

    private void OnStatusChanged(object? sender, GuardStatus status) =>
        StatusChanged?.Invoke(this, Status);

    private void OnAudioErrorChanged(object? sender, string? error) =>
        StatusChanged?.Invoke(this, Status);

    private void LoadSettings()
    {
        var loaded = _settingsStore.Load();
        Settings = loaded with
        {
            StartWithWindows = WindowsStartupRegistration.IsEnabled,
            UseTraditionalChinese =
                loaded.UseTraditionalChinese ?? Strings.UseTraditionalChinese,
            AudioAllowlist =
                AudioAllowlistSettings.Normalize(loaded.AudioAllowlist)
        };
        Strings.SetUseTraditionalChinese(Settings.UseTraditionalChinese.Value);
        AppearanceManager.Apply(Settings);
        _settingsStore.Save(Settings);
    }

    private sealed class UnavailableAudioSessionController
        : IAudioSessionController
    {
        internal static UnavailableAudioSessionController Instance { get; } = new();

        public event EventHandler? SessionsChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<AudioMuteChanged>? MuteChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<AudioSession>> GetSessionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioSession>>([]);

        public Task SetMuteAsync(
            AudioSessionKey key,
            bool muted,
            Guid eventContext,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
