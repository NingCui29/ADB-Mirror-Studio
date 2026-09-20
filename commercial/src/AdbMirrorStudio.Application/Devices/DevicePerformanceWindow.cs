using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Application.Devices;

public sealed record DevicePerformanceReading(
    double? CpuUsagePercent,
    double? CpuAveragePercent,
    double? GpuUsagePercent,
    double? GpuAveragePercent,
    int CpuSampleCount,
    int GpuSampleCount,
    DateTimeOffset SampledAt,
    string? GpuSource);

public sealed class DevicePerformanceWindow(TimeSpan window)
{
    private readonly Queue<(DateTimeOffset Time, double? Cpu, double? Gpu)> _samples = new();
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

        _samples.Enqueue((sampledAt, cpu, counters.GpuUsagePercent));
        while (_samples.Count > 0 && sampledAt - _samples.Peek().Time > window)
            _samples.Dequeue();

        var cpuSamples = _samples.Where(sample => sample.Cpu.HasValue).Select(sample => sample.Cpu!.Value).ToArray();
        var gpuSamples = _samples.Where(sample => sample.Gpu.HasValue).Select(sample => sample.Gpu!.Value).ToArray();
        return new DevicePerformanceReading(
            cpu,
            cpuSamples.Length == 0 ? null : cpuSamples.Average(),
            counters.GpuUsagePercent,
            gpuSamples.Length == 0 ? null : gpuSamples.Average(),
            cpuSamples.Length,
            gpuSamples.Length,
            sampledAt,
            counters.GpuSource);
    }
}
