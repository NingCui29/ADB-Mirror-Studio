namespace AdbMirrorStudio.Application.Updates;

public sealed record AppUpdateInfo(
    string CurrentVersion,
    string LatestVersion,
    bool IsUpdateAvailable,
    string ReleaseUrl,
    AppUpdatePackage? Installer,
    string? ReleaseNotes);

public sealed record AppUpdatePackage(
    string FileName,
    string DownloadUrl,
    long Size,
    string Sha256);

public sealed record UpdateDownloadProgress(long BytesReceived, long TotalBytes)
{
    public double Percentage => TotalBytes <= 0
        ? 0
        : Math.Clamp(BytesReceived * 100d / TotalBytes, 0, 100);
}

public interface IUpdateService
{
    Task<AppUpdateInfo> CheckAsync(CancellationToken cancellationToken = default);

    Task<string> DownloadInstallerAsync(
        AppUpdateInfo update,
        string destinationDirectory,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<IDisposable> AcquireVerifiedInstallerAsync(
        AppUpdateInfo update,
        string installerPath,
        CancellationToken cancellationToken = default);
}
