namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Fact]
    public async Task Shell_HostAdbInputShowsCorrectionWithoutExecutingOrBecomingBusy()
    {
        var adb = new FakeAdb();
        using var model = Create(adb);

        await model.RunDeviceShellCommandAsync("selected-device", "adb shell pm list packages | findstr com.tp");

        Assert.Empty(adb.ShellCommands);
        Assert.Contains("pm list packages com.tp", model.ShellOutput);
        Assert.Contains("未执行", model.StatusText);
        Assert.False(model.IsBusy);
        Assert.False(model.IsShellCommandRunning);

        await model.RunDeviceShellCommandAsync("selected-device", "pm list packages com.tp");

        Assert.Equal(("selected-device", "pm list packages com.tp"), Assert.Single(adb.ShellCommands));
        Assert.DoesNotContain("输入提示", model.ShellOutput);
    }

    [Fact]
    public void Shell_ValidationBeforeConfirmationShowsActionableInputHint()
    {
        using var model = Create(new FakeAdb());

        Assert.False(model.TryValidateDeviceShellCommand("adb -s other-device shell getprop"));
        Assert.Contains("adb shell", model.ShellOutput);
        Assert.Contains("未执行", model.StatusText);
        Assert.True(model.TryValidateDeviceShellCommand("echo 'adb shell' | grep adb"));
    }
}
