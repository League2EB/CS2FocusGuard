namespace CS2FocusGuard.Core;

public enum GuardState
{
    Disabled,
    Waiting,
    Suppressed,
    UserOverride,
    Error
}

public sealed record GuardStatus(GuardState State, string? Detail = null);

public sealed record NotificationState(
    string SelectedProfile,
    string ActiveProfile,
    string OffProfile);

public sealed record GuardJournal(
    string OriginalSelectedProfile,
    string TargetProfile,
    DateTimeOffset CreatedAtUtc);

public interface INotificationController
{
    string SuppressionProfileId { get; }

    Task<NotificationState> GetStateAsync(CancellationToken cancellationToken = default);

    Task SetSelectedProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default);
}

public interface IGuardJournalStore
{
    GuardJournal? Load();

    void Save(GuardJournal journal);

    void Clear();
}

public interface IGameProcessProbe
{
    bool IsRunning();
}

public interface IGuardComponent
{
    Task InitializeAsync(
        bool enabled,
        bool gameRunning,
        CancellationToken cancellationToken = default);

    Task SetEnabledAsync(
        bool enabled,
        bool gameRunning,
        CancellationToken cancellationToken = default);

    Task EvaluateAsync(
        bool gameRunning,
        CancellationToken cancellationToken = default);

    Task ShutdownAsync(CancellationToken cancellationToken = default);
}

