using System.Buffers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using AdbMirrorStudio.Application.Updates;
using AdbMirrorStudio.Infrastructure.Serialization;

namespace AdbMirrorStudio.Infrastructure.Updates;

public sealed class GitHubUpdateService(
    HttpClient httpClient,
    string currentVersion,
    string owner,
    string repository) : IUpdateService
{
    private const long MaximumInstallerSize = 1024L * 1024 * 1024;

    public async Task<AppUpdateInfo> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ADB-Mirror-Studio-UpdateChecker/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        GitHubRelease release;
        try
        {
            using var response = await httpClient.SendAsync(request, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            release = await response.Content.ReadFromJsonAsync(
                    AdbMirrorStudioJsonContext.Default.GitHubRelease,
                    timeout.Token)
                .ConfigureAwait(false) ?? throw new InvalidOperationException("GitHub 返回了空的版本信息。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("连接 GitHub 检查更新超时。");
        }

        var current = ParseVersion(currentVersion);
        var latest = ParseVersion(release.TagName);
        var expectedInstallerName = $"ADB-Mirror-Studio-Setup-{NormalizeDisplayVersion(release.TagName)}-win-x64.exe";
        var asset = (release.Assets ?? []).FirstOrDefault(item =>
            string.Equals(item.Name, expectedInstallerName, StringComparison.OrdinalIgnoreCase)
            && IsTrustedInstallerAsset(item));
        var installer = asset is null ? null : CreateInstallerPackage(asset);

        return new AppUpdateInfo(
            NormalizeDisplayVersion(currentVersion),
            NormalizeDisplayVersion(release.TagName),
            latest > current,
            release.HtmlUrl,
            installer,
            release.Body);
    }

    public async Task<string> DownloadInstallerAsync(
        AppUpdateInfo update,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        if (!update.IsUpdateAvailable)
        {
            throw new InvalidOperationException("当前没有可安装的新版本。");
        }

        var installer = update.Installer
            ?? throw new InvalidOperationException("新版本未提供可验证的 Windows x64 安装包。");
        ValidateInstallerPackage(installer);
        var expectedInstallerName = $"ADB-Mirror-Studio-Setup-{NormalizeDisplayVersion(update.LatestVersion)}-win-x64.exe";
        if (!installer.FileName.Equals(expectedInstallerName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包文件名与目标版本不一致。");
        }
        Directory.CreateDirectory(destinationDirectory);

        var destinationPath = Path.GetFullPath(Path.Combine(destinationDirectory, installer.FileName));
        var normalizedDirectory = Path.GetFullPath(destinationDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!destinationPath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("安装包文件名不安全。");
        }

        if (File.Exists(destinationPath))
        {
            if (new FileInfo(destinationPath).Length == installer.Size
                && await HasExpectedHashAsync(destinationPath, installer.Sha256, cancellationToken).ConfigureAwait(false))
            {
                progress?.Report(new UpdateDownloadProgress(installer.Size, installer.Size));
                cancellationToken.ThrowIfCancellationRequested();
                return destinationPath;
            }
        }

        var temporaryPath = destinationPath + $".{Guid.NewGuid():N}.download";
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, installer.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("ADB-Mirror-Studio-Updater/1.0");
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            if (!IsTrustedDownloadUrl(response.RequestMessage?.RequestUri?.AbsoluteUri ?? installer.DownloadUrl))
            {
                throw new InvalidDataException("安装包重定向到了不受信任的来源。");
            }

            if (response.Content.Headers.ContentLength is { } contentLength && contentLength != installer.Size)
            {
                throw new InvalidDataException("安装包响应大小与 GitHub 元数据不一致。");
            }

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var target = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
            long received = 0;
            try
            {
                while (true)
                {
                    var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > MaximumInstallerSize || received > installer.Size)
                    {
                        throw new InvalidDataException("安装包大小与 GitHub 元数据不一致。");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report(new UpdateDownloadProgress(received, installer.Size));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            await target.DisposeAsync().ConfigureAwait(false);

            if (received != installer.Size)
            {
                throw new InvalidDataException($"安装包大小校验失败：应为 {installer.Size} 字节，实际为 {received} 字节。");
            }
            if (!await HasExpectedHashAsync(temporaryPath, installer.Sha256, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("安装包 SHA256 校验失败，文件已删除。");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            progress?.Report(new UpdateDownloadProgress(installer.Size, installer.Size));
            return destinationPath;
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the download/verification error if cleanup is blocked by another process.
            }
            throw;
        }
    }

    public async Task<IDisposable> AcquireVerifiedInstallerAsync(
        AppUpdateInfo update,
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();
        var installer = update.Installer
            ?? throw new InvalidOperationException("新版本未提供可验证的 Windows x64 安装包。");
        ValidateInstallerPackage(installer);
        var expectedName = $"ADB-Mirror-Studio-Setup-{NormalizeDisplayVersion(update.LatestVersion)}-win-x64.exe";
        if (!update.IsUpdateAvailable
            || !installer.FileName.Equals(expectedName, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(installerPath).Equals(installer.FileName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("待启动安装包与目标版本不一致。");
        }

        var stream = new FileStream(installerPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (stream.Length != installer.Size
                || !await HasExpectedHashAsync(stream, installer.Sha256, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("安装包启动前校验失败，请重新下载。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static bool IsTrustedInstallerAsset(GitHubAsset asset) =>
        string.Equals(asset.State, "uploaded", StringComparison.OrdinalIgnoreCase)
        && asset.Name?.StartsWith("ADB-Mirror-Studio-Setup-V", StringComparison.OrdinalIgnoreCase) == true
        && asset.Name.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)
        && asset.Size is > 0 and <= MaximumInstallerSize
        && TryNormalizeSha256(asset.Digest, out _)
        && IsTrustedDownloadUrl(asset.BrowserDownloadUrl);

    private static AppUpdatePackage CreateInstallerPackage(GitHubAsset asset)
    {
        _ = TryNormalizeSha256(asset.Digest, out var sha256);
        return new AppUpdatePackage(asset.Name, asset.BrowserDownloadUrl, asset.Size, sha256!);
    }

    private static void ValidateInstallerPackage(AppUpdatePackage installer)
    {
        if (Path.GetFileName(installer.FileName) != installer.FileName
            || !installer.FileName.StartsWith("ADB-Mirror-Studio-Setup-V", StringComparison.OrdinalIgnoreCase)
            || !installer.FileName.EndsWith("-win-x64.exe", StringComparison.OrdinalIgnoreCase)
            || installer.Size is <= 0 or > MaximumInstallerSize
            || !TryNormalizeSha256(installer.Sha256, out _)
            || !IsTrustedDownloadUrl(installer.DownloadUrl))
        {
            throw new InvalidOperationException("安装包元数据无效或来源不受信任。");
        }
    }

    private static bool IsTrustedDownloadUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.IsDefaultPort
        && string.IsNullOrEmpty(uri.UserInfo)
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool TryNormalizeSha256(string? value, out string? sha256)
    {
        sha256 = value?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true
            ? value[7..]
            : value;
        if (sha256?.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            sha256 = null;
            return false;
        }
        sha256 = sha256.ToUpperInvariant();
        return true;
    }

    private static async Task<bool> HasExpectedHashAsync(
        string path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await HasExpectedHashAsync(stream, expectedSha256, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasExpectedHashAsync(
        Stream stream,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeSha256(expectedSha256, out var normalizedHash)) return false;
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).Equals(normalizedHash, StringComparison.OrdinalIgnoreCase);
    }

    private static Version ParseVersion(string value)
    {
        var normalized = value.Trim().TrimStart('v', 'V').Split(['+', '-'], 2)[0];
        return Version.TryParse(normalized, out var version)
            ? new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision))
            : throw new FormatException($"无法识别版本号：{value}");
    }

    private static string NormalizeDisplayVersion(string value)
    {
        var version = ParseVersion(value);
        return $"V{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }

}

internal sealed record GitHubRelease(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("html_url")] string HtmlUrl,
    [property: JsonPropertyName("body")] string? Body,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubAsset> Assets);

internal sealed record GitHubAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string? Digest,
    [property: JsonPropertyName("state")] string State);
