using CS2FocusGuard.App;

namespace CS2FocusGuard.Core.Tests;

public sealed class AppSettingsStoreTests
{
    [Fact]
    public void LegacySettingsUseDefaultAppearance()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(
            Path.Combine(directory.Path, "settings.json"),
            """
            {
              "Enabled": true,
              "StartWithWindows": false,
              "CloseToTray": true,
              "UseTraditionalChinese": true,
              "AudioAllowlist": []
            }
            """);

        var settings = new AppSettingsStore(directory.Path).Load();

        Assert.False(settings.UseLargeInterface);
        Assert.False(settings.UseDarkTheme);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AppearanceSettingsRoundTrip(
        bool useLargeInterface,
        bool useDarkTheme)
    {
        using var directory = new TemporaryDirectory();
        var store = new AppSettingsStore(directory.Path);
        var expected = new AppSettings(
            UseLargeInterface: useLargeInterface,
            UseDarkTheme: useDarkTheme);

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected, actual);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"CS2FocusGuard.Tests.{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
