using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Infrastructure.Processes;

namespace AdbMirrorStudio.UnitTests;

public sealed class ProcessCommandRunnerTests
{
    [Fact]
    public async Task RunAsync_TerminatesProcessTreeAfterTimeout()
    {
        var runner = new ProcessCommandRunner();
        var request = LongRunningCommand(TimeSpan.FromMilliseconds(150));

        var result = await runner.RunAsync(request);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_TerminatesProcessTreeAfterCancellation()
    {
        var runner = new ProcessCommandRunner();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var result = await runner.RunAsync(LongRunningCommand(TimeSpan.FromSeconds(10)), cancellation.Token);

        Assert.False(result.TimedOut);
        Assert.True(result.Cancelled);
    }

    [Fact]
    public async Task RunAsync_PreCancelledRequestDoesNotStartAProcess()
    {
        var runner = new ProcessCommandRunner();
        var missingExecutable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(
            new CommandRequest(missingExecutable, []), new CancellationToken(true)));
    }

    [Fact]
    public async Task RunAsync_InvalidTimeoutIsRejectedBeforeStartingAProcess()
    {
        var runner = new ProcessCommandRunner();
        var missingExecutable = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runner.RunAsync(
            new CommandRequest(missingExecutable, [], Timeout: TimeSpan.FromMilliseconds(-2))));
    }

    [Fact]
    public async Task RunAsync_TimeoutAlsoBoundsInheritedOutputPipeDrain()
    {
        var runner = new ProcessCommandRunner();
        var command = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var request = new CommandRequest(command,
            ["/d", "/c", "start /b ping 127.0.0.1 -n 5"], Timeout: TimeSpan.FromMilliseconds(300));

        var result = await runner.RunAsync(request).WaitAsync(TimeSpan.FromSeconds(3));

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
    }

    private static CommandRequest LongRunningCommand(TimeSpan timeout)
    {
        var command = Environment.GetEnvironmentVariable("ComSpec")
            ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return new CommandRequest(
            command,
            ["/d", "/c", "ping 127.0.0.1 -n 6 > nul"],
            Timeout: timeout);
    }
}
