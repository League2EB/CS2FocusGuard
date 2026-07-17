using System.Diagnostics;
using System.IO;
using System.Security;
using CS2FocusGuard.Core;
using Microsoft.Win32;

namespace CS2FocusGuard.App;

internal sealed record ApplicationDescriptor(
    string Id,
    string DisplayName,
    string? IconPath);

internal sealed class ApplicationCatalog(IAudioSessionController audioController)
{
    private const string UninstallPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly Dictionary<string, string> KnownNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discord"] = "Discord",
            ["oopz"] = "Oopz",
            ["kook"] = "KOOK",
            [AudioApplicationIdentity.SystemSounds] = "Windows system sounds"
        };

    internal async Task<IReadOnlyList<ApplicationDescriptor>> GetApplicationsAsync(
        IEnumerable<string> allowlist,
        CancellationToken cancellationToken = default)
    {
        var installedTask = Task.Run(GetInstalledApplications, cancellationToken);
        var sessions = await audioController.GetSessionsAsync(cancellationToken);
        var installed = await installedTask;
        var applications = installed.ToDictionary(
            application => application.Id,
            StringComparer.OrdinalIgnoreCase);

        foreach (var session in sessions)
        {
            var id = AudioApplicationIdentity.Normalize(session.ApplicationId);
            if (id.Length == 0 || id.StartsWith("unknown:", StringComparison.Ordinal))
            {
                continue;
            }

            applications[id] = new ApplicationDescriptor(
                id,
                FriendlyName(id, session.DisplayName),
                FindRunningExecutable(id));
        }

        foreach (var id in AudioAllowlistSettings.Normalize(allowlist))
        {
            applications.TryAdd(
                id,
                new ApplicationDescriptor(id, FriendlyName(id, id), null));
        }

        foreach (var id in AudioAllowlistSettings.GetDefaults())
        {
            applications.TryAdd(
                id,
                new ApplicationDescriptor(id, FriendlyName(id, id), null));
        }

        return applications.Values
            .Where(application => application.Id != "cs2")
            .OrderBy(application => application.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static List<ApplicationDescriptor> GetInstalledApplications()
    {
        var applications = new Dictionary<string, ApplicationDescriptor>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                ReadInstalledApplications(hive, view, applications);
            }
        }

        return applications.Values.ToList();
    }

    private static void ReadInstalledApplications(
        RegistryHive hive,
        RegistryView view,
        Dictionary<string, ApplicationDescriptor> applications)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath);
            if (uninstall is null)
            {
                return;
            }

            foreach (var subkeyName in uninstall.GetSubKeyNames())
            {
                using var applicationKey = uninstall.OpenSubKey(subkeyName);
                if (applicationKey is null)
                {
                    continue;
                }

                var displayName = applicationKey.GetValue("DisplayName") as string;
                if (string.IsNullOrWhiteSpace(displayName) ||
                    applicationKey.GetValue("SystemComponent") is int systemComponent &&
                    systemComponent == 1)
                {
                    continue;
                }

                var iconPath = ParseIconPath(
                    applicationKey.GetValue("DisplayIcon") as string);
                var id = GetApplicationId(displayName, iconPath);
                if (id.Length == 0)
                {
                    continue;
                }

                applications[id] = new ApplicationDescriptor(
                    id,
                    displayName.Trim(),
                    iconPath);
            }
        }
        catch (IOException)
        {
        }
        catch (SecurityException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string GetApplicationId(string displayName, string? iconPath)
    {
        foreach (var known in KnownNames)
        {
            if (displayName.Contains(known.Value, StringComparison.OrdinalIgnoreCase))
            {
                return known.Key;
            }
        }

        if (!string.IsNullOrWhiteSpace(iconPath) &&
            Path.GetExtension(iconPath).Equals(
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileNameWithoutExtension(iconPath);
            if (!string.IsNullOrWhiteSpace(fileName) &&
                !fileName.Contains("unins", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Equals("setup", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Equals("update", StringComparison.OrdinalIgnoreCase))
            {
                return AudioApplicationIdentity.Normalize(fileName);
            }
        }

        return string.Empty;
    }

    private static string? ParseIconPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        string path;
        if (expanded.StartsWith('"'))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            path = closingQuote > 1
                ? expanded[1..closingQuote]
                : expanded.Trim('"');
        }
        else
        {
            var executableEnd = expanded.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            path = executableEnd >= 0
                ? expanded[..(executableEnd + 4)]
                : expanded.Split(',')[0];
        }

        path = path.Trim();
        return File.Exists(path) ? path : null;
    }

    private static string FriendlyName(string id, string candidate)
    {
        if (KnownNames.TryGetValue(id, out var knownName))
        {
            return knownName;
        }

        return string.IsNullOrWhiteSpace(candidate) ? id : candidate;
    }

    private static string? FindRunningExecutable(string id)
    {
        if (id == AudioApplicationIdentity.SystemSounds)
        {
            return null;
        }

        var processes = Process.GetProcessesByName(id);
        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
                catch (System.ComponentModel.Win32Exception)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }

        return null;
    }
}
