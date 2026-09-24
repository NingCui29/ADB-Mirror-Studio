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
        var gpuPercent = ParseUsagePercent(gpuValue);
        var ddrPath = lines.FirstOrDefault(line => line.StartsWith("DMC_PATH=", StringComparison.Ordinal))?[9..];
        var ddrValue = lines.Where(line => line.StartsWith("DMC_VALUE=", StringComparison.Ordinal))
            .Select(line => line[10..])
            .FirstOrDefault(value => ParseUsagePercent(value) is not null);
        var ddrPercent = ParseUsagePercent(ddrValue);
        var ddrFrequency = ParseFrequencyHertz(ddrValue);

        var thermalSamples = lines.Where(line => line.StartsWith("THERMAL=", StringComparison.Ordinal))
            .Select(ParseThermalSample).OfType<ThermalSample>().ToArray();
        var cpuTemperature = thermalSamples.Where(sample => IsCpuThermalType(sample.Type))
            .OrderByDescending(sample => sample.Celsius).FirstOrDefault();
        var gpuTemperature = thermalSamples.Where(sample => IsGpuThermalType(sample.Type))
            .OrderByDescending(sample => sample.Celsius).FirstOrDefault();
        var memoryTotal = ParseMemoryKilobytes(lines, "MemTotal:");
        var memoryAvailable = ParseMemoryKilobytes(lines, "MemAvailable:");
        if (memoryTotal is not > 0 || memoryAvailable is null || memoryAvailable < 0 || memoryAvailable > memoryTotal)
        {
            memoryTotal = null;
            memoryAvailable = null;
        }

        return new DevicePerformanceCounters(
            ticks.Sum(),
            checked(ticks[3] + ticks[4]),
            gpuPercent,
            gpuPercent is null ? null : gpuPath,
            cpuTemperature?.Celsius,
            cpuTemperature?.Path,
            gpuTemperature?.Celsius,
            gpuTemperature?.Path,
            memoryTotal,
            memoryAvailable,
            ddrPercent,
            ddrPercent is null ? null : ddrFrequency,
            ddrPercent is null ? null : ddrPath);
    }

    private static double? ParseUsagePercent(string? value)
    {
        if (value is null) return null;
        var rawPercent = value.Split('@', 2)[0].TrimEnd('%').Trim();
        return double.TryParse(rawPercent, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed)
            && double.IsFinite(parsed) && parsed is >= 0 and <= 100
            ? parsed
            : null;
    }

    private static long? ParseFrequencyHertz(string? value)
    {
        if (value is null) return null;
        var separator = value.IndexOf('@');
        if (separator < 0 || separator == value.Length - 1) return null;
        var rawFrequency = value[(separator + 1)..].Trim();
        if (rawFrequency.EndsWith("Hz", StringComparison.OrdinalIgnoreCase))
            rawFrequency = rawFrequency[..^2].Trim();
        return long.TryParse(rawFrequency, NumberStyles.None, CultureInfo.InvariantCulture, out var hertz) && hertz > 0
            ? hertz
            : null;
    }

    private static long? ParseMemoryKilobytes(IEnumerable<string> lines, string field)
    {
        var line = lines.FirstOrDefault(candidate => candidate.StartsWith(field, StringComparison.Ordinal));
        if (line is null) return null;
        var value = line[field.Length..].Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes)
            ? kilobytes
            : null;
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
