using AdbMirrorStudio.Infrastructure.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class AdbEndpointTests
{
    [Theory]
    [InlineData("192.168.1.20", "192.168.1.20:5555")]
    [InlineData("192.168.1.20:37099", "192.168.1.20:37099")]
    [InlineData("android-lab.local", "android-lab.local:5555")]
    [InlineData("[2001:db8::1]:37111", "[2001:db8::1]:37111")]
    [InlineData("2001:db8::1", "[2001:db8::1]:5555")]
    [InlineData("[::1]", "[::1]:5555")]
    [InlineData(" LAB.local:5037 ", "lab.local:5037")]
    public void Normalize_ReturnsCanonicalEndpoint(string input, string expected)
    {
        Assert.Equal(expected, AdbEndpoint.Normalize(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("host:70000")]
    [InlineData("not a host")]
    [InlineData("device.local/path")]
    [InlineData("user@device.local")]
    [InlineData("device.local?port=5555")]
    [InlineData("device.local#fragment")]
    [InlineData("device.local:")]
    [InlineData("device.local:0")]
    [InlineData("device.local:+5555")]
    [InlineData("device.local\\other")]
    [InlineData("[::1]:")]
    [InlineData("[::1]garbage")]
    [InlineData("[::1]:65536")]
    [InlineData("tcp://device.local")]
    [InlineData("device\n.local")]
    public void Normalize_RejectsInvalidEndpoint(string input)
    {
        Assert.ThrowsAny<ArgumentException>(() => AdbEndpoint.Normalize(input));
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("[::1]")]
    [InlineData("device.local")]
    public void Normalize_ValidatesDefaultPortForEveryAddressFamily(string input)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AdbEndpoint.Normalize(input, 0));
    }
}
