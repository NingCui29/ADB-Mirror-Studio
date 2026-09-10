using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Application.Diagnostics;
using AdbMirrorStudio.Infrastructure.Diagnostics;

namespace AdbMirrorStudio.UnitTests;

public sealed class DiagnosticsServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdbMirrorStudio.Diagnostics", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RunAsync_ReturnsStructuredChecksWithoutThrowing()
    {
        Directory.CreateDirectory(_directory);
        var adb = Path.Combine(_directory, "adb.exe");
        var scrcpy = Path.Combine(_directory, "scrcpy.exe");
        await File.WriteAllTextAsync(adb, "test");
        await File.WriteAllTextAsync(scrcpy, "test");
        var service = new DiagnosticsService(new SuccessfulRunner(), adb, scrcpy);

        var results = await service.RunAsync();

        Assert.Equal(7, results.Count);
        Assert.Contains(results, item => item.Id == "adb-version" && item.Severity == DiagnosticSeverity.Success);
        Assert.Contains(results, item => item.Id == "mdns");
    }

    [Fact]
    public async Task RunAsync_MissingBinaries_AreErrors()
    {
        var service = new DiagnosticsService(
            new SuccessfulRunner(),
            Path.Combine(_directory, "adb.exe"),
            Path.Combine(_directory, "scrcpy.exe"));

        var results = await service.RunAsync();

        Assert.Equal(DiagnosticSeverity.Error, results.Single(item => item.Id == "adb-file").Severity);
        Assert.Equal(DiagnosticSeverity.Error, results.Single(item => item.Id == "scrcpy-version").Severity);
    }

    [Fact]
    public async Task RunAsync_EmptyBinariesAreErrorsInsteadOfClaimingFileIntegrity()
    {
        Directory.CreateDirectory(_directory);
        var adb = Path.Combine(_directory, "adb.exe");
        var scrcpy = Path.Combine(_directory, "scrcpy.exe");
        await File.WriteAllBytesAsync(adb, []);
        await File.WriteAllBytesAsync(scrcpy, []);
        var service = new DiagnosticsService(new SuccessfulRunner(), adb, scrcpy);

        var results = await service.RunAsync();

        Assert.Equal(DiagnosticSeverity.Error, results.Single(item => item.Id == "adb-file").Severity);
        Assert.Equal(DiagnosticSeverity.Error, results.Single(item => item.Id == "scrcpy-file").Severity);
    }

    [Fact]
    public async Task RunAsync_PropagatesCommandCancellation()
    {
        Directory.CreateDirectory(_directory);
        var adb = Path.Combine(_directory, "adb.exe");
        var scrcpy = Path.Combine(_directory, "scrcpy.exe");
        await File.WriteAllTextAsync(adb, "test");
        await File.WriteAllTextAsync(scrcpy, "test");
        var service = new DiagnosticsService(new CancelledRunner(), adb, scrcpy);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RunAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class SuccessfulRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(0, "version 1.0\n", string.Empty, TimeSpan.Zero, false, false));
    }

    private sealed class CancelledRunner : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CommandResult(-1, string.Empty, string.Empty, TimeSpan.Zero, false, true));
    }
}
