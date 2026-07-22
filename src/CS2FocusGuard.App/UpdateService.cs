using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace CS2FocusGuard.App;

internal sealed record AvailableUpdate(
    Version Version,
    string InstallerFileName,
    Uri InstallerUri,
    Uri ChecksumUri);

internal sealed record UpdateDownloadProgress(
    long BytesReceived,
    long? TotalBytes,
    bool IsVerifying = false);

internal static class UpdatePromptTestMode
{
    internal const string Argument = "--test-update-prompt";

    internal static bool IsRequested(IEnumerable<string> arguments)
    {
#if DEBUG
        return arguments.Contains(Argument, StringComparer.OrdinalIgnoreCase);
#else
        return false;
#endif
    }

    internal static AvailableUpdate CreateSyntheticUpdate(Version currentVersion)
    {
        ArgumentNullException.ThrowIfNull(currentVersion);
        var build = currentVersion.Build < 0 ? 1 : checked(currentVersion.Build + 1);
        var version = new Version(
            currentVersion.Major,
            currentVersion.Minor,
            build);
        var fileName = $"CS2FocusGuard-Setup-{version:3}-x64.exe";
        var assetUri = new Uri(
            $"https://update-test.invalid/{fileName}",
            UriKind.Absolute);
        return new AvailableUpdate(
            version,
            fileName,
            assetUri,
            new Uri($"{assetUri}.sha256", UriKind.Absolute));
    }

    internal static bool ShouldInstall(
        bool userAccepted,
        bool isTestMode) =>
        userAccepted && !isTestMode;
}

internal static class UpdateInstallerLauncher
{
    internal const string SilentInstallArgument = "/VERYSILENT";
    internal const string SuppressMessagesArgument = "/SUPPRESSMSGBOXES";
    internal const string NoRestartArgument = "/NORESTART";
    internal const string CloseApplicationsArgument = "/CLOSEAPPLICATIONS";
    internal const string RestartApplicationArgument = "/RESTARTAPP";

    internal static void Launch(string installerPath)
    {
        if (!File.Exists(installerPath))
        {
            throw new FileNotFoundException("The downloaded update installer was not found.", installerPath);
        }

        var startInfo = CreateStartInfo(installerPath);
        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("The update installer could not be started.");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string installerPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = installerPath,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(SilentInstallArgument);
        startInfo.ArgumentList.Add(SuppressMessagesArgument);
        startInfo.ArgumentList.Add(NoRestartArgument);
        startInfo.ArgumentList.Add(CloseApplicationsArgument);
        startInfo.ArgumentList.Add(RestartApplicationArgument);
        return startInfo;
    }
}

internal sealed class UpdateService
{
    private const string RepositoryOwner = "League2EB";
    private const string RepositoryName = "CS2FocusGuard";
    private const string InstallerPrefix = "CS2FocusGuard-Setup-";
    private const string InstallerSuffix = "-x64.exe";
    private const long MaximumInstallerSize = 512L * 1024 * 1024;
    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{RepositoryOwner}/{RepositoryName}/releases/latest");

    private readonly HttpClient _httpClient;
    private readonly Version _currentVersion;
    private readonly string _updatesDirectory;

    internal UpdateService(
        HttpClient httpClient,
        Version currentVersion,
        string updatesDirectory)
    {
        _httpClient = httpClient;
        _currentVersion = NormalizeVersion(currentVersion);
        _updatesDirectory = updatesDirectory;
    }

