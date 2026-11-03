using AdbMirrorStudio.Domain.Devices;
using Microsoft.UI.Xaml.Controls;

namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Theory]
    [InlineData(DeviceState.Offline)]
    [InlineData(DeviceState.Unauthorized)]
    public async Task UnavailableDevice_DisablesActionsUntilOnline(DeviceState state)
    {
        var adb = new FakeAdb { Devices = [Device("target") with { State = state }] };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = "target";
        model.SelectedAppPackage = "com.example.app";
        Assert.Equal("等待连接设备", model.SelectedMirrorStatusText);
        Assert.False(model.CanUseSelectedDevice);
        Assert.False(model.CanStartSelectedMirror);
        Assert.False(model.CanRunSelectedAppAction);

        adb.Devices = [Device("target")];
        await model.RefreshAsync();
        Assert.True(model.CanUseSelectedDevice);
        Assert.True(model.CanStartSelectedMirror);

        adb.Devices = [];
        await model.RefreshAsync();
        Assert.False(model.CanUseSelectedDevice);
        Assert.False(model.CanStartSelectedMirror);
    }

    [Theory]
    [InlineData("refresh", false)]
    [InlineData("profile", false)]
    [InlineData("history", false)]
    [InlineData("refresh", true)]
    [InlineData("profile", true)]
    [InlineData("history", true)]
    public async Task SettingsSaveFailure_RemainsVisible(string operation, bool accessDenied)
    {
        var settings = new FakeSettings
        {
            SaveFailure = accessDenied ? new UnauthorizedAccessException("denied") : new IOException("disk full")
        };
        using var model = Create(new FakeAdb(), settings: settings);
        await ApplySetting();
        Assert.Contains("设置暂时无法保存", model.StatusText);
        Assert.Equal(InfoBarSeverity.Error, model.StatusSeverity);
        Assert.True(model.IsStatusOpen);
        Assert.Empty(settings.Saves);

        settings.SaveFailure = null;
        await ApplySetting();
        Assert.DoesNotContain("无法保存", model.StatusText);
        Assert.Single(settings.Saves);

        async Task ApplySetting()
        {
            switch (operation)
            {
                case "refresh": await model.SetAutoRefreshAsync(!model.AutoRefresh); break;
                case "profile": await model.SetMirrorProfileAsync(model.MirrorProfiles[0].Id); break;
                default: await model.ClearRememberedEndpointsAsync(); break;
            }
        }
    }
}
