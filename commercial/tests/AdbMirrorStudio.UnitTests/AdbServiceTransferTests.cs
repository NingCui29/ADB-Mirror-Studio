using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Infrastructure.Adb;

namespace AdbMirrorStudio.UnitTests;

public sealed class AdbServiceTransferTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"adb-mirror-tests-{Guid.NewGuid():N}");

    public AdbServiceTransferTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task InstallApkAsync_PassesPathAsSingleArgument()
    {
        var adbPath = CreateFile("adb.exe");
        var apkPath = CreateFile("my app.apk");
        var runner = new CapturingRunner("Success");
        var service = new AdbService(runner, adbPath);

        var output = await service.InstallApkAsync("device-1", apkPath);

        Assert.Equal("Success", output);
        Assert.Equal(["-s", "device-1", "install", "-r", Path.GetFullPath(apkPath)], runner.LastRequest!.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(3), runner.LastRequest.Timeout);
    }

    [Fact]
    public async Task PushFileAsync_BuildsDownloadDestination()
    {
        var adbPath = CreateFile("adb.exe");
        var localPath = CreateFile("季度 报告.pdf");
        var runner = new CapturingRunner("1 file pushed");
        var service = new AdbService(runner, adbPath);

        await service.PushFileAsync("device-2", localPath);

        Assert.Equal(["-s", "device-2", "push", Path.GetFullPath(localPath), "/sdcard/Download/季度 报告.pdf"], runner.LastRequest!.Arguments);
        Assert.Equal(TimeSpan.FromMinutes(5), runner.LastRequest.Timeout);
    }

    [Fact]
    public async Task InstallApkAsync_RejectsMissingFileBeforeStartingProcess()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new CapturingRunner("unused");
        var service = new AdbService(runner, adbPath);

        await Assert.ThrowsAsync<FileNotFoundException>(() => service.InstallApkAsync("device-1", Path.Combine(_directory, "missing.apk")));

        Assert.Null(runner.LastRequest);
    }

    [Fact]
    public async Task EnableTcpIpAsync_ValidatesAndPassesPort()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new CapturingRunner("restarting in TCP mode port: 4321");
        var service = new AdbService(runner, adbPath);

        await service.EnableTcpIpAsync("usb-device", 4321);

        Assert.Equal(["-s", "usb-device", "tcpip", "4321"], runner.LastRequest!.Arguments);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.EnableTcpIpAsync("usb-device", 70000));
    }

    [Fact]
    public async Task GetPerformanceCountersAsync_UsesSelectedDeviceAndReadOnlyShellProbe()
    {
        var runner = new CapturingRunner("cpu 1 2 3 4 5 6 7 8\nGPU_PATH=/sys/class/devfreq/fb000000.gpu/load\nGPU_VALUE=42@1000000000Hz\n");
        var service = new AdbService(runner, CreateFile("adb.exe"));

        var counters = await service.GetPerformanceCountersAsync("device-2");

        Assert.Equal(42, counters.GpuUsagePercent);
        Assert.Equal(["-s", "device-2", "shell"], runner.LastRequest!.Arguments.Take(3));
        Assert.Contains("/proc/stat", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.Contains("/proc/meminfo", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.Contains("/sys/class/devfreq/*gpu*/load", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.Contains("/sys/class/thermal/thermal_zone*", runner.LastRequest.Arguments[3], StringComparison.Ordinal);
        Assert.Equal(TimeSpan.FromSeconds(8), runner.LastRequest.Timeout);
    }

    [Fact]
    public async Task PullFileAsync_UsesRemoteAbsolutePathAndLocalDirectory()
    {
        var adbPath = CreateFile("adb.exe");
        var destination = Path.Combine(_directory, "downloads");
        var runner = new CapturingRunner("1 file pulled");
        var service = new AdbService(runner, adbPath);

        await service.PullFileAsync("device", "/sdcard/Download/report.pdf", destination);

        Assert.Equal(["-s", "device", "pull", "/sdcard/Download/report.pdf"], runner.LastRequest!.Arguments.Take(4));
        Assert.Equal(Path.GetFullPath(destination), Path.GetDirectoryName(runner.LastRequest.Arguments[^1]));
        Assert.Equal("downloaded file", await File.ReadAllTextAsync(Path.Combine(destination, "report.pdf")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PullFileAsync("device", "relative.txt", destination));
    }

    [Fact]
    public async Task CaptureScreenshotAsync_CapturesPullsAndRemovesTemporaryFile()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new CapturingRunner("ok");
        var service = new AdbService(runner, adbPath);
        var destination = Path.Combine(_directory, "screen shot.png");

        var result = await service.CaptureScreenshotAsync("device", destination);

        Assert.Equal(Path.GetFullPath(destination), result);
        Assert.Equal(3, runner.Requests.Count);
        Assert.Equal("screencap", runner.Requests[0].Arguments[3]);
        Assert.Equal("pull", runner.Requests[1].Arguments[2]);
        Assert.Equal("rm", runner.Requests[2].Arguments[3]);
        Assert.Equal("downloaded file", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(runner.Requests[1].Arguments[^1]));
    }

    [Fact]
    public async Task CaptureScreenshotAsync_RemovesTemporaryFileWhenCaptureFails()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new SequenceRunner(
            new CommandResult(1, string.Empty, "capture failed", TimeSpan.Zero, false, false),
            new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, false));
        var service = new AdbService(runner, adbPath);

        await Assert.ThrowsAsync<AdbCommandException>(() =>
            service.CaptureScreenshotAsync("device", Path.Combine(_directory, "failed.png")));

        Assert.Equal(2, runner.Requests.Count);
        Assert.Equal("screencap", runner.Requests[0].Arguments[3]);
        Assert.Equal("rm", runner.Requests[1].Arguments[3]);
    }

    [Fact]
    public async Task GetDeviceDetailsAsync_PrefersOverrideResolution()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new SequenceRunner(
            Success("15"),
            Success("35"),
            Success("Physical size: 1440x3200\nOverride size: 1080x2400"),
            Success("level: 88\nstatus: 2"),
            Success("Filesystem Size Used Avail Use% Mounted on\n/data 100G 40G 60G 40% /data"));
        var service = new AdbService(runner, adbPath);

        var details = await service.GetDeviceDetailsAsync("device");

        Assert.Equal("1080x2400", details.Resolution);
        Assert.Equal(88, details.BatteryLevel);
    }

    [Fact]
    public async Task GetDeviceDetailsAsync_CollectsIndependentFieldsConcurrently()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new ConcurrentDetailsRunner();
        var service = new AdbService(runner, adbPath);

        await service.GetDeviceDetailsAsync("device");

        Assert.True(runner.MaxConcurrency > 1);
        Assert.Equal(5, runner.RequestCount);
    }

    [Fact]
    public async Task GetLogcatSnapshotAsync_UsesBoundedLineCount()
    {
        var adbPath = CreateFile("adb.exe");
        var runner = new CapturingRunner("log line");
        var service = new AdbService(runner, adbPath);

        var output = await service.GetLogcatSnapshotAsync("device", 750);

        Assert.Equal("log line", output);
        Assert.Equal(["-s", "device", "logcat", "-d", "-t", "750"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public async Task SendKeyEventAsync_UsesValidatedKeyCode()
    {
        var runner = new CapturingRunner(string.Empty);
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await service.SendKeyEventAsync("device", 187);

        Assert.Equal(["-s", "device", "shell", "input", "keyevent", "187"], runner.LastRequest!.Arguments);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SendKeyEventAsync("device", 1000));
    }

    [Fact]
    public async Task GetInstalledAppsAsync_ParsesAndSortsUserPackages()
    {
        var runner = new CapturingRunner("package:com.zeta.app\npackage:com.alpha.app\n");
        var service = new AdbService(runner, CreateFile("adb.exe"));

        var apps = await service.GetInstalledAppsAsync("device");

        Assert.Equal(["com.alpha.app", "com.zeta.app"], apps.Select(app => app.PackageName));
        Assert.Equal(["-s", "device", "shell", "pm", "list", "packages", "-3"], runner.LastRequest!.Arguments);
    }

    [Fact]
    public async Task AppActions_PassPackageAsSingleValidatedArgument()
    {
        var runner = new CapturingRunner("Success");
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await service.LaunchAppAsync("device", "com.example.app");
        Assert.Contains("com.example.app", runner.LastRequest!.Arguments);
        await service.ForceStopAppAsync("device", "com.example.app");
        Assert.Equal(["-s", "device", "shell", "am", "force-stop", "com.example.app"], runner.LastRequest!.Arguments);
        await service.UninstallAppAsync("device", "com.example.app");
        Assert.Equal(["-s", "device", "uninstall", "com.example.app"], runner.LastRequest!.Arguments);
        await Assert.ThrowsAsync<ArgumentException>(() => service.LaunchAppAsync("device", "bad;package"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UninstallAppAsync("device", ".bad.package"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.UninstallAppAsync("device", "bad..package"));
    }

    [Fact]
    public async Task RunShellCommandAsync_TargetsSelectedDeviceWithoutStartingWindowsShell()
    {
        var runner = new CapturingRunner("Pixel 9\n");
        var adbPath = CreateFile("adb.exe");
        var service = new AdbService(runner, adbPath);

        var output = await service.RunShellCommandAsync("device-2", "getprop ro.product.model");

        Assert.Equal("Pixel 9", output);
        Assert.Equal(adbPath, runner.LastRequest!.FileName);
        Assert.Equal(["-s", "device-2", "shell", "getprop ro.product.model"], runner.LastRequest.Arguments);
        Assert.True(runner.LastRequest.SensitiveArguments);
        Assert.DoesNotContain("cmd.exe", runner.LastRequest.FileName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("")]
    [InlineData("getprop\nreboot")]
    public async Task RunShellCommandAsync_RejectsEmptyOrMultilineCommands(string command)
    {
        var runner = new CapturingRunner(string.Empty);
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await Assert.ThrowsAsync<ArgumentException>(() => service.RunShellCommandAsync("device", command));
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("printf '%s' 'hello world'")]
    [InlineData("printf '%s' \"a'b\" | wc -c")]
    [InlineData("getprop ro.product.model && getprop ro.build.version.sdk")]
    public async Task RunShellCommandAsync_PreservesCompleteRemoteShellExpression(string command)
    {
        var runner = new CapturingRunner(string.Empty);
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await service.RunShellCommandAsync("device", command);

        // ADB joins everything after 'shell' with spaces before sending it to Android.
        Assert.Equal(command, string.Join(' ', runner.LastRequest!.Arguments.Skip(3)));
    }

    [Theory]
    [InlineData("failed to connect to '192.0.2.1:5555': Connection refused")]
    [InlineData("cannot connect to 192.0.2.1:5555")]
    [InlineData("failed to authenticate to 192.0.2.1:5555")]
    [InlineData("")]
    public async Task ConnectAsync_RejectsFailureRepliesEvenWithZeroExitCode(string output)
    {
        var runner = new CapturingRunner(output);
        var service = new AdbService(runner, CreateFile("adb.exe"));

        var error = await Assert.ThrowsAsync<AdbCommandException>(() => service.ConnectAsync("192.0.2.1"));

        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData("connected to 192.0.2.1:5555")]
    [InlineData("already connected to 192.0.2.1:5555")]
    public async Task ConnectAsync_AcceptsConfirmedConnections(string output)
    {
        var runner = new CapturingRunner(output);
        var service = new AdbService(runner, CreateFile("adb.exe"));

        Assert.Equal(output, await service.ConnectAsync("192.0.2.1"));
    }

    [Fact]
    public async Task PairAsync_RequiresAcknowledgementAndKeepsPairingCodeSensitive()
    {
        var runner = new CapturingRunner("Failed to pair: wrong password");
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await Assert.ThrowsAsync<AdbCommandException>(() => service.PairAsync("192.0.2.1:40000", "123456"));
        Assert.True(runner.LastRequest!.SensitiveArguments);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PairAsync("192.0.2.1:40000", "123:45"));
        Assert.Single(runner.Requests);
    }

    [Fact]
    public async Task GetInstalledAppsAsync_ClassifiesSystemAndUserPackagesIndependently()
    {
        var runner = new SequenceRunner(
            Success("package:com.user.app\npackage:com.system.app\npackage:com.user.app\npackage:\n"),
            Success("package:com.system.app\n"));
        var service = new AdbService(runner, CreateFile("adb.exe"));

        var apps = await service.GetInstalledAppsAsync("device", includeSystemApps: true);

        Assert.Equal(2, apps.Count);
        Assert.True(apps.Single(app => app.PackageName == "com.system.app").IsSystemApp);
        Assert.False(apps.Single(app => app.PackageName == "com.user.app").IsSystemApp);
        Assert.Equal(["-s", "device", "shell", "pm", "list", "packages", "-s"], runner.Requests[1].Arguments);
    }

    [Fact]
    public async Task IsOnlineAsync_PropagatesCancellationInsteadOfReportingOffline()
    {
        var runner = new SequenceRunner(new CommandResult(-1, string.Empty, string.Empty, TimeSpan.Zero, false, true));
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.IsOnlineAsync("device"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.IsOnlineAsync(" "));
    }

    [Fact]
    public async Task FailedTransfer_PreservesStderrAlongsideStdoutProgress()
    {
        var runner = new SequenceRunner(new CommandResult(1, "[ 20%] file.txt", "adb: error: remote Permission denied", TimeSpan.Zero, false, false));
        var service = new AdbService(runner, CreateFile("adb.exe"));

        var exception = await Assert.ThrowsAsync<AdbCommandException>(() => service.PushFileAsync("device", CreateFile("file.txt")));

        Assert.StartsWith("adb: error: remote Permission denied", exception.Message);
        Assert.Contains("[ 20%] file.txt", exception.Message);
    }

    [Fact]
    public async Task PushAndPull_PreserveSpecialCharactersAsSyncProtocolPaths()
    {
        var runner = new CapturingRunner("Success");
        var service = new AdbService(runner, CreateFile("adb.exe"));
        var local = CreateFile("报告 & 'final'.txt");

        await service.PushFileAsync("device", local, "/sdcard/my files & docs/");
        Assert.Equal("/sdcard/my files & docs/报告 & 'final'.txt", runner.LastRequest!.Arguments[^1]);
        await service.PullFileAsync("device", "/sdcard/my files & docs/报告 & 'final'.txt", _directory);
        Assert.Equal("/sdcard/my files & docs/报告 & 'final'.txt", runner.LastRequest!.Arguments[^2]);
        Assert.Equal("downloaded file", await File.ReadAllTextAsync(Path.Combine(_directory, "报告 & 'final'.txt")));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PushFileAsync("device", local, "relative/path"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.PushFileAsync("device", local, "/sdcard/new\nline"));
    }

    [Fact]
    public async Task CaptureScreenshotAsync_PreservesExistingFileAndCleansPartialDownloadOnFailure()
    {
        var destination = CreateFile("previous.png");
        await File.WriteAllTextAsync(destination, "previous screenshot");
        var runner = new FailingScreenshotRunner();
        var service = new AdbService(runner, CreateFile("adb.exe"));

        await Assert.ThrowsAsync<AdbCommandException>(() => service.CaptureScreenshotAsync("device", destination));

        Assert.Equal("previous screenshot", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(runner.PartialPath));
        Assert.True(runner.RemoteFileRemoved);
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static CommandResult Success(string output) =>
        new(0, output, string.Empty, TimeSpan.Zero, false, false);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class CapturingRunner(string output) : ICommandRunner
    {
        public CommandRequest? LastRequest { get; private set; }
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            Requests.Add(request);
            if (request.Arguments.Count > 3 && request.Arguments[2] == "shell"
                && request.Arguments[3].StartsWith("if [ -d ", StringComparison.Ordinal))
            {
                return Task.FromResult(Success("file"));
            }
            if (request.Arguments.Count > 4 && request.Arguments[2] == "pull")
            {
                File.WriteAllText(request.Arguments[^1], "downloaded file");
            }
            return Task.FromResult(new CommandResult(0, output, string.Empty, TimeSpan.Zero, false, false));
        }
    }

    private sealed class FailingScreenshotRunner : ICommandRunner
    {
        public string? PartialPath { get; private set; }
        public bool RemoteFileRemoved { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments[2] == "pull")
            {
                PartialPath = request.Arguments[^1];
                File.WriteAllText(PartialPath, "partial png");
                return Task.FromResult(new CommandResult(1, string.Empty, "device disconnected", TimeSpan.Zero, false, false));
            }
            if (request.Arguments[3] == "rm") RemoteFileRemoved = true;
            return Task.FromResult(Success(string.Empty));
        }
    }

    private sealed class SequenceRunner(params CommandResult[] results) : ICommandRunner
    {
        private readonly Queue<CommandResult> _results = new(results);
        public List<CommandRequest> Requests { get; } = [];

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class ConcurrentDetailsRunner : ICommandRunner
    {
        private int _active;
        private int _maxConcurrency;
        private int _requestCount;
        public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);
        public int RequestCount => Volatile.Read(ref _requestCount);

        public async Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _requestCount);
            var active = Interlocked.Increment(ref _active);
            while (true)
            {
                var maximum = Volatile.Read(ref _maxConcurrency);
                if (active <= maximum || Interlocked.CompareExchange(ref _maxConcurrency, active, maximum) == maximum) break;
            }

            try
            {
                await Task.Delay(50, cancellationToken);
                return Success(string.Empty);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }
}
