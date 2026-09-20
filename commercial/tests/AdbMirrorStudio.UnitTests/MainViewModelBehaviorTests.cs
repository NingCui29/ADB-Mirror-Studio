using AdbMirrorStudio.App;
using AdbMirrorStudio.App.ViewModels;
using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Diagnostics;
using AdbMirrorStudio.Application.Mirroring;
using AdbMirrorStudio.Application.Settings;
using AdbMirrorStudio.Application.Updates;
using AdbMirrorStudio.Domain.Devices;
using AdbMirrorStudio.Domain.Mirroring;
using AdbMirrorStudio.Domain.Settings;
using Microsoft.UI.Xaml.Controls;

namespace AdbMirrorStudio.UnitTests;

public sealed class MainViewModelBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstall_UsesConfirmedTargetAndPackageAfterSelectionChanges(bool byName)
    {
        var adb = new FakeAdb();
        using var model = Create(adb);
        model.SelectedDeviceSerial = "other-device";
        model.SelectedAppPackage = "other.app";
        model.PackageNameInput = "other.app";

        if (byName) await model.UninstallPackageByNameAsync("confirmed-device", "confirmed.app");
        else await model.RunAppActionAsync("uninstall", "confirmed-device", "confirmed.app");

        Assert.Equal(("confirmed-device", "confirmed.app"), Assert.Single(adb.Uninstalls));
        Assert.Equal("other.app", model.PackageNameInput);
    }

    [Fact]
    public async Task Shell_UsesConfirmedTargetAndExpression()
    {
        var adb = new FakeAdb();
        using var model = Create(adb);
        model.SelectedDeviceSerial = "other-device";
        model.ShellCommand = "other command";

        await model.RunDeviceShellCommandAsync("confirmed-device", "echo confirmed");

        Assert.Equal(("confirmed-device", "echo confirmed"), Assert.Single(adb.ShellCommands));
    }

    [Fact]
    public async Task Refresh_UnchangedDevicePreservesCardSelectionAndLoadedApps()
    {
        var adb = new FakeAdb { Devices = [Device("device")] };
        using var model = Create(adb);
        await model.RefreshAsync();
        await model.RefreshInstalledAppsAsync(false);
        var card = Assert.Single(model.Devices);
        var app = Assert.Single(model.InstalledApps);
        var collectionChanges = 0;
        model.Devices.CollectionChanged += (_, _) => collectionChanges++;
        adb.Devices = [Device("device") with { LastSeen = DateTimeOffset.UtcNow.AddSeconds(5) }];

        await model.RefreshAsync(silent: true);

        Assert.Equal(0, collectionChanges);
        Assert.Same(card, Assert.Single(model.Devices));
        Assert.Same(app, Assert.Single(model.InstalledApps));
        Assert.Equal("device", model.SelectedDeviceSerial);
    }

    [Fact]
    public async Task Refresh_LostTargetDoesNotSwitchToAnotherDeviceOnLaterTicks()
    {
        var adb = new FakeAdb { Devices = [Device("first")] };
        using var model = Create(adb);
        await model.RefreshAsync();
        adb.Devices = [Device("other")];

        await model.RefreshAsync(silent: true);
        await model.RefreshAsync(silent: true);

        Assert.Null(model.SelectedDeviceSerial);
        Assert.Contains("重新选择", model.StatusText);
    }

    [Fact]
    public async Task BackgroundRefresh_DoesNotReplaceOperationStatus()
    {
        var adb = new FakeAdb { Devices = [Device("device")] };
        using var model = Create(adb);
        await model.RefreshAsync();
        await model.RunDeviceShellCommandAsync("device", "echo done");
        var status = model.StatusText;

        await model.RefreshAsync(silent: true);

        Assert.Equal(status, model.StatusText);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task Refresh_DisposeCancelsCommandAndDiscardsLateSnapshot()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<DeviceInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var adb = new FakeAdb { GetDevices = token => { captured = token; return completion.Task; } };
        var model = Create(adb);
        var refresh = model.RefreshAsync();

        model.Dispose();
        completion.SetResult([Device("late")]);
        await refresh;
        await model.RunDeviceShellCommandAsync("device", "echo too-late");

        Assert.True(captured.IsCancellationRequested);
        Assert.Empty(model.Devices);
        Assert.Empty(adb.ShellCommands);
    }

    [Fact]
    public async Task Shell_CancelWinsOverLateSuccessfulResponse()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb { Shell = _ => completion.Task };
        using var model = Create(adb);
        var command = model.RunDeviceShellCommandAsync("device", "echo done");

        model.CancelDeviceShellCommand();
        completion.SetResult("late response");
        await command;

        Assert.Contains("已取消", model.ShellOutput);
        Assert.DoesNotContain("late response", model.ShellOutput);
        Assert.False(model.IsShellCommandRunning);
    }

    [Fact]
    public async Task Details_DiscardSuccessfulResponseAfterShutdown()
    {
        var completion = new TaskCompletionSource<DeviceDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb { Details = _ => completion.Task };
        var model = Create(adb);
        var operation = model.GetDeviceDetailsAsync("device");
        model.Dispose();
        completion.SetResult(new("device", "12", "31", "1920x1080", 100, "charging", "available"));

        Assert.Null(await operation);
    }

    [Fact]
    public async Task Connect_ShutdownDoesNotRememberLateSuccess()
    {
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb { Connect = _ => completion.Task };
        var model = Create(adb);
        model.Endpoint = "127.0.0.1:5555";
        var operation = model.ConnectAsync();
        model.Dispose();
        completion.SetResult("connected to 127.0.0.1:5555");
        await operation;

        Assert.Empty(model.RememberedEndpoints);
    }

    [Fact]
    public async Task Apps_OlderFailureDoesNotOverwriteNewerSuccess()
    {
        var older = new TaskCompletionSource<IReadOnlyList<InstalledApp>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        var adb = new FakeAdb { GetApps = _ => ++count == 1 ? older.Task : Task.FromResult<IReadOnlyList<InstalledApp>>([new("new.app")]) };
        using var model = Create(adb);
        model.SelectedDeviceSerial = "device";
        var first = model.RefreshInstalledAppsAsync(false);
        await model.RefreshInstalledAppsAsync(false);
        var status = model.StatusText;

        older.SetException(new IOException("old request failed"));
        await first;

        Assert.Equal(status, model.StatusText);
        Assert.Equal("new.app", Assert.Single(model.InstalledApps).PackageName);
    }

    [Fact]
    public async Task Upload_ManualPathAndApkSelectionDoNotReuseAnOldQueue()
    {
        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        try
        {
            var adb = new FakeAdb();
            using var model = Create(adb);
            model.SetTransferFiles([first]);
            model.TransferFilePath = second;
            model.ApkFilePath = "separate.apk";

            await model.PushFilesAsync("device");
            await model.InstallApkAsync("device");

            Assert.Equal(second, Assert.Single(adb.Pushes));
            Assert.Equal("separate.apk", Assert.Single(adb.Installs));
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public async Task Upload_RetryCancellationResetsPreviousCompletionFlags()
    {
        var first = Path.GetTempFileName();
        var second = Path.GetTempFileName();
        try
        {
            var adb = new FakeAdb();
            using var model = Create(adb);
            model.SetTransferFiles([first, second]);
            await model.PushFilesAsync("device");
            Assert.Equal(InfoBarSeverity.Success, model.StatusSeverity);
            var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            adb.Push = _ => completion.Task;
            var retry = model.PushFilesAsync("device");

            model.CancelTransfer();
            completion.SetResult("late success");
            await retry;

            Assert.All(model.TransferQueue, item => { Assert.False(item.IsComplete); Assert.Equal("已取消", item.Status); });
            Assert.False(model.IsTransferRunning);
        }
        finally { File.Delete(first); File.Delete(second); }
    }

    [Fact]
    public async Task Update_DownloadUsesVersionCapturedBeforeConfirmation()
    {
        var updates = new FakeUpdates();
        using var model = Create(new FakeAdb(), updates: updates);
        await model.CheckForUpdatesAsync();
        var confirmed = model.AvailableUpdate!;
        updates.Next = confirmed with { LatestVersion = "V3.0.0" };
        await model.CheckForUpdatesAsync();

        await model.DownloadAndVerifyUpdateAsync(confirmed);

        Assert.Same(confirmed, updates.Downloaded);
    }

    [Fact]
    public void Sessions_OldFailureDoesNotReplaceNewActiveSessionOrStatus()
    {
        var mirror = new FakeMirror();
        using var model = Create(new FakeAdb(), mirror);
        var current = new MirrorSession("new", "device", MirrorSessionState.Running, 123, DateTimeOffset.UtcNow);
        mirror.ActiveSessions = [current];
        mirror.Emit(current);
        var status = model.StatusText;

        mirror.Emit(current with { Id = "old", State = MirrorSessionState.Failed, Error = "old failure" });

        Assert.Equal("new", Assert.Single(model.Sessions).Session.Id);
        Assert.Equal(status, model.StatusText);
    }

    [Fact]
    public async Task Stop_RecordingFailureIsNotReplacedBySuccessfulStopMessage()
    {
        var mirror = new FakeMirror { FailOnStop = true };
        using var model = Create(new FakeAdb(), mirror);
        var recording = new MirrorSession("recording", "device", MirrorSessionState.Running, 123, DateTimeOffset.UtcNow,
            RecordPath: "recording.mp4");
        mirror.ActiveSessions = [recording];
        mirror.Emit(recording);

        await model.StopMirrorAsync("device");

        Assert.Contains("录屏异常结束", model.StatusText);
        Assert.Contains("封装失败", model.StatusText);
        Assert.Equal(InfoBarSeverity.Error, model.StatusSeverity);
    }

    [Fact]
    public async Task TcpIpTargetLabelDoesNotRepeatSerial()
    {
        const string serial = "10.67.116.12:5555";
        var adb = new FakeAdb
        {
            Devices = [new(serial, "mt", "product", DeviceState.Online, ConnectionKind.TcpIp, DateTimeOffset.UtcNow)]
        };
        using var model = Create(adb);

        await model.RefreshAsync();

        Assert.Equal($"mt {serial}", model.SelectedDeviceLabel);
    }

    [Fact]
    public async Task MirrorActionsTrackSelectedDeviceSessionState()
    {
        var mirror = new FakeMirror();
        var adb = new FakeAdb { Devices = [Device("device")] };
        using var model = Create(adb, mirror);
        await model.RefreshAsync();
        Assert.True(model.CanStartSelectedMirror);
        Assert.False(model.CanStopSelectedMirror);

        var running = new MirrorSession("session", "device", MirrorSessionState.Running, 123, DateTimeOffset.UtcNow);
        mirror.ActiveSessions = [running];
        mirror.Emit(running);

        Assert.False(model.CanStartSelectedMirror);
        Assert.True(model.CanStopSelectedMirror);
        Assert.False(model.CanArrangeMirrorWindows);

        mirror.ActiveSessions = [];
        mirror.Emit(running with { State = MirrorSessionState.Exited });

        Assert.True(model.CanStartSelectedMirror);
        Assert.False(model.CanStopSelectedMirror);
    }

    [Fact]
    public async Task PerformanceReadingsResetWhenSelectedDeviceChangesAndIgnoreLateOldDevice()
    {
        var oldRequest = new TaskCompletionSource<DevicePerformanceCounters>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb
        {
            Devices = [Device("first"), Device("second")],
            Performance = (serial, _) => serial == "first"
                ? oldRequest.Task
                : Task.FromResult(new DevicePerformanceCounters(200, 100, 35, "gpu", 62.8, "cpu", 60.5, "gpu-temp"))
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = "first";
        var oldPoll = model.RefreshPerformanceAsync();

        model.SelectedDeviceSerial = "second";
        await model.RefreshPerformanceAsync();
        oldRequest.SetResult(new DevicePerformanceCounters(900, 300, 95, "gpu", 99, "old-cpu", 99, "old-gpu"));
        await oldPoll;

        Assert.Equal("35.0%", model.GpuUsageText);
        Assert.Equal("计算中", model.CpuUsageText);
        Assert.Equal("62.8 °C", model.CpuTemperatureText);
        Assert.Equal("60.5 °C", model.GpuTemperatureText);
        Assert.Equal("second", model.SelectedDeviceSerial);
    }

    [Fact]
    public async Task PerformanceReadingsShowCpuIntervalAndThirtySecondAverage()
    {
        var samples = new Queue<DevicePerformanceCounters>([
            new(100, 50, 20, "gpu", 61, "cpu", 58, "gpu-temp"),
            new(200, 80, 40, "gpu", 62, "cpu", 59, "gpu-temp"),
            new(300, 130, 60, "gpu", 63, "cpu", 60, "gpu-temp")]);
        var adb = new FakeAdb
        {
            Devices = [Device("device")],
            Performance = (_, _) => Task.FromResult(samples.Dequeue())
        };
        using var model = Create(adb);
        await model.RefreshAsync();

        await model.RefreshPerformanceAsync();
        Assert.Equal("计算中", model.CpuUsageText);
        await model.RefreshPerformanceAsync();
        await model.RefreshPerformanceAsync();

        Assert.Equal("50.0%", model.CpuUsageText);
        Assert.Equal("60.0%", model.CpuAverageText);
        Assert.Equal("60.0%", model.GpuUsageText);
        Assert.Equal("40.0%", model.GpuAverageText);
        Assert.Equal("63.0 °C", model.CpuTemperatureText);
        Assert.Equal("60.0 °C", model.GpuTemperatureText);
        model.SelectedDeviceSerial = null;
        Assert.Equal("—", model.CpuUsageText);
        Assert.Equal("—", model.GpuAverageText);
        Assert.Equal("—", model.CpuTemperatureText);
        Assert.Equal("—", model.GpuTemperatureText);
    }

    private static DeviceInfo Device(string serial) => new(serial, "model", "product", DeviceState.Online, ConnectionKind.Usb, DateTimeOffset.UtcNow);

    private static MainViewModel Create(FakeAdb adb, FakeMirror? mirror = null, FakeUpdates? updates = null)
    {
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new ImmediateContext());
            return new MainViewModel(new AppServices(adb, mirror ?? new FakeMirror(), new FakeSettings(), new FakeDiagnostics(), updates ?? new FakeUpdates()));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private sealed class ImmediateContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class FakeSettings : IAppSettingsStore
    {
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(AppSettings.Default);
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakeDiagnostics : IDiagnosticsService
    {
        public Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<DiagnosticItem>>([]);
    }

    private sealed class FakeUpdates : IUpdateService
    {
        public AppUpdateInfo Next { get; set; } = new("V1.0.0", "V2.0.0", true, "https://example.test/release", new("installer.exe", "https://example.test/installer", 1, new string('A', 64)), null);
        public AppUpdateInfo? Downloaded { get; private set; }
        public Task<AppUpdateInfo> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(Next);
        public Task<string> DownloadInstallerAsync(AppUpdateInfo update, string destinationDirectory, IProgress<UpdateDownloadProgress>? progress = null, CancellationToken cancellationToken = default)
        { Downloaded = update; return Task.FromResult("installer.exe"); }
        public Task<IDisposable> AcquireVerifiedInstallerAsync(AppUpdateInfo update, string installerPath, CancellationToken cancellationToken = default) => Task.FromResult<IDisposable>(new MemoryStream());
    }

    private sealed class FakeMirror : IMirrorSessionManager
    {
        public IReadOnlyCollection<MirrorSession> ActiveSessions { get; set; } = [];
        public bool FailOnStop { get; init; }
        public event EventHandler<MirrorSession>? SessionChanged;
        public void Emit(MirrorSession session) => SessionChanged?.Invoke(this, session);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public Task<MirrorSession> StartAsync(string deviceSerial, MirrorProfile profile, string? windowTitle = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string deviceSerial, CancellationToken cancellationToken = default)
        {
            var stopping = ActiveSessions.Single();
            ActiveSessions = [];
            Emit(stopping with { State = FailOnStop ? MirrorSessionState.Failed : MirrorSessionState.Exited,
                Error = FailOnStop ? "封装失败" : null });
            return Task.CompletedTask;
        }
    }

    private sealed class FakeAdb : IAdbService
    {
        public IReadOnlyList<DeviceInfo> Devices { get; set; } = [];
        public Func<CancellationToken, Task<IReadOnlyList<DeviceInfo>>>? GetDevices { get; init; }
        public Func<CancellationToken, Task<IReadOnlyList<InstalledApp>>>? GetApps { get; init; }
        public Func<CancellationToken, Task<string>>? Shell { get; init; }
        public Func<CancellationToken, Task<string>>? Connect { get; init; }
        public Func<CancellationToken, Task<DeviceDetails>>? Details { get; init; }
        public Func<string, CancellationToken, Task<DevicePerformanceCounters>>? Performance { get; init; }
        public Func<CancellationToken, Task<string>>? Push { get; set; }
        public List<(string, string)> Uninstalls { get; } = [];
        public List<(string, string)> ShellCommands { get; } = [];
        public List<string> Pushes { get; } = [];
        public List<string> Installs { get; } = [];
        public Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default) => GetDevices?.Invoke(cancellationToken) ?? Task.FromResult(Devices);
        public Task<IReadOnlyList<MdnsService>> DiscoverAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<MdnsService>>([]);
        public Task<string> ConnectAsync(string endpoint, CancellationToken cancellationToken = default) => Connect?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        public Task<string> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DisconnectAsync(string serial, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RebootAsync(string serial, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> EnableTcpIpAsync(string serial, int port = 5555, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> InstallApkAsync(string serial, string apkPath, CancellationToken cancellationToken = default)
        { Installs.Add(apkPath); return Task.FromResult("Success"); }
        public Task<string> PushFileAsync(string serial, string localPath, string remoteDirectory = "/sdcard/Download/", CancellationToken cancellationToken = default)
        { Pushes.Add(localPath); return Push?.Invoke(cancellationToken) ?? Task.FromResult("Success"); }
        public Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<DeviceDetails> GetDeviceDetailsAsync(string serial, CancellationToken cancellationToken = default) => Details?.Invoke(cancellationToken) ?? throw new NotSupportedException();
        public Task<DevicePerformanceCounters> GetPerformanceCountersAsync(string serial, CancellationToken cancellationToken = default) => Performance?.Invoke(serial, cancellationToken) ?? throw new NotSupportedException();
        public Task<string> CaptureScreenshotAsync(string serial, string localPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetLogcatSnapshotAsync(string serial, int maxLines = 500, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> PullFileAsync(string serial, string remotePath, string localDirectory, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(string serial, bool includeSystemApps = false, CancellationToken cancellationToken = default) => GetApps?.Invoke(cancellationToken) ?? Task.FromResult<IReadOnlyList<InstalledApp>>([new("loaded.app")]);
        public Task UninstallAppAsync(string serial, string packageName, CancellationToken cancellationToken = default)
        { Uninstalls.Add((serial, packageName)); return Task.CompletedTask; }
        public Task<string> RunShellCommandAsync(string serial, string command, CancellationToken cancellationToken = default)
        { ShellCommands.Add((serial, command)); return Shell?.Invoke(cancellationToken) ?? Task.FromResult("done"); }
    }
}
