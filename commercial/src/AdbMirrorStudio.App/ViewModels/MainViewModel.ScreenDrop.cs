using System.Collections.ObjectModel;
using System.ComponentModel;
using AdbMirrorStudio.Domain.Devices;
using Microsoft.UI.Xaml.Controls;

namespace AdbMirrorStudio.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly SemaphoreSlim _fileOperationGate = new(1, 1);
    private bool _fileOperationBusy;
    private Task _screenDropTail = Task.CompletedTask;
    private ScreenDropBatchViewModel? _activeScreenDrop;

    public ObservableCollection<ScreenDropBatchViewModel> ScreenDropBatches { get; } = [];
    public bool HasScreenDropBatches => ScreenDropBatches.Count > 0;

    private bool HasNonFileOperation => Volatile.Read(ref _busyCount) > (_fileOperationBusy ? 1 : 0);

    private void EnterFileBusy()
    {
        _fileOperationBusy = true;
        EnterBusy();
    }

    private void ExitFileBusy()
    {
        _fileOperationBusy = false;
        ExitBusy();
    }

    public Task RunScreenDropAsync(string? serial, IReadOnlyList<string> paths)
    {
        if (_disposed || paths.Count == 0) return Task.CompletedTask;
        var batch = new ScreenDropBatchViewModel(serial, paths);
        ScreenDropBatches.Insert(0, batch);
        OnPropertyChanged(nameof(HasScreenDropBatches));
        return RunPendingScreenDropAsync(batch, serial);
    }

    public async Task RunPendingScreenDropAsync(ScreenDropBatchViewModel batch, string? serial)
    {
        if (_disposed || !batch.CanRun || !ScreenDropBatches.Contains(batch)) return;
        if (batch.PinnedSerial is { } pinned && pinned != serial)
        {
            SetStatus($"此批次已指定设备 {pinned}，请在任务中心选择该设备后执行。", InfoBarSeverity.Warning);
            return;
        }
        if (string.IsNullOrWhiteSpace(serial) || !IsDropTargetOnline(serial))
        {
            SetStatus("拖放文件已保存在任务中心，请选择在线设备后执行。", InfoBarSeverity.Warning);
            return;
        }
        if (HasNonFileOperation)
        {
            DeferBusyScreenDrop(batch, serial);
            return;
        }

        // Reserve a FIFO position before notifying the UI or yielding to another drop.
        var predecessor = _screenDropTail;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _screenDropTail = finished.Task;
        batch.Queue(serial);
        try
        {
            await predecessor.WaitAsync(_lifetimeToken);
            await _fileOperationGate.WaitAsync(_lifetimeToken);
            try
            {
                _lifetimeToken.ThrowIfCancellationRequested();
                if (!IsDropTargetOnline(serial))
                {
                    foreach (var item in batch.Items) item.Status = "失败：目标设备已离线";
                    FinishScreenDrop(batch, 0, batch.Items.Count);
                    return;
                }
                if (HasNonFileOperation)
                {
                    DeferBusyScreenDrop(batch, serial);
                    return;
                }
                await ExecuteScreenDropAsync(batch, serial);
            }
            finally
            {
                _fileOperationGate.Release();
            }
        }
        catch (OperationCanceledException) when (_lifetimeToken.IsCancellationRequested)
        {
            foreach (var item in batch.Items) item.Status = "已取消";
            FinishScreenDrop(batch, 0, 0);
        }
        finally
        {
            finished.TrySetResult();
        }
    }

    public void CancelScreenDrop(ScreenDropBatchViewModel batch)
    {
        if (ReferenceEquals(_activeScreenDrop, batch)) CancelTransfer();
    }

    private bool IsDropTargetOnline(string serial) =>
        Devices.Any(device => device.Serial == serial && device.State == DeviceState.Online);

    private void DeferBusyScreenDrop(ScreenDropBatchViewModel batch, string serial)
    {
        batch.Defer(serial);
        SetStatus($"其他操作正在进行，{serial} 的拖放批次已保存在任务中心，请稍后执行。", InfoBarSeverity.Warning);
    }

    private async Task ExecuteScreenDropAsync(ScreenDropBatchViewModel batch, string serial)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _transferCancellation = cancellation;
        _activeScreenDrop = batch;
        Interlocked.Exchange(ref _transferRunning, 1);
        EnterFileBusy();
        OnPropertyChanged(nameof(IsTransferRunning));
        NotifyTasksState();
        batch.Start();
        var succeeded = 0;
        var failed = 0;
        try
        {
            SetStatus($"正在向 {serial} 执行 {batch.Items.Count} 个拖放任务", InfoBarSeverity.Informational);
            foreach (var item in batch.Items)
            {
                if (cancellation.IsCancellationRequested) break;
                item.Status = item.InstallApk ? "安装中" : "推送中";
                try
                {
                    if (item.InstallApk)
                        await _adb.InstallApkAsync(serial, item.Path, cancellation.Token);
                    else
                        await _adb.PushFileAsync(serial, item.Path, cancellationToken: cancellation.Token);
                    cancellation.Token.ThrowIfCancellationRequested();
                    item.Status = "已完成";
                    succeeded++;
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                    item.Status = "已取消";
                    break;
                }
                catch (Exception exception)
                {
                    item.Status = $"失败：{exception.Message}";
                    failed++;
                }
            }
            if (cancellation.IsCancellationRequested)
            {
                foreach (var item in batch.Items.Skip(succeeded + failed)) item.Status = "已取消";
            }
            FinishScreenDrop(batch, succeeded, failed);
        }
        finally
        {
            _activeScreenDrop = null;
            _transferCancellation = null;
            Interlocked.Exchange(ref _transferRunning, 0);
            OnPropertyChanged(nameof(IsTransferRunning));
            NotifyTasksState();
            ExitFileBusy();
        }
    }

    private void FinishScreenDrop(ScreenDropBatchViewModel batch, int succeeded, int failed)
    {
        var cancelled = batch.Items.Count - succeeded - failed;
        batch.Finish($"成功 {succeeded}，失败 {failed}，取消 {cancelled}");
        if (_disposed) return;
        var severity = failed > 0 ? InfoBarSeverity.Error
            : cancelled > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        SetStatus($"{batch.DeviceLabel} 拖放任务结束：{batch.Status}", severity);
    }
}

