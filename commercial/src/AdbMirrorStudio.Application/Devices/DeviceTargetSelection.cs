using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Application.Devices;

public static class DeviceTargetSelection
{
    public static string? Resolve(string? currentSerial, IEnumerable<DeviceInfo> devices, bool allowAutoSelection = true)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var onlineDevices = devices.Where(device => device.State == DeviceState.Online).ToArray();
        if (!string.IsNullOrWhiteSpace(currentSerial))
        {
            return onlineDevices.Any(device => string.Equals(device.Serial, currentSerial, StringComparison.Ordinal))
                ? currentSerial : null;
        }

        return allowAutoSelection && onlineDevices.Length == 1 ? onlineDevices[0].Serial : null;
    }
}
