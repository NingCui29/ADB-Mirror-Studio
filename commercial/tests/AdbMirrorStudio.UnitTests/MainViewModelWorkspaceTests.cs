using AdbMirrorStudio.App.ViewModels;
using AdbMirrorStudio.Domain.Devices;
using AdbMirrorStudio.Domain.Mirroring;

namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Fact]
    public async Task WorkspacePresentation_SelectionSwitchesMirroredReadyAndMissingTargets()
    {
        var running = new MirrorSession("session", "mirrored", MirrorSessionState.Running, 123, DateTimeOffset.UtcNow);
        var mirror = new FakeMirror { ActiveSessions = [running] };
        using var model = Create(new FakeAdb { Devices = [Device("mirrored"), Device("ready")] }, mirror);
        await model.RefreshAsync();
        Assert.Null(model.SelectedDeviceSerial);
        AssertMirrorPresentation(model, false, "开始镜像", "等待连接设备", "连接设备，开始镜像");
        var changed = new HashSet<string>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        model.SelectedDeviceSerial = "mirrored";
        AssertMirrorPresentation(model, true, "停止镜像", "镜像运行中", "镜像正在独立窗口中运行");
        AssertMirrorPresentationNotified(changed);
        changed.Clear();

        model.SelectedDeviceSerial = "ready";
        AssertMirrorPresentation(model, false, "开始镜像", "设备已就绪", "将设备屏幕带到电脑上");
        AssertMirrorPresentationNotified(changed);
        changed.Clear();

        model.SelectedDeviceSerial = "absent";
        AssertMirrorPresentation(model, false, "开始镜像", "等待连接设备", "连接设备，开始镜像");
        AssertMirrorPresentationNotified(changed);
        changed.Clear();

        model.SelectedDeviceSerial = null;
        AssertMirrorPresentation(model, false, "开始镜像", "等待连接设备", "连接设备，开始镜像");
        AssertMirrorPresentationNotified(changed);
        Assert.False(model.CanUseSelectedDevice);
    }

    [Fact]
    public async Task WorkspacePresentation_RefreshNotifiesAfterAutomaticSelectionAndTargetLoss()
    {
        var running = new MirrorSession("session", "device", MirrorSessionState.Running, 123, DateTimeOffset.UtcNow);
        var mirror = new FakeMirror { ActiveSessions = [running] };
        var adb = new FakeAdb { Devices = [Device("device")] };
        using var model = Create(adb, mirror);
        var observedStatuses = new List<string>();
        model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(model.SelectedMirrorStatusText))
                observedStatuses.Add(model.SelectedMirrorStatusText);
        };

        await model.RefreshAsync();
        Assert.Equal("device", model.SelectedDeviceSerial);
        Assert.Equal("镜像运行中", observedStatuses.Last());
        Assert.Equal("停止镜像", model.SelectedMirrorActionText);

        mirror.ActiveSessions = [];
        await model.RefreshAsync(silent: true);
        Assert.Equal("设备已就绪", observedStatuses.Last());
        Assert.Equal("开始镜像", model.SelectedMirrorActionText);

        adb.Devices = [Device("device") with { State = DeviceState.Offline }];
        await model.RefreshAsync(silent: true);
        Assert.Null(model.SelectedDeviceSerial);
        Assert.Equal("等待连接设备", observedStatuses.Last());
        model.SelectedDeviceSerial = "device";
        Assert.Equal("等待连接设备", model.SelectedMirrorStatusText);
    }

    [Fact]
    public async Task WorkspacePresentation_CurrentSessionEndUpdatesButStaleEndCannotClearNewSession()
    {
        var current = new MirrorSession("new", "device", MirrorSessionState.Running, 123, DateTimeOffset.UtcNow);
        var mirror = new FakeMirror { ActiveSessions = [current] };
        using var model = Create(new FakeAdb { Devices = [Device("device")] }, mirror);
        await model.RefreshAsync();
        var changed = new HashSet<string>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        mirror.Emit(current with { Id = "old", State = MirrorSessionState.Exited });
        Assert.Equal("停止镜像", model.SelectedMirrorActionText);
        Assert.Equal("镜像运行中", model.SelectedMirrorStatusText);
        Assert.DoesNotContain(nameof(model.SelectedMirrorStatusText), changed);

        mirror.ActiveSessions = [];
        mirror.Emit(current with { State = MirrorSessionState.Exited });
        AssertMirrorPresentation(model, false, "开始镜像", "设备已就绪", "将设备屏幕带到电脑上");
        AssertMirrorPresentationNotified(changed);
    }

    [Fact]
    public async Task WorkspacePresentation_PerformanceBarsClampInvalidReadingsAndResetOnFailure()
    {
        var sample = 0;
        var adb = new FakeAdb
        {
            Devices = [Device("device")],
            Performance = (_, _) => ++sample switch
            {
                1 => Task.FromResult(new DevicePerformanceCounters(100, 50, 125, "gpu",
                    MemoryTotalKilobytes: 1000, MemoryAvailableKilobytes: 250, DdrUsagePercent: -10)),
                2 => Task.FromResult(new DevicePerformanceCounters(200, 100, double.NaN, "gpu",
                    DdrUsagePercent: double.PositiveInfinity)),
                _ => Task.FromException<DevicePerformanceCounters>(new IOException("sampling failed"))
            }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        await model.SetPerformanceMonitoringAsync(true);
        var changed = new HashSet<string>();
        model.PropertyChanged += (_, args) => changed.Add(args.PropertyName!);

        await model.RefreshPerformanceAsync();
        Assert.Equal(100d, model.GpuUsagePercent);
        Assert.Equal(75d, model.MemoryUsagePercent);
        Assert.Equal(0d, model.DdrUsagePercent);
        Assert.Contains(nameof(model.GpuUsagePercent), changed);
        Assert.Contains(nameof(model.MemoryUsagePercent), changed);

        changed.Clear();
        await model.RefreshPerformanceAsync();
        Assert.Equal(50d, model.CpuUsagePercent);
        Assert.Equal(0d, model.GpuUsagePercent);
        Assert.Equal(0d, model.MemoryUsagePercent);
        Assert.Equal(0d, model.DdrUsagePercent);
        Assert.Contains(nameof(model.CpuUsagePercent), changed);
        Assert.Contains(nameof(model.GpuUsagePercent), changed);
        Assert.Contains(nameof(model.MemoryUsagePercent), changed);

        await model.RefreshPerformanceAsync();
        Assert.All(new[] { model.CpuUsagePercent, model.GpuUsagePercent, model.MemoryUsagePercent, model.DdrUsagePercent },
            percent => Assert.Equal(0d, percent));
        Assert.Equal("暂不可用", model.CpuUsageText);
    }

    [Fact]
    public async Task WorkspacePresentation_DeviceStatusWarning_FlagsUnauthorizedAndOfflineTargets()
    {
        var unauthorizedDevice = new DeviceInfo("unauth", "ModelU", "ProductU", DeviceState.Unauthorized, ConnectionKind.Usb, DateTimeOffset.UtcNow);
        var offlineDevice = new DeviceInfo("offline", "ModelO", "ProductO", DeviceState.Offline, ConnectionKind.TcpIp, DateTimeOffset.UtcNow);
        var onlineDevice = new DeviceInfo("online", "ModelL", "ProductL", DeviceState.Online, ConnectionKind.Usb, DateTimeOffset.UtcNow);

        using var model = Create(new FakeAdb { Devices = [unauthorizedDevice, offlineDevice, onlineDevice] });
        await model.RefreshAsync();

        model.SelectedDeviceSerial = "unauth";
        Assert.True(model.HasSelectedDeviceWarning);
        Assert.Equal("设备未授权", model.SelectedDeviceWarningTitle);
        Assert.Contains("允许 USB 调试", model.SelectedDeviceWarningMessage);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning, model.SelectedDeviceWarningSeverity);

        model.SelectedDeviceSerial = "offline";
        Assert.True(model.HasSelectedDeviceWarning);
        Assert.Equal("设备已离线", model.SelectedDeviceWarningTitle);
        Assert.Contains("检查 USB 数据线或无线网络连接", model.SelectedDeviceWarningMessage);
        Assert.Equal(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Error, model.SelectedDeviceWarningSeverity);

        model.SelectedDeviceSerial = "online";
        Assert.False(model.HasSelectedDeviceWarning);
        Assert.Equal(string.Empty, model.SelectedDeviceWarningTitle);

        model.SelectedDeviceSerial = null;
        Assert.False(model.HasSelectedDeviceWarning);
    }

    [Fact]
    public async Task WorkspacePresentation_ActiveTaskCount_TracksMirrorSessionsAndTransfers()
    {
        var session = new MirrorSession("s1", "dev1", MirrorSessionState.Running, 101, DateTimeOffset.UtcNow);
        var mirror = new FakeMirror { ActiveSessions = [session] };
        using var model = Create(new FakeAdb { Devices = [Device("dev1")] }, mirror);

        Assert.False(model.HasActiveTasks);
        Assert.Equal(0, model.ActiveTaskCount);

        await model.RefreshAsync();

        Assert.True(model.HasActiveTasks);
        Assert.Equal(1, model.ActiveTaskCount);
        Assert.Equal("1", model.ActiveTaskCountLabel);
    }

    [Fact]
    public void DiagnosticItemViewModel_SeverityBrush_MatchesSeverityLevel()
    {
        var success = new DiagnosticItemViewModel(new AdbMirrorStudio.Application.Diagnostics.DiagnosticItem("adb", "Adb", "OK", AdbMirrorStudio.Application.Diagnostics.DiagnosticSeverity.Success));
        var warning = new DiagnosticItemViewModel(new AdbMirrorStudio.Application.Diagnostics.DiagnosticItem("conflict", "Conflict", "Port taken", AdbMirrorStudio.Application.Diagnostics.DiagnosticSeverity.Warning));
        var error = new DiagnosticItemViewModel(new AdbMirrorStudio.Application.Diagnostics.DiagnosticItem("scrcpy", "Scrcpy", "Missing", AdbMirrorStudio.Application.Diagnostics.DiagnosticSeverity.Error));

        Assert.Equal(Microsoft.UI.ColorHelper.FromArgb(255, 48, 200, 117), ((Microsoft.UI.Xaml.Media.SolidColorBrush)success.SeverityBrush).Color);
        Assert.Equal(Microsoft.UI.ColorHelper.FromArgb(255, 234, 163, 0), ((Microsoft.UI.Xaml.Media.SolidColorBrush)warning.SeverityBrush).Color);
        Assert.Equal(Microsoft.UI.ColorHelper.FromArgb(255, 232, 93, 93), ((Microsoft.UI.Xaml.Media.SolidColorBrush)error.SeverityBrush).Color);
    }

    private static void AssertMirrorPresentation(MainViewModel model, bool mirroring, string action, string status, string headline)
    {
        Assert.Equal(mirroring, model.SelectedDeviceIsMirroring);
        Assert.Equal(action, model.SelectedMirrorActionText);
        Assert.Equal(status, model.SelectedMirrorStatusText);
        Assert.Equal(headline, model.SelectedMirrorHeadline);
        Assert.Contains("独立", model.SelectedMirrorDescription);
    }

    private static void AssertMirrorPresentationNotified(HashSet<string> properties)
    {
        foreach (var property in new[]
                 {
                     nameof(MainViewModel.SelectedDeviceIsMirroring), nameof(MainViewModel.SelectedMirrorActionText),
                     nameof(MainViewModel.SelectedMirrorStatusText), nameof(MainViewModel.SelectedMirrorHeadline),
                     nameof(MainViewModel.SelectedMirrorDescription), nameof(MainViewModel.CanStartSelectedMirror),
                     nameof(MainViewModel.CanStopSelectedMirror)
                 })
            Assert.Contains(property, properties);
    }
}
