using System.Collections.Concurrent;
using System.Diagnostics;
using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Domain.Mirroring;
using AdbMirrorStudio.Infrastructure.Adb;
using AdbMirrorStudio.Infrastructure.Scrcpy;

namespace AdbMirrorStudio.UnitTests;

// These tests run disposable local command processes, never ADB or scrcpy devices.
public sealed class MirrorSessionLifecycleTests
{
    private static readonly MirrorProfile ExplicitProfile = MirrorProfile.Performance with { VideoCodec = "h264" };

    [Theory]
    [InlineData(23)]
    [InlineData(0)]
    public async Task StartAsync_ImmediateExitThrowsAndReleasesSession(int exitCode)
    {
        await using var fixture = new SessionFixture($"echo ERROR: test encoder failed 1>&2 & exit /b {exitCode}");
        var states = new ConcurrentQueue<MirrorSessionState>();
        fixture.Manager.SessionChanged += (_, session) => states.Enqueue(session.State);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.StartAsync("test-device", ExplicitProfile));

        Assert.Contains(exitCode == 0 ? "立即退出" : "test encoder failed", exception.Message);
        Assert.DoesNotContain(MirrorSessionState.Running, states);
        Assert.Empty(fixture.Manager.ActiveSessions);
    }

    [Fact]
    public async Task StartAsync_CancelledDuringStartupTerminatesProcessBeforeReturning()
    {
        await using var fixture = new SessionFixture();
        using var cancellation = new CancellationTokenSource();
        int? processId = null;
        fixture.Manager.SessionChanged += (_, session) =>
        {
            if (session.State != MirrorSessionState.Starting) return;
            processId = session.ProcessId;
            cancellation.Cancel();
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Manager.StartAsync("test-device", ExplicitProfile, cancellationToken: cancellation.Token));

        Assert.NotNull(processId);
        Assert.False(IsProcessRunning(processId.Value));
        Assert.Empty(fixture.Manager.ActiveSessions);
    }

    [Fact]
    public async Task StopAsync_CompletesCleanupEvenIfTokenIsCancelledAfterShutdownBegins()
    {
        await using var fixture = new SessionFixture();
        var started = await fixture.Manager.StartAsync("test-device", ExplicitProfile);
        using var cancellation = new CancellationTokenSource();
        fixture.Manager.SessionChanged += (_, session) =>
        {
            if (session.State == MirrorSessionState.Stopping) cancellation.Cancel();
        };

        await fixture.Manager.StopAsync("test-device", cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Empty(fixture.Manager.ActiveSessions);
        Assert.False(IsProcessRunning(started.ProcessId!.Value));
    }

    [Fact]
    public async Task DisposeAsync_WaitsForInFlightStartupAndSharesCompletion()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adbRunner = new StubRunner(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;
            return Success("device");
        });
        await using var fixture = new SessionFixture(adbRunner: adbRunner);
        var starting = fixture.Manager.StartAsync("test-device", ExplicitProfile);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var firstDispose = fixture.Manager.DisposeAsync().AsTask();
        var secondDispose = fixture.Manager.DisposeAsync().AsTask();
        Assert.Same(firstDispose, secondDispose);
        Assert.False(firstDispose.IsCompleted);
        release.TrySetResult();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => starting);
        await firstDispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fixture.ProcessCount);
        Assert.Empty(fixture.Manager.ActiveSessions);
    }

    [Fact]
    public async Task StartAsync_ChangedAudioOrCodecIsNotTreatedAsExistingConfiguration()
    {
        await using var fixture = new SessionFixture();
        var started = await fixture.Manager.StartAsync("test-device", ExplicitProfile);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.StartAsync(
            "test-device", ExplicitProfile with { AudioEnabled = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.StartAsync(
            "test-device", ExplicitProfile with { VideoCodec = "h265" }));
        var repeated = await fixture.Manager.StartAsync("test-device", ExplicitProfile);

        Assert.Equal(started.Id, repeated.Id);
        Assert.Equal(1, fixture.ProcessCount);
    }

    [Fact]
    public async Task RestartAsync_UsesNewProfilePreferenceWithCachedEncoderCapabilities()
    {
        var probes = 0;
        var probeRunner = new StubRunner((_, _) =>
        {
            Interlocked.Increment(ref probes);
            return Task.FromResult(Success("--video-codec=h264 --video-codec=h265"));
        });
        await using var fixture = new SessionFixture(probeRunner: probeRunner);
        var balanced = await fixture.Manager.StartAsync("test-device", MirrorProfile.Balanced);

        var quality = await fixture.Manager.RestartAsync("test-device", MirrorProfile.Quality);

        Assert.Equal("h264", balanced.VideoCodec);
        Assert.Equal("h265", quality.VideoCodec);
        Assert.Equal(1, probes);
        Assert.Single(fixture.Manager.ActiveSessions);
        Assert.False(IsProcessRunning(balanced.ProcessId!.Value));
    }

    [Fact]
    public async Task RestartAsync_RetriesEncoderProbeAfterUnsuccessfulProbe()
    {
        var probes = 0;
        var probeRunner = new StubRunner((_, _) => Task.FromResult(
            Interlocked.Increment(ref probes) == 1
                ? new CommandResult(1, "h265", "ERROR: device disconnected", TimeSpan.Zero, false, false)
                : Success("h264 h265")));
        await using var fixture = new SessionFixture(probeRunner: probeRunner);
        var first = await fixture.Manager.StartAsync("test-device", MirrorProfile.Quality);

        var second = await fixture.Manager.RestartAsync("test-device", MirrorProfile.Quality);

        Assert.Equal("h264", first.VideoCodec);
        Assert.Equal("h265", second.VideoCodec);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task RestartAsync_ConcurrentStartCannotReplaceTheRequestedRestart()
    {
        await using var fixture = new SessionFixture();
        await fixture.Manager.StartAsync("test-device", ExplicitProfile);
        Task<MirrorSession>? competingStart = null;
        fixture.Manager.SessionChanged += (_, session) =>
        {
            if (session.State == MirrorSessionState.Stopping && competingStart is null)
            {
                competingStart = fixture.Manager.StartAsync("test-device", ExplicitProfile);
            }
        };

        var restarted = await fixture.Manager.RestartAsync("test-device", ExplicitProfile with { VideoCodec = "h265" });

        Assert.NotNull(competingStart);
        await Assert.ThrowsAsync<InvalidOperationException>(() => competingStart);
        Assert.Equal("h265", restarted.VideoCodec);
        Assert.Equal(restarted.Id, Assert.Single(fixture.Manager.ActiveSessions).Id);
        Assert.Equal(2, fixture.ProcessCount);
    }

    [Fact]
    public async Task RestartAsync_FailedReplacementRestoresOriginalMirror()
    {
        await using var fixture = new SessionFixture(commandForLaunch: launch => launch == 2
            ? "echo ERROR: replacement encoder failed 1>&2 & exit /b 23"
            : "ping 127.0.0.1 -n 30 > nul");
        var original = await fixture.Manager.StartAsync("test-device", ExplicitProfile);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Manager.RestartAsync("test-device", ExplicitProfile with { VideoCodec = "h265" }));

        Assert.Contains("已恢复原镜像", exception.Message);
        var restored = Assert.Single(fixture.Manager.ActiveSessions);
        Assert.Equal(MirrorSessionState.Running, restored.State);
        Assert.Equal("h264", restored.VideoCodec);
        Assert.NotEqual(original.Id, restored.Id);
        Assert.False(IsProcessRunning(original.ProcessId!.Value));
        Assert.Equal(3, fixture.ProcessCount);
    }

    [Fact]
    public async Task StartAsync_CancelledEncoderProbeDoesNotLaunchMirror()
    {
        var probeRunner = new StubRunner((_, _) => Task.FromResult(
            new CommandResult(-1, "", "", TimeSpan.Zero, false, true)));
        await using var fixture = new SessionFixture(probeRunner: probeRunner);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.Manager.StartAsync("test-device", MirrorProfile.Balanced));

        Assert.Equal(0, fixture.ProcessCount);
        Assert.Empty(fixture.Manager.ActiveSessions);
    }

    [Fact]
    public async Task SessionChanged_ThrowingSubscriberCannotLeakProcessOrSkipOtherSubscribers()
    {
        await using var fixture = new SessionFixture();
        var states = new ConcurrentQueue<MirrorSessionState>();
        fixture.Manager.SessionChanged += (_, _) => throw new InvalidOperationException("test subscriber failure");
        fixture.Manager.SessionChanged += (_, session) => states.Enqueue(session.State);
        var started = await fixture.Manager.StartAsync("test-device", ExplicitProfile);

        await fixture.Manager.StopAsync("test-device");

        Assert.Contains(MirrorSessionState.Running, states);
        Assert.Contains(MirrorSessionState.Exited, states);
        Assert.Empty(fixture.Manager.ActiveSessions);
        Assert.False(IsProcessRunning(started.ProcessId!.Value));
    }

    [Fact]
    public async Task ArrangeWindowsAsync_ProcessExitingWhileWaitingForWindowDoesNotThrow()
    {
        await using var fixture = new SessionFixture("ping 127.0.0.1 -n 3 > nul");
        await fixture.Manager.StartAsync("test-device", ExplicitProfile);

        var count = await fixture.Manager.ArrangeWindowsAsync(MirrorWindowLayout.Grid);

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task StartAsync_ImmediateFailureReleasesRecordingForAnotherDevice()
    {
        var directory = Directory.CreateTempSubdirectory("mirror-session-tests-");
        try
        {
            var path = Path.Combine(directory.FullName, "recording.mp4");
            await using var fixture = new SessionFixture("echo ERROR: test encoder failed 1>&2 & exit /b 23");
            var profile = ExplicitProfile with { RecordPath = path };
            await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Manager.StartAsync("first-device", profile));

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => fixture.Manager.StartAsync("second-device", profile));

            Assert.Contains("test encoder failed", exception.Message);
            Assert.Equal(2, fixture.ProcessCount);
            Assert.Empty(fixture.Manager.ActiveSessions);
            Assert.False(File.Exists(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task StopAsync_ForcedRecordingTerminationReportsPotentiallyIncompleteFile()
    {
        var directory = Directory.CreateTempSubdirectory("mirror-session-tests-");
        try
        {
            await using var fixture = new SessionFixture();
            var sessions = new ConcurrentQueue<MirrorSession>();
            fixture.Manager.SessionChanged += (_, session) => sessions.Enqueue(session);
            var profile = ExplicitProfile with { RecordPath = Path.Combine(directory.FullName, "recording.mp4") };
            var started = await fixture.Manager.StartAsync("test-device", profile);

            await fixture.Manager.StopAsync("test-device");

            var final = sessions.Last();
            Assert.Equal(MirrorSessionState.Failed, final.State);
            Assert.Contains("录屏文件可能不完整", final.Error);
            Assert.Empty(fixture.Manager.ActiveSessions);
            Assert.False(IsProcessRunning(started.ProcessId!.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAsync_NonzeroRecordingExitAfterStopRequestRemainsFailed(bool writesError)
    {
        var directory = Directory.CreateTempSubdirectory("mirror-session-tests-");
        try
        {
            var command = "ping 127.0.0.1 -n 3 > nul & "
                + (writesError ? "echo ERROR: recording muxer write failed 1>&2 & " : "")
                + "exit /b 23";
            await using var fixture = new SessionFixture(command);
            var sessions = new ConcurrentQueue<MirrorSession>();
            fixture.Manager.SessionChanged += (_, session) => sessions.Enqueue(session);
            var profile = ExplicitProfile with { RecordPath = Path.Combine(directory.FullName, "recording.mp4") };
            var started = await fixture.Manager.StartAsync("test-device", profile);

            // The command exits itself while Stop waits for its recording window;
            // it is not force-killed, and StopRequested is already set.
            await fixture.Manager.StopAsync("test-device");

            Assert.Contains(sessions, session => session.State == MirrorSessionState.Stopping);
            var final = sessions.Last();
            Assert.Equal(MirrorSessionState.Failed, final.State);
            Assert.Equal(23, final.ExitCode);
            Assert.Equal(writesError ? "ERROR: recording muxer write failed" : "scrcpy 异常退出（退出码 23）。", final.Error);
            Assert.Empty(fixture.Manager.ActiveSessions);
            Assert.False(IsProcessRunning(started.ProcessId!.Value));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static CommandResult Success(string output) => new(0, output, "", TimeSpan.Zero, false, false);

    [Fact]
    public async Task StartStopDispose_ParentExitWithInheritedPipesHasBoundedDrainAndPreservesError()
    {
        // The disposable loopback child retains the parent's pipes for seven seconds;
        // it ends itself, without touching any device or user application.
        await using var fixture = new SessionFixture(
            "echo ERROR: parent encoder failed 1>&2 & start /b ping 127.0.0.1 -n 8 & exit /b 23");
        var starting = fixture.Manager.StartAsync("test-device", ExplicitProfile);
        var stopping = fixture.Manager.StopAsync("test-device");
        var disposing = fixture.Manager.DisposeAsync().AsTask();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => starting.WaitAsync(TimeSpan.FromSeconds(5)));
        await Task.WhenAll(stopping, disposing).WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal("ERROR: parent encoder failed", exception.Message);
        Assert.Empty(fixture.Manager.ActiveSessions);
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class StubRunner(Func<CommandRequest, CancellationToken, Task<CommandResult>> callback) : ICommandRunner
    {
        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default) =>
            callback(request, cancellationToken);
    }

    private sealed class SessionFixture : IAsyncDisposable
    {
        private int _processCount;

        public SessionFixture(
            string command = "ping 127.0.0.1 -n 30 > nul",
            ICommandRunner? adbRunner = null,
            ICommandRunner? probeRunner = null,
            Func<int, string>? commandForLaunch = null)
        {
            var commandPath = Environment.GetEnvironmentVariable("ComSpec")
                ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
            adbRunner ??= new StubRunner((_, _) => Task.FromResult(Success("device")));
            probeRunner ??= new StubRunner((_, _) => Task.FromResult(Success("h264 h265")));
            var adb = new AdbService(adbRunner, commandPath);
            Manager = new MirrorSessionManager(adb, commandPath, probeRunner, startInfo =>
            {
                var launch = Interlocked.Increment(ref _processCount);
                startInfo.ArgumentList.Clear();
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(commandForLaunch?.Invoke(launch) ?? command);
                return new Process { StartInfo = startInfo };
            });
        }

        public MirrorSessionManager Manager { get; }
        public int ProcessCount => _processCount;
        public ValueTask DisposeAsync() => Manager.DisposeAsync();
    }
}
