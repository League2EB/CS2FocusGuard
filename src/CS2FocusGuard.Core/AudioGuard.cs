namespace CS2FocusGuard.Core;

public static class AudioApplicationIdentity
{
    public const string SystemSounds = "system-sounds";

    public static string Normalize(string value)
    {
        var normalized = value.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^4];
        }

        return normalized.ToLowerInvariant();
    }
}

public static class AudioAllowlistSettings
{
    public static IEnumerable<string> GetDefaults()
    {
        yield return "discord";
        yield return "oopz";
        yield return "kook";
    }

    public static string[] Normalize(IEnumerable<string>? values)
    {
        var source = values ?? GetDefaults();
        return source
            .Select(AudioApplicationIdentity.Normalize)
            .Where(value => value.Length > 0 && value != "cs2")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

public sealed record AudioSessionKey(string EndpointId, string SessionId);

public sealed record AudioSession(
    AudioSessionKey Key,
    string ApplicationId,
    string DisplayName,
    bool IsMuted);

public sealed record AudioMuteChanged(
    AudioSessionKey Key,
    string ApplicationId,
    bool IsMuted,
    Guid EventContext);

public sealed record AudioGuardJournalEntry(
    AudioSessionKey Key,
    string ApplicationId,
    bool OriginalMute);

public sealed record AudioGuardJournal(
    DateTimeOffset CreatedAtUtc,
    IReadOnlyList<AudioGuardJournalEntry> Entries);

public interface IAudioSessionController
{
    event EventHandler? SessionsChanged;

    event EventHandler<AudioMuteChanged>? MuteChanged;

    Task<IReadOnlyList<AudioSession>> GetSessionsAsync(
        CancellationToken cancellationToken = default);

    Task SetMuteAsync(
        AudioSessionKey key,
        bool muted,
        Guid eventContext,
        CancellationToken cancellationToken = default);
}

public interface IAudioGuardJournalStore
{
    AudioGuardJournal? Load();

    void Save(AudioGuardJournal journal);

    void Clear();
}

public sealed class AudioGuardCoordinator : IGuardComponent, IDisposable
{
    private static readonly StringComparer IdentityComparer =
        StringComparer.OrdinalIgnoreCase;

    private readonly IAudioSessionController _controller;
    private readonly IAudioGuardJournalStore _journalStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Guid _eventContext = Guid.NewGuid();
    private readonly Dictionary<AudioSessionKey, AudioGuardJournalEntry> _entries = [];
    private readonly HashSet<string> _temporaryAllowlist = new(IdentityComparer);
    private HashSet<string> _allowlist = new(IdentityComparer);
    private int _reconcileRequested;
    private int _reconcileWorkerActive;
    private bool _enabled;
    private bool _active;
    private bool _disposed;

    public AudioGuardCoordinator(
        IAudioSessionController controller,
        IAudioGuardJournalStore journalStore)
    {
        _controller = controller;
        _journalStore = journalStore;
        _controller.SessionsChanged += OnSessionsChanged;
        _controller.MuteChanged += OnMuteChanged;
    }

    public event EventHandler<string?>? ErrorChanged;

    public string? LastError { get; private set; }

    public void ConfigureAllowlist(IEnumerable<string> allowlist) =>
        SetAllowlist(allowlist);

    public Task InitializeAsync(
        bool enabled,
        bool gameRunning,
        CancellationToken cancellationToken = default) =>
        InitializeAsync(enabled, gameRunning, _allowlist, cancellationToken);

    public async Task InitializeAsync(
        bool enabled,
        bool gameRunning,
        IEnumerable<string> allowlist,
        CancellationToken cancellationToken = default)
    {
        await RunLockedAsync(
            async () =>
            {
                SetAllowlist(allowlist);
                LoadJournal();
                _enabled = enabled;
                _active = enabled && gameRunning;
                if (_active)
                {
                    await ReconcileActiveAsync(cancellationToken);
                }
                else
                {
                    await RestoreEntriesAsync(cancellationToken);
                }
            },
            cancellationToken);
    }

    public Task EvaluateAsync(
        bool gameRunning,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                if (_active && !gameRunning)
                {
                    _active = false;
                    _temporaryAllowlist.Clear();
                    await RestoreEntriesAsync(cancellationToken);
                    return;
                }

                if (!_active && _enabled && gameRunning)
                {
                    _active = true;
                }

                if (_active)
                {
                    await ReconcileActiveAsync(cancellationToken);
                }
            },
            cancellationToken);

    public Task SetEnabledAsync(
        bool enabled,
        bool gameRunning,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                _enabled = enabled;
                var shouldBeActive = enabled && gameRunning;
                if (!shouldBeActive)
                {
                    _active = false;
                    _temporaryAllowlist.Clear();
                    await RestoreEntriesAsync(cancellationToken);
                    return;
                }

                _active = true;
                await ReconcileActiveAsync(cancellationToken);
            },
            cancellationToken);

