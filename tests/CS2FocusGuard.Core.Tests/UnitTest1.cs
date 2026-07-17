using CS2FocusGuard.Core;

namespace CS2FocusGuard.Core.Tests;

public sealed class GuardCoordinatorTests
{
    [Fact]
    public async Task GameStartSuppressesAndGameExitRestoresOriginalProfile()
    {
        var controller = new FakeNotificationController("unrestricted", "alarms");
        var journal = new MemoryJournalStore();
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, false);
        await coordinator.EvaluateAsync(true);

        Assert.Equal("alarms", controller.SelectedProfile);
        Assert.Equal(GuardState.Suppressed, coordinator.Status.State);
        Assert.NotNull(journal.Value);

        await coordinator.EvaluateAsync(false);

        Assert.Equal("unrestricted", controller.SelectedProfile);
        Assert.Equal(GuardState.Waiting, coordinator.Status.State);
        Assert.Null(journal.Value);
        Assert.Equal(["alarms", "unrestricted"], controller.SetCalls);
    }

    [Fact]
    public async Task ExistingSuppressionModeIsNotRestoredByTheGuard()
    {
        var controller = new FakeNotificationController("alarms", "alarms");
        var journal = new MemoryJournalStore();
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, true);
        await coordinator.EvaluateAsync(false);

        Assert.Equal("alarms", controller.SelectedProfile);
        Assert.Empty(controller.SetCalls);
        Assert.Null(journal.Value);
    }

    [Fact]
    public async Task ManualChangeRevokesOwnershipAndIsPreserved()
    {
        var controller = new FakeNotificationController("unrestricted", "alarms");
        var journal = new MemoryJournalStore();
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, true);
        controller.SelectedProfile = "priority";
        await coordinator.EvaluateAsync(true);

        Assert.Equal(GuardState.UserOverride, coordinator.Status.State);
        Assert.Null(journal.Value);

        await coordinator.EvaluateAsync(false);

        Assert.Equal("priority", controller.SelectedProfile);
        Assert.Single(controller.SetCalls);
    }

    [Fact]
    public async Task DisablingWhileGameRunsRestoresOriginalProfile()
    {
        var controller = new FakeNotificationController("priority", "alarms");
        var journal = new MemoryJournalStore();
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, true);
        await coordinator.SetEnabledAsync(false, true);

        Assert.False(coordinator.IsEnabled);
        Assert.Equal("priority", controller.SelectedProfile);
        Assert.Equal(GuardState.Disabled, coordinator.Status.State);
        Assert.Null(journal.Value);
    }

    [Fact]
    public async Task RepeatedGameChecksDoNotReapplySuppression()
    {
        var controller = new FakeNotificationController("unrestricted", "alarms");
        var coordinator = new GuardCoordinator(controller, new MemoryJournalStore());

        await coordinator.InitializeAsync(true, true);
        await coordinator.EvaluateAsync(true);
        await coordinator.EvaluateAsync(true);

        Assert.Single(controller.SetCalls);
        Assert.Equal("alarms", controller.SetCalls[0]);
    }

    [Fact]
    public async Task StaleJournalRestoresWhenGameIsNotRunning()
    {
        var controller = new FakeNotificationController("alarms", "alarms");
        var journal = new MemoryJournalStore
        {
            Value = new GuardJournal(
                "unrestricted",
                "alarms",
                DateTimeOffset.UtcNow.AddMinutes(-5))
        };
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, false);

        Assert.Equal("unrestricted", controller.SelectedProfile);
        Assert.Equal(GuardState.Waiting, coordinator.Status.State);
        Assert.Null(journal.Value);
    }

    [Fact]
    public async Task StaleJournalContinuesOwnershipWhileGameRuns()
    {
        var controller = new FakeNotificationController("alarms", "alarms");
        var journal = new MemoryJournalStore
        {
            Value = new GuardJournal(
                "priority",
                "alarms",
                DateTimeOffset.UtcNow.AddMinutes(-5))
        };
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, true);
        await coordinator.EvaluateAsync(false);

        Assert.Equal("priority", controller.SelectedProfile);
        Assert.Null(journal.Value);
    }

    [Fact]
    public async Task AJournalWithDifferentCurrentProfileIsDiscarded()
    {
        var controller = new FakeNotificationController("priority", "alarms");
        var journal = new MemoryJournalStore
        {
            Value = new GuardJournal(
                "unrestricted",
                "alarms",
                DateTimeOffset.UtcNow.AddMinutes(-5))
        };
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, false);

        Assert.Equal("priority", controller.SelectedProfile);
        Assert.Empty(controller.SetCalls);
        Assert.Null(journal.Value);
    }

    [Fact]
    public async Task FailedReadbackEntersErrorAndClearsUnneededJournal()
    {
        var controller = new FakeNotificationController("unrestricted", "alarms")
        {
            IgnoreSet = true
        };
        var journal = new MemoryJournalStore();
        var coordinator = new GuardCoordinator(controller, journal);

        await coordinator.InitializeAsync(true, true);

        Assert.Equal(GuardState.Error, coordinator.Status.State);
        Assert.Null(journal.Value);
        Assert.Equal("unrestricted", controller.SelectedProfile);
    }

    private sealed class FakeNotificationController(
        string selectedProfile,
        string suppressionProfile) : INotificationController
    {
        public string SelectedProfile { get; set; } = selectedProfile;

        public string SuppressionProfileId { get; } = suppressionProfile;

        public bool IgnoreSet { get; init; }

        public List<string> SetCalls { get; } = [];

        public Task<NotificationState> GetStateAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(
                new NotificationState(
                    SelectedProfile,
                    SelectedProfile,
                    "unrestricted"));

        public Task SetSelectedProfileAsync(
            string profileId,
            CancellationToken cancellationToken = default)
        {
            SetCalls.Add(profileId);
            if (!IgnoreSet)
            {
                SelectedProfile = profileId;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class MemoryJournalStore : IGuardJournalStore
    {
        public GuardJournal? Value { get; set; }

        public GuardJournal? Load() => Value;

        public void Save(GuardJournal journal) => Value = journal;

        public void Clear() => Value = null;
    }
}