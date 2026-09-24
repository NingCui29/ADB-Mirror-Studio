namespace AdbMirrorStudio.Domain.Devices;

public enum DeviceState
{
    Unknown,
    Discovered,
    Pairing,
    Connecting,
    Online,
    Offline,
    Unauthorized,
    Recovery,
    Bootloader,
    Error
}

public enum ConnectionKind
{
    Unknown,
    Usb,
    TcpIp
}

public sealed record DeviceInfo(
    string Serial,
    string Model,
    string Product,
    DeviceState State,
    ConnectionKind ConnectionKind,
    DateTimeOffset LastSeen)
{
    public string DisplayName
    {
        get
        {
            var deviceName = Model == "—" ? "未知设备" : Model;
            return ConnectionKind == ConnectionKind.TcpIp
                ? $"{deviceName} {Serial}"
                : Model == "—" ? Serial : deviceName;
        }
    }
}

public sealed record MdnsService(string Name, string ServiceType, string Endpoint);

public sealed record DeviceSnapshot(long Version, DateTimeOffset CapturedAt, IReadOnlyList<DeviceInfo> Devices);

public sealed record DeviceDetails(
    string Serial,
    string AndroidVersion,
    string ApiLevel,
    string Resolution,
    int? BatteryLevel,
    string BatteryStatus,
    string StorageSummary);

public sealed record DevicePerformanceCounters(
    long CpuTotalTicks,
    long CpuIdleTicks,
    double? GpuUsagePercent,
    string? GpuSource,
    double? CpuTemperatureCelsius = null,
    string? CpuTemperatureSource = null,
    double? GpuTemperatureCelsius = null,
    string? GpuTemperatureSource = null,
    long? MemoryTotalKilobytes = null,
    long? MemoryAvailableKilobytes = null);

public sealed record InstalledApp(string PackageName, bool IsSystemApp = false);
