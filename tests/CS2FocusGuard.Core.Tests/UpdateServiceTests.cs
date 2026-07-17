using System.Net;
using System.Net.Http;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CS2FocusGuard.App;

namespace CS2FocusGuard.Core.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdateAsyncReturnsNewerStableReleaseWithExpectedAssets()
    {
        using var handler = new StubHttpMessageHandler(
            _ => JsonResponse(CreateReleaseDocument("1.0.6")));
        using var client = new HttpClient(handler);
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new UpdateService(client, new Version(1, 0, 5, 0), temporaryDirectory.Path);

        var update = await service.CheckForUpdateAsync();

        Assert.NotNull(update);
        Assert.Equal(new Version(1, 0, 6), update.Version);
        Assert.Equal("CS2FocusGuard-Setup-1.0.6-x64.exe", update.InstallerFileName);
        Assert.Equal(
            "https://github.com/League2EB/CS2FocusGuard/releases/download/v1.0.6/CS2FocusGuard-Setup-1.0.6-x64.exe",
            update.InstallerUri.AbsoluteUri);
        Assert.Equal(
            "CS2FocusGuard/1.0.5",
            handler.Requests.Single().Headers.UserAgent.ToString());
    }

    [Fact]
    public async Task CheckForUpdateAsyncIgnoresCurrentVersion()
    {
        using var handler = new StubHttpMessageHandler(
            _ => JsonResponse(CreateReleaseDocument("1.0.5")));
        using var client = new HttpClient(handler);
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new UpdateService(client, new Version(1, 0, 5), temporaryDirectory.Path);

        var update = await service.CheckForUpdateAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckForUpdateAsyncIgnoresReleaseWithoutChecksumAsset()
    {
        using var handler = new StubHttpMessageHandler(
            _ => JsonResponse(CreateReleaseDocument("1.0.6", includeChecksum: false)));
        using var client = new HttpClient(handler);
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new UpdateService(client, new Version(1, 0, 5), temporaryDirectory.Path);

        var update = await service.CheckForUpdateAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task CheckForUpdateAsyncRejectsUnexpectedDownloadLocation()
    {
        using var handler = new StubHttpMessageHandler(
            _ => JsonResponse(CreateReleaseDocument("1.0.6", host: "example.test")));
        using var client = new HttpClient(handler);
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new UpdateService(client, new Version(1, 0, 5), temporaryDirectory.Path);

        var update = await service.CheckForUpdateAsync();

        Assert.Null(update);
    }

    [Fact]
    public async Task DownloadInstallerAsyncValidatesChecksumAndStoresInstaller()
    {
        var payload = Encoding.UTF8.GetBytes("signed installer payload");
        var fileName = "CS2FocusGuard-Setup-1.0.6-x64.exe";
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var installerUri = new Uri(
            $"https://github.com/League2EB/CS2FocusGuard/releases/download/v1.0.6/{fileName}");
        var checksumUri = new Uri($"{installerUri}.sha256");
        using var handler = new StubHttpMessageHandler(
            request => request.RequestUri == checksumUri
                ? TextResponse($"{checksum}  {fileName}\n")
                : BinaryResponse(payload));
        using var client = new HttpClient(handler);
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new UpdateService(client, new Version(1, 0, 5), temporaryDirectory.Path);
        var update = new AvailableUpdate(
            new Version(1, 0, 6),
            fileName,
            installerUri,
            checksumUri);

        var installerPath = await service.DownloadInstallerAsync(update);

        Assert.Equal(Path.Combine(temporaryDirectory.Path, fileName), installerPath);
        Assert.Equal(payload, await File.ReadAllBytesAsync(installerPath));
        Assert.Empty(Directory.GetFiles(temporaryDirectory.Path, "*.part"));
    }

    [Fact]
    public async Task DownloadInstallerAsyncDeletesPartialFileWhenChecksumDoesNotMatch()
    {
        var payload = Encoding.UTF8.GetBytes("installer payload");
        var fileName = "CS2FocusGuard-Setup-1.0.6-x64.exe";
        var installerUri = new Uri(
            $"https://github.com/League2EB/CS2FocusGuard/releases/download/v1.0.6/{fileName}");
        var checksumUri = new Uri($"{installerUri}.sha256");
        using var handler = new StubHttpMessageHandler(
            request => request.RequestUri == checksumUri
                ? TextResponse($"{new string('0', 64)}  {fileName}\n")
                : BinaryResponse(payload));
        using var client = new HttpClient(handler);
        using var temporaryDirectory = new TemporaryDirectory();
        var service = new UpdateService(client, new Version(1, 0, 5), temporaryDirectory.Path);
        var update = new AvailableUpdate(
            new Version(1, 0, 6),
            fileName,
            installerUri,
            checksumUri);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.DownloadInstallerAsync(update));

        Assert.Empty(Directory.GetFiles(temporaryDirectory.Path));
    }

    [Fact]
    public void CreateStartInfoUsesSilentUpdateArguments()
    {
        var startInfo = UpdateInstallerLauncher.CreateStartInfo(@"C:\updates\setup.exe");

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.Equal(
            [
                "/VERYSILENT",
                "/SUPPRESSMSGBOXES",
                "/NORESTART",
                "/CLOSEAPPLICATIONS",
                "/RESTARTAPP"
            ],
            startInfo.ArgumentList);
    }

    private static string CreateReleaseDocument(
        string version,
        bool includeChecksum = true,
        string host = "github.com")
    {
        var installer = $"CS2FocusGuard-Setup-{version}-x64.exe";
        var installerUrl =
            $"https://{host}/League2EB/CS2FocusGuard/releases/download/v{version}/{installer}";
        var assets = includeChecksum
            ? new[]
            {
                new { name = installer, browser_download_url = installerUrl },
                new
                {
                    name = $"{installer}.sha256",
                    browser_download_url = $"{installerUrl}.sha256"
                }
            }
            : [new { name = installer, browser_download_url = installerUrl }];
        return JsonSerializer.Serialize(
            new
            {
                tag_name = $"v{version}",
                draft = false,
                prerelease = false,
                assets
            });
    }

    private static HttpResponseMessage BinaryResponse(byte[] content) =>
        new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(content)
        };

    private static HttpResponseMessage JsonResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage TextResponse(string content) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/plain")
        };

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        internal List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responseFactory(request));
        }
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
