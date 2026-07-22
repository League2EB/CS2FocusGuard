using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using CS2FocusGuard.Core;
using Microsoft.Win32;

namespace CS2FocusGuard.App;

internal static class AppDataPaths
{
    internal static string DataDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CS2FocusGuard");

    internal static string UpdatesDirectory =>
        Path.Combine(DataDirectory, "updates");
}

internal static class StartupErrorLog
{
    private static readonly object Gate = new();

    internal static string Path =>
        System.IO.Path.Combine(AppDataPaths.DataDirectory, "startup-error.log");

    internal static void Write(
        string stage,
        Exception exception,
        string? dataDirectory = null)
    {
        try
        {
            lock (Gate)
            {
                var directory = dataDirectory ?? AppDataPaths.DataDirectory;
                Directory.CreateDirectory(directory);
                File.AppendAllText(
                    System.IO.Path.Combine(directory, "startup-error.log"),
                    $"{DateTimeOffset.Now:O} [{stage}]{Environment.NewLine}" +
                    $"{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class UpdateDiagnostics
{
    private static readonly object Gate = new();

    internal static string Path =>
        System.IO.Path.Combine(AppDataPaths.DataDirectory, "update.log");

    internal static void Write(
        string operationId,
        string stage,
        Version targetVersion,
        string? detail = null,
        Exception? exception = null,
        string? dataDirectory = null)
    {
        try
        {
            lock (Gate)
            {
                var directory = dataDirectory ?? AppDataPaths.DataDirectory;
                Directory.CreateDirectory(directory);
                var currentVersion =
                    typeof(UpdateDiagnostics).Assembly.GetName().Version?.ToString(3) ??
                    "unknown";
                var entry =
                    $"{DateTimeOffset.Now:O} [Update] " +
                    $"Operation={operationId} Stage={stage} " +
                    $"Current={currentVersion} Target={targetVersion:3} " +
                    $"Process={Environment.ProcessId}";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    entry += $" Detail={detail}";
                }

                entry += Environment.NewLine;
                if (exception is not null)
                {
                    entry += $"{exception}{Environment.NewLine}";
                }

                File.AppendAllText(
                    System.IO.Path.Combine(directory, "update.log"),
                    entry);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal static class QuietHoursProfiles
{
    internal const string Unrestricted = "Microsoft.QuietHoursProfile.Unrestricted";
    internal const string PriorityOnly = "Microsoft.QuietHoursProfile.PriorityOnly";
    internal const string AlarmsOnly = "Microsoft.QuietHoursProfile.AlarmsOnly";
}

internal sealed class QuietHoursNotificationController : INotificationController, IDisposable
{
    private readonly StaDispatcher _dispatcher = new();

    public string SuppressionProfileId =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? QuietHoursProfiles.PriorityOnly
            : QuietHoursProfiles.AlarmsOnly;

    public Task<NotificationState> GetStateAsync(
        CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(
            () =>
            {
                using var session = QuietHoursSession.Create();
                var state = new NotificationState(
                    session.Settings.GetUserSelectedProfile(),
                    session.Settings.GetActiveProfile(),
                    session.Settings.GetOffProfileId());
                Validate(state);
                return state;
            },
            cancellationToken);

    public Task SetSelectedProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            throw new ArgumentException("The notification profile cannot be empty.", nameof(profileId));
        }

        return _dispatcher.InvokeAsync(
            () =>
            {
                using var session = QuietHoursSession.Create();
                session.Settings.SetUserSelectedProfile(profileId);
            },
            cancellationToken);
    }

    public void Dispose() => _dispatcher.Dispose();

    private static void Validate(NotificationState state)
    {
        if (string.IsNullOrWhiteSpace(state.SelectedProfile) ||
            string.IsNullOrWhiteSpace(state.ActiveProfile) ||
            string.IsNullOrWhiteSpace(state.OffProfile))
        {
            throw new PlatformNotSupportedException(
                "This Windows build did not return a valid notification profile.");
        }
    }

    private sealed class QuietHoursSession : IDisposable
    {
        private static readonly Guid ClassId =
            new("F53321FA-34F8-4B7F-B9A3-361877CB94CF");

        private readonly object _raw;

        private QuietHoursSession(object raw)
        {
            _raw = raw;
            Settings = (IQuietHoursSettings)raw;
        }

        internal IQuietHoursSettings Settings { get; }

        internal static QuietHoursSession Create()
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            {
                throw new PlatformNotSupportedException("Windows 10 or later is required.");
            }

            var type = Type.GetTypeFromCLSID(ClassId, throwOnError: true)
                ?? throw new PlatformNotSupportedException(
                    "Windows Quiet Hours is unavailable.");
            var raw = Activator.CreateInstance(type)
                ?? throw new InvalidOperationException(
                    "Windows Quiet Hours activation returned no object.");
            return new QuietHoursSession(raw);
        }

        public void Dispose()
        {
            if (Marshal.IsComObject(_raw))
            {
                Marshal.FinalReleaseComObject(_raw);
            }
        }
    }

    [ComImport]
    [Guid("6BFF4732-81EC-4FFB-AE67-B6C1BC29631F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [SuppressMessage(
        "Interoperability",
        "SYSLIB1096",
        Justification = "This undocumented interface requires classic COM marshalling.")]
    internal interface IQuietHoursSettings
    {
        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetUserSelectedProfile();

        void SetUserSelectedProfile(
            [MarshalAs(UnmanagedType.LPWStr)] string profileId);

        IntPtr ReservedGetProfile(
            [MarshalAs(UnmanagedType.LPWStr)] string profileId);

        void ReservedGetAllProfileData(out uint count, IntPtr profileData);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string ReservedGetDisplayNameForProfile(
            [MarshalAs(UnmanagedType.LPWStr)] string profileId);

        IntPtr ReservedGetQuietMomentsManager();

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetOffProfileId();

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetActiveQuietMomentProfile();

        void SetActiveQuietMomentProfile(
            [MarshalAs(UnmanagedType.LPWStr)] string profileId);

        [return: MarshalAs(UnmanagedType.LPWStr)]
        string GetActiveProfile();
    }
}

internal sealed class StaDispatcher : IDisposable
{
    private readonly BlockingCollection<Action> _queue = [];
    private readonly Thread _thread;
    private bool _disposed;

    internal StaDispatcher()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Windows notification controller"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    internal Task<T> InvokeAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _queue.Add(
            () =>
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    completion.TrySetCanceled(cancellationToken);
                    return;
                }

                try
                {
                    completion.TrySetResult(operation());
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            },
            cancellationToken);

        return completion.Task;
    }

    internal async Task InvokeAsync(
        Action operation,
        CancellationToken cancellationToken)
    {
        await InvokeAsync(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(2));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var operation in _queue.GetConsumingEnumerable())
        {
            operation();
        }
    }
}

internal sealed class Cs2ProcessProbe : IGameProcessProbe
{
    public bool IsRunning()
    {
        var processes = Process.GetProcessesByName("cs2");
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }
}

internal sealed record AppSettings(
    bool Enabled = true,
    bool StartWithWindows = false,
    bool CloseToTray = true,
    bool? UseTraditionalChinese = null,
    string[]? AudioAllowlist = null,
    bool UseLargeInterface = false,
    bool UseDarkTheme = false);

internal sealed class AppSettingsStore
{
    private readonly string _path;

    internal AppSettingsStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "settings.json");
    }