public sealed class ScreenDropBatchViewModel : INotifyPropertyChanged
{
    internal ScreenDropBatchViewModel(string? serial, IReadOnlyList<string> paths)
    {
        DeviceLabel = string.IsNullOrWhiteSpace(serial) ? "未选择设备" : serial;
        Items = paths.Select(path => new ScreenDropItemViewModel(path)).OrderByDescending(item => item.InstallApk).ToArray();
    }

    public string DeviceLabel { get; private set; }
    public string Status { get; private set; } = "待选择在线设备";
    public bool CanRun { get; private set; } = true;
    public bool CanCancel { get; private set; }
    internal string? PinnedSerial { get; private set; }
    public IReadOnlyList<ScreenDropItemViewModel> Items { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    internal void Queue(string serial)
    {
        PinnedSerial = serial;
        DeviceLabel = serial;
        CanRun = false;
        Status = "等待执行";
        PropertyChanged?.Invoke(this, new(nameof(DeviceLabel)));
        PropertyChanged?.Invoke(this, new(nameof(CanRun)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }

    internal void Defer(string serial)
    {
        PinnedSerial = serial;
        DeviceLabel = serial;
        CanRun = true;
        Status = "其他操作进行中，请稍后执行";
        PropertyChanged?.Invoke(this, new(nameof(DeviceLabel)));
        PropertyChanged?.Invoke(this, new(nameof(CanRun)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }

    internal void Start()
    {
        CanCancel = true;
        Status = "执行中";
        PropertyChanged?.Invoke(this, new(nameof(CanCancel)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }

    internal void Finish(string summary)
    {
        CanCancel = false;
        Status = summary;
        PropertyChanged?.Invoke(this, new(nameof(CanCancel)));
        PropertyChanged?.Invoke(this, new(nameof(Status)));
    }
}

public sealed class ScreenDropItemViewModel(string path) : INotifyPropertyChanged
{
    public string Path { get; } = path;
    public string FileName { get; } = System.IO.Path.GetFileName(path);
    internal bool InstallApk { get; } = string.Equals(System.IO.Path.GetExtension(path), ".apk", StringComparison.OrdinalIgnoreCase);
    public string ActionLabel => InstallApk ? "安装 APK" : "推送文件";

    private string _status = "等待中";
    public string Status
    {
        get => _status;
        internal set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new(nameof(Status)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