public sealed class GuardCoordinator(
    INotificationController notificationController,
    IGuardJournalStore journalStore) : IGuardComponent, IDisposable
{
    private readonly INotificationController _notificationController =
        notificationController;
    private readonly IGuardJournalStore _journalStore = journalStore;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private GuardSession? _session;

    public event EventHandler<GuardStatus>? StatusChanged;

    public bool IsEnabled { get; private set; }

    public GuardStatus Status { get; private set; } = new(GuardState.Disabled);

    public void Dispose() => _gate.Dispose();

    public Task InitializeAsync(
        bool enabled,
        bool gameRunning,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                IsEnabled = enabled;
                await RecoverAsync(enabled, gameRunning, cancellationToken);

                if (!enabled)
                {
                    await EndSessionAsync(cancellationToken);
                    Publish(GuardState.Disabled);
                }
                else if (gameRunning)
                {
                    if (_session is null)
                    {
                        await BeginSessionAsync(cancellationToken);
                    }
                }
                else
                {
                    Publish(GuardState.Waiting);
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
                IsEnabled = enabled;

                if (!enabled)
                {
                    await EndSessionAsync(cancellationToken);
                    Publish(GuardState.Disabled);
                }
                else if (gameRunning)
                {
                    await BeginSessionAsync(cancellationToken);
                }
                else
                {
                    Publish(GuardState.Waiting);
                }
            },
            cancellationToken);

    public Task EvaluateAsync(
        bool gameRunning,
        CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                if (!IsEnabled)
                {
                    return;
                }

                if (gameRunning)
                {
                    if (_session is null)
                    {
                        await BeginSessionAsync(cancellationToken);
                    }
                    else
                    {
                        await VerifyOwnershipAsync(cancellationToken);
                    }
                }
                else
                {
                    await EndSessionAsync(cancellationToken);
                    Publish(GuardState.Waiting);
                }
            },
            cancellationToken);

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        RunLockedAsync(
            async () =>
            {
                IsEnabled = false;
                await EndSessionAsync(cancellationToken);
                Publish(GuardState.Disabled);
            },
            cancellationToken);

    private async Task RecoverAsync(
        bool enabled,
        bool gameRunning,
        CancellationToken cancellationToken)
    {
        var journal = _journalStore.Load();
        if (journal is null)
        {
            return;
        }

        var current = await _notificationController.GetStateAsync(cancellationToken);
        if (!ProfileEquals(current.SelectedProfile, journal.TargetProfile))
        {
            _journalStore.Clear();
            return;
        }

        if (enabled && gameRunning)
        {
            _session = new GuardSession(
                journal.OriginalSelectedProfile,
                journal.TargetProfile,
                true,
                false);
            Publish(GuardState.Suppressed);
            return;
        }

        await RestoreProfileAsync(journal.OriginalSelectedProfile, cancellationToken);
        _journalStore.Clear();
    }

    private async Task BeginSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is not null)
        {
            return;
        }

        var current = await _notificationController.GetStateAsync(cancellationToken);
        var target = _notificationController.SuppressionProfileId;

        if (ProfileEquals(current.SelectedProfile, target))
        {
            _session = new GuardSession(current.SelectedProfile, target, false, false);
            Publish(GuardState.Suppressed);
            return;
        }

        var journal = new GuardJournal(
            current.SelectedProfile,
            target,
            DateTimeOffset.UtcNow);
        _journalStore.Save(journal);

        await _notificationController.SetSelectedProfileAsync(target, cancellationToken);
        var changed = await _notificationController.GetStateAsync(cancellationToken);
        if (!ProfileEquals(changed.SelectedProfile, target))
        {
            _journalStore.Clear();
            throw new InvalidOperationException("Windows did not apply the requested notification mode.");
        }

        _session = new GuardSession(current.SelectedProfile, target, true, false);
        Publish(GuardState.Suppressed);
    }

    private async Task VerifyOwnershipAsync(CancellationToken cancellationToken)
    {
        if (_session is not { ChangedByUs: true, OwnershipLost: false } session)
        {
            return;
        }

        var current = await _notificationController.GetStateAsync(cancellationToken);
        if (ProfileEquals(current.SelectedProfile, session.TargetProfile))
        {
            return;
        }

        _session = session with { OwnershipLost = true };
        _journalStore.Clear();
        Publish(GuardState.UserOverride);
    }

    private async Task EndSessionAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        var session = _session;
        if (session.ChangedByUs && !session.OwnershipLost)
        {
            var current = await _notificationController.GetStateAsync(cancellationToken);
            if (ProfileEquals(current.SelectedProfile, session.TargetProfile))
            {
                await RestoreProfileAsync(session.OriginalSelectedProfile, cancellationToken);
            }
        }

        _journalStore.Clear();
        _session = null;
    }

    private async Task RestoreProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        await _notificationController.SetSelectedProfileAsync(profileId, cancellationToken);
        var restored = await _notificationController.GetStateAsync(cancellationToken);
        if (!ProfileEquals(restored.SelectedProfile, profileId))
        {
            throw new InvalidOperationException("Windows did not restore the original notification mode.");
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
            Publish(GuardState.Error, exception.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void Publish(GuardState state, string? detail = null)
    {
        var next = new GuardStatus(state, detail);
        if (next == Status)
        {
            return;
        }

        Status = next;
        StatusChanged?.Invoke(this, next);
    }

    private static bool ProfileEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private sealed record GuardSession(
        string OriginalSelectedProfile,
        string TargetProfile,
        bool ChangedByUs,
        bool OwnershipLost);
}

public sealed class GuardMonitor(
    IReadOnlyList<IGuardComponent> components,
    IGameProcessProbe processProbe,
    TimeSpan? pollInterval = null) : IAsyncDisposable
{
    private readonly IReadOnlyList<IGuardComponent> _components = components;
    private readonly IGameProcessProbe _processProbe = processProbe;
    private readonly TimeSpan _pollInterval =
        pollInterval ?? TimeSpan.FromSeconds(1);
    private CancellationTokenSource? _cancellation;
    private Task? _loop;

    public GuardMonitor(
        GuardCoordinator coordinator,
        IGameProcessProbe processProbe,
        TimeSpan? pollInterval = null)
        : this([coordinator], processProbe, pollInterval)
    {
    }

    public async Task StartAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        if (_loop is not null)
        {
            return;
        }

        var gameRunning = _processProbe.IsRunning();
        foreach (var component in _components)
        {
            await component.InitializeAsync(
                enabled,
                gameRunning,
                cancellationToken);
        }

        _cancellation = new CancellationTokenSource();
        _loop = RunAsync(_cancellation.Token);
    }

    public async Task SetEnabledAsync(
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var gameRunning = _processProbe.IsRunning();
        foreach (var component in _components)
        {
            await component.SetEnabledAsync(
                enabled,
                gameRunning,
                cancellationToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cancellation is not null)
        {
            await _cancellation.CancelAsync();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loop = null;
        _cancellation?.Dispose();
        _cancellation = null;
        foreach (var component in _components.Reverse())
        {
            await component.ShutdownAsync(cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_pollInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var gameRunning = _processProbe.IsRunning();
            foreach (var component in _components)
            {
                await component.EvaluateAsync(
                    gameRunning,
                    cancellationToken);
            }
        }
    }
}
