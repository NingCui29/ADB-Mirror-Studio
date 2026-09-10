using System.Net;
using System.Net.Sockets;
using System.Globalization;

namespace AdbMirrorStudio.Infrastructure.Adb;

public static class AdbEndpoint
{
    public static string Normalize(string value, int defaultPort = 5555)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var input = value.Trim();
        ValidatePort(defaultPort);
        if (input.Any(character => char.IsWhiteSpace(character) || char.IsControl(character))
            || input.IndexOfAny(['/', '\\', '@', '?', '#']) >= 0)
        {
            throw InvalidEndpoint();
        }

        // An unbracketed IPv6 value denotes the whole address, without a port.
        if (!input.StartsWith('[') && IPAddress.TryParse(input, out var address)
            && address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return $"[{address}]:{defaultPort}";
        }

        if (input.StartsWith('['))
        {
            var closingBracket = input.IndexOf(']');
            if (closingBracket <= 1
                || !IPAddress.TryParse(input[1..closingBracket], out var ipv6)
                || ipv6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                throw InvalidEndpoint();
            }

            var suffix = input[(closingBracket + 1)..];
            var port = suffix.Length == 0 ? defaultPort
                : suffix.StartsWith(':') ? ParsePort(suffix[1..]) : throw InvalidEndpoint();
            return $"[{ipv6}]:{port}";
        }

        var separator = input.IndexOf(':');
        var host = separator < 0 ? input : input[..separator];
        var hostPort = separator < 0 ? defaultPort : ParsePort(input[(separator + 1)..]);
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4))
        {
            throw InvalidEndpoint();
        }
        return $"{host.ToLowerInvariant()}:{hostPort}";
    }

    private static int ParsePort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            throw InvalidEndpoint();
        ValidatePort(port);
        return port;
    }

    private static ArgumentException InvalidEndpoint() =>
        new("请输入有效的 IPv4、IPv6 或主机名地址，可在地址后指定端口。", "value");

    private static void ValidatePort(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口必须介于 1 和 65535 之间。");
    }
}
