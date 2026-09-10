using AdbMirrorStudio.Domain.Devices;
using AdbMirrorStudio.Infrastructure.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class AdbOutputParserTests
{
    [Fact]
    public void ParseDevices_ParsesUsbTcpAndStates()
    {
        const string output = """
            * daemon started successfully
            List of devices attached
            R5CT1234567             device product:dm3q model:SM_S9180 device:dm3q transport_id:1
            192.168.1.20:37133      offline product:panther model:Pixel_7 transport_id:2
            emulator-5554           unauthorized transport_id:3
            """;

        var devices = AdbOutputParser.ParseDevices(output);

        Assert.Equal(3, devices.Count);
        Assert.Equal("SM S9180", devices[0].Model);
        Assert.Equal("SM S9180", devices[0].DisplayName);
        Assert.Equal(DeviceState.Online, devices[0].State);
        Assert.Equal(ConnectionKind.Usb, devices[0].ConnectionKind);
        Assert.Equal(DeviceState.Offline, devices[1].State);
        Assert.Equal(ConnectionKind.TcpIp, devices[1].ConnectionKind);
        Assert.Equal("Pixel 7 192.168.1.20:37133", devices[1].DisplayName);
        Assert.Equal(DeviceState.Unauthorized, devices[2].State);
        Assert.Equal("emulator-5554", devices[2].DisplayName);
    }

    [Fact]
    public void DisplayName_UsesFallbackNameForUnknownTcpDevice()
    {
        var device = new DeviceInfo(
            "10.0.0.8:5555",
            "—",
            "—",
            DeviceState.Online,
            ConnectionKind.TcpIp,
            DateTimeOffset.UtcNow);

        Assert.Equal("未知设备 10.0.0.8:5555", device.DisplayName);
    }

    [Fact]
    public void ParseDevices_RecognizesAutoConnectedWirelessTransport()
    {
        const string output = "List of devices attached\nadb-serial-random._adb-tls-connect._tcp device model:Pixel_9 transport_id:1";

        Assert.Equal(ConnectionKind.TcpIp, AdbOutputParser.ParseDevices(output).Single().ConnectionKind);
    }

    [Fact]
    public void ParseMdnsServices_OnlyReturnsAdbTlsServices()
    {
        const string output = """
            adb-ABCD pairing._adb-tls-pairing._tcp 192.168.1.8:37001
            adb-ABCD connect._adb-tls-connect._tcp 192.168.1.8:38555
            printer _ipp._tcp 192.168.1.9:631
            """;

        var services = AdbOutputParser.ParseMdnsServices(output);

        Assert.Equal(2, services.Count);
        Assert.Equal("192.168.1.8:38555", services[1].Endpoint);
    }
}
