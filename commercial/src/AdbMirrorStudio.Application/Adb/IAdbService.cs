using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Application.Adb;

public interface IAdbService
{
    Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MdnsService>> DiscoverAsync(CancellationToken cancellationToken = default);
    Task<string> ConnectAsync(string endpoint, CancellationToken cancellationToken = default);
    Task<string> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string serial, CancellationToken cancellationToken = default);
    Task RebootAsync(string serial, CancellationToken cancellationToken = default);
    Task<string> EnableTcpIpAsync(string serial, int port = 5555, CancellationToken cancellationToken = default);
    Task<string> InstallApkAsync(string serial, string apkPath, CancellationToken cancellationToken = default);
    Task<string> PushFileAsync(string serial, string localPath, string remoteDirectory = "/sdcard/Download/", CancellationToken cancellationToken = default);
    Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default);
    Task<DeviceDetails> GetDeviceDetailsAsync(string serial, CancellationToken cancellationToken = default);
    Task<DevicePerformanceCounters> GetPerformanceCountersAsync(string serial, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<string> CaptureScreenshotAsync(string serial, string localPath, CancellationToken cancellationToken = default);
    Task<string> GetLogcatSnapshotAsync(string serial, int maxLines = 500, CancellationToken cancellationToken = default);
    Task<string> PullFileAsync(string serial, string remotePath, string localDirectory, CancellationToken cancellationToken = default);
    Task SendKeyEventAsync(string serial, int keyCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(string serial, bool includeSystemApps = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task LaunchAppAsync(string serial, string packageName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task ForceStopAppAsync(string serial, string packageName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task UninstallAppAsync(string serial, string packageName, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    Task<string> RunShellCommandAsync(string serial, string command, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

public sealed class AdbCommandException(string message, int? exitCode = null) : Exception(message)
{
    public int? ExitCode { get; } = exitCode;
}