    internal async Task<AvailableUpdate?> CheckForUpdateAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("CS2FocusGuard", _currentVersion.ToString(3)));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var release = document.RootElement;

        if (IsTrue(release, "draft") || IsTrue(release, "prerelease") ||
            !TryGetReleaseVersion(release, out var releaseVersion) ||
            releaseVersion.CompareTo(_currentVersion) <= 0)
        {
            return null;
        }

        var versionText = releaseVersion.ToString(3);
        var installerFileName = $"{InstallerPrefix}{versionText}{InstallerSuffix}";
        var checksumFileName = $"{installerFileName}.sha256";
        var installerUri = CreateReleaseAssetUri(versionText, installerFileName);
        var checksumUri = CreateReleaseAssetUri(versionText, checksumFileName);
        if (!TryGetAssetUri(release, installerFileName, installerUri) ||
            !TryGetAssetUri(release, checksumFileName, checksumUri))
        {
            return null;
        }

        return new AvailableUpdate(
            releaseVersion,
            installerFileName,
            installerUri,
            checksumUri);
    }

    internal async Task<string> DownloadInstallerAsync(
        AvailableUpdate update,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        Directory.CreateDirectory(_updatesDirectory);

        var installerPath = Path.Combine(_updatesDirectory, update.InstallerFileName);
        var temporaryPath = $"{installerPath}.{Guid.NewGuid():N}.part";
        try
        {
            var expectedHash = await DownloadChecksumAsync(update, cancellationToken);

            using var request = new HttpRequestMessage(HttpMethod.Get, update.InstallerUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength;
            if (totalBytes is > MaximumInstallerSize)
            {
                throw new InvalidDataException("The update installer exceeds the maximum size.");
            }

            byte[] actualHash;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(81920);
                long bytesReceived = 0;
                try
                {
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
                        if (count == 0)
                        {
                            break;
                        }

                        bytesReceived += count;
                        if (bytesReceived > MaximumInstallerSize)
                        {
                            throw new InvalidDataException("The update installer exceeds the maximum size.");
                        }

                        hash.AppendData(buffer, 0, count);
                        await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                        progress?.Report(new UpdateDownloadProgress(bytesReceived, totalBytes));
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                progress?.Report(new UpdateDownloadProgress(bytesReceived, totalBytes, IsVerifying: true));
                actualHash = hash.GetHashAndReset();
                await output.FlushAsync(cancellationToken);
            }

            if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            {
                throw new InvalidDataException("The update installer checksum did not match.");
            }

            File.Move(temporaryPath, installerPath, overwrite: true);
            return installerPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private async Task<byte[]> DownloadChecksumAsync(
        AvailableUpdate update,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, update.ChecksumUri);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 4096)
        {
            throw new InvalidDataException("The update checksum file is too large.");
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        var fields = content.Trim().Split(
            [' ', '\t'],
            StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2 ||
            !string.Equals(fields[1].TrimStart('*'), update.InstallerFileName, StringComparison.Ordinal) ||
            fields[0].Length != 64 ||
            !IsHexadecimal(fields[0]))
        {
            throw new InvalidDataException("The update checksum file is invalid.");
        }

        return Convert.FromHexString(fields[0]);
    }

    private static bool IsTrue(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.True;

    private static bool TryGetReleaseVersion(JsonElement release, out Version version)
    {
        version = default!;
        if (!release.TryGetProperty("tag_name", out var tagProperty) ||
            tagProperty.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var tag = tagProperty.GetString();
        if (string.IsNullOrWhiteSpace(tag) || !tag.StartsWith('v') ||
            !Version.TryParse(tag[1..], out var parsedVersion))
        {
            return false;
        }

        try
        {
            version = NormalizeVersion(parsedVersion);
            return string.Equals(tag, $"v{version:3}", StringComparison.Ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static Uri CreateReleaseAssetUri(string version, string fileName) =>
        new(
            $"https://github.com/{RepositoryOwner}/{RepositoryName}/releases/download/v{version}/{fileName}",
            UriKind.Absolute);

    private static bool TryGetAssetUri(
        JsonElement release,
        string expectedName,
        Uri expectedUri)
    {
        if (!release.TryGetProperty("assets", out var assets) ||
            assets.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameProperty) ||
                nameProperty.ValueKind is not JsonValueKind.String ||
                !string.Equals(nameProperty.GetString(), expectedName, StringComparison.Ordinal) ||
                !asset.TryGetProperty("browser_download_url", out var urlProperty) ||
                urlProperty.ValueKind is not JsonValueKind.String ||
                !Uri.TryCreate(urlProperty.GetString(), UriKind.Absolute, out var parsedUri) ||
                parsedUri.Scheme != Uri.UriSchemeHttps ||
                Uri.Compare(
                    parsedUri,
                    expectedUri,
                    UriComponents.AbsoluteUri,
                    UriFormat.SafeUnescaped,
                    StringComparison.Ordinal) != 0)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static Version NormalizeVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (version.Build < 0 || version.Revision > 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(version),
                "Update versions must contain exactly three numeric components.");
        }

        return new Version(version.Major, version.Minor, version.Build);
    }

    private static bool IsHexadecimal(string value)
    {
        foreach (var character in value)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
