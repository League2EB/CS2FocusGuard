using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using CS2FocusGuard.App;
using CS2FocusGuard.Core;

namespace CS2FocusGuard.Core.Tests;

public sealed class AppearanceTests
{
    [Fact]
    public void ThemesScalingAndExpandedAllowlistLoadOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    VerifyAppearance();
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
            });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void VerifyAppearance()
    {
        using var directory = new TemporaryDirectory();
        var application = new System.Windows.Application
        {
            Resources = LoadApplicationResources()
        };

        AppearanceManager.ApplyTheme(useDarkTheme: true);
        var darkBackground = AssertOpaqueWindowBackground(
            application,
            Color.FromRgb(0x12, 0x18, 0x23));

        AppearanceManager.ApplyInterfaceMetrics(useLargeInterface: true);
        Assert.Equal(
            15d,
            Assert.IsType<double>(
                application.FindResource("Metric.Font.Body")));
        Assert.Equal(
            60d,
            Assert.IsType<double>(
                application.FindResource("Metric.Control.ToggleWidth")));

        AppearanceManager.ApplyTheme(useDarkTheme: false);
        AppearanceManager.ApplyInterfaceMetrics(useLargeInterface: false);
        Assert.Equal(
            12d,
            Assert.IsType<double>(
                application.FindResource("Metric.Font.Body")));
        var lightBackground = AssertOpaqueWindowBackground(
            application,
            Color.FromRgb(0xFA, 0xFB, 0xFE));

        var runtime = new AppRuntime(
            directory.Path,
            static () => throw new InvalidOperationException(),
            TimeSpan.Zero,
            new NeverRunningProcessProbe());
        MainWindow? window = null;
        try
        {
            window = new MainWindow(runtime)
            {
                Width = 1200,
                Height = 1200,
                Opacity = 0,
                ShowActivated = false,
                ShowInTaskbar = false
            };

            window.Show();
            window.UpdateLayout();
            var source = Assert.IsType<HwndSource>(
                PresentationSource.FromVisual(window));
            Assert.NotNull(source.CompositionTarget);
            Assert.Equal(
                byte.MaxValue,
                source.CompositionTarget.BackgroundColor.A);
            AssertOpaqueWindowBackground(
                window.Background,
                lightBackground.GradientStops[0].Color);
            var allowlist = FindVisualChild<ListBox>(window);
            Assert.NotNull(allowlist);
            var viewModel = (MainViewModel)window.DataContext;
            var homeScroller = (ScrollViewer)window.FindName("HomeScroller");
            var settingsScroller = (ScrollViewer)window.FindName("SettingsScroller");
            var content = (Grid)window.FindName("ResponsiveContent");
            var allowlistCard = (Border)window.FindName("AudioAllowlistCard");
            var homeHeader = (Grid)window.FindName("HomeHeader");
            var settingsHeader = (Grid)window.FindName("SettingsHeader");
            var settingsButton = (Button)window.FindName("SettingsButton");
            var backButton = (Button)window.FindName("BackButton");
            var headerBadgeRays = (System.Windows.Shapes.Path)
                window.FindName("HeaderBadgeRays");
            var versionValue = (TextBlock)window.FindName("VersionValue");
            var appVersion = typeof(MainViewModel).Assembly.GetName().Version
                ?? throw new InvalidOperationException();
            var cardBottom = allowlistCard.TranslatePoint(
                new Point(0, allowlistCard.ActualHeight),
                content);

            Assert.Equal(MainPage.Home, viewModel.SelectedPage);
            Assert.Equal(Visibility.Visible, homeHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsHeader.Visibility);
            Assert.Equal(Visibility.Visible, homeScroller.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsScroller.Visibility);
            Assert.Same(
                viewModel.NavigateSettingsCommand,
                settingsButton.Command);
            Assert.Equal(12d, settingsButton.FontSize);
            Assert.Equal(38d, settingsButton.Width);
            Assert.Equal(Stretch.Fill, headerBadgeRays.Stretch);
            Assert.Equal(24d, headerBadgeRays.ActualWidth);
            Assert.Equal(24d, headerBadgeRays.ActualHeight);
            Assert.Null(window.FindName("GlassTabBar"));
            Assert.True(allowlist.ActualHeight >= 80);
            Assert.InRange(
                Math.Abs(content.ActualHeight - homeScroller.ActualHeight),
                0,
                1);
            Assert.InRange(
                Math.Abs(cardBottom.Y - content.ActualHeight),
                0,
                1);

            settingsButton.Command.Execute(settingsButton.CommandParameter);
            window.UpdateLayout();

            Assert.Equal(MainPage.Settings, viewModel.SelectedPage);
            Assert.Equal(Visibility.Collapsed, homeHeader.Visibility);
            Assert.Equal(Visibility.Visible, settingsHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, homeScroller.Visibility);
            Assert.Equal(Visibility.Visible, settingsScroller.Visibility);
            Assert.Same(viewModel.NavigateHomeCommand, backButton.Command);
            Assert.Equal(
                appVersion.ToString(3),
                MainViewModel.FormatVersionText(
                    appVersion,
                    isDebugBuild: false));
            Assert.Equal(
                $"{appVersion.ToString(3)} ({Strings.Get("DebugBuild")})",
                MainViewModel.FormatVersionText(
                    appVersion,
                    isDebugBuild: true));
#if DEBUG
            Assert.Equal(
                MainViewModel.FormatVersionText(appVersion, isDebugBuild: true),
                viewModel.VersionText);
#else
            Assert.Equal(
                MainViewModel.FormatVersionText(appVersion, isDebugBuild: false),
                viewModel.VersionText);
#endif
            Assert.Equal(viewModel.VersionText, versionValue.Text);

            viewModel.UseDarkTheme = true;
            viewModel.UseLargeInterface = true;
            backButton.Command.Execute(backButton.CommandParameter);
            window.UpdateLayout();

            darkBackground = AssertOpaqueWindowBackground(
                application,
                Color.FromRgb(0x12, 0x18, 0x23));
            Assert.Equal(
                15d,
                Assert.IsType<double>(
                    application.FindResource("Metric.Font.Body")));
            Assert.Equal(15d, settingsButton.FontSize);
            Assert.Equal(48d, settingsButton.Width);
            Assert.Equal(31d, headerBadgeRays.ActualWidth);
            Assert.Equal(31d, headerBadgeRays.ActualHeight);
            AssertOpaqueWindowBackground(
                window.Background,
                darkBackground.GradientStops[0].Color);
            Assert.Equal(
                byte.MaxValue,
                source.CompositionTarget.BackgroundColor.A);
            Assert.Equal(650, window.MinWidth);
            Assert.Equal(MainPage.Home, viewModel.SelectedPage);
            Assert.Equal(Visibility.Visible, homeHeader.Visibility);
            Assert.Equal(Visibility.Collapsed, settingsHeader.Visibility);
            Assert.Equal(Visibility.Visible, homeScroller.Visibility);
            VerifyUpdateDialogs(window);
        }
        finally
        {
            if (window is not null)
            {
                window.AllowClose();
                window.Close();
            }

            application.Shutdown();
            runtime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void VerifyUpdateDialogs(Window owner)
    {
        var availableWindow = new UpdateAvailableWindow(
            new Version(1, 0, 2))
        {
            Owner = owner
        };
        availableWindow.Loaded += (_, _) =>
        {
            var updateButton = (Button)availableWindow.FindName(
                "UpdateNowButton");
            updateButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
        };
        availableWindow.ShowDialog();
        Assert.True(availableWindow.ShouldUpdate);

        using var cancellationSource = new CancellationTokenSource();
        var progressWindow = new UpdateProgressWindow(cancellationSource)
        {
            Owner = owner
        };
        try
        {
            progressWindow.Show();
            progressWindow.ReportProgress(
                new UpdateDownloadProgress(50, 100));
            progressWindow.UpdateLayout();
            var progressBar = (ProgressBar)progressWindow.FindName(
                "UpdateProgressBar");
            Assert.Equal(50, progressWindow.ProgressPercent);
            Assert.Equal(50, progressBar.Value);
            Assert.NotNull(progressWindow.FindName("ProgressStatusText"));
            progressWindow.ShowVerification();
            progressWindow.UpdateLayout();
            Assert.False(progressWindow.CanCancel);
            Assert.Equal(100, progressWindow.ProgressPercent);
            Assert.Equal(100, progressBar.Value);
        }
        finally
        {
            progressWindow.AllowClose();
            progressWindow.Close();
        }

        var failureWindow = new UpdateFailureWindow(
            "operation-1",
            new Version(1, 0, 2),
            "UpdateFailureNetwork",
            new Uri(
                "https://github.com/League2EB/CS2FocusGuard/releases/tag/v1.0.2"),
            @"C:\logs\update.log")
        {
            Owner = owner
        };
        failureWindow.Loaded += (_, _) =>
        {
            Assert.NotNull(failureWindow.FindName("OpenReleasePageButton"));
            var retryButton = (Button)failureWindow.FindName("RetryButton");
            retryButton.RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
        };
        failureWindow.ShowDialog();
        Assert.True(failureWindow.ShouldRetry);
    }

    private static LinearGradientBrush AssertOpaqueWindowBackground(
        System.Windows.Application application,
        Color firstColor) =>
        AssertOpaqueWindowBackground(
            Assert.IsAssignableFrom<Brush>(
                application.FindResource("WindowBackgroundBrush")),
            firstColor);

    private static LinearGradientBrush AssertOpaqueWindowBackground(
        Brush? brush,
        Color firstColor)
    {
        var background = Assert.IsType<LinearGradientBrush>(
            brush);
        Assert.Equal(firstColor, background.GradientStops[0].Color);
        Assert.All(
            background.GradientStops,
            stop => Assert.Equal(byte.MaxValue, stop.Color.A));
        return background;
    }

    private static ResourceDictionary LoadApplicationResources()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(
            new ResourceDictionary
            {
                Source = new Uri(
                    "/CS2FocusGuard;component/Themes/LightTheme.xaml",
                    UriKind.Relative)
            });
        resources.MergedDictionaries.Add(
            new ResourceDictionary
            {
                Source = new Uri(
                    "/CS2FocusGuard;component/Resources/InterfaceMetrics.Standard.xaml",
                    UriKind.Relative)
            });
        return resources;
    }

    private static T? FindVisualChild<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match)
            {
                return match;
            }

            var descendant = FindVisualChild<T>(child);
            if (descendant is not null)
            {
                return descendant;
            }
        }

        return null;
    }

    private sealed class NeverRunningProcessProbe : IGameProcessProbe
    {
        public bool IsRunning() => false;
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
