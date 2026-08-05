using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SerialLog.Update;

namespace SerialLog.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_ReturnsAvailableForNewerCompleteRelease()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new StubHttpMessageHandler(CreateReleaseResponse("v0.3.0", includeAssets: true));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client);

        var result = await service.CheckForUpdatesAsync("v0.2.0", force: true);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal(new ReleaseVersion(0, 3, 0), result.Release?.Version);
        Assert.Equal("SerialLog-v0.3.0-win-x64-portable.zip", result.Release?.PackageAsset.Name);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_RejectsMissingAssetsAndRecordsFailure()
    {
        using var temp = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);
        using var handler = new StubHttpMessageHandler(CreateReleaseResponse("v0.3.0", includeAssets: false));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client, () => now);

        var result = await service.CheckForUpdatesAsync("v0.2.0", force: true);
        var state = new UpdateStateStore(Path.Combine(temp.Path, "update-state.json")).Load();

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Equal(now, state.LastFailedCheckUtc);
        Assert.Null(state.LastSuccessfulCheckUtc);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsSecondAutomaticCheckWithinTwentyFourHours()
    {
        using var temp = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);
        using var handler = new StubHttpMessageHandler(CreateReleaseResponse("v0.2.0", includeAssets: false));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client, () => now);

        var first = await service.CheckForUpdatesAsync("v0.2.0", force: false);
        now = now.AddHours(23);
        var second = await service.CheckForUpdatesAsync("v0.2.0", force: false);

        Assert.Equal(UpdateCheckStatus.NoUpdate, first.Status);
        Assert.Equal(UpdateCheckStatus.Skipped, second.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ManualCheckBypassesThrottle()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new StubHttpMessageHandler(CreateReleaseResponse("v0.2.0", includeAssets: false));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client);

        await service.CheckForUpdatesAsync("v0.2.0", force: false);
        await service.CheckForUpdatesAsync("v0.2.0", force: true);

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_DoesNotOfferOlderRelease()
    {
        using var temp = new TemporaryDirectory();
        using var handler = new StubHttpMessageHandler(CreateReleaseResponse("v0.1.9", includeAssets: true));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client);

        var result = await service.CheckForUpdatesAsync("v0.2.0", force: true);

        Assert.Equal(UpdateCheckStatus.NoUpdate, result.Status);
        Assert.Null(result.Release);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SkipsAutomaticRetryForTwoHoursAfterFailure()
    {
        using var temp = new TemporaryDirectory();
        var now = new DateTimeOffset(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);
        using var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        });
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client, () => now);

        var first = await service.CheckForUpdatesAsync("v0.2.0", force: false);
        now = now.AddMinutes(119);
        var second = await service.CheckForUpdatesAsync("v0.2.0", force: false);

        Assert.Equal(UpdateCheckStatus.Failed, first.Status);
        Assert.Equal(UpdateCheckStatus.Skipped, second.Status);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public void CanInstallInPlace_RequiresBundledUpdater()
    {
        using var temp = new TemporaryDirectory();
        var install = Path.Combine(temp.Path, "SerialLog");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, UpdatePaths.ApplicationFileName), "app");
        using var service = new UpdateService(Path.Combine(temp.Path, "updates"), install);

        var canInstall = service.CanInstallInPlace(install, out var reason);

        Assert.False(canInstall);
        Assert.Contains("更新助手", reason);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_ReportsHttpAndJsonFailures()
    {
        using var temp = new TemporaryDirectory();
        using var httpHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}")
        });
        using var httpClient = new HttpClient(httpHandler);
        using var httpService = new UpdateService(
            Path.Combine(temp.Path, "http"),
            temp.Path,
            httpClient);

        var httpResult = await httpService.CheckForUpdatesAsync("v0.2.0", force: true);

        using var jsonHandler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json")
        });
        using var jsonClient = new HttpClient(jsonHandler);
        using var jsonService = new UpdateService(
            Path.Combine(temp.Path, "json"),
            temp.Path,
            jsonClient);
        var jsonResult = await jsonService.CheckForUpdatesAsync("v0.2.0", force: true);

        Assert.Equal(UpdateCheckStatus.Failed, httpResult.Status);
        Assert.Equal(UpdateCheckStatus.Failed, jsonResult.Status);
    }

    [Fact]
    public async Task DownloadAndPrepareAsync_VerifiesAssetsAndCreatesPendingJob()
    {
        using var temp = new TemporaryDirectory();
        var install = Path.Combine(temp.Path, "SerialLog");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, UpdatePaths.ApplicationFileName), "old");
        File.WriteAllText(Path.Combine(install, UpdatePaths.UpdaterFileName), "updater-runtime");
        var package = CreatePortablePackage();
        var packageHash = Convert.ToHexString(SHA256.HashData(package));
        var checksum = Encoding.UTF8.GetBytes($"{packageHash}  package.zip");
        using var handler = new RoutingHttpMessageHandler(package, checksum);
        using var client = new HttpClient(handler);
        var updateRoot = Path.Combine(temp.Path, "updates");
        using var service = new UpdateService(updateRoot, install, client);
        var release = CreateDownloadRelease(package, checksum, packageHash);

        var prepared = await service.DownloadAndPrepareAsync(release, install, 123);
        var job = UpdateJobStore.Load(prepared.JobFilePath);
        var state = new UpdateStateStore(Path.Combine(updateRoot, "update-state.json")).Load();

        Assert.True(File.Exists(prepared.UpdaterExecutablePath));
        Assert.True(File.Exists(Path.Combine(job.StagingDirectory, UpdatePaths.ApplicationFileName)));
        Assert.Equal("0.3.0", state.PendingUpdate?.TargetVersion);
        Assert.Equal(prepared.JobFilePath, state.PendingUpdate?.JobFilePath);
    }

    [Fact]
    public async Task DownloadAndPrepareAsync_DeletesPackageWhenShaFileDoesNotMatch()
    {
        using var temp = new TemporaryDirectory();
        var install = Path.Combine(temp.Path, "SerialLog");
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, UpdatePaths.ApplicationFileName), "old");
        File.WriteAllText(Path.Combine(install, UpdatePaths.UpdaterFileName), "updater-runtime");
        var package = CreatePortablePackage();
        var packageHash = Convert.ToHexString(SHA256.HashData(package));
        var checksum = Encoding.UTF8.GetBytes($"{new string('0', 64)}  package.zip");
        using var handler = new RoutingHttpMessageHandler(package, checksum);
        using var client = new HttpClient(handler);
        var updateRoot = Path.Combine(temp.Path, "updates");
        using var service = new UpdateService(updateRoot, install, client);
        var release = CreateDownloadRelease(package, checksum, packageHash);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.DownloadAndPrepareAsync(release, install, 123));

        var downloadedPackage = Path.Combine(
            updateRoot,
            "downloads",
            "0.3.0",
            release.PackageAsset.Name);
        Assert.False(File.Exists(downloadedPackage));
    }

    [Theory]
    [InlineData("v0.3.0-beta")]
    [InlineData("0.3.0")]
    public async Task CheckForUpdatesAsync_RejectsInvalidReleaseTag(string tag)
    {
        using var temp = new TemporaryDirectory();
        using var handler = new StubHttpMessageHandler(CreateReleaseResponse(tag, includeAssets: true));
        using var client = new HttpClient(handler);
        using var service = new UpdateService(temp.Path, temp.Path, client);

        var result = await service.CheckForUpdatesAsync("v0.2.0", force: true);

        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    private static HttpResponseMessage CreateReleaseResponse(string tag, bool includeAssets)
    {
        var version = tag.TrimStart('v');
        var packageName = $"SerialLog-v{version}-win-x64-portable.zip";
        var assets = includeAssets
            ? new object[]
            {
                new
                {
                    name = packageName,
                    browser_download_url = $"https://example.test/{packageName}",
                    size = 1234,
                    digest = new string('A', 64).Insert(0, "sha256:")
                },
                new
                {
                    name = $"{packageName}.sha256.txt",
                    browser_download_url = $"https://example.test/{packageName}.sha256.txt",
                    size = 80,
                    digest = new string('B', 64).Insert(0, "sha256:")
                }
            }
            : [];
        var json = JsonSerializer.Serialize(new
        {
            tag_name = tag,
            body = "更新说明",
            html_url = "https://example.test/release",
            published_at = "2026-08-04T10:00:00Z",
            draft = false,
            prerelease = false,
            assets
        });
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static UpdateReleaseInfo CreateDownloadRelease(
        byte[] package,
        byte[] checksum,
        string packageHash)
    {
        var checksumHash = Convert.ToHexString(SHA256.HashData(checksum));
        return new UpdateReleaseInfo(
            new ReleaseVersion(0, 3, 0),
            "v0.3.0",
            "更新说明",
            DateTimeOffset.UtcNow,
            new Uri("https://example.test/release"),
            new UpdateAsset(
                "SerialLog-v0.3.0-win-x64-portable.zip",
                new Uri("https://example.test/package"),
                package.LongLength,
                $"sha256:{packageHash}"),
            new UpdateAsset(
                "SerialLog-v0.3.0-win-x64-portable.zip.sha256.txt",
                new Uri("https://example.test/checksum"),
                checksum.LongLength,
                $"sha256:{checksumHash}"));
    }

    private static byte[] CreatePortablePackage()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteZipEntry(archive, UpdatePaths.ApplicationFileName, "new-app");
            WriteZipEntry(archive, UpdatePaths.UpdaterFileName, "new-updater");
        }

        return buffer.ToArray();
    }

    private static void WriteZipEntry(ZipArchive archive, string name, string content)
    {
        var entry = archive.CreateEntry(name);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StubHttpMessageHandler(HttpResponseMessage response)
        {
            _statusCode = response.StatusCode;
            _body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            response.Dispose();
        }

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly byte[] _package;
        private readonly byte[] _checksum;

        public RoutingHttpMessageHandler(byte[] package, byte[] checksum)
        {
            _package = package;
            _checksum = checksum;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var bytes = request.RequestUri?.AbsolutePath.EndsWith("checksum", StringComparison.Ordinal) == true
                ? _checksum
                : _package;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        }
    }
}
