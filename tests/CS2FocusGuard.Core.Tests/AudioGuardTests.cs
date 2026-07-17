using CS2FocusGuard.Core;

namespace CS2FocusGuard.Core.Tests;

public sealed class AudioGuardCoordinatorTests
{
    [Fact]
    public async Task GameStartMutesOnlyApplicationsOutsideAllowlist()
    {
        var controller = new FakeAudioSessionController(
            Session("other"),
            Session("discord"),
            Session("cs2"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());

        await coordinator.InitializeAsync(true, true, ["discord"]);

        Assert.True(controller.Session("other").IsMuted);
        Assert.False(controller.Session("discord").IsMuted);
        Assert.False(controller.Session("cs2").IsMuted);
    }

    [Fact]
    public async Task GameExitRestoresOriginalMuteStates()
    {
        var controller = new FakeAudioSessionController(
            Session("other"),
            Session("already-muted", muted: true));
        var journal = new MemoryAudioJournalStore();
        using var coordinator = new AudioGuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, true, []);
        await coordinator.EvaluateAsync(false);

        Assert.False(controller.Session("other").IsMuted);
        Assert.True(controller.Session("already-muted").IsMuted);
        Assert.Null(journal.Value);
    }

    [Fact]
    public async Task NewAudioSessionIsMutedWhileGameRuns()
    {
        var controller = new FakeAudioSessionController();
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, true, []);

        controller.AddSession(Session("new-app"));
        await coordinator.EvaluateAsync(true);