    public Task UpdateAllowlistAsync(
        IEnumerable<string> allowlist,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                SetAllowlist(allowlist);
                if (!_active)
                {
                    return;
                }

                await ReconcileActiveAsync(cancellationToken);
            },
            cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                _enabled = false;
                _active = false;
                _temporaryAllowlist.Clear();
                await RestoreEntriesAsync(cancellationToken);
            },
            cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _controller.SessionsChanged -= OnSessionsChanged;
        _controller.MuteChanged -= OnMuteChanged;
        _gate.Dispose();
    }

    private async Task ReconcileActiveAsync(CancellationToken cancellationToken)
    {
        var sessions = await _controller.GetSessionsAsync(cancellationToken);
        var error = await RestoreTrackedEntriesAsync(
            sessions,
            entry => IsPermanentlyAllowed(entry.ApplicationId),
            removeEntries: true,
            cancellationToken);
        var addedEntries = false;

        foreach (var session in sessions)
        {
            if (IsAllowed(session.ApplicationId) || session.IsMuted)
            {
                continue;
            }

            if (!_entries.ContainsKey(session.Key))
            {
                _entries[session.Key] = new AudioGuardJournalEntry(
                    session.Key,
                    NormalizeIdentity(session.ApplicationId),
                    session.IsMuted);
                addedEntries = true;
            }
        }

        if (addedEntries)
        {
            SaveJournal();
        }

        foreach (var session in sessions)
        {
            if (IsAllowed(session.ApplicationId) || session.IsMuted)
            {
                continue;
            }

            try
            {
                await _controller.SetMuteAsync(
                    session.Key,
                    true,
                    _eventContext,
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                error ??= exception.Message;
            }
        }

        PublishError(error);
    }

    private async Task RestoreEntriesAsync(CancellationToken cancellationToken)
    {
        if (_entries.Count == 0)
        {
            _journalStore.Clear();
            PublishError(null);
            return;
        }

        var sessions = await _controller.GetSessionsAsync(cancellationToken);
        var error = await RestoreTrackedEntriesAsync(
            sessions,
            _ => true,
            removeEntries: true,
            cancellationToken);
        PublishError(error);
    }

    private async Task RestoreApplicationAsync(
        string applicationId,
        bool removeEntries,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeIdentity(applicationId);
        var entries = _entries.Values
            .Where(entry => IdentityComparer.Equals(entry.ApplicationId, normalized))
            .ToArray();
        if (entries.Length == 0)
        {
            return;
        }

        var sessions = await _controller.GetSessionsAsync(cancellationToken);
        var error = await RestoreTrackedEntriesAsync(
            sessions,
            entry => entries.Contains(entry),
            removeEntries,
            cancellationToken);
        PublishError(error);
    }

    private async Task<string?> RestoreTrackedEntriesAsync(
        IReadOnlyList<AudioSession> sessions,
        Func<AudioGuardJournalEntry, bool> predicate,
        bool removeEntries,
        CancellationToken cancellationToken)
    {
        string? error = null;
        var journalChanged = false;
        foreach (var entry in _entries.Values.Where(predicate).ToArray())
        {
            var matches = FindMatches(sessions, entry);
            if (matches.Length == 0)
            {
                continue;
            }

            var restored = true;
            foreach (var session in matches)
            {
                try
                {
                    await _controller.SetMuteAsync(
                        session.Key,
                        entry.OriginalMute,
                        _eventContext,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    error ??= exception.Message;
                    restored = false;
                }
            }

            if (restored && removeEntries)
            {
                _entries.Remove(entry.Key);
                journalChanged = true;
            }
        }

        if (journalChanged)
        {
            SaveJournal();
        }

        return error;
    }

    private static AudioSession[] FindMatches(
        IReadOnlyList<AudioSession> sessions,
        AudioGuardJournalEntry entry)
    {
        var exact = sessions
            .Where(session => session.Key == entry.Key)
            .ToArray();
        if (exact.Length > 0)
        {
            return exact;
        }

        return sessions
            .Where(
                session =>
                    string.Equals(
                        NormalizeIdentity(session.ApplicationId),
                        entry.ApplicationId,
                        StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private void LoadJournal()
    {
        _entries.Clear();
        var journal = _journalStore.Load();
        if (journal is null)
        {
            return;
        }

        foreach (var entry in journal.Entries)
        {
            _entries[entry.Key] = entry with
            {
                ApplicationId = NormalizeIdentity(entry.ApplicationId)
            };
        }
    }

    private void SaveJournal()
    {
        if (_entries.Count == 0)
        {
            _journalStore.Clear();
            return;
        }

        _journalStore.Save(
            new AudioGuardJournal(
                DateTimeOffset.UtcNow,
                _entries.Values.ToArray()));
    }

    private void SetAllowlist(IEnumerable<string> allowlist)
    {
        _allowlist = AudioAllowlistSettings.Normalize(allowlist)
            .ToHashSet(IdentityComparer);
    }

    private bool IsAllowed(string applicationId)
    {
        var normalized = NormalizeIdentity(applicationId);
        return IsPermanentlyAllowed(normalized) ||
            _temporaryAllowlist.Contains(normalized);
    }

    private bool IsPermanentlyAllowed(string applicationId)
    {
        var normalized = NormalizeIdentity(applicationId);
        return normalized == "cs2" || _allowlist.Contains(normalized);
    }

    private static string NormalizeIdentity(string applicationId) =>
        AudioApplicationIdentity.Normalize(applicationId);

    private void OnSessionsChanged(object? sender, EventArgs e) =>
        QueueReconcile();

    private void OnMuteChanged(object? sender, AudioMuteChanged e)
    {
        if (e.EventContext == _eventContext || e.IsMuted)
        {
            return;
        }

        QueueEvent(
            cancellationToken => HandleExternalUnmuteAsync(e, cancellationToken));
    }

    private async Task HandleExternalUnmuteAsync(
        AudioMuteChanged change,
        CancellationToken cancellationToken)
    {
        if (!_active || IsAllowed(change.ApplicationId))
        {
            return;
        }

        var applicationId = NormalizeIdentity(change.ApplicationId);
        _temporaryAllowlist.Add(applicationId);
        await RestoreApplicationAsync(
            applicationId,
            removeEntries: false,
            cancellationToken);
    }

    private async Task ReconcileAfterEventAsync(CancellationToken cancellationToken)
    {
        if (_active)
        {
            await ReconcileActiveAsync(cancellationToken);
        }
        else if (_entries.Count > 0)
        {
            await RestoreEntriesAsync(cancellationToken);
        }
    }

    private void QueueEvent(Func<CancellationToken, Task> operation)
    {
        if (_disposed)
        {
            return;
        }

        _ = RunEventAsync(operation);
    }

    private void QueueReconcile()
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _reconcileRequested, 1);
        if (Interlocked.CompareExchange(ref _reconcileWorkerActive, 1, 0) == 0)
        {
            _ = DrainReconcileRequestsAsync();
        }
    }

    private async Task DrainReconcileRequestsAsync()
    {
        try
        {
            while (Interlocked.Exchange(ref _reconcileRequested, 0) == 1)
            {
                await RunLockedAsync(
                    () => ReconcileAfterEventAsync(CancellationToken.None),
                    CancellationToken.None);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _reconcileWorkerActive, 0);
            if (Volatile.Read(ref _reconcileRequested) == 1 &&
                Interlocked.CompareExchange(ref _reconcileWorkerActive, 1, 0) == 0)
            {
                _ = DrainReconcileRequestsAsync();
            }
        }
    }

    private async Task RunEventAsync(Func<CancellationToken, Task> operation)
    {
        try
        {
            await RunLockedAsync(() => operation(CancellationToken.None), CancellationToken.None);
        }
        catch (Exception exception)
        {
            PublishError(exception.Message);
        }
    }

    private async Task RunLockedAsync(
        Func<Task> operation,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            PublishError(exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void PublishError(string? error)
    {
        if (string.Equals(LastError, error, StringComparison.Ordinal))
        {
            return;
        }

        LastError = error;
        ErrorChanged?.Invoke(this, error);
    }
}
