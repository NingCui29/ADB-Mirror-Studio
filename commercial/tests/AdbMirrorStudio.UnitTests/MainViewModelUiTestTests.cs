using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Fact]
    public async Task UiTest_SelectionChangeDiscardsLateInspectionAndCanInspectNewDevice()
    {
        var firstInspection = new TaskCompletionSource<IReadOnlyList<UiTestNode>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectedNode = new UiTestNode("new screen", "", "", "button", "[0,0][20,30]", true, true, false);
        CancellationToken firstToken = default;
        var adb = new FakeAdb
        {
            Devices = [Device("first"), Device("second")],
            InspectUi = (serial, token) =>
            {
                if (serial == "second") return Task.FromResult<IReadOnlyList<UiTestNode>>([selectedNode]);
                firstToken = token;
                return firstInspection.Task;
            }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = "first";
        var stale = model.InspectUiTestAsync();
        Assert.True(model.IsUiTestRunning);
        model.SelectedDeviceSerial = "second";
        firstInspection.SetResult([selectedNode with { Text = "stale screen" }]);
        await stale;

        Assert.True(firstToken.IsCancellationRequested);
        Assert.Empty(model.UiTestNodes);
        Assert.False(model.IsUiTestRunning);
        await model.InspectUiTestAsync();
        Assert.Equal(selectedNode, Assert.Single(model.UiTestNodes));
        Assert.Contains("second", model.UiTestStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UiTest_CancelDuringStepDiscardsLateSuccess(bool selectAnotherDevice)
    {
        using var output = new QaOutput();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb
        {
            Devices = [Device("first"), Device("second")],
            ExecuteUi = (_, _) => { started.SetResult(); return finish.Task; }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = "first";

        UiTestStep[] steps = selectAnotherDevice ? [new(UiTestAction.Back), new(UiTestAction.Tap, "登录")] : [new(UiTestAction.Back)];
        var run = model.RunUiTestAsync(steps, output.Path);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (selectAnotherDevice) model.SelectedDeviceSerial = "second";
        else model.CancelUiTest();
        finish.SetResult();
        await run;

        Assert.Equal(("first", UiTestAction.Back), Assert.Single(adb.UiActions));
        var report = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(output.Path, "report.txt", SearchOption.AllDirectories)));
        Assert.Contains("已取消", report);
        Assert.DoesNotContain("测试通过", report);
        Assert.Contains("已取消", model.UiTestStatus);
        Assert.False(model.IsUiTestRunning);
    }

    [Fact]
    public async Task UiTest_FailedAssertionRecordsCauseAndStopsSubsequentSteps()
    {
        using var output = new QaOutput();
        var adb = new FakeAdb
        {
            Devices = [Device("first")],
            ExecuteUi = (step, _) => step.Action == UiTestAction.AssertVisible
                ? Task.FromException(new AdbCommandException("找不到登录按钮"))
                : Task.CompletedTask
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        await model.RunUiTestAsync([new(UiTestAction.Back), new(UiTestAction.AssertVisible, "登录"), new(UiTestAction.Screenshot)], output.Path);

        Assert.Equal([("first", UiTestAction.Back), ("first", UiTestAction.AssertVisible)], adb.UiActions);
        Assert.Empty(adb.Screenshots);
        var report = await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(output.Path, "report.txt", SearchOption.AllDirectories)));
        Assert.Contains("1/3", report);
        Assert.Contains("2/3", report);
        Assert.Contains("找不到登录按钮", report);
        Assert.DoesNotContain("3/3", report);
        Assert.DoesNotContain("测试通过", report);
        Assert.Contains("找不到登录按钮", model.UiTestStatus);
    }

    [Fact]
    public async Task UiTest_ReplayPreservesOriginalEvidenceAndReportsEachRun()
    {
        using var output = new QaOutput();
        var capture = 0;
        var logSerials = new List<string>();
        var adb = new FakeAdb
        {
            Devices = [Device("first")],
            Screenshot = path =>
            {
                File.WriteAllBytes(path, [(byte)++capture]);
                return Task.FromResult(path);
            },
            Logcat = (serial, _) =>
            {
                logSerials.Add(serial);
                return Task.FromResult($"log for capture {capture}");
            }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        UiTestStep[] steps = [new(UiTestAction.Screenshot), new(UiTestAction.Logcat)];
        await model.RunUiTestAsync(steps, output.Path);
        await model.RunUiTestAsync(steps, output.Path);

        Assert.Equal(["first", "first"], logSerials);
        Assert.Equal(2, adb.Screenshots.Count);
        Assert.Equal(2, Directory.GetDirectories(output.Path).Length);
        for (int i = 0; i < adb.Screenshots.Count; i++)
        {
            var (serial, screenshot) = adb.Screenshots[i];
            Assert.Equal("first", serial);
            var directory = Path.GetDirectoryName(screenshot)!;
            var report = await File.ReadAllTextAsync(Path.Combine(directory, "report.txt"));
            var log = Assert.Single(Directory.GetFiles(directory, "*logcat.txt"));
            Assert.Equal([(byte)(i + 1)], await File.ReadAllBytesAsync(screenshot));
            Assert.Equal($"log for capture {i + 1}", await File.ReadAllTextAsync(log));
            Assert.Contains("first", report);
            Assert.Contains(Path.GetFileName(screenshot), report);
            Assert.Contains(Path.GetFileName(log), report);
            Assert.Contains("测试通过", report);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UiTest_BusyDeviceOperationExplainsWhyEntryIsUnavailable(bool runPlan)
    {
        using var output = new QaOutput();
        var finish = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb { Devices = [Device("online")], Shell = _ => finish.Task };
        using var model = Create(adb);
        await model.RefreshAsync();
        var shell = model.RunDeviceShellCommandAsync("online", "echo ready");
        try
        {
            Assert.False(model.CanUseSelectedDevice);
            if (runPlan) await model.RunUiTestAsync([new(UiTestAction.Back)], output.Path);
            else await model.InspectUiTestAsync();
            Assert.Contains("其他操作", model.UiTestStatus);
            Assert.False(model.IsUiTestRunning);
            Assert.Empty(adb.UiActions);
            Assert.Empty(Directory.GetFileSystemEntries(output.Path));
        }
        finally
        {
            finish.SetResult("ready");
            await shell;
        }
        Assert.True(model.CanUseSelectedDevice);
    }

    [Fact]
    public async Task UiTest_RepeatedEntryDuringRunPreservesReportAndDoesNotRunTwice()
    {
        using var output = new QaOutput();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb
        {
            Devices = [Device("online")],
            ExecuteUi = (_, _) => { started.SetResult(); return finish.Task; }
        };
        using var model = Create(adb);
        await model.RefreshAsync();
        var run = model.RunUiTestAsync([new(UiTestAction.Back)], output.Path);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var before = model.UiTestStatus;
            Assert.Contains("online", before);
            await model.InspectUiTestAsync();
            await model.RunUiTestAsync([new(UiTestAction.Back)], output.Path);
            Assert.Equal(before, model.UiTestStatus);
            Assert.True(model.IsUiTestRunning);
            Assert.Single(adb.UiActions);
        }
        finally
        {
            finish.SetResult();
            await run;
        }
        Assert.Contains("测试通过", model.UiTestStatus);
    }

    [Fact]
    public async Task UiTest_UnavailableDeviceExplainsMissingSelectionAndOfflineState()
    {
        var adb = new FakeAdb { Devices = [Device("offline") with { State = DeviceState.Offline }] };
        using var model = Create(adb);
        await model.RefreshAsync();
        model.SelectedDeviceSerial = null;
        await model.InspectUiTestAsync();
        Assert.Contains("请选择", model.UiTestStatus);

        model.SelectedDeviceSerial = "offline";
        await model.InspectUiTestAsync();
        Assert.Contains("离线", model.UiTestStatus);
        Assert.False(model.IsUiTestRunning);
    }

    private sealed class QaOutput : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("adb-mirror-qa-tests-").FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
