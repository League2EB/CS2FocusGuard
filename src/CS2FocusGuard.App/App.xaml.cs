using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CS2FocusGuard.Core;
using Forms = System.Windows.Forms;

namespace CS2FocusGuard.App;

[SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF Application owns and disposes these fields in OnExit.")]
public partial class App : System.Windows.Application
{
    private const string InstanceName = "Local\\CS2FocusGuard-5DDB1D23";
    private const string PipeName = "CS2FocusGuard-5DDB1D23";
    private static readonly TimeSpan UpdateRequestTimeout = TimeSpan.FromSeconds(15);
    private Mutex? _instanceMutex;
    private CancellationTokenSource? _pipeCancellation;
    private AppRuntime? _runtime;
    private MainWindow? _mainWindow;
    private Forms.NotifyIcon? _trayIcon;
    private Forms.ToolStripMenuItem? _trayStatusItem;
    private Forms.ToolStripMenuItem? _trayUpdateItem;
    private Forms.ToolStripMenuItem? _trayOpenItem;
    private Forms.ToolStripMenuItem? _trayExitItem;
    private System.Drawing.Icon? _applicationIcon;
    private HttpClient? _updateHttpClient;
    private UpdateService? _updateService;
    private AvailableUpdate? _availableUpdate;
    private bool _isExiting;
    private bool _isUpdatePromptOpen;
    private bool _runtimeDisposed;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceMutex = new Mutex(
            initiallyOwned: true,
            InstanceName,
            out var isFirstInstance);

        var isExitCommand = e.Args.Contains("--exit");
        if (!isFirstInstance)
        {
            var sent = await SendCommandAsync(
                isExitCommand ? (byte)2 : (byte)1,
                showFailure: !isExitCommand);
            if (!sent && isExitCommand)
            {
                Environment.ExitCode = 1;
            }

            _instanceMutex.Dispose();
            _instanceMutex = null;
            Shutdown();
            return;
        }

        if (isExitCommand)
        {
            Shutdown();
            return;
        }

        _pipeCancellation = new CancellationTokenSource();
        _ = RunPipeServerAsync(_pipeCancellation.Token);

        try
        {
            _runtime = new AppRuntime();
            _runtime.StatusChanged += OnStatusChanged;
            Strings.LanguageChanged += OnLanguageChanged;

            _mainWindow = new MainWindow(_runtime);
            MainWindow = _mainWindow;
            CreateTrayIcon();

            if (!e.Args.Contains("--background"))
            {
                _mainWindow.Show();
            }

            try
            {
                await _runtime.InitializeAsync();
                InitializeUpdateService();
                _ = CheckForUpdateAsync();
            }
            catch (Exception exception)
            {
                _runtime.ReportInitializationError(exception);
            }
        }
        catch (Exception exception)
        {
            StartupErrorLog.Write("Application startup", exception);
            System.Windows.MessageBox.Show(
                exception.Message,
                Strings.Get("AppTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            await DisposeRuntimeAsync();
            Shutdown();
        }
    }

    internal async Task RequestExitAsync()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        await DisposeRuntimeAsync();

        _trayIcon?.Dispose();
        _trayIcon = null;
        _applicationIcon?.Dispose();
        _applicationIcon = null;

        if (_pipeCancellation is not null)
        {
            await _pipeCancellation.CancelAsync();
        }

        _mainWindow?.AllowClose();
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        DisposeRuntimeAsync().GetAwaiter().GetResult();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_runtimeDisposed)
        {
            DisposeRuntimeAsync().GetAwaiter().GetResult();
        }

        _pipeCancellation?.Cancel();
        _pipeCancellation?.Dispose();
        _trayIcon?.Dispose();
        _applicationIcon?.Dispose();
        _updateHttpClient?.Dispose();

        if (_instanceMutex is not null)
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }

        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        _trayStatusItem = new Forms.ToolStripMenuItem
        {
            Enabled = false,
            Text = Strings.Status(_runtime!.Status)
        };
        _trayUpdateItem = new Forms.ToolStripMenuItem
        {
            Visible = false
        };
        _trayUpdateItem.Click += (_, _) =>
            Dispatcher.Invoke(() => _ = PromptForUpdateAsync());
        _trayOpenItem = new Forms.ToolStripMenuItem(Strings.Get("Open"));
        _trayOpenItem.Click += (_, _) => Dispatcher.Invoke(ShowMainWindow);
        _trayExitItem = new Forms.ToolStripMenuItem(Strings.Get("Exit"));
        _trayExitItem.Click += (_, _) =>
            _ = Dispatcher.InvokeAsync(RequestExitAsync);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(_trayStatusItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_trayUpdateItem);
        menu.Items.Add(_trayOpenItem);
        menu.Items.Add(_trayExitItem);

        _applicationIcon = LoadApplicationIcon();
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = _applicationIcon,
            Text = Strings.Get("AppTitle"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowMainWindow);
    }

    private void InitializeUpdateService()
    {
        var version = GetType().Assembly.GetName().Version
            ?? throw new InvalidOperationException("The application version is unavailable.");
        _updateHttpClient = new HttpClient
        {
            Timeout = UpdateRequestTimeout
        };
        _updateService = new UpdateService(
            _updateHttpClient,
            version,
            AppDataPaths.UpdatesDirectory);
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateService is null)
        {
            return;
        }

        try
        {
            var update = await _updateService.CheckForUpdateAsync();
            if (update is null || _isExiting)
            {
                return;
            }

            await Dispatcher.InvokeAsync(() => PresentUpdate(update));
        }
        catch (OperationCanceledException) when (_isExiting)
        {
        }
        catch (Exception exception)
        {
            StartupErrorLog.Write("Update check", exception);
        }
    }

    private void PresentUpdate(AvailableUpdate update)
    {
        if (_isExiting)
        {
            return;
        }

        _availableUpdate = update;
        if (_trayUpdateItem is not null)
        {
            _trayUpdateItem.Text = Strings.Format(
                "UpdateAvailableMenu",
                update.Version.ToString(3));
            _trayUpdateItem.Visible = true;
        }

        if (_mainWindow?.IsVisible is true)
        {
            _ = PromptForUpdateAsync();
        }
        else
        {
            _trayIcon?.ShowBalloonTip(
                5000,
                Strings.Get("UpdateAvailableTitle"),
                Strings.Format("UpdateAvailableMessage", update.Version.ToString(3)),
                Forms.ToolTipIcon.Info);
        }
    }

    private async Task PromptForUpdateAsync()
    {
        if (_isExiting || _isUpdatePromptOpen || _availableUpdate is null)
        {
            return;
        }

        _isUpdatePromptOpen = true;
        try
        {
            ShowMainWindow();
            var dialog = new UpdateAvailableWindow(_availableUpdate.Version)
            {
                Owner = _mainWindow
            };
            dialog.ShowDialog();
            if (dialog.ShouldUpdate)
            {
                await InstallUpdateAsync(_availableUpdate);
            }
        }
        finally
        {
            _isUpdatePromptOpen = false;
        }
    }

    private async Task InstallUpdateAsync(AvailableUpdate update)
    {
        if (_updateService is null || _isExiting)
        {
            return;
        }

        using var cancellationSource = new CancellationTokenSource();
        var progressWindow = new UpdateProgressWindow(cancellationSource)
        {
            Owner = _mainWindow
        };
        var updateStarted = false;
        progressWindow.Show();

        try
        {
            var progress = new Progress<UpdateDownloadProgress>(progressWindow.ReportProgress);
            var installerPath = await _updateService.DownloadInstallerAsync(
                update,
                progress,
                cancellationSource.Token);
            progressWindow.ShowInstalling();
            UpdateInstallerLauncher.Launch(installerPath);
            updateStarted = true;
            progressWindow.AllowClose();
            progressWindow.Close();
            await RequestExitAsync();
        }
        catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StartupErrorLog.Write("Update install", exception);
            System.Windows.MessageBox.Show(
                Strings.Get("UpdateFailed"),
                Strings.Get("AppTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            if (!updateStarted)
            {
                progressWindow.AllowClose();
                progressWindow.Close();
            }
        }
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var resource = GetResourceStream(
            new Uri("pack://application:,,,/Assets/AppIcon.ico"));
        if (resource is null)
        {
            return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
        }

        using var stream = resource.Stream;
        using var icon = new System.Drawing.Icon(stream);
        return (System.Drawing.Icon)icon.Clone();
    }

    private void ShowMainWindow() => _mainWindow?.ShowAndActivate();

    private void OnStatusChanged(object? sender, GuardStatus status)
    {
        Dispatcher.Invoke(
            () =>
            {
                if (_trayStatusItem is not null)
                {
                    _trayStatusItem.Text = Strings.Status(status);
                }

                if (_trayIcon is not null)
                {
                    var text = $"{Strings.Get("AppTitle")}: {Strings.Status(status)}";
                    _trayIcon.Text = text[..Math.Min(text.Length, 63)];
                }
            });
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => OnLanguageChanged(sender, e));
            return;
        }

        if (_runtime is null)
        {
            return;
        }

        if (_trayStatusItem is not null)
        {
            _trayStatusItem.Text = Strings.Status(_runtime.Status);
        }

        if (_trayOpenItem is not null)
        {
            _trayOpenItem.Text = Strings.Get("Open");
        }

        if (_trayUpdateItem is not null && _availableUpdate is not null)
        {
            _trayUpdateItem.Text = Strings.Format(
                "UpdateAvailableMenu",
                _availableUpdate.Version.ToString(3));
        }

        if (_trayExitItem is not null)
        {
            _trayExitItem.Text = Strings.Get("Exit");
        }

        if (_trayIcon is not null)
        {
            var text = $"{Strings.Get("AppTitle")}: {Strings.Status(_runtime.Status)}";
            _trayIcon.Text = text[..Math.Min(text.Length, 63)];
        }
    }

    private async Task DisposeRuntimeAsync()
    {
        if (_runtimeDisposed)
        {
            return;
        }

        _runtimeDisposed = true;
        if (_runtime is not null)
        {
            _runtime.StatusChanged -= OnStatusChanged;
            Strings.LanguageChanged -= OnLanguageChanged;
            await _runtime.DisposeAsync();
        }
    }

    private async Task RunPipeServerAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(cancellationToken);
                var command = server.ReadByte();
                if (command == 2)
                {
                    server.WriteByte(1);
                    await server.FlushAsync(cancellationToken);
                    await await Dispatcher.InvokeAsync(RequestExitAsync);
                }
                else
                {
                    await Dispatcher.InvokeAsync(ShowMainWindow);
                    server.WriteByte(1);
                    await server.FlushAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            StartupErrorLog.Write("Command pipe", exception);
        }
    }

    private static async Task<bool> SendCommandAsync(
        byte command,
        bool showFailure)
    {
        try
        {
            await using var client = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await client.ConnectAsync(2000);
            client.WriteByte(command);
            await client.FlushAsync();
            var acknowledgement = new byte[1];
            var count = await client.ReadAsync(acknowledgement)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15));
            return count == 1 && acknowledgement[0] == 1;
        }
        catch (Exception exception) when (
            exception is TimeoutException or IOException)
        {
            StartupErrorLog.Write("Command client", exception);
            if (showFailure)
            {
                System.Windows.MessageBox.Show(
                    Strings.Get("AlreadyRunning"),
                    Strings.Get("AppTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            return false;
        }
    }
}

