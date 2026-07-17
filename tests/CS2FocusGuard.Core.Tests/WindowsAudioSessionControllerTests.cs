using CS2FocusGuard.App;
using CS2FocusGuard.Core;

namespace CS2FocusGuard.Core.Tests;

public sealed class WindowsAudioSessionControllerTests
{
    [Fact]
    public async Task ControllerEnumeratesCurrentAudioSessions()
    {
        var controller = await Task.Run(() => new WindowsAudioSessionController())
            .WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var sessions = await controller.GetSessionsAsync()
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.NotNull(sessions);
        }
        finally
        {
            await Task.Run(controller.Dispose)
                .WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AudioTimeoutDoesNotPreventRuntimeInitialization()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"CS2FocusGuard.Tests.{Guid.NewGuid():N}");
        try
        {
            await using var runtime = new AppRuntime(
                dataDirectory,
                static () =>
                {
                    Thread.Sleep(100);
                    return new FakeAudioSessionController();
                },
                TimeSpan.FromMilliseconds(10),
                new StoppedGameProbe());

            await runtime.InitializeAsync();

            Assert.Equal(GuardState.Error, runtime.Status.State);
            Assert.Contains(
                "did not complete",
                runtime.Status.Detail,
                StringComparison.Ordinal);
            Assert.True(
                File.Exists(
                    Path.Combine(dataDirectory, "startup-error.log")));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private sealed class StoppedGameProbe : IGameProcessProbe
    {
        public bool IsRunning() => false;
    }

    private sealed class FakeAudioSessionController
        : IManagedAudioSessionController
    {
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

        public void Dispose()
        {
        }
    }
}

public sealed class ApplicationAllowlistOrderingTests
{
    [Fact]
    public void AllowedApplicationsAppearBeforeOtherApplications()
    {
        var applications = new[]
        {
            Item("Browser", false),
            Item("Discord", true),
            Item("KOOK", true),
            Item("Audio Player", false)
        };

        var ordered = ApplicationAllowlistOrdering.Order(applications)
            .Select(application => application.DisplayName)
            .ToArray();

        Assert.Equal(
            ["Discord", "KOOK", "Audio Player", "Browser"],
            ordered);

        applications[0].IsAllowed = true;
        var reordered = ApplicationAllowlistOrdering.Order(applications)
            .Select(application => application.DisplayName)
            .ToArray();

        Assert.Equal(
            ["Browser", "Discord", "KOOK", "Audio Player"],
            reordered);
    }

    private static ApplicationAllowlistItem Item(string displayName, bool isAllowed) =>
        new(
            new ApplicationDescriptor(
                displayName.ToLowerInvariant(),
                displayName,
                null),
            isAllowed,
            static _ => { });
}
