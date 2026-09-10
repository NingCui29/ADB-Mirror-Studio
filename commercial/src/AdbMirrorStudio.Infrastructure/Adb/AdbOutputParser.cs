using System.Text.RegularExpressions;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Infrastructure.Adb;

public static partial class AdbOutputParser
{
    public static IReadOnlyList<DeviceInfo> ParseDevices(string output, DateTimeOffset? capturedAt = null)
    {
        var devices = new List<DeviceInfo>();
        var timestamp = capturedAt ?? DateTimeOffset.UtcNow;
        var foundHeader = false;

        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("List of devices attached", StringComparison.Ordinal))
            {
                foundHeader = true;
                continue;
            }

            if (!foundHeader || line.StartsWith('*')) continue;

            var parts = WhitespaceRegex().Split(line, 3);
            if (parts.Length < 2) continue;

            var serial = parts[0];
            var rawState = parts[1];
            var metadata = parts.Length == 3 ? ParseMetadata(parts[2]) : new Dictionary<string, string>();

            devices.Add(new DeviceInfo(
                serial,
                Humanize(metadata.GetValueOrDefault("model", "—")),
                metadata.GetValueOrDefault("product", "—"),
                MapState(rawState),
                DetectConnectionKind(serial),
                timestamp));
        }

        return devices;
    }

    public static IReadOnlyList<MdnsService> ParseMdnsServices(string output)
    {
        var services = new List<MdnsService>();
        foreach (var rawLine in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = WhitespaceRegex().Split(rawLine.Trim());
            var serviceType = parts.FirstOrDefault(part =>
                part.Contains("_adb-tls-", StringComparison.Ordinal));
            if (parts.Length < 3 || serviceType is null) continue;
            services.Add(new MdnsService(parts[0], serviceType, parts[^1]));
        }

        return services;
    }

    private static Dictionary<string, string> ParseMetadata(string metadata)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in WhitespaceRegex().Split(metadata))
        {
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1) continue;
            result[token[..separator]] = token[(separator + 1)..];
        }
        return result;
    }

    private static DeviceState MapState(string state) => state switch
    {
        "device" => DeviceState.Online,
        "offline" => DeviceState.Offline,
        "unauthorized" => DeviceState.Unauthorized,
        "recovery" => DeviceState.Recovery,
        "bootloader" => DeviceState.Bootloader,
        _ => DeviceState.Unknown
    };

    private static ConnectionKind DetectConnectionKind(string serial) =>
        serial.Contains(':', StringComparison.Ordinal)
        || serial.Contains("_adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
            ? ConnectionKind.TcpIp : ConnectionKind.Usb;

    private static string Humanize(string value) => value.Replace('_', ' ');

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
