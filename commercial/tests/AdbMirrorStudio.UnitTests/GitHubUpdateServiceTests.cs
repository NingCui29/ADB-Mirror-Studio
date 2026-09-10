using System.Net;
using System.Security.Cryptography;
using System.Text;
using AdbMirrorStudio.Application.Updates;
using AdbMirrorStudio.Infrastructure.Updates;

namespace AdbMirrorStudio.UnitTests;

public sealed class GitHubUpdateServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsVerifiableInstallerWhenNewerVersionExists()
    {
        using var client = ClientFor("V1.2.0");
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");

        var update = await service.CheckAsync();

        Assert.True(update.IsUpdateAvailable);
        Assert.Equal("V1.2.0", update.LatestVersion);
        Assert.NotNull(update.Installer);
        Assert.Equal("ADB-Mirror-Studio-Setup-V1.2.0-win-x64.exe", update.Installer.FileName);
        Assert.Equal(123456, update.Installer.Size);
        Assert.Equal(new string('A', 64), update.Installer.Sha256);
    }

    [Fact]
    public async Task CheckAsync_DoesNotOfferSameVersion()
    {
        using var client = ClientFor("v1.0.0");
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");

        var update = await service.CheckAsync();

        Assert.False(update.IsUpdateAvailable);
        Assert.Equal("V1.0.0", update.CurrentVersion);
    }

    [Fact]
    public async Task CheckAsync_NormalizesTwoComponentReleaseTag()
    {
        using var client = ClientFor("V1.0");
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");

        var update = await service.CheckAsync();

        Assert.False(update.IsUpdateAvailable);
        Assert.Equal("V1.0.0", update.LatestVersion);
    }

    [Fact]
    public async Task CheckAsync_DoesNotOfferSameVersionWithShortCurrentVersion()
    {
        using var client = ClientFor("V1.0.0");
        var service = new GitHubUpdateService(client, "V1.0", "owner", "repo");

        Assert.False((await service.CheckAsync()).IsUpdateAvailable);
    }

    [Fact]
    public async Task CheckAsync_DoesNotOfferDirectInstallWithoutGitHubDigest()
    {
        using var client = ClientFor("V1.2.0", includeDigest: false);
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");

        var update = await service.CheckAsync();

        Assert.True(update.IsUpdateAvailable);
        Assert.Null(update.Installer);
        Assert.Equal("https://example.test/release", update.ReleaseUrl);
    }

    [Fact]
    public async Task CheckAsync_RejectsInstallerWhoseNameDoesNotMatchReleaseVersion()
    {
        using var client = ClientFor("V1.2.0", installerTag: "V1.1.0");
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");

        var update = await service.CheckAsync();

        Assert.True(update.IsUpdateAvailable);
        Assert.Null(update.Installer);
    }

    [Fact]
    public async Task DownloadInstallerAsync_VerifiesAndMovesCompletedPackage()
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var sha256 = Convert.ToHexString(SHA256.HashData(payload));
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, sha256);
        var directory = NewTemporaryDirectory();
        try
        {
            var progress = new List<UpdateDownloadProgress>();
            var result = await service.DownloadInstallerAsync(
                update,
                directory,
                new Progress<UpdateDownloadProgress>(item => progress.Add(item)));

            Assert.Equal(payload, await File.ReadAllBytesAsync(result));
            Assert.False(File.Exists(result + ".download"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_DeletesPackageWhenHashDoesNotMatch()
    {
        var payload = Encoding.UTF8.GetBytes("tampered installer payload");
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, new string('0', 64));
        var directory = NewTemporaryDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadInstallerAsync(update, directory));

            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_AcceptsPrefixedDigestAndReusesVerifiedCache()
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ =>
        {
            calls++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, "sha256:" + Convert.ToHexString(SHA256.HashData(payload)));
        var directory = NewTemporaryDirectory();
        try
        {
            var path = await service.DownloadInstallerAsync(update, directory);

            Assert.Equal(path, await service.DownloadInstallerAsync(update, directory));
            Assert.Equal(1, calls);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_ConcurrentDownloadsDoNotDeleteEachOthersTemporaryFile()
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Interlocked.Increment(ref calls) == 1
                ? new StreamContent(new GatedStream(payload, enteredRead, releaseRead.Task))
                : new ByteArrayContent(payload)
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var directory = NewTemporaryDirectory();
        var first = service.DownloadInstallerAsync(update, directory);
        try
        {
            await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = await service.DownloadInstallerAsync(update, directory);
            releaseRead.TrySetResult();

            Assert.Equal(second, await first);
            Assert.Equal(payload, await File.ReadAllBytesAsync(second));
            Assert.Single(Directory.EnumerateFiles(directory));
        }
        finally
        {
            releaseRead.TrySetResult();
            await first;
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_CancellationRemovesOnlyItsOwnTemporaryFile()
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GatedStream(payload, enteredRead, releaseRead.Task))
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var directory = NewTemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        var download = service.DownloadInstallerAsync(update, directory, cancellationToken: cancellation.Token);
        try
        {
            await enteredRead.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var otherDownload = Path.Combine(directory, update.Installer!.FileName + ".download");
            await File.WriteAllTextAsync(otherDownload, "unrelated download");
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            Assert.Equal([otherDownload], Directory.GetFiles(directory));
        }
        finally
        {
            releaseRead.TrySetResult();
            try { await download; } catch (OperationCanceledException) { }
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_PreCancelledRequestDoesNotTouchExistingFile()
    {
        using var client = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("Unexpected request")));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(32, new string('0', 64));
        var directory = NewTemporaryDirectory();
        var path = Path.Combine(directory, update.Installer!.FileName);
        try
        {
            await File.WriteAllTextAsync(path, "existing file");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.DownloadInstallerAsync(update, directory, cancellationToken: new CancellationToken(true)));

            Assert.Equal("existing file", await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://example.test/installer.exe")]
    [InlineData("http://github.com/installer.exe")]
    [InlineData("https://github.com:444/installer.exe")]
    [InlineData("https://user:password@github.com/installer.exe")]
    public async Task DownloadInstallerAsync_RejectsUntrustedRedirect(string finalUrl)
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl),
            Content = new ByteArrayContent(payload)
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var directory = NewTemporaryDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => service.DownloadInstallerAsync(
                UpdateFor(payload.Length, Convert.ToHexString(SHA256.HashData(payload))), directory));

            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadInstallerAsync_RejectsWrongContentLengthBeforeReadingBody()
    {
        var enteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new GatedStream([1, 2, 3], enteredRead, Task.CompletedTask))
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var directory = NewTemporaryDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                service.DownloadInstallerAsync(UpdateFor(2, new string('0', 64)), directory));

            Assert.False(enteredRead.Task.IsCompleted);
            Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AcquireVerifiedInstallerAsync_RejectsTamperingAfterDownloadAndReleasesLock()
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var directory = NewTemporaryDirectory();
        try
        {
            var path = await service.DownloadInstallerAsync(update, directory);
            payload[0] ^= 1;
            await File.WriteAllBytesAsync(path, payload);

            await Assert.ThrowsAsync<InvalidDataException>(() => service.AcquireVerifiedInstallerAsync(update, path));
            File.Delete(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AcquireVerifiedInstallerAsync_PreventsChangesUntilLeaseIsDisposed()
    {
        var payload = Encoding.UTF8.GetBytes("verified installer payload");
        using var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        }));
        var service = new GitHubUpdateService(client, "V1.0.0", "owner", "repo");
        var update = UpdateFor(payload.Length, Convert.ToHexString(SHA256.HashData(payload)));
        var directory = NewTemporaryDirectory();
        try
        {
            var path = await service.DownloadInstallerAsync(update, directory);
            using (await service.AcquireVerifiedInstallerAsync(update, path))
            {
                Assert.Throws<IOException>(() => File.WriteAllText(path, "tamper"));
                if (OperatingSystem.IsWindows()) Assert.Throws<IOException>(() => File.Delete(path));
                Assert.Equal(payload, await File.ReadAllBytesAsync(path));
            }

            await File.WriteAllTextAsync(path, "lease released");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HttpClient ClientFor(
        string tag,
        bool includeDigest = true,
        string? installerTag = null)
    {
        installerTag ??= tag;
        var digestJson = includeDigest ? $"\"sha256:{new string('A', 64)}\"" : "null";
        return new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""
        {
          "tag_name": "{{tag}}",
          "html_url": "https://example.test/release",
          "body": "notes",
          "assets": [
            {
              "name": "ADB-Mirror-Studio-Setup-{{installerTag}}-win-x64.exe",
              "browser_download_url": "https://github.com/owner/repo/releases/download/{{tag}}/installer.exe",
              "size": 123456,
              "digest": {{digestJson}},
              "state": "uploaded"
            },
            {
              "name": "app-win-x64.zip",
              "browser_download_url": "https://github.com/owner/repo/releases/download/{{tag}}/app.zip",
              "size": 654321,
              "digest": "sha256:{{new string('B', 64)}}",
              "state": "uploaded"
            }
          ]
        }
        """, Encoding.UTF8, "application/json")
        }));
    }

    private static AppUpdateInfo UpdateFor(long size, string sha256) => new(
        "V1.0.0",
        "V1.2.0",
        true,
        "https://github.com/owner/repo/releases/tag/V1.2.0",
        new AppUpdatePackage(
            "ADB-Mirror-Studio-Setup-V1.2.0-win-x64.exe",
            "https://github.com/owner/repo/releases/download/V1.2.0/installer.exe",
            size,
            sha256),
        "notes");

    private static string NewTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "AdbMirrorStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(responseFactory(request));
    }

    private sealed class GatedStream(byte[] payload, TaskCompletionSource enteredRead, Task releaseRead) : MemoryStream(payload)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            enteredRead.TrySetResult();
            await releaseRead.WaitAsync(cancellationToken);
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
}
