using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Application.Devices;

public sealed class DeviceRefreshCoordinator(IAdbService adbService)
{
    private long _requestedVersion;

    public async Task<DeviceSnapshot?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var version = Interlocked.Increment(ref _requestedVersion);
        var devices = await adbService.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        // A slow, older request must never overwrite a newer device snapshot.
        if (version != Volatile.Read(ref _requestedVersion))
        {
            return null;
        }

        return new DeviceSnapshot(version, DateTimeOffset.UtcNow, devices);
    }

    public void InvalidatePendingRefreshes() => Interlocked.Increment(ref _requestedVersion);
}
