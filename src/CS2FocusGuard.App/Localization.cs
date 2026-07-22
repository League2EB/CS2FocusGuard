using System.Globalization;
using CS2FocusGuard.Core;

namespace CS2FocusGuard.App;

internal static class Strings
{
    private static bool _useTraditionalChinese =
        IsTraditionalChinese(CultureInfo.CurrentUICulture);

    private static readonly IReadOnlyDictionary<string, (string English, string Chinese)> Values =
        new Dictionary<string, (string, string)>
        {
            ["AppTitle"] = ("CS2 Focus Guard", "CS2 專注守衛"),
            ["Subtitle"] = (
                "Block notifications and unapproved app audio while Counter-Strike 2 is running.",
                "Counter-Strike 2 執行時阻擋通知與未允許的應用程式聲音。"),
            ["SettingsTab"] = ("Settings", "設定"),
            ["Back"] = ("Back", "返回"),
            ["Enabled"] = ("Enable", "啟用"),
            ["EnabledDescription"] = (
                "Monitor cs2.exe and automatically control notifications and app audio.",
                "監控 cs2.exe 並自動控制通知與應用程式聲音。"),
            ["StartWithWindows"] = ("Start with Windows", "隨 Windows 啟動"),
            ["StartDescription"] = (
                "Launch quietly in the system tray after sign-in.",
                "登入後在系統匣中安靜啟動。"),
            ["CloseToTray"] = ("Close to system tray", "關閉時縮到系統匣"),
            ["CloseDescription"] = (
                "Keep monitoring when the window is closed.",
                "關閉視窗後繼續在背景監控。"),
            ["General"] = ("General", "一般"),
            ["GeneralDescription"] = (
                "Control startup and background behavior.",
                "控制啟動與背景執行方式。"),
            ["Display"] = ("Display", "顯示"),
            ["DisplayDescription"] = (
                "Adjust the interface size and color theme.",
                "調整介面大小與色彩主題。"),
            ["InterfaceSize"] = ("Interface size", "介面大小"),
            ["InterfaceSizeDescription"] = (
                "Enlarge text, controls, icons, and spacing.",
                "放大文字、控制項、圖示與間距。"),
            ["StandardSize"] = ("Standard", "標準"),
            ["LargeSize"] = ("Large", "放大"),
            ["Theme"] = ("Theme", "主題"),
            ["ThemeDescription"] = (
                "Choose a light or dark appearance.",
                "選擇白天或黑夜外觀。"),
            ["LightTheme"] = ("Light", "白天"),
            ["DarkTheme"] = ("Dark", "黑夜"),
            ["Language"] = ("Language", "語言"),
            ["LanguageDescription"] = (
                "Choose the interface language.",
                "選擇介面顯示語言。"),
            ["About"] = ("About", "關於"),
            ["AboutDescription"] = (
                "Application and build information.",
                "應用程式與組建資訊。"),
            ["Version"] = ("Version", "版本"),
            ["DebugBuild"] = ("Debug", "偵錯"),
            ["Status"] = ("Status", "狀態"),
            ["Disabled"] = ("Disabled", "已停用"),
            ["Waiting"] = ("Waiting for Counter-Strike 2", "正在等待 Counter-Strike 2"),
            ["Suppressed"] = (
                "Notifications and unapproved app audio blocked",
                "已阻擋通知與未允許的應用程式聲音"),
            ["UserOverride"] = (
                "Windows notification mode was changed manually",
                "Windows 通知模式已由使用者手動變更"),
            ["Error"] = ("Protection unavailable", "防護功能無法使用"),
            ["AudioAllowlist"] = ("Audio allowlist", "音訊白名單"),
            ["AudioAllowlistDescription"] = (
                "Only allowed apps can play audio while Counter-Strike 2 is running.",
                "Counter-Strike 2 執行時，只有允許的應用程式可以發出聲音。"),
            ["Cs2AlwaysAllowed"] = (
                "Counter-Strike 2 is always allowed.",
                "Counter-Strike 2 永遠保持允許。"),
            ["SearchApplications"] = ("Search applications", "搜尋應用程式"),
            ["Refresh"] = ("Refresh", "重新整理"),
            ["LoadingApplications"] = ("Loading applications…", "正在載入應用程式…"),
            ["NoApplications"] = (
                "No matching applications.",
                "找不到符合的應用程式。"),
            ["Open"] = ("Open", "開啟主畫面"),
            ["Exit"] = ("Exit", "結束"),
            ["LanguageToggle"] = ("Switch language", "切換語言"),
            ["AlreadyRunning"] = (
                "CS2 Focus Guard is already running.",
                "CS2 專注守衛已經在執行。"),
            ["UpdateAvailableTitle"] = (
                "Update available",
                "有可用更新"),
            ["UpdateAvailableMessage"] = (
                "Version {0} is available. Download and install it now?",
                "已有 {0} 版本可用。現在下載並安裝嗎？"),
            ["UpdateAvailableMenu"] = (
                "Update to version {0}",
                "更新至 {0} 版本"),
            ["UpdateNow"] = ("Update now", "立即更新"),
            ["UpdateLater"] = ("Later", "稍後"),
            ["UpdateDownloadingTitle"] = (
                "Downloading update",
                "正在下載更新"),
            ["UpdatePreparing"] = (
                "Preparing the update…",
                "正在準備更新…"),
            ["UpdateDownloadingProgress"] = (
                "Downloaded {0} of {1}",
                "已下載 {0} / {1}"),
            ["UpdateDownloadingUnknownProgress"] = (
                "Downloaded {0}",
                "已下載 {0}"),
            ["UpdateVerifying"] = (
                "Verifying the downloaded update…",
                "正在驗證下載的更新…"),
            ["UpdateInstalling"] = (
                "Installing the update. The app will restart shortly.",
                "正在安裝更新，程式即將自動重新啟動。"),
            ["UpdateFailed"] = (
                "The update could not be completed. Please try again later.",
                "無法完成更新，請稍後再試。"),
            ["UpdateFailedTitle"] = (
                "Update failed",
                "更新失敗"),
            ["UpdateFailedMessage"] = (
                "The update could not be completed: {0}",
                "無法完成更新：{0}"),
            ["UpdateFallbackMessage"] = (
                "Try again or manually download version {0} from GitHub Releases.",
                "請重試，或從 GitHub Releases 手動下載 {0} 版本。"),
            ["UpdateFailureNetwork"] = (
                "The update files could not be downloaded.",
                "無法下載更新檔案。"),
            ["UpdateFailureValidation"] = (
                "The downloaded update did not pass security validation.",
                "下載的更新未通過安全驗證。"),
            ["UpdateFailureStorage"] = (
                "The update files could not be saved on this device.",
                "無法將更新檔案儲存至此裝置。"),
            ["UpdateFailureInstaller"] = (
                "The update installer could not be started.",
                "無法啟動更新安裝程式。"),
            ["UpdateFailureBrowser"] = (
                "The download page could not be opened.",
                "無法開啟下載頁。"),
            ["UpdateFailureUnexpected"] = (
                "An unexpected error occurred.",
                "發生未預期的錯誤。"),
            ["UpdateLogLocation"] = (
                "Diagnostic log: {0}",
                "診斷紀錄：{0}"),
            ["Retry"] = ("Try again", "重試"),
            ["OpenReleasePage"] = (
                "Open download page",
                "前往下載頁"),
            ["Close"] = ("Close", "關閉"),
            ["Cancel"] = ("Cancel", "取消")
        };

    internal static event EventHandler? LanguageChanged;

    internal static bool UseTraditionalChinese => _useTraditionalChinese;

    internal static void SetUseTraditionalChinese(bool useTraditionalChinese)
    {
        if (_useTraditionalChinese == useTraditionalChinese)
        {
            return;
        }

        _useTraditionalChinese = useTraditionalChinese;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    internal static string Get(string key)
    {
        var (english, chinese) = Values[key];
        return _useTraditionalChinese ? chinese : english;
    }

    internal static string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), arguments);

    internal static string Status(GuardStatus status) =>
        status.State switch
        {
            GuardState.Disabled => Get("Disabled"),
            GuardState.Waiting => Get("Waiting"),
            GuardState.Suppressed => Get("Suppressed"),
            GuardState.UserOverride => Get("UserOverride"),
            GuardState.Error when !string.IsNullOrWhiteSpace(status.Detail) =>
                $"{Get("Error")}: {status.Detail}",
            GuardState.Error => Get("Error"),
            _ => Get("Error")
        };

    private static bool IsTraditionalChinese(CultureInfo culture) =>
        culture.Name.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
        culture.Name.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
        culture.Name.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase);
}
