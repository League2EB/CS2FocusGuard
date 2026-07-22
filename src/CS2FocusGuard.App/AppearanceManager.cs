using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CS2FocusGuard.App;

internal static class AppearanceManager
{
    private const string ThemePathPrefix = "Themes/";
    private const string InterfaceMetricsPathPrefix =
        "Resources/InterfaceMetrics.";
    private const int ImmersiveDarkMode = 20;
    private const int LegacyImmersiveDarkMode = 19;
    private const int SystemBackdropType = 38;
    private const int NoSystemBackdrop = 1;
    private const int MainWindowBackdrop = 2;
    private static readonly System.Windows.Media.Color LightWindowColor =
        System.Windows.Media.Color.FromRgb(0xF4, 0xF6, 0xFA);
    private static readonly System.Windows.Media.Color DarkWindowColor =
        System.Windows.Media.Color.FromRgb(0x10, 0x14, 0x1D);
    private static bool _useDarkTheme;

    internal static void Apply(AppSettings settings)
    {
        ApplyTheme(settings.UseDarkTheme);
        ApplyInterfaceMetrics(settings.UseLargeInterface);
    }

    internal static void ApplyTheme(bool useDarkTheme)
    {
        _useDarkTheme = useDarkTheme;
        var application = System.Windows.Application.Current;
        if (!CanAccess(application))
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var theme = new ResourceDictionary
        {
            Source = new Uri(
                useDarkTheme
                    ? "/CS2FocusGuard;component/Themes/DarkTheme.xaml"
                    : "/CS2FocusGuard;component/Themes/LightTheme.xaml",
                UriKind.Relative)
        };
        var currentTheme = dictionaries.FirstOrDefault(
            dictionary =>
                dictionary.Source?.OriginalString.Contains(
                    ThemePathPrefix,
                    StringComparison.OrdinalIgnoreCase) is true);

        if (currentTheme is null)
        {
            dictionaries.Insert(0, theme);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(currentTheme)] = theme;
        }

        foreach (Window window in application.Windows)
        {
            ApplyWindowTheme(window);
        }
    }

    internal static void ApplyInterfaceMetrics(bool useLargeInterface)
    {
        var application = System.Windows.Application.Current;
        if (!CanAccess(application))
        {
            return;
        }

        var dictionaries = application.Resources.MergedDictionaries;
        var metrics = new ResourceDictionary
        {
            Source = new Uri(
                useLargeInterface
                    ? "/CS2FocusGuard;component/Resources/InterfaceMetrics.Large.xaml"
                    : "/CS2FocusGuard;component/Resources/InterfaceMetrics.Standard.xaml",
                UriKind.Relative)
        };
        var currentMetrics = dictionaries.FirstOrDefault(
            dictionary =>
                dictionary.Source?.OriginalString.Contains(
                    InterfaceMetricsPathPrefix,
                    StringComparison.OrdinalIgnoreCase) is true);

        if (currentMetrics is null)
        {
            dictionaries.Add(metrics);
        }
        else
        {
            dictionaries[dictionaries.IndexOf(currentMetrics)] = metrics;
        }
    }

    internal static void RegisterWindow(Window window) =>
        window.SourceInitialized += OnWindowSourceInitialized;

    private static bool CanAccess(
        [NotNullWhen(true)] System.Windows.Application? application) =>
        application is not null &&
        !application.Dispatcher.HasShutdownStarted &&
        application.Dispatcher.CheckAccess();

    private static void OnWindowSourceInitialized(object? sender, EventArgs e)
    {
        if (sender is Window window)
        {
            ApplyWindowTheme(window);
        }
    }

    private static void ApplyWindowTheme(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = _useDarkTheme ? 1 : 0;
        var micaApplied = false;
        try
        {
            if (DwmSetWindowAttribute(
                    handle,
                    ImmersiveDarkMode,
                    ref enabled,
                    sizeof(int)) != 0)
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    LegacyImmersiveDarkMode,
                    ref enabled,
                    sizeof(int));
            }

            if (IsMicaSupported)
            {
                var backdrop = MainWindowBackdrop;
                micaApplied = DwmSetWindowAttribute(
                    handle,
                    SystemBackdropType,
                    ref backdrop,
                    sizeof(int)) == 0;
            }
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        if (!micaApplied && IsMicaSupported)
        {
            var backdrop = NoSystemBackdrop;
            try
            {
                _ = DwmSetWindowAttribute(
                    handle,
                    SystemBackdropType,
                    ref backdrop,
                    sizeof(int));
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        ApplyWindowBackground(window);
    }

    internal static bool IsMicaSupported =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22523) &&
        !SystemParameters.IsRemoteSession;

    private static void ApplyWindowBackground(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource source &&
            source.CompositionTarget is not null)
        {
            source.CompositionTarget.BackgroundColor = _useDarkTheme
                ? DarkWindowColor
                : LightWindowColor;
        }

        window.SetResourceReference(
            Window.BackgroundProperty,
            "WindowBackgroundBrush");
    }

    [SuppressMessage(
        "Interoperability",
        "SYSLIB1054:Use 'LibraryImportAttribute' instead of 'DllImportAttribute'",
        Justification = "This fixed-size Windows API call does not require generated marshalling.")]
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
