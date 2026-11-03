using AdbMirrorStudio.Application.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class DeviceShellCommandTests
{
    [Theory]
    [InlineData("adb shell pm list packages | findstr com.tp")]
    [InlineData("  adb\t-s other-device shell reboot  ")]
    [InlineData("ADB.EXE shell getprop")]
    [InlineData(".\\adb.exe -s other-device shell getprop")]
    [InlineData("\"C:\\Android SDK\\platform-tools\\adb.exe\" shell getprop")]
    [InlineData("'C:/Android SDK/platform-tools/adb.exe' -s other-device shell getprop")]
    [InlineData("adb|cat")]
    public void Validate_RejectsLeadingAdbExecutableWithDeviceShellGuidance(string command)
    {
        var error = Assert.Throws<ArgumentException>(() => DeviceShellCommand.Validate(command));

        Assert.Contains("Android 设备 Shell", error.Message);
        Assert.Contains("pm list packages com.tp", error.Message);
    }

    [Theory]
    [InlineData("pm list packages com.tp")]
    [InlineData("pm list packages | grep -F 'com.tp'")]
    [InlineData("echo 'adb shell'")]
    [InlineData("printf '%s' \"C:\\Android SDK\\adb.exe\"")]
    [InlineData("printf '%s' 'findstr: not found'")]
    [InlineData("pm list packages | findstr com.tp")]
    [InlineData("printf '%s' \"a'b\" | wc -c")]
    [InlineData("getprop ro.product.model && getprop ro.build.version.sdk")]
    [InlineData("adbd --help")]
    public void Validate_AllowsDeviceExpressionsAndQuotedHostCommandText(string command)
    {
        DeviceShellCommand.Validate(command);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("getprop\nreboot")]
    [InlineData("getprop\rreboot")]
    [InlineData("getprop\0")]
    public void Validate_RejectsEmptyOrControlCharacterCommands(string command)
    {
        Assert.Throws<ArgumentException>(() => DeviceShellCommand.Validate(command));
    }

    [Fact]
    public void Validate_PreservesLengthLimitAndAllowsTabs()
    {
        DeviceShellCommand.Validate("echo\t" + new string('a', 4091));

        Assert.Throws<ArgumentException>(() => DeviceShellCommand.Validate("echo " + new string('a', 4092)));
    }
}
