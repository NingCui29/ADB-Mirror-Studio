using AdbMirrorStudio.Domain.Devices;
using Microsoft.UI.Xaml.Controls;

namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_RefreshesDevicesAndPreservesHistorySaveFailure(bool accessDenied)
    {
        var settings = new FakeSettings
        {
            SaveFailure = accessDenied ? new UnauthorizedAccessException("denied") : new IOException("disk full")
        };
        using var model = Create(new FakeAdb
        {
            Connect = _ => Task.FromResult("connected"), Devices = [Device("target")]
        }, settings: settings);
        model.Endpoint = "192.0.2.1:5555";
        await model.ConnectAsync();
        Assert.Equal("target", Assert.Single(model.Devices).Serial);
        Assert.Contains("设置暂时无法保存", model.StatusText);
        Assert.Equal(InfoBarSeverity.Error, model.StatusSeverity);
        Assert.Empty(settings.Saves);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Uninstall_RemovesConfirmedPackageAndReportsRefreshOutcome(bool byName, bool failRefresh)
    {
        var uninstalled = false;
        var adb = new FakeAdb
        {
            GetApps = _ => uninstalled
                ? failRefresh ? throw new IOException("device disconnected")
                    : Task.FromResult<IReadOnlyList<InstalledApp>>([new("remaining.app")])
                : Task.FromResult<IReadOnlyList<InstalledApp>>([new("removed.app"), new("remaining.app")])
        };
        using var model = Create(adb);
        model.SelectedDeviceSerial = "target";
        await model.RefreshInstalledAppsAsync(false);
        uninstalled = true;
        if (byName) await model.UninstallPackageByNameAsync("target", "removed.app");
        else await model.RunAppActionAsync("uninstall", "target", "removed.app");
        Assert.Equal("remaining.app", Assert.Single(model.InstalledApps).PackageName);
        Assert.NotEqual("removed.app", model.SelectedAppPackage);
        Assert.Contains("已从 target 卸载 removed.app", model.StatusText);
        Assert.Equal(failRefresh ? InfoBarSeverity.Error : InfoBarSeverity.Success, model.StatusSeverity);
        if (failRefresh) Assert.Contains("刷新应用列表失败", model.StatusText);
        Assert.Single(adb.Uninstalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uninstall_LateRefreshDoesNotOverwriteNewTarget(bool byName)
    {
        var response = new TaskCompletionSource<IReadOnlyList<InstalledApp>>();
        using var model = Create(new FakeAdb { GetApps = _ => response.Task });
        model.SelectedDeviceSerial = "old";
        var operation = byName ? model.UninstallPackageByNameAsync("old", "removed.app")
            : model.RunAppActionAsync("uninstall", "old", "removed.app");
        model.SelectedDeviceSerial = "new";
        var status = model.StatusText;
        response.SetResult([new("old.app")]);
        await operation;
        Assert.Empty(model.InstalledApps);
        Assert.Equal(status, model.StatusText);
    }
}
