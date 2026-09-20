using System.Globalization;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Infrastructure.Adb;

public static class DevicePerformanceParser
{
    public static DevicePerformanceCounters Parse(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var cpuLine = lines.FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal));
        if (cpuLine is null) throw new FormatException("设备未返回 /proc/stat 的整机 CPU 计数器。");

        var fields = cpuLine.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 9) throw new FormatException("设备 CPU 计数器字段不完整。");
        var ticks = new long[8];
        for (var index = 0; index < ticks.Length; index++)
        {
            if (!long.TryParse(fields[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out ticks[index]))
                throw new FormatException("设备 CPU 计数器包含无效值。");
        }

        var gpuPath = lines.FirstOrDefault(line => line.StartsWith("GPU_PATH=", StringComparison.Ordinal))?[9..];
        var gpuValue = lines.FirstOrDefault(line => line.StartsWith("GPU_VALUE=", StringComparison.Ordinal))?[10..];
        double? gpuPercent = null;
        if (gpuValue is not null)
        {
            var rawPercent = gpuValue.Split('@', 2)[0].TrimEnd('%').Trim();
            if (double.TryParse(rawPercent, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
                && double.IsFinite(parsed) && parsed is >= 0 and <= 100)
            {
                gpuPercent = parsed;
            }
        }

        var thermalSamples = lines.Where(line => line.StartsWith("THERMAL=", StringComparison.Ordinal))
            .Select(ParseThermalSample).OfType<ThermalSample>().ToArray();
        var cpuTemperature = thermalSamples.Where(sample => IsCpuThermalType(sample.Type))
            .OrderByDescending(sample => sample.Celsius).FirstOrDefault();
        var gpuTemperature = thermalSamples.Where(sample => IsGpuThermalType(sample.Type))
            .OrderByDescending(sample => sample.Celsius).FirstOrDefault();

        return new DevicePerformanceCounters(
            ticks.Sum(),
            checked(ticks[3] + ticks[4]),
            gpuPercent,
            gpuPercent is null ? null : gpuPath,
            cpuTemperature?.Celsius,
            cpuTemperature?.Path,
            gpuTemperature?.Celsius,
            gpuTemperature?.Path);
    }

    private static ThermalSample? ParseThermalSample(string line)
    {
        var fields = line[8..].Split('|', 3);
        if (fields.Length != 3 || !fields[0].StartsWith("/sys/class/thermal/thermal_zone", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(fields[1])
            || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var millidegrees)
            || millidegrees is < 1000 or > 150000) return null;

        return new ThermalSample(fields[0], fields[1], millidegrees / 1000d);
    }

    private static bool IsCpuThermalType(string type) =>
        type.Contains("cpu", StringComparison.OrdinalIgnoreCase)
        || type.Contains("bigcore", StringComparison.OrdinalIgnoreCase)
        || type.Contains("littlecore", StringComparison.OrdinalIgnoreCase)
        || type.Contains("cluster", StringComparison.OrdinalIgnoreCase)
        || type.StartsWith("core", StringComparison.OrdinalIgnoreCase);

    private static bool IsGpuThermalType(string type) =>
        type.Contains("gpu", StringComparison.OrdinalIgnoreCase)
        || type.Contains("mali", StringComparison.OrdinalIgnoreCase)
        || type.Contains("adreno", StringComparison.OrdinalIgnoreCase);

    private sealed record ThermalSample(string Path, string Type, double Celsius);
}
