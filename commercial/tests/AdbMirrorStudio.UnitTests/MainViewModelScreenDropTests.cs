using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Fact]
    public async Task ScreenDrop_SecondDropKeepsBothTargetsAndBothOriginalPathLists()
    {
        using var files = new DropTestFiles();
        var firstApk = files.Add("first.apk");
        var firstFile = files.Add("first note.txt");
        var secondApk = files.Add("second.APK");
        var secondFile = files.Add("second note.txt");
        var enteredInstall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishInstall = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb
        {
            Devices = [Device("device-A"), Device("device-B")],
            Install = (path, _) =>
            {
                if (path != firstApk) return Task.FromResult("Success");
                enteredInstall.SetResult();
                return finishInstall.Task;
            }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = "device-A";

        string[] first = [firstApk, firstFile];
        var installing = model.RunScreenDropAsync(model.SelectedDeviceSerial, first);
        Task queued = Task.CompletedTask;
        try
        {
            await enteredInstall.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SelectedDeviceSerial = "device-B";
            string[] second = [secondApk, secondFile];
            queued = model.RunScreenDropAsync(model.SelectedDeviceSerial, second);
            first[1] = secondFile;
            second[1] = firstFile;
            model.ApkFilePath = secondApk;
            Assert.Equal([("device-A", "install", firstApk)], adb.FileOperations);
        }
        finally
        {
            finishInstall.TrySetResult("Success");
            await Task.WhenAll(installing, queued).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([
            ("device-A", "install", firstApk), ("device-A", "push", firstFile),
            ("device-B", "install", secondApk), ("device-B", "push", secondFile)
        ], adb.FileOperations);
        var batches = model.ScreenDropBatches.ToArray();
        Assert.Equal(2, batches.Length);
        Assert.Equal(["second.APK", "second note.txt"], batches[0].Items.Select(item => item.FileName));
        Assert.Contains("device-B", batches[0].DeviceLabel);
        Assert.Equal(["first.apk", "first note.txt"], batches[1].Items.Select(item => item.FileName));
        Assert.Contains("device-A", batches[1].DeviceLabel);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ScreenDrop_MixedFilesInstallAllApksThenPushFilesInStableOrderDespiteFailures()
    {
        using var files = new DropTestFiles();
        var brokenApk = files.Add("z-broken.apk");
        var workingApk = files.Add("a-working.ApK");
        var failedFile = files.Add("z-first.bin");
        var workingFile = files.Add("a-second.zip");
        var pushCount = 0;
        var adb = new FakeAdb
        {
            Devices = [Device("serial")],
            Install = (path, _) => path == brokenApk
                ? Task.FromException<string>(new AdbCommandException("INSTALL_FAILED_VERSION_DOWNGRADE"))
                : Task.FromResult("Success"),
            Push = _ => ++pushCount == 1
                ? Task.FromException<string>(new AdbCommandException("Failed to copy"))
                : Task.FromResult("1 file pushed")
        };
        using var model = Create(adb);
        await model.RefreshAsync();

        await model.RunScreenDropAsync("serial", [failedFile, brokenApk, workingFile, workingApk]);

        Assert.Equal([
            ("serial", "install", brokenApk), ("serial", "install", workingApk),
            ("serial", "push", failedFile), ("serial", "push", workingFile)
        ], adb.FileOperations);
        var batch = Assert.Single(model.ScreenDropBatches);
        Assert.Contains("成功 2", batch.Status);
        Assert.Contains("失败 2", batch.Status);
        var items = batch.Items.ToDictionary(item => item.Path);
        Assert.Equal(4, items.Count);
        Assert.Contains("INSTALL_FAILED_VERSION_DOWNGRADE", items[brokenApk].Status);
        Assert.Equal("已完成", items[workingApk].Status);
        Assert.Contains("Failed to copy", items[failedFile].Status);
        Assert.Equal("已完成", items[workingFile].Status);
        Assert.Equal("安装 APK", items[brokenApk].ActionLabel);
        Assert.Equal("推送文件", items[failedFile].ActionLabel);
        Assert.False(model.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenDrop_CancelOnlyStopsActiveBatchIncludingAnInstallReturningLate(bool useBatchCancel)
    {
        using var files = new DropTestFiles();
        var slowApk = files.Add("slow.apk");
        var skippedFile = files.Add("skipped.zip");
        var queuedFile = files.Add("queued.zip");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken captured = default;
        var adb = new FakeAdb
        {
            Devices = [Device("A"), Device("B")],
            Install = (_, token) => { captured = token; entered.SetResult(); return late.Task; }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        var first = model.RunScreenDropAsync("A", [slowApk, skippedFile]);
        Task second = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SelectedDeviceSerial = "B";
            second = model.RunScreenDropAsync("B", [queuedFile]);
            var runningBatch = model.ScreenDropBatches[1];
            var waitingBatch = model.ScreenDropBatches[0];
            Assert.True(runningBatch.CanCancel);
            Assert.False(waitingBatch.CanCancel);
            model.CancelScreenDrop(waitingBatch);
            Assert.False(captured.IsCancellationRequested);
            if (useBatchCancel) model.CancelScreenDrop(runningBatch);
            else model.CancelTransfer();
            Assert.True(captured.IsCancellationRequested);
        }
        finally
        {
            late.TrySetResult("Success");
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal([("A", "install", slowApk), ("B", "push", queuedFile)], adb.FileOperations);
        var cancelled = model.ScreenDropBatches.Single(batch => batch.DeviceLabel.Contains("A"));
        Assert.All(cancelled.Items, item => Assert.Contains("取消", item.Status));
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ScreenDrop_UnselectedDraftOnlyRunsAfterAnExplicitOnlineTargetIsChosen()
    {
        using var files = new DropTestFiles();
        var apk = files.Add("ready.apk");
        var adb = new FakeAdb { Devices = [Device("connected"), Device("offline") with { State = DeviceState.Offline }] };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = null;
        await model.RunScreenDropAsync(null, [apk]);
        var draft = Assert.Single(model.ScreenDropBatches);
        Assert.True(draft.CanRun);
        Assert.Empty(adb.FileOperations);

        model.SelectedDeviceSerial = "offline";
        await model.RunPendingScreenDropAsync(draft, "offline");
        Assert.True(draft.CanRun);
        Assert.Empty(adb.FileOperations);

        model.SelectedDeviceSerial = "connected";
        Assert.Empty(adb.FileOperations);
        await model.RunPendingScreenDropAsync(draft, "connected");
        await model.RunPendingScreenDropAsync(draft, "connected");
        Assert.False(draft.CanRun);
        Assert.Equal([("connected", "install", apk)], adb.FileOperations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScreenDrop_WaitsForExistingManualTransferOrInstall(bool manualInstall)
    {
        using var files = new DropTestFiles();
        var manualFile = files.Add(manualInstall ? "manual.apk" : "manual.zip");
        var dropped = files.Add(manualInstall ? "drop.zip" : "drop.apk");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> Hold()
        {
            entered.SetResult();
            return release.Task;
        }
        var adb = new FakeAdb
        {
            Devices = [Device("manual-A"), Device("drop-B")],
            Install = (_, _) => manualInstall ? Hold() : Task.FromResult("Success"),
            Push = _ => manualInstall ? Task.FromResult("1 file pushed") : Hold()
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = "manual-A";
        if (manualInstall) model.ApkFilePath = manualFile;
        else model.SetTransferFiles([manualFile]);
        var manual = manualInstall ? model.InstallApkAsync("manual-A") : model.PushFilesAsync("manual-A");
        Task queued = Task.CompletedTask;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            model.SelectedDeviceSerial = "drop-B";
            queued = model.RunScreenDropAsync("drop-B", [dropped]);
            Assert.Single(adb.FileOperations);
            Assert.Single(model.ScreenDropBatches);
        }
        finally
        {
            release.TrySetResult("Success");
            await Task.WhenAll(manual, queued).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal([
            ("manual-A", manualInstall ? "install" : "push", manualFile),
            ("drop-B", manualInstall ? "push" : "install", dropped)
        ], adb.FileOperations);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task ScreenDrop_DuringUiInspectionKeepsDraftUntilExplicitRun()
    {
        using var files = new DropTestFiles();
        var apk = files.Add("waiting.apk");
        var inspectionResult = new TaskCompletionSource<IReadOnlyList<UiTestNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb
        {
            Devices = [Device("online")],
            InspectUi = (_, _) => inspectionResult.Task
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        var inspection = model.InspectUiTestAsync();
        try
        {
            Assert.True(model.IsUiTestRunning);
            await model.RunScreenDropAsync("online", [apk]).WaitAsync(TimeSpan.FromSeconds(5));
            var staged = Assert.Single(model.ScreenDropBatches);
            Assert.True(staged.CanRun);
            Assert.False(staged.CanCancel);
            await model.RunPendingScreenDropAsync(staged, "online").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(adb.FileOperations);
        }
        finally
        {
            inspectionResult.TrySetResult([]);
            await inspection;
        }

        var draft = Assert.Single(model.ScreenDropBatches);
        Assert.True(draft.CanRun);
        Assert.Empty(adb.FileOperations);
        await model.RunPendingScreenDropAsync(draft, "online");
        Assert.False(draft.CanRun);
        Assert.Equal([("online", "install", apk)], adb.FileOperations);
    }

    private sealed class DropTestFiles : IDisposable
    {
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("adb-mirror-drop-tests-");

        public string Add(string name)
        {
            var path = Path.Combine(_root.FullName, name);
            File.WriteAllText(path, name);
            return path;
        }

        public void Dispose() => _root.Delete(recursive: true);
    }
}
