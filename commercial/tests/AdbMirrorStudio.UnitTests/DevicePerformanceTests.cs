using AdbMirrorStudio.Application.Devices;
using AdbMirrorStudio.Domain.Devices;
using AdbMirrorStudio.Infrastructure.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class DevicePerformanceTests
{
    [Fact]
    public void ParsesWholeDeviceCpuAndMaliGpuLoad()
    {
        var counters = DevicePerformanceParser.Parse(
            "cpu  100 2 30 200 4 5 6 0 0 0\nGPU_PATH=/sys/class/devfreq/fb000000.gpu/load\nGPU_VALUE=76@1000000000Hz\n");

        Assert.Equal(347, counters.CpuTotalTicks);
        Assert.Equal(204, counters.CpuIdleTicks);
        Assert.Equal(76, counters.GpuUsagePercent);
        Assert.Equal("/sys/class/devfreq/fb000000.gpu/load", counters.GpuSource);
    }

    [Fact]
    public void ParsesGpuPercentWithDriverWhitespace()
    {
        var counters = DevicePerformanceParser.Parse("cpu 1 2 3 4 5 6 7 8\nGPU_VALUE=42 %\n");

        Assert.Equal(42, counters.GpuUsagePercent);
    }

    [Fact]
    public void ParsesCpuHotspotAndGpuThermalZonesInMillidegrees()
    {
        var counters = DevicePerformanceParser.Parse(
            "cpu 1 2 3 4 5 6 7 8\n" +
            "THERMAL=/sys/class/thermal/thermal_zone0|soc-thermal|61923\n" +
            "THERMAL=/sys/class/thermal/thermal_zone1|bigcore0-thermal|62846\n" +
            "THERMAL=/sys/class/thermal/thermal_zone2|littlecore-thermal|61000\n" +
            "THERMAL=/sys/class/thermal/thermal_zone5|gpu-thermal|60500\n" +
            "THERMAL=/sys/class/thermal/thermal_zone7|test_battery|2600\n");

        Assert.Equal(62.846, counters.CpuTemperatureCelsius);
        Assert.Equal("/sys/class/thermal/thermal_zone1", counters.CpuTemperatureSource);
        Assert.Equal(60.5, counters.GpuTemperatureCelsius);
        Assert.Equal("/sys/class/thermal/thermal_zone5", counters.GpuTemperatureSource);
    }

    [Fact]
    public void ParsesTotalAndAvailableMemoryInKilobytes()
    {
        var counters = DevicePerformanceParser.Parse(
            "cpu 1 2 3 4 5 6 7 8\nMemTotal: 8104732 kB\nMemAvailable: 1662564 kB\n");

        Assert.Equal(8104732, counters.MemoryTotalKilobytes);
        Assert.Equal(1662564, counters.MemoryAvailableKilobytes);
    }

    [Theory]
    [InlineData("MemTotal: 1000 kB\nMemAvailable: 1200 kB")]
    [InlineData("MemTotal: invalid kB\nMemAvailable: 500 kB")]
    [InlineData("MemTotal: 1000 kB")]
    public void InvalidOrIncompleteMemoryDoesNotProduceUsage(string memoryLines)
    {
        var counters = DevicePerformanceParser.Parse("cpu 1 2 3 4 5 6 7 8\n" + memoryLines);

        Assert.Null(counters.MemoryTotalKilobytes);
        Assert.Null(counters.MemoryAvailableKilobytes);
    }

    [Fact]
    public void UnnamedOrInvalidThermalZonesDoNotBecomeCpuOrGpuTemperatures()
    {
        var counters = DevicePerformanceParser.Parse(
            "cpu 1 2 3 4 5 6 7 8\n" +
            "THERMAL=/sys/class/thermal/thermal_zone0|soc-thermal|62000\n" +
            "THERMAL=/sys/class/thermal/thermal_zone1|cpu-thermal|62\n" +
            "THERMAL=/sys/class/thermal/thermal_zone2|gpu-thermal|invalid\n");

        Assert.Null(counters.CpuTemperatureCelsius);
        Assert.Null(counters.GpuTemperatureCelsius);
    }

    [Theory]
    [InlineData("GPU_VALUE=unavailable")]
    [InlineData("GPU_VALUE=150@1000000000Hz")]
    [InlineData("")]
    public void UnsupportedGpuDoesNotProduceInventedUtilization(string gpuLine)
    {
        var counters = DevicePerformanceParser.Parse("cpu 1 2 3 4 5 6 7 8\n" + gpuLine);

        Assert.Null(counters.GpuUsagePercent);
    }

    [Fact]
    public void ComputesCpuDeltasAndRecentThirtySecondSampleMeans()
    {
        var window = new DevicePerformanceWindow(TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.UtcNow;
        var first = window.Add(new DevicePerformanceCounters(100, 50, 20, "gpu"), start);
        var second = window.Add(new DevicePerformanceCounters(200, 80, 40, "gpu"), start.AddSeconds(2));
        var third = window.Add(new DevicePerformanceCounters(300, 130, 60, "gpu"), start.AddSeconds(4));
        var later = window.Add(new DevicePerformanceCounters(400, 180, 80, "gpu"), start.AddSeconds(36));

        Assert.Null(first.CpuUsagePercent);
        Assert.Equal(70, second.CpuUsagePercent);
        Assert.Equal(70, second.CpuAveragePercent);
        Assert.Equal(50, third.CpuUsagePercent);
        Assert.Equal(60, third.CpuAveragePercent);
        Assert.Equal(40, third.GpuAveragePercent);
        Assert.Equal(50, later.CpuAveragePercent);
        Assert.Equal(80, later.GpuAveragePercent);
    }

    [Fact]
    public void ComputesCurrentAndRecentMemoryUsage()
    {
        var window = new DevicePerformanceWindow(TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.UtcNow;
        var first = window.Add(new DevicePerformanceCounters(100, 50, null, null,
            MemoryTotalKilobytes: 1000, MemoryAvailableKilobytes: 400), start);
        var second = window.Add(new DevicePerformanceCounters(200, 100, null, null,
            MemoryTotalKilobytes: 1000, MemoryAvailableKilobytes: 200), start.AddSeconds(2));
        var later = window.Add(new DevicePerformanceCounters(300, 150, null, null,
            MemoryTotalKilobytes: 1000, MemoryAvailableKilobytes: 500), start.AddSeconds(34));

        Assert.Equal(60, first.MemoryUsagePercent);
        Assert.Equal(70, second.MemoryAveragePercent);
        Assert.Equal(50, later.MemoryUsagePercent);
        Assert.Equal(50, later.MemoryAveragePercent);
        Assert.Equal(1, later.MemorySampleCount);
    }

    [Fact]
    public void CounterResetDoesNotShowNegativeOrGreaterThanHundredCpu()
    {
        var window = new DevicePerformanceWindow(TimeSpan.FromSeconds(30));
        var start = DateTimeOffset.UtcNow;
        window.Add(new DevicePerformanceCounters(1000, 500, null, null), start);

        var reset = window.Add(new DevicePerformanceCounters(20, 10, null, null), start.AddSeconds(2));
        var resumed = window.Add(new DevicePerformanceCounters(120, 60, null, null), start.AddSeconds(4));

        Assert.Null(reset.CpuUsagePercent);
        Assert.Equal(50, resumed.CpuUsagePercent);
    }
}
