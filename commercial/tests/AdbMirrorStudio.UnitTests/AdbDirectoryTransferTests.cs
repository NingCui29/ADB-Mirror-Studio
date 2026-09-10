using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Infrastructure.Adb;
using System.Collections.Concurrent;

namespace AdbMirrorStudio.UnitTests;

public sealed class AdbDirectoryTransferTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"adb-directory-tests-{Guid.NewGuid():N}");

    public AdbDirectoryTransferTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PullFileAsync_CreatesUnicodeDirectoryTreeAndEmptyDirectoriesWithoutAdbBasename()
    {
        const string remote = "/sdcard/中文 '&目录";
        var runner = new TransferRunner(true,
            [remote, remote + "/深层目录", remote + "/空目录"],
            [remote + "/深层目录/中文 &文件'.txt", remote + "/根文件.txt"]);
        var service = CreateService(runner);
        var downloads = Path.Combine(_directory, "本地下载");
        var destination = Path.Combine(downloads, "中文 '&目录");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "keep.txt"), "keep existing content");

        await service.PullFileAsync("device", remote, downloads);

        Assert.True(Directory.Exists(Path.Combine(destination, "空目录")));
        Assert.Equal("downloaded", await File.ReadAllTextAsync(Path.Combine(destination, "深层目录", "中文 &文件'.txt")));
        Assert.Equal("downloaded", await File.ReadAllTextAsync(Path.Combine(destination, "根文件.txt")));
        Assert.Equal("keep existing content", await File.ReadAllTextAsync(Path.Combine(destination, "keep.txt")));
        Assert.Equal(2, runner.Pulls.Count);
        Assert.All(runner.Pulls, pull => Assert.EndsWith(".download", pull.Arguments[^1]));
        Assert.Contains("'\\''", runner.Requests[0].Arguments[^1]);
        Assert.Empty(Directory.EnumerateFiles(downloads, "*.download", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("CON.txt")]
    [InlineData("NUL .txt")]
    [InlineData("CON .txt")]
    [InlineData("CONIN$")]
    [InlineData("CONOUT$.txt")]
    [InlineData("bad:name.txt")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("nested\\file.txt")]
    [InlineData("line\nbreak.txt")]
    public async Task PullFileAsync_RejectsUnrepresentableTreeBeforeCreatingDirectories(string name)
    {
        var runner = new TransferRunner(true, ["/sdcard/tree"], ["/sdcard/tree/" + name]);
        var service = CreateService(runner);
        var downloads = Path.Combine(_directory, "downloads");

        await Assert.ThrowsAsync<AdbCommandException>(() => service.PullFileAsync("device", "/sdcard/tree", downloads));

        Assert.Empty(runner.Pulls);
        Assert.False(Directory.Exists(downloads));
    }

    [Fact]
    public async Task PullFileAsync_RejectsPathsOutsideRequestedRemoteRoot()
    {
        var runner = new TransferRunner(true, ["/sdcard/tree"], ["/sdcard/tree-sibling/file.txt"]);

        await Assert.ThrowsAsync<AdbCommandException>(() => CreateService(runner).PullFileAsync("device", "/sdcard/tree", _directory));

        Assert.Empty(runner.Pulls);
    }

    [Fact]
    public async Task PullFileAsync_RejectsCaseCollisionsBeforeTransfer()
    {
        var runner = new TransferRunner(true, ["/sdcard/tree"], ["/sdcard/tree/Report.txt", "/sdcard/tree/report.txt"]);

        await Assert.ThrowsAsync<AdbCommandException>(() => CreateService(runner).PullFileAsync("device", "/sdcard/tree", _directory));

        Assert.Empty(runner.Pulls);
        Assert.False(Directory.Exists(Path.Combine(_directory, "tree")));
    }

    [Fact]
    public async Task PullFileAsync_RejectsIncompleteDirectoryStructure()
    {
        var runner = new TransferRunner(true, ["/sdcard/tree"], ["/sdcard/tree/created-later/file.txt"]);

        await Assert.ThrowsAsync<AdbCommandException>(() => CreateService(runner).PullFileAsync("device", "/sdcard/tree", _directory));

        Assert.Empty(runner.Pulls);
    }

    [Fact]
    public async Task PullFileAsync_RejectsTruncatedEnumerationInsteadOfReportingPartialSuccess()
    {
        var runner = new TransferRunner(true, ["/sdcard/tree"], []) { DirectoryOutput = "/sdcard/tree\0\n[输出已截断]" };

        await Assert.ThrowsAsync<AdbCommandException>(() => CreateService(runner).PullFileAsync("device", "/sdcard/tree", _directory));

        Assert.Empty(runner.Pulls);
        Assert.False(Directory.Exists(Path.Combine(_directory, "tree")));
    }

    [Fact]
    public async Task PullFileAsync_RejectsOversizedEnumerationBeforeWritingFiles()
    {
        var files = Enumerable.Range(0, 10000).Select(index => $"/sdcard/tree/{index}.txt").ToArray();
        var runner = new TransferRunner(true, ["/sdcard/tree"], files);

        var error = await Assert.ThrowsAsync<AdbCommandException>(() => CreateService(runner).PullFileAsync("device", "/sdcard/tree", _directory));

        Assert.Contains("10000", error.Message);
        Assert.Empty(runner.Pulls);
    }

    [Fact]
    public async Task PullFileAsync_CancellationCleansPartialFileAndStopsFurtherTransfers()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new TransferRunner(true, ["/sdcard/tree"], ["/sdcard/tree/first.txt", "/sdcard/tree/second.txt"])
        {
            AfterPull = cancellation.Cancel
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService(runner).PullFileAsync("device", "/sdcard/tree", _directory, cancellation.Token));

        Assert.Single(runner.Pulls);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_directory, "tree")));
    }

    [Fact]
    public async Task PullFileAsync_FailedUnicodeFileTransferPreservesPreviousFile()
    {
        var runner = new TransferRunner(false, [], []) { FailPull = true };
        var destination = Path.Combine(_directory, "中文 &文件'.txt");
        await File.WriteAllTextAsync(destination, "previous data");

        await Assert.ThrowsAsync<AdbCommandException>(() =>
            CreateService(runner).PullFileAsync("device", "/sdcard/中文 &文件'.txt", _directory));

        Assert.Equal("previous data", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.download"));
    }

    [Fact]
    public async Task PullFileAsync_RejectsDotSegmentsBeforeRunningAdb()
    {
        var runner = new TransferRunner(false, [], []);

        await Assert.ThrowsAsync<ArgumentException>(() => CreateService(runner).PullFileAsync("device", "/sdcard/../data", _directory));

        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transfer_CancellationWhileUiContinuationIsQueuedPreservesPreviousFile(bool screenshot)
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new QueuedPullRunner();
        var service = CreateService(runner);
        var destination = Path.Combine(_directory, screenshot ? "previous.png" : "previous.txt");
        await File.WriteAllTextAsync(destination, "previous data");
        var context = new QueuedContext();
        var previousContext = SynchronizationContext.Current;
        Task<string> transfer;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            transfer = screenshot
                ? service.CaptureScreenshotAsync("device", destination, cancellation.Token)
                : service.PullFileAsync("device", "/sdcard/previous.txt", _directory, cancellation.Token);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }

        runner.CompletePull();
        await context.HasPosted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        context.Drain();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transfer.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("previous data", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(runner.TemporaryPath));
    }

    private AdbService CreateService(ICommandRunner runner)
    {
        var path = Path.Combine(_directory, "adb.exe");
        File.WriteAllText(path, "fixture");
        return new AdbService(runner, path);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private sealed class QueuedPullRunner : ICommandRunner
    {
        private readonly TaskCompletionSource<CommandResult> _pull = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? TemporaryPath { get; private set; }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Arguments[2] == "pull")
            {
                TemporaryPath = request.Arguments[^1];
                File.WriteAllText(TemporaryPath, "new data");
                return _pull.Task;
            }
            var output = request.Arguments[^1].StartsWith("if [ -d ", StringComparison.Ordinal) ? "file" : string.Empty;
            return Task.FromResult(new CommandResult(0, output, string.Empty, TimeSpan.Zero, false, false));
        }

        public void CompletePull() => _pull.SetResult(new CommandResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, false));
    }

    private sealed class QueuedContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public TaskCompletionSource HasPosted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            HasPosted.TrySetResult();
        }

        public void Drain()
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (_callbacks.TryDequeue(out var pending)) pending.Callback(pending.State);
            }
            finally { SetSynchronizationContext(previous); }
        }
    }

    private sealed class TransferRunner(bool isDirectory, string[] directories, string[] files) : ICommandRunner
    {
        public List<CommandRequest> Requests { get; } = [];
        public List<CommandRequest> Pulls { get; } = [];
        public string? DirectoryOutput { get; init; }
        public bool FailPull { get; init; }
        public Action? AfterPull { get; init; }

        public Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (request.Arguments[2] == "pull")
            {
                Pulls.Add(request);
                Assert.False(Directory.Exists(request.Arguments[^1]));
                Assert.True(File.Exists(request.Arguments[^1]), "An existing regular target prevents adb from recursing if the source became a directory.");
                File.WriteAllText(request.Arguments[^1], "downloaded");
                AfterPull?.Invoke();
                return Task.FromResult(new CommandResult(FailPull ? 1 : 0, string.Empty,
                    FailPull ? "device disconnected" : string.Empty, TimeSpan.Zero, false, false));
            }
            var command = request.Arguments[^1];
            var output = command.StartsWith("if [ -d ", StringComparison.Ordinal) ? isDirectory ? "directory" : "file"
                : command.Contains("-type d", StringComparison.Ordinal) ? DirectoryOutput ?? ListOutput(directories)
                : ListOutput(files);
            return Task.FromResult(new CommandResult(0, output, string.Empty, TimeSpan.Zero, false, false));
        }

        private static string ListOutput(string[] paths) => string.Concat(paths.Select(path => path + '\0')) + "\0ADB_MIRROR_LIST_END\0";
    }
}