    internal AppSettings Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path))
                    ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
    }

    internal void Save(AppSettings settings) =>
        AtomicJsonFile.Write(_path, settings);
}

internal sealed class JsonGuardJournalStore : IGuardJournalStore
{
    private readonly string _path;

    internal JsonGuardJournalStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "guard-state.json");
    }

    public GuardJournal? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<GuardJournal>(File.ReadAllText(_path))
                : null;
        }
        catch (JsonException)
        {
            Clear();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(GuardJournal journal) =>
        AtomicJsonFile.Write(_path, journal);

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}

internal sealed class JsonAudioGuardJournalStore : IAudioGuardJournalStore
{
    private readonly string _path;

    internal JsonAudioGuardJournalStore(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        _path = Path.Combine(dataDirectory, "audio-guard-state.json");
    }

    public AudioGuardJournal? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AudioGuardJournal>(File.ReadAllText(_path))
                : null;
        }
        catch (JsonException)
        {
            Clear();
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    public void Save(AudioGuardJournal journal) =>
        AtomicJsonFile.Write(_path, journal);

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch (IOException)
        {
        }
    }
}

internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options =
        new() { WriteIndented = true };

    internal static void Write<T>(string path, T value)
    {
        var temporaryPath = $"{path}.{Environment.ProcessId}.tmp";
        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, true);
    }
}

internal static class WindowsStartupRegistration
{
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CS2FocusGuard";

    internal static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string;
        }
    }

    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
        {
            var executable = Environment.ProcessPath
                ?? throw new InvalidOperationException(
                    "The application executable path is unavailable.");
            key.SetValue(ValueName, $"\"{executable}\" --background");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
