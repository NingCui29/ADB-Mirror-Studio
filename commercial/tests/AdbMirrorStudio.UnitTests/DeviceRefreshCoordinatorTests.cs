using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Devices;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.UnitTests;

public sealed class DeviceRefreshCoordinatorTests
{
    [Fact]
    public async Task RefreshAsync_DiscardsSlowOlderResult()
    {
        var first = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SequencedAdbService(first.Task, second.Task);
        var coordinator = new DeviceRefreshCoordinator(service);

        var olderTask = coordinator.RefreshAsync();
        var newerTask = coordinator.RefreshAsync();
        second.SetResult([Device("new")]);
        first.SetResult([Device("old")]);

        var newer = await newerTask;
        var older = await olderTask;

        Assert.NotNull(newer);
        Assert.Equal("new", newer!.Devices.Single().Serial);
        Assert.Null(older);
    }

    private static DeviceInfo Device(string serial) =>
        new(serial, "—", "—", DeviceState.Online, ConnectionKind.Usb, DateTimeOffset.UtcNow);

    [Fact]
    public async Task RefreshAsync_DoesNotPublishCancelledResultWhenServiceIgnoresCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var result = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DeviceRefreshCoordinator(new SequencedAdbService(result.Task));
        var refresh = coordinator.RefreshAsync(cancellation.Token);
        cancellation.Cancel();
        result.SetResult([Device("cancelled")]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
    }

    [Fact]
    public async Task RefreshAsync_AlreadyCancelledRequestDoesNotInvalidateActiveRefresh()
    {
        var result = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DeviceRefreshCoordinator(new SequencedAdbService(result.Task));
        var refresh = coordinator.RefreshAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.RefreshAsync(new CancellationToken(true)));
        result.SetResult([Device("active")]);

        Assert.Equal("active", (await refresh)!.Devices.Single().Serial);
    }

    [Fact]
    public async Task InvalidatePendingRefreshes_DiscardsOutstandingSnapshot()
    {
        var result = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new DeviceRefreshCoordinator(new SequencedAdbService(result.Task));
        var refresh = coordinator.RefreshAsync();
        coordinator.InvalidatePendingRefreshes();
        result.SetResult([Device("old")]);

        Assert.Null(await refresh);
    }

    private sealed class SequencedAdbService(params Task<IReadOnlyList<DeviceInfo>>[] results) : IAdbService
    {
        private int _index;
        public Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            results[Interlocked.Increment(ref _index) - 1];
        public Task<IReadOnlyList<MdnsService>> DiscoverAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> ConnectAsync(string endpoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(string serial, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task RebootAsync(string serial, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> EnableTcpIpAsync(string serial, int port = 5555, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> InstallApkAsync(string serial, string apkPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> PushFileAsync(string serial, string localPath, string remoteDirectory = "/sdcard/Download/", CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DeviceDetails> GetDeviceDetailsAsync(string serial, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> CaptureScreenshotAsync(string serial, string localPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetLogcatSnapshotAsync(string serial, int maxLines = 500, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> PullFileAsync(string serial, string remotePath, string localDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
