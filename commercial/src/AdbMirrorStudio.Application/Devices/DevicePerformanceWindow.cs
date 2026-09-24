using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Application.Devices;

public sealed record DevicePerformanceReading(
    double? CpuUsagePercent,
    double? CpuAveragePercent,
    double? GpuUsagePercent,
    double? GpuAveragePercent,
    double? MemoryUsagePercent,
    double? MemoryAveragePercent,
    int CpuSampleCount,
    int GpuSampleCount,
    int MemorySampleCount,
    DateTimeOffset SampledAt,
    string? GpuSource);

public sealed class DevicePerformanceWindow(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset Time, double? Cpu, double? Gpu, double? Memory)> _samples = new();
    private DevicePerformanceCounters? _previous;

    public DevicePerformanceReading Add(DevicePerformanceCounters counters, DateTimeOffset sampledAt)
    {
        double? cpu = null;
        if (_previous is not null)
        {
            var totalDelta = counters.CpuTotalTicks - _previous.CpuTotalTicks;
            var idleDelta = counters.CpuIdleTicks - _previous.CpuIdleTicks;
            if (totalDelta > 0 && idleDelta >= 0 && idleDelta <= totalDelta)
            {
                cpu = Math.Clamp(100d * (totalDelta - idleDelta) / totalDelta, 0d, 100d);
            }
        }
        _previous = counters;

        double? memory = null;
        if (counters.MemoryTotalKilobytes is { } total && total > 0
            && counters.MemoryAvailableKilobytes is { } available
            && available >= 0 && available <= total)
        {
            memory = 100d * (total - available) / total;
        }

        _samples.Enqueue((sampledAt, cpu, counters.GpuUsagePercent, memory));
        while (_samples.Count > 0 && sampledAt - _samples.Peek().Time > window)
            _samples.Dequeue();

        var cpuSamples = _samples.Where(sample => sample.Cpu.HasValue).Select(sample => sample.Cpu!.Value).ToArray();
        var gpuSamples = _samples.Where(sample => sample.Gpu.HasValue).Select(sample => sample.Gpu!.Value).ToArray();
        var memorySamples = _samples.Where(sample => sample.Memory.HasValue).Select(sample => sample.Memory!.Value).ToArray();
        return new DevicePerformanceReading(
            cpu,
            cpuSamples.Length == 0 ? null : cpuSamples.Average(),
            counters.GpuUsagePercent,
            gpuSamples.Length == 0 ? null : gpuSamples.Average(),
            memory,
            memorySamples.Length == 0 ? null : memorySamples.Average(),
            cpuSamples.Length,
            gpuSamples.Length,
            memorySamples.Length,
            sampledAt,
            counters.GpuSource);
    }
}