        Assert.True(controller.Session("new-app").IsMuted);
    }

    [Fact]
    public async Task AddingToAllowlistImmediatelyRestoresApplication()
    {
        var controller = new FakeAudioSessionController(Session("music"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, true, []);

        await coordinator.UpdateAllowlistAsync(["music"]);

        Assert.False(controller.Session("music").IsMuted);
    }

    [Fact]
    public async Task RemovingFromAllowlistImmediatelyMutesApplication()
    {
        var controller = new FakeAudioSessionController(Session("music"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, true, ["music"]);

        await coordinator.UpdateAllowlistAsync([]);

        Assert.True(controller.Session("music").IsMuted);
    }

    [Fact]
    public async Task DisabledGuardDoesNotReactivateDuringLaterGameChecks()
    {
        var controller = new FakeAudioSessionController(Session("other"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, false, []);

        await coordinator.SetEnabledAsync(false, false);
        await coordinator.EvaluateAsync(true);

        Assert.False(controller.Session("other").IsMuted);
    }

    [Fact]
    public async Task AllowedApplicationRestoreIsRetriedAfterFailure()
    {
        var controller = new FakeAudioSessionController(Session("music"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, true, []);
        controller.FailNextUnmute = true;

        await coordinator.UpdateAllowlistAsync(["music"]);
        Assert.True(controller.Session("music").IsMuted);

        await coordinator.EvaluateAsync(true);
        Assert.False(controller.Session("music").IsMuted);
    }

    [Fact]
    public async Task ManualUnmuteTemporarilyAllowsEverySessionForApplication()
    {
        var controller = new FakeAudioSessionController(
            Session("browser", "one"),
            Session("browser", "two"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, true, []);
        Assert.All(controller.Sessions, session => Assert.True(session.IsMuted));

        controller.ExternalSetMute("browser", "one", muted: false);
        await WaitUntilAsync(
            () => controller.Sessions.All(session => !session.IsMuted));
        controller.AddSession(Session("browser", "three"));
        await coordinator.EvaluateAsync(true);

        Assert.All(controller.Sessions, session => Assert.False(session.IsMuted));
        await coordinator.EvaluateAsync(false);
        Assert.All(controller.Sessions, session => Assert.False(session.IsMuted));
    }

    [Fact]
    public async Task RepeatedChecksDoNotReplaceOriginalState()
    {
        var controller = new FakeAudioSessionController(Session("other"));
        using var coordinator = new AudioGuardCoordinator(
            controller,
            new MemoryAudioJournalStore());
        await coordinator.InitializeAsync(true, true, []);
        await coordinator.EvaluateAsync(true);
        await coordinator.EvaluateAsync(false);

        Assert.False(controller.Session("other").IsMuted);
    }

    [Fact]
    public async Task StaleJournalRestoresMatchingApplicationOnNewSession()
    {
        var oldKey = new AudioSessionKey("old-device", "old-session");
        var journal = new MemoryAudioJournalStore
        {
            Value = new AudioGuardJournal(
                DateTimeOffset.UtcNow.AddMinutes(-1),
                [new AudioGuardJournalEntry(oldKey, "other", false)])
        };
        var controller = new FakeAudioSessionController(
            Session("other", endpointId: "new-device"));
        using var coordinator = new AudioGuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(false, false, []);

        Assert.False(controller.Session("other").IsMuted);
        Assert.Null(journal.Value);
    }

    [Fact]
    public void AllowlistNormalizationSeedsDefaultsOnlyForMissingSetting()
    {
        Assert.Equal(
            ["discord", "kook", "oopz"],
            AudioAllowlistSettings.Normalize(null));
        Assert.Empty(AudioAllowlistSettings.Normalize([]));
        Assert.Equal(
            ["discord", "oopz"],
            AudioAllowlistSettings.Normalize([" Discord.exe ", "discord", "OOPZ"]));
    }

    private static MutableAudioSession Session(
        string applicationId,
        string? sessionId = null,
        bool muted = false,
        string endpointId = "device") =>
        new(
            new AudioSessionKey(endpointId, sessionId ?? applicationId),
            applicationId,
            applicationId,
            muted);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    private sealed class FakeAudioSessionController(params MutableAudioSession[] sessions)
        : IAudioSessionController
    {
        private readonly List<MutableAudioSession> _sessions = [.. sessions];

        public event EventHandler? SessionsChanged;

        public event EventHandler<AudioMuteChanged>? MuteChanged;

        internal IReadOnlyList<MutableAudioSession> Sessions => _sessions;

        internal bool FailNextUnmute { get; set; }

        public Task<IReadOnlyList<AudioSession>> GetSessionsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AudioSession>>(
                _sessions
                    .Select(
                        session => new AudioSession(
                            session.Key,
                            session.ApplicationId,
                            session.DisplayName,
                            session.IsMuted))
                    .ToArray());

        public Task SetMuteAsync(
            AudioSessionKey key,
            bool muted,
            Guid eventContext,
            CancellationToken cancellationToken = default)
        {
            var session = _sessions.Single(session => session.Key == key);
            if (!muted && FailNextUnmute)
            {
                FailNextUnmute = false;
                throw new InvalidOperationException("Simulated audio failure.");
            }

            session.IsMuted = muted;
            MuteChanged?.Invoke(
                this,
                new AudioMuteChanged(
                    key,
                    session.ApplicationId,
                    muted,
                    eventContext));
            return Task.CompletedTask;
        }

        internal MutableAudioSession Session(string applicationId) =>
            _sessions.First(
                session => session.ApplicationId.Equals(
                    applicationId,
                    StringComparison.OrdinalIgnoreCase));

        internal void AddSession(MutableAudioSession session)
        {
            _sessions.Add(session);
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }

        internal void ExternalSetMute(
            string applicationId,
            string sessionId,
            bool muted)
        {
            var session = _sessions.Single(
                session =>
                    session.ApplicationId == applicationId &&
                    session.Key.SessionId == sessionId);
            session.IsMuted = muted;
            MuteChanged?.Invoke(
                this,
                new AudioMuteChanged(
                    session.Key,
                    applicationId,
                    muted,
                    Guid.NewGuid()));
        }
    }

    private sealed class MutableAudioSession(
        AudioSessionKey key,
        string applicationId,
        string displayName,
        bool isMuted)
    {
        internal AudioSessionKey Key { get; } = key;

        internal string ApplicationId { get; } = applicationId;

        internal string DisplayName { get; } = displayName;

        internal bool IsMuted { get; set; } = isMuted;
    }

    private sealed class MemoryAudioJournalStore : IAudioGuardJournalStore
    {
        internal AudioGuardJournal? Value { get; set; }

        public AudioGuardJournal? Load() => Value;

        public void Save(AudioGuardJournal journal) => Value = journal;

        public void Clear() => Value = null;
    }
}
