using AdbMirrorStudio.Application.Adb;

namespace AdbMirrorStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private CancellationTokenSource? _uiTestCancellation;
    private IReadOnlyList<UiTestNode> _uiTestNodes = [];
    private string _uiTestStatus = string.Empty;

    public bool IsUiTestRunning => _uiTestCancellation is not null;
    public IReadOnlyList<UiTestNode> UiTestNodes
    {
        get => _uiTestNodes;
        private set => SetField(ref _uiTestNodes, value);
    }
    public string UiTestStatus
    {
        get => _uiTestStatus;
        private set => SetField(ref _uiTestStatus, value);
    }

    public Task InspectUiTestAsync() => WithUiTestDeviceAsync(async (serial, token) =>
    {
        UiTestNodes = [];
        UiTestStatus = $"正在读取 {serial} 的界面…";
        var nodes = await _adb.InspectUiAsync(serial, token);
        token.ThrowIfCancellationRequested();
        UiTestNodes = nodes;
        UiTestStatus = $"{serial} · 已读取 {nodes.Count} 个控件";
    });

    public Task RunUiTestAsync(IReadOnlyList<UiTestStep> steps, string outputDirectory) =>
        WithUiTestDeviceAsync(async (serial, token) =>
        {
            UiTestNodes = [];
            UiTestStatus = string.Empty;
            await new UiTestRunner(_adb).RunAsync(serial, steps, outputDirectory, line =>
            {
                UiTestStatus = UiTestStatus.Length == 0 ? line : UiTestStatus + Environment.NewLine + line;
            }, token);
        });

    public void CancelUiTest() => _uiTestCancellation?.Cancel();

    private async Task WithUiTestDeviceAsync(Func<string, CancellationToken, Task> operation)
    {
        if (_disposed || IsUiTestRunning) return;
        if (IsBusy)
        {
            UiTestStatus = "正在执行其他操作，请稍后再试。";
            return;
        }
        if (!CanUseSelectedDevice || SelectedDeviceSerial is not { } serial)
        {
            UiTestStatus = HasSelectedDeviceWarning ? SelectedDeviceWarningTitle : "请选择一台可用的在线设备。";
            return;
        }

        using var source = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _uiTestCancellation = source;
        EnterBusy();
        OnPropertyChanged(nameof(IsUiTestRunning));
        try
        {
            await operation(serial, source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested)
        {
            UiTestStatus = $"{serial} · 已取消";
        }
        catch (Exception ex)
        {
            UiTestStatus = $"{serial} · 界面测试失败 · {ex.Message}";
        }
        finally
        {
            _uiTestCancellation = null;
            ExitBusy();
            OnPropertyChanged(nameof(IsUiTestRunning));
        }
    }
}
