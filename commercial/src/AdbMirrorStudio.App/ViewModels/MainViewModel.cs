using System.Collections.ObjectModel;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Devices;
using AdbMirrorStudio.Application.Diagnostics;
using AdbMirrorStudio.Application.Mirroring;
using AdbMirrorStudio.Application.Settings;
using AdbMirrorStudio.Application.Updates;
using AdbMirrorStudio.Domain.Devices;
using AdbMirrorStudio.Domain.Mirroring;
using AdbMirrorStudio.Domain.Settings;
using AdbMirrorStudio.Infrastructure.Adb;
using Microsoft.UI;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AdbMirrorStudio.App.ViewModels;

public sealed class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IAdbService _adb;
    private readonly IMirrorSessionManager _mirrorSessions;
    private readonly DeviceRefreshCoordinator _refreshCoordinator;
    private readonly IAppSettingsStore _settingsStore;
    private readonly IDiagnosticsService _diagnosticsService;
    private readonly IUpdateService _updates;
    private readonly SynchronizationContext _uiContext;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly ConcurrentDictionary<string, string> _sessionFailures = new(StringComparer.Ordinal);
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _transferCancellation;
    private CancellationTokenSource? _updateDownloadCancellation;
    private CancellationTokenSource? _shellCommandCancellation;
    private CancellationTokenSource? _performanceCancellation;
    private DevicePerformanceWindow _performanceWindow = new(TimeSpan.FromSeconds(30));
    private int _performanceGeneration;
    private string _cpuUsageText = "—";
    private string _cpuAverageText = "—";
    private string _cpuTemperatureText = "—";
    private string _gpuUsageText = "—";
    private string _gpuAverageText = "—";
    private string _gpuTemperatureText = "—";
    private string _memoryUsageText = "—";
    private string _memoryAverageText = "—";
    private string _memoryDetailText = "—";
    private string _ddrUsageText = "—";
    private string _ddrAverageText = "—";
    private string _ddrFrequencyText = "—";
    private string _performanceHint = "选择在线设备后开始采样";
    private int _busyCount;
    private int _transferRunning;
    private bool _disposed;
    private bool _isBusy;
    private bool _applyingDeviceSnapshot;
    private bool _allowAutomaticDeviceSelection = true;
    private bool _settingTransferFiles;
    private bool _diagnosticsRunning;
    private bool _checkingUpdates;
    private long _appsRequestVersion;
    private long _statusVersion;
    private string _statusText = "正在初始化设备服务…";
    private bool _isStatusOpen = true;
    private string _endpoint = "192.168.1.100:5555";
    private string _pairEndpoint = string.Empty;
    private string _pairingCode = string.Empty;
    private string _transferFilePath = string.Empty;
    private string _apkFilePath = string.Empty;
    private string _selectedMirrorProfileId = MirrorProfile.Balanced.Id;
    private string _recordingPath = string.Empty;
    private string _updateStatusText = "尚未检查更新";
    private bool _updateAvailable;
    private bool _isUpdateDownloading;
    private double _updateDownloadProgress;
    private AppUpdateInfo? _availableUpdate;
    private string _remoteFilePath = "/sdcard/Download/";
    private string _localDownloadDirectory = string.Empty;
    private string? _selectedDeviceSerial;
    private string? _selectedAppPackage;
    private string _packageNameInput = string.Empty;
    private string _shellCommand = string.Empty;
    private string _shellOutput = "尚未执行设备命令。";
    private bool _isShellCommandRunning;
    private AppSettings _settings = AppSettings.Default;

    public MainViewModel(AppServices services)
    {
        _adb = services.Adb;
        _mirrorSessions = services.MirrorSessions;
        _settingsStore = services.Settings;
        _diagnosticsService = services.Diagnostics;
        _updates = services.Updates;
        _lifetimeToken = _lifetimeCancellation.Token;
        _refreshCoordinator = new DeviceRefreshCoordinator(_adb);
        _uiContext = SynchronizationContext.Current
            ?? throw new InvalidOperationException("MainViewModel 必须在 UI 线程创建。");
        _mirrorSessions.SessionChanged += OnSessionChanged;
    }

    public ObservableCollection<DeviceCardViewModel> Devices { get; } = [];
    public ObservableCollection<string> RememberedEndpoints { get; } = [];
    public ObservableCollection<string> DiscoveredPairingEndpoints { get; } = [];
    public ObservableCollection<MirrorSessionCardViewModel> Sessions { get; } = [];
    public ObservableCollection<DiagnosticItemViewModel> Diagnostics { get; } = [];
    public ObservableCollection<TransferItemViewModel> TransferQueue { get; } = [];
    public ObservableCollection<InstalledAppViewModel> InstalledApps { get; } = [];
    public ObservableCollection<RecordingItemViewModel> Recordings { get; } = [];
    public IReadOnlyList<MirrorProfile> MirrorProfiles { get; } = MirrorProfile.Presets;
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                OnPropertyChanged(nameof(CanDownloadAndInstallUpdate));
                OnPropertyChanged(nameof(CanUseSelectedDevice));
                OnPropertyChanged(nameof(CanStartSelectedMirror));
                OnPropertyChanged(nameof(CanStopSelectedMirror));
                OnPropertyChanged(nameof(CanInstallApk));
                OnPropertyChanged(nameof(CanPushFiles));
                OnPropertyChanged(nameof(CanRunSelectedAppAction));
                OnPropertyChanged(nameof(CanArrangeMirrorWindows));
            }
        }
    }
    public bool IsIdle => !IsBusy;
    public bool IsTransferRunning => Volatile.Read(ref _transferRunning) != 0;

    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetField(ref _statusText, value))
            {
                _statusVersion++;
                IsStatusOpen = true;
                OnPropertyChanged(nameof(StatusSeverity));
            }
        }
    }

    public bool IsStatusOpen
    {
        get => _isStatusOpen;
        set => SetField(ref _isStatusOpen, value);
    }

    public InfoBarSeverity StatusSeverity => GetStatusSeverity(StatusText);

    public string Endpoint
    {
        get => _endpoint;
        set => SetField(ref _endpoint, value);
    }

    public string PairEndpoint
    {
        get => _pairEndpoint;
        set => SetField(ref _pairEndpoint, value);
    }

    public string PairingCode
    {
        get => _pairingCode;
        set => SetField(ref _pairingCode, value);
    }

    public string TransferFilePath
    {
        get => _transferFilePath;
        set
        {
            if (IsTransferRunning || !SetField(ref _transferFilePath, value)) return;
            if (!_settingTransferFiles) TransferQueue.Clear();
            OnPropertyChanged(nameof(CanPushFiles));
            OnPropertyChanged(nameof(HasNoTransferTasks));
        }
    }

    public string ApkFilePath
    {
        get => _apkFilePath;
        set
        {
            if (SetField(ref _apkFilePath, value)) OnPropertyChanged(nameof(CanInstallApk));
        }
    }

    public string SelectedMirrorProfileId
    {
        get => _selectedMirrorProfileId;
        set => SetField(ref _selectedMirrorProfileId, value);
    }

    public string RecordingPath
    {
        get => _recordingPath;
        set
        {
            if (SetField(ref _recordingPath, value)) OnPropertyChanged(nameof(IsRecordingEnabled));
        }
    }

    public bool IsRecordingEnabled => !string.IsNullOrWhiteSpace(RecordingPath);

    public string OnlineSummary => $"{Devices.Count(device => device.State == DeviceState.Online)} 台在线";
    public bool HasNoSessions => Sessions.Count == 0;
    public bool HasNoTransferTasks => TransferQueue.Count == 0;
    public bool HasNoRecordings => Recordings.Count == 0;
    public string Theme => _settings.Theme;
    public bool AutoRefresh => _settings.AutoRefresh;
    public bool HasRememberedEndpoints => RememberedEndpoints.Count > 0;
    public bool FirstRunCompleted => _settings.FirstRunCompleted;
    public string AppVersion => AppVersionInfo.ProductVersion;
    public string DataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AdbMirrorStudio");
    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetField(ref _updateStatusText, value);
    }
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set
        {
            if (SetField(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(CanDownloadAndInstallUpdate));
            }
        }
    }
    public bool CanDownloadAndInstallUpdate =>
        UpdateAvailable && _availableUpdate?.Installer is not null && !IsBusy && !IsUpdateDownloading;
    public string LatestUpdateVersion => _availableUpdate?.LatestVersion ?? string.Empty;
    public AppUpdateInfo? AvailableUpdate => _availableUpdate;
    public bool IsUpdateDownloading
    {
        get => _isUpdateDownloading;
        private set
        {
            if (SetField(ref _isUpdateDownloading, value))
            {
                OnPropertyChanged(nameof(CanDownloadAndInstallUpdate));
            }
        }
    }
    public double UpdateDownloadProgress
    {
        get => _updateDownloadProgress;
        private set => SetField(ref _updateDownloadProgress, value);
    }
    public string RemoteFilePath
    {
        get => _remoteFilePath;
        set => SetField(ref _remoteFilePath, value);
    }
    public string LocalDownloadDirectory
    {
        get => _localDownloadDirectory;
        set => SetField(ref _localDownloadDirectory, value);
    }
    public string? SelectedDeviceSerial
    {
        get => _selectedDeviceSerial;
        set
        {
            if (_applyingDeviceSnapshot) return;
            if (!SetField(ref _selectedDeviceSerial, value)) return;
            _allowAutomaticDeviceSelection = false;
            _appsRequestVersion++;
            ResetPerformance();
            InstalledApps.Clear();
            SelectedAppPackage = null;
            OnPropertyChanged(nameof(SelectedDeviceLabel));
            OnPropertyChanged(nameof(HasSelectedDevice));
            OnPropertyChanged(nameof(CanUseSelectedDevice));
            OnPropertyChanged(nameof(CanStartSelectedMirror));
            OnPropertyChanged(nameof(CanStopSelectedMirror));
            OnPropertyChanged(nameof(CanInstallApk));
            OnPropertyChanged(nameof(CanPushFiles));
            OnPropertyChanged(nameof(CanRunSelectedAppAction));
        }
    }
    public string SelectedDeviceLabel => GetDeviceLabel(SelectedDeviceSerial);
    public string CpuUsageText { get => _cpuUsageText; private set => SetField(ref _cpuUsageText, value); }
    public string CpuAverageText { get => _cpuAverageText; private set => SetField(ref _cpuAverageText, value); }
    public string CpuTemperatureText { get => _cpuTemperatureText; private set => SetField(ref _cpuTemperatureText, value); }
    public string GpuUsageText { get => _gpuUsageText; private set => SetField(ref _gpuUsageText, value); }
    public string GpuAverageText { get => _gpuAverageText; private set => SetField(ref _gpuAverageText, value); }
    public string GpuTemperatureText { get => _gpuTemperatureText; private set => SetField(ref _gpuTemperatureText, value); }
    public string MemoryUsageText { get => _memoryUsageText; private set => SetField(ref _memoryUsageText, value); }
    public string MemoryAverageText { get => _memoryAverageText; private set => SetField(ref _memoryAverageText, value); }
    public string MemoryDetailText { get => _memoryDetailText; private set => SetField(ref _memoryDetailText, value); }
    public string DdrUsageText { get => _ddrUsageText; private set => SetField(ref _ddrUsageText, value); }
    public string DdrAverageText { get => _ddrAverageText; private set => SetField(ref _ddrAverageText, value); }
    public string DdrFrequencyText { get => _ddrFrequencyText; private set => SetField(ref _ddrFrequencyText, value); }
    public string PerformanceHint { get => _performanceHint; private set => SetField(ref _performanceHint, value); }

    public async Task RefreshPerformanceAsync()
    {
        if (_disposed || SelectedDeviceSerial is not { } serial || _performanceCancellation is not null) return;
        if (Devices.FirstOrDefault(device => device.Serial == serial)?.State != DeviceState.Online) return;
        var generation = _performanceGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _performanceCancellation = cancellation;
        try
        {
            var counters = await _adb.GetPerformanceCountersAsync(serial, cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || generation != _performanceGeneration
                || !string.Equals(serial, SelectedDeviceSerial, StringComparison.Ordinal)) return;
            var reading = _performanceWindow.Add(counters, DateTimeOffset.UtcNow);
            CpuUsageText = FormatPerformancePercent(reading.CpuUsagePercent, "计算中");
            CpuAverageText = FormatPerformancePercent(reading.CpuAveragePercent, "计算中");
            CpuTemperatureText = FormatTemperature(counters.CpuTemperatureCelsius, "不可用");
            GpuUsageText = FormatPerformancePercent(reading.GpuUsagePercent, "不可用");
            GpuAverageText = FormatPerformancePercent(reading.GpuAveragePercent, "不可用");
            GpuTemperatureText = FormatTemperature(counters.GpuTemperatureCelsius, "不可用");
            MemoryUsageText = FormatPerformancePercent(reading.MemoryUsagePercent, "不可用");
            MemoryAverageText = FormatPerformancePercent(reading.MemoryAveragePercent, "不可用");
            MemoryDetailText = FormatMemoryUsage(counters.MemoryTotalKilobytes, counters.MemoryAvailableKilobytes);
            DdrUsageText = FormatPerformancePercent(reading.DdrUsagePercent, "不可用");
            DdrAverageText = FormatPerformancePercent(reading.DdrAveragePercent, "不可用");
            DdrFrequencyText = FormatFrequency(counters.DdrFrequencyHertz, "不可用");
            var unavailable = new List<string>();
            if (reading.GpuUsagePercent is null) unavailable.Add("GPU");
            if (reading.DdrUsagePercent is null) unavailable.Add("DDR");
            PerformanceHint = unavailable.Count == 0
                ? "整机 CPU / GPU / 内存 / DDR · 最近 30 秒有效采样均值 · 约每 2 秒更新"
                : $"整机性能 · 最近 30 秒有效采样均值；此设备未提供可读的 {string.Join("、", unavailable)} 占用节点";
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (!_disposed && generation == _performanceGeneration)
            {
                _performanceWindow = new DevicePerformanceWindow(TimeSpan.FromSeconds(30));
                CpuUsageText = "暂不可用";
                CpuAverageText = "—";
                CpuTemperatureText = "暂不可用";
                GpuUsageText = "暂不可用";
                GpuAverageText = "—";
                GpuTemperatureText = "暂不可用";
                MemoryUsageText = "暂不可用";
                MemoryAverageText = "—";
                MemoryDetailText = "—";
                DdrUsageText = "暂不可用";
                DdrAverageText = "—";
                DdrFrequencyText = "—";
                PerformanceHint = "设备性能采样失败，下一次刷新将重试";
            }
        }
        finally
        {
            if (ReferenceEquals(_performanceCancellation, cancellation)) _performanceCancellation = null;
            cancellation.Dispose();
        }
    }

    private void ResetPerformance()
    {
        _performanceGeneration++;
        var previous = _performanceCancellation;
        _performanceCancellation = null;
        previous?.Cancel();
        _performanceWindow = new DevicePerformanceWindow(TimeSpan.FromSeconds(30));
        CpuUsageText = "—";
        CpuAverageText = "—";
        CpuTemperatureText = "—";
        GpuUsageText = "—";
        GpuAverageText = "—";
        GpuTemperatureText = "—";
        MemoryUsageText = "—";
        MemoryAverageText = "—";
        MemoryDetailText = "—";
        DdrUsageText = "—";
        DdrAverageText = "—";
        DdrFrequencyText = "—";
        PerformanceHint = SelectedDeviceSerial is null
            ? "选择在线设备后开始采样"
            : Devices.FirstOrDefault(device => device.Serial == SelectedDeviceSerial)?.State == DeviceState.Online
                ? "正在采集设备性能…"
                : "当前设备未在线，连接后开始采样";
    }

    private static string FormatPerformancePercent(double? value, string fallback) =>
        value is { } percent ? $"{percent:F1}%" : fallback;
    private static string FormatTemperature(double? value, string fallback) =>
        value is { } celsius ? $"{celsius:F1} °C" : fallback;
    private static string FormatFrequency(long? value, string fallback)
    {
        if (value is not { } hertz || hertz <= 0) return fallback;
        return hertz >= 1_000_000_000
            ? $"频率 {hertz / 1_000_000_000d:F3} GHz"
            : $"频率 {hertz / 1_000_000d:F0} MHz";
    }
    private static string FormatMemoryUsage(long? totalKilobytes, long? availableKilobytes)
    {
        if (totalKilobytes is not { } total || total <= 0 || availableKilobytes is not { } available
            || available < 0 || available > total) return "—";
        const double kilobytesPerGibibyte = 1024d * 1024d;
        var used = total - available;
        return $"已用 {used / kilobytesPerGibibyte:F1} / {total / kilobytesPerGibibyte:F1} GiB";
    }
    public bool HasSelectedDevice => !string.IsNullOrWhiteSpace(SelectedDeviceSerial);
    public bool SelectedDeviceIsMirroring => Devices.FirstOrDefault(device =>
        string.Equals(device.Serial, SelectedDeviceSerial, StringComparison.Ordinal))?.IsMirroring == true;
    public bool CanUseSelectedDevice => IsIdle && HasSelectedDevice;
    public bool CanStartSelectedMirror => CanUseSelectedDevice && !SelectedDeviceIsMirroring;
    public bool CanStopSelectedMirror => CanUseSelectedDevice && SelectedDeviceIsMirroring;
    public bool CanInstallApk => CanUseSelectedDevice && File.Exists(ApkFilePath);
    public bool CanPushFiles => CanUseSelectedDevice
        && (TransferQueue.Count > 0 || File.Exists(TransferFilePath));
    public bool CanRunSelectedAppAction => CanUseSelectedDevice
        && !string.IsNullOrWhiteSpace(SelectedAppPackage);
    public bool CanArrangeMirrorWindows => IsIdle && Sessions.Count > 1;
    public string? SelectedAppPackage
    {
        get => _selectedAppPackage;
        set
        {
            if (SetField(ref _selectedAppPackage, value)) OnPropertyChanged(nameof(CanRunSelectedAppAction));
        }
    }
    public string PackageNameInput
    {
        get => _packageNameInput;
        set => SetField(ref _packageNameInput, value);
    }
    public string ShellCommand
    {
        get => _shellCommand;
        set => SetField(ref _shellCommand, value);
    }
    public string ShellOutput
    {
        get => _shellOutput;
        private set => SetField(ref _shellOutput, value);
    }
    public bool IsShellCommandRunning
    {
        get => _isShellCommandRunning;
        private set => SetField(ref _isShellCommandRunning, value);
    }

    public string GetDeviceLabel(string? serial)
    {
        if (string.IsNullOrWhiteSpace(serial)) return "未选择设备";
        var displayName = Devices.FirstOrDefault(device =>
            string.Equals(device.Serial, serial, StringComparison.Ordinal))?.DisplayName;
        if (string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, serial, StringComparison.Ordinal))
        {
            return serial;
        }

        return displayName.Contains(serial, StringComparison.Ordinal)
            ? displayName
            : $"{displayName}（{serial}）";
    }

    private static InfoBarSeverity GetStatusSeverity(string status)
    {
        if (status.Contains("没有错误", StringComparison.Ordinal))
        {
            return InfoBarSeverity.Success;
        }

        if (status.Contains("失败", StringComparison.Ordinal)
            || status.Contains("异常", StringComparison.Ordinal)
            || status.Contains("错误", StringComparison.Ordinal)
            || status.Contains("无法", StringComparison.Ordinal))
        {
            return InfoBarSeverity.Error;
        }

        if (status.Contains("取消", StringComparison.Ordinal)
            || status.Contains("请选择", StringComparison.Ordinal)
            || status.Contains("未发现", StringComparison.Ordinal)
            || status.Contains("暂时", StringComparison.Ordinal)
            || status.Contains("没有可", StringComparison.Ordinal))
        {
            return InfoBarSeverity.Warning;
        }

        if (status.Contains("完成", StringComparison.Ordinal)
            || status.StartsWith("已", StringComparison.Ordinal)
            || status.Contains("正常", StringComparison.Ordinal))
        {
            return InfoBarSeverity.Success;
        }

        return InfoBarSeverity.Informational;
    }

    public async Task InitializeAsync()
    {
        if (_disposed) return;
        var loadedSettings = await _settingsStore.LoadAsync(_lifetimeToken);
        if (_disposed) return;
        var upgradedSettings = loadedSettings.UpgradeConnectionHistory();
        var normalizedHistory = NormalizeConnectionEndpoints(upgradedSettings.RememberedEndpoints);
        _settings = upgradedSettings with
        {
            LastEndpoint = normalizedHistory.FirstOrDefault() ?? string.Empty,
            HasConnectedBefore = normalizedHistory.Length > 0,
            RememberedEndpoints = normalizedHistory
        };
        var loadedHistory = loadedSettings.RememberedEndpoints ?? [];
        ReplaceRememberedEndpoints(_settings.RememberedEndpoints);
        Endpoint = string.Empty;
        SelectedMirrorProfileId = MirrorProfile.Presets.Any(profile => profile.Id == _settings.MirrorProfileId)
            ? _settings.MirrorProfileId
            : MirrorProfile.Balanced.Id;
        OnPropertyChanged(nameof(Theme));
        OnPropertyChanged(nameof(AutoRefresh));
        if (loadedSettings.SchemaVersion != AppSettings.CurrentSchemaVersion
            || loadedSettings.AutoReconnect
            || loadedSettings.HasConnectedBefore != _settings.HasConnectedBefore
            || !string.Equals(loadedSettings.LastEndpoint, _settings.LastEndpoint, StringComparison.OrdinalIgnoreCase)
            || !loadedHistory.SequenceEqual(_settings.RememberedEndpoints, StringComparer.OrdinalIgnoreCase))
        {
            await SaveSettingsSafelyAsync();
        }
        await DiscoverAsync();
        if (_disposed) return;
        await RefreshAsync();
    }

    public async Task SetThemeAsync(string theme)
    {
        if (_disposed) return;
        if (theme is not ("System" or "Light" or "Dark"))
        {
            throw new ArgumentOutOfRangeException(nameof(theme));
        }

        _settings = _settings with { Theme = theme };
        OnPropertyChanged(nameof(Theme));
        await SaveSettingsSafelyAsync();
    }

    public async Task SetAutoRefreshAsync(bool enabled)
    {
        if (_disposed) return;
        if (_settings.AutoRefresh == enabled) return;
        _settings = _settings with { AutoRefresh = enabled };
        OnPropertyChanged(nameof(AutoRefresh));
        await SaveSettingsSafelyAsync();
        StatusText = enabled ? "已启用设备自动刷新（每 5 秒）" : "已关闭设备自动刷新";
    }

    public async Task SetMirrorProfileAsync(string profileId)
    {
        if (_disposed) return;
        if (!MirrorProfile.Presets.Any(profile => profile.Id == profileId)) return;
        SelectedMirrorProfileId = profileId;
        _settings = _settings with { MirrorProfileId = profileId };
        await SaveSettingsSafelyAsync();
        StatusText = $"镜像预设已切换为 {MirrorProfile.Presets.First(profile => profile.Id == profileId).Name}";
    }

    public async Task CompleteFirstRunAsync()
    {
        if (_disposed) return;
        if (_settings.FirstRunCompleted) return;
        _settings = _settings with { FirstRunCompleted = true };
        OnPropertyChanged(nameof(FirstRunCompleted));
        await SaveSettingsSafelyAsync();
    }

    public async Task InstallApkAsync(string? serial)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }

        EnterBusy();
        StatusText = $"正在向 {serial} 安装 APK…";
        try
        {
            var result = await _adb.InstallApkAsync(serial, ApkFilePath, _lifetimeToken);
            StatusText = string.IsNullOrWhiteSpace(result) ? "APK 安装完成" : $"APK 安装完成：{result}";
        }
        catch (Exception exception)
        {
            StatusText = $"APK 安装失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task PushFileAsync(string? serial)
        => await PushFilesAsync(serial);

    public void SetTransferFiles(IEnumerable<string> paths)
    {
        if (_disposed) return;
        if (IsTransferRunning)
        {
            StatusText = "文件传输进行中，完成或取消后才能更改队列";
            return;
        }
        var files = paths.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        TransferQueue.Clear();
        foreach (var path in files) TransferQueue.Add(new TransferItemViewModel(path));
        _settingTransferFiles = true;
        try { TransferFilePath = files.FirstOrDefault() ?? string.Empty; }
        finally { _settingTransferFiles = false; }
        OnPropertyChanged(nameof(CanPushFiles));
        OnPropertyChanged(nameof(HasNoTransferTasks));
        StatusText = files.Length == 0 ? "未选择有效文件" : $"已加入 {files.Length} 个文件";
    }

    public async Task PushFilesAsync(string? serial)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }

        if (TransferQueue.Count == 0 && File.Exists(TransferFilePath))
        {
            TransferQueue.Add(new TransferItemViewModel(TransferFilePath));
        }
        if (TransferQueue.Count == 0)
        {
            StatusText = "请选择或拖入至少一个文件";
            return;
        }

        if (Interlocked.CompareExchange(ref _transferRunning, 1, 0) != 0)
        {
            StatusText = "已有文件传输任务正在运行";
            return;
        }
        OnPropertyChanged(nameof(IsTransferRunning));

        var transferCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _transferCancellation = transferCancellation;
        EnterBusy();
        StatusText = $"正在向 {serial} 推送 {TransferQueue.Count} 个文件…";
        var completed = 0;
        var failed = 0;
        foreach (var item in TransferQueue)
        {
            item.IsComplete = false;
            item.Status = "等待中";
        }
        try
        {
            foreach (var item in TransferQueue)
            {
                transferCancellation.Token.ThrowIfCancellationRequested();
                item.Status = "传输中";
                try
                {
                    await _adb.PushFileAsync(serial, item.Path, cancellationToken: transferCancellation.Token);
                    transferCancellation.Token.ThrowIfCancellationRequested();
                    item.Status = "已完成";
                    item.IsComplete = true;
                    completed++;
                }
                catch (OperationCanceledException)
                {
                    item.Status = "已取消";
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = $"失败：{exception.Message}";
                    failed++;
                }
            }
            StatusText = failed == 0
                ? $"文件任务完成：{completed} 个成功"
                : $"文件任务完成：{completed} 个成功，{failed} 个失败";
        }
        catch (OperationCanceledException)
        {
            foreach (var item in TransferQueue.Where(item => !item.IsComplete && item.Status == "等待中"))
            {
                item.Status = "已取消";
            }
            StatusText = $"传输已取消，已完成 {completed} 个文件";
        }
        catch (Exception exception)
        {
            StatusText = $"文件推送失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_transferCancellation, transferCancellation)) _transferCancellation = null;
            transferCancellation.Dispose();
            Interlocked.Exchange(ref _transferRunning, 0);
            OnPropertyChanged(nameof(IsTransferRunning));
            ExitBusy();
        }
    }

    public void CancelTransfer()
    {
        if (_transferCancellation is null)
        {
            StatusText = "当前没有文件传输任务";
            return;
        }
        _transferCancellation.Cancel();
        StatusText = "正在取消文件传输…";
    }

    public async Task DiscoverAsync()
    {
        if (_disposed) return;
        try
        {
            var services = await _adb.DiscoverAsync(_lifetimeToken);
            if (_disposed) return;
            DiscoveredPairingEndpoints.Clear();
            foreach (var endpoint in services
                         .Where(service => service.ServiceType.Contains("pairing", StringComparison.OrdinalIgnoreCase))
                         .Select(service => service.Endpoint)
                         .Distinct(StringComparer.Ordinal))
            {
                DiscoveredPairingEndpoints.Add(endpoint);
            }
            if (string.IsNullOrWhiteSpace(PairEndpoint) && DiscoveredPairingEndpoints.Count > 0)
            {
                PairEndpoint = DiscoveredPairingEndpoints[0];
            }
        }
        catch
        {
            // mDNS is optional; manual pairing remains available.
        }
    }

    public async Task PairAsync()
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(PairEndpoint) || string.IsNullOrWhiteSpace(PairingCode))
        {
            StatusText = "请输入配对地址和手机显示的配对码";
            return;
        }

        EnterBusy();
        StatusText = $"正在配对 {PairEndpoint}…";
        try
        {
            var result = await _adb.PairAsync(PairEndpoint, PairingCode, _lifetimeToken);
            StatusText = result;
            await DiscoverAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"配对失败：{exception.Message}";
        }
        finally
        {
            PairingCode = string.Empty;
            ExitBusy();
        }
    }

    public async Task DisconnectAsync(string serial)
    {
        if (_disposed) return;
        EnterBusy();
        StatusText = $"正在断开 {serial}…";
        try
        {
            await _adb.DisconnectAsync(serial, _lifetimeToken);
            StatusText = $"已断开 {serial}";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"断开失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task RebootAsync(string serial)
    {
        if (_disposed) return;
        EnterBusy();
        StatusText = $"正在重启 {serial}…";
        try
        {
            await _adb.RebootAsync(serial, _lifetimeToken);
            StatusText = $"已向 {serial} 发送重启命令，设备重新上线可能需要约一分钟";
        }
        catch (Exception exception)
        {
            StatusText = $"重启失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task EnableTcpIpAsync(string serial, int port)
    {
        if (_disposed) return;
        EnterBusy();
        StatusText = $"正在让 {serial} 监听 TCP/IP 端口 {port}…";
        try
        {
            var result = await _adb.EnableTcpIpAsync(serial, port, _lifetimeToken);
            StatusText = string.IsNullOrWhiteSpace(result)
                ? $"设备已切换到 TCP/IP 端口 {port}"
                : result;
        }
        catch (Exception exception)
        {
            StatusText = $"TCP/IP 切换失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task RefreshAsync(bool silent = false)
    {
        if (_disposed || (silent && (IsBusy || _refreshCancellation is not null))) return;
        var refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        var previousCancellation = Interlocked.Exchange(ref _refreshCancellation, refreshCancellation);
        previousCancellation?.Cancel();
        if (!silent)
        {
            EnterBusy();
            StatusText = "正在刷新设备…";
        }
        var statusVersion = _statusVersion;

        try
        {
            var snapshot = await _refreshCoordinator.RefreshAsync(refreshCancellation.Token);
            if (snapshot is null || _disposed || refreshCancellation.IsCancellationRequested
                || !ReferenceEquals(_refreshCancellation, refreshCancellation)) return;

            var activeSessions = _mirrorSessions.ActiveSessions
                .OrderByDescending(session => session.StartedAt)
                .ToArray();
            var running = activeSessions.Where(session => session.State == MirrorSessionState.Running)
                .Select(session => session.DeviceSerial).ToHashSet(StringComparer.Ordinal);
            var selectedDevice = SelectedDeviceSerial;
            _applyingDeviceSnapshot = true;
            try
            {
                for (var index = 0; index < snapshot.Devices.Count; index++)
                {
                    var device = snapshot.Devices[index];
                    var card = Devices.FirstOrDefault(item => item.Represents(device))
                        ?? new DeviceCardViewModel(device, running.Contains(device.Serial));
                    card.IsMirroring = running.Contains(device.Serial);
                    var previousIndex = Devices.IndexOf(card);
                    if (previousIndex < 0) Devices.Insert(index, card);
                    else if (previousIndex != index) Devices.Move(previousIndex, index);
                }
                while (Devices.Count > snapshot.Devices.Count) Devices.RemoveAt(Devices.Count - 1);
            }
            finally { _applyingDeviceSnapshot = false; }

            Sessions.Clear();
            foreach (var session in activeSessions.Where(session => session.State is MirrorSessionState.Starting or MirrorSessionState.Running or MirrorSessionState.Stopping))
            {
                var displayName = Devices.FirstOrDefault(device => device.Serial == session.DeviceSerial)?.DisplayName;
                Sessions.Add(new MirrorSessionCardViewModel(session, displayName));
            }
            OnPropertyChanged(nameof(HasNoSessions));
            OnPropertyChanged(nameof(CanArrangeMirrorWindows));
            OnPropertyChanged(nameof(SelectedDeviceIsMirroring));
            OnPropertyChanged(nameof(CanStartSelectedMirror));
            OnPropertyChanged(nameof(CanStopSelectedMirror));

            foreach (var recordingSession in activeSessions.Where(session => !string.IsNullOrWhiteSpace(session.RecordPath)))
            {
                var existing = Recordings.FirstOrDefault(item => item.SessionId == recordingSession.Id);
                if (existing is null)
                {
                    Recordings.Insert(0, new RecordingItemViewModel(recordingSession));
                }
                else
                {
                    existing.Update(recordingSession);
                }
            }
            OnPropertyChanged(nameof(HasNoRecordings));

            var resolvedDevice = DeviceTargetSelection.Resolve(selectedDevice, snapshot.Devices, _allowAutomaticDeviceSelection);
            var targetLost = selectedDevice is not null && resolvedDevice is null;
            SelectedDeviceSerial = resolvedDevice;
            OnPropertyChanged(nameof(SelectedDeviceSerial));
            OnPropertyChanged(nameof(SelectedDeviceLabel));
            OnPropertyChanged(nameof(HasSelectedDevice));
            OnPropertyChanged(nameof(CanUseSelectedDevice));
            OnPropertyChanged(nameof(CanStartSelectedMirror));
            OnPropertyChanged(nameof(CanStopSelectedMirror));
            OnPropertyChanged(nameof(CanInstallApk));
            OnPropertyChanged(nameof(CanPushFiles));
            OnPropertyChanged(nameof(CanRunSelectedAppAction));

            if (targetLost)
                StatusText = "当前目标设备已离线，请重新选择设备";
            else if (!silent && statusVersion == _statusVersion)
                StatusText = snapshot.Devices.Count == 0
                    ? "未发现设备，可通过 USB 或无线地址连接"
                    : $"已发现 {snapshot.Devices.Count} 台设备";
            OnPropertyChanged(nameof(OnlineSummary));
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && !silent && statusVersion == _statusVersion
                && ReferenceEquals(Volatile.Read(ref _refreshCancellation), refreshCancellation))
            {
                StatusText = "刷新已取消";
            }
        }
        catch (Exception exception)
        {
            if (!_disposed && statusVersion == _statusVersion
                && ReferenceEquals(Volatile.Read(ref _refreshCancellation), refreshCancellation))
            {
                StatusText = $"刷新失败：{exception.Message}";
            }
        }
        finally
        {
            Interlocked.CompareExchange(ref _refreshCancellation, null, refreshCancellation);
            refreshCancellation.Dispose();
            if (!silent) ExitBusy();
        }
    }

    public async Task ConnectAsync()
    {
        if (_disposed) return;
        var endpoint = Endpoint.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            StatusText = "请输入设备 IP 地址和端口";
            return;
        }

        EnterBusy();
        try
        {
            var normalizedEndpoint = AdbEndpoint.Normalize(endpoint);
            Endpoint = normalizedEndpoint;
            StatusText = $"正在连接 {normalizedEndpoint}…";
            var result = await _adb.ConnectAsync(normalizedEndpoint, _lifetimeToken);
            _lifetimeToken.ThrowIfCancellationRequested();
            StatusText = result;
            await RememberEndpointsAsync([normalizedEndpoint]);
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            StatusText = $"连接失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task ClearRememberedEndpointsAsync()
    {
        if (_disposed) return;
        _settings = _settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            LastEndpoint = string.Empty,
            AutoReconnect = false,
            HasConnectedBefore = false,
            RememberedEndpoints = []
        };
        ReplaceRememberedEndpoints([]);
        Endpoint = string.Empty;
        await SaveSettingsSafelyAsync();
        StatusText = "已清除连接历史；当前在线设备不会断开";
    }

    public async Task StartMirrorAsync(string serial)
    {
        if (_disposed) return;
        EnterBusy();
        StatusText = $"正在启动 {serial} 的镜像…";
        try
        {
            var card = Devices.FirstOrDefault(device => device.Serial == serial);
            var profile = MirrorProfile.Presets.FirstOrDefault(item => item.Id == SelectedMirrorProfileId)
                ?? MirrorProfile.Balanced;
            if (IsRecordingEnabled)
            {
                profile = profile with
                {
                    RecordPath = RecordingPath,
                    Fullscreen = false,
                    AlwaysOnTop = false
                };
            }
            var session = await _mirrorSessions.StartAsync(serial, profile, card?.DisplayName, _lifetimeToken);
            StatusText = !string.IsNullOrWhiteSpace(session.RecordPath)
                ? $"已启动 {serial} 的镜像并录制到 {session.RecordPath}"
                : $"已使用“{session.ProfileName}”预设启动 {serial} 的镜像";
            StatusText += AudioPlaybackNotice(session);
        }
        catch (Exception exception)
        {
            StatusText = $"镜像启动失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task StopMirrorAsync(string serial)
    {
        if (_disposed) return;
        var stopping = _mirrorSessions.ActiveSessions.FirstOrDefault(session => session.DeviceSerial == serial);
        EnterBusy();
        StatusText = $"正在停止 {serial} 的镜像…";
        try
        {
            await _mirrorSessions.StopAsync(serial, _lifetimeToken);
            StatusText = stopping is not null && _sessionFailures.TryRemove(stopping.Id, out var failure)
                ? $"镜像已停止，录屏异常结束：{failure}"
                : $"已停止 {serial} 的镜像";
        }
        catch (Exception exception)
        {
            StatusText = $"停止镜像失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task ToggleMirrorRecordingAsync(string serial, string? recordPath = null)
    {
        if (_disposed) return;
        var current = _mirrorSessions.ActiveSessions.FirstOrDefault(session =>
            session.DeviceSerial == serial && session.State == MirrorSessionState.Running);
        if (current is null)
        {
            StatusText = $"设备 {serial} 没有正在运行的镜像";
            return;
        }

        var isStoppingRecording = !string.IsNullOrWhiteSpace(current.RecordPath);
        if (!isStoppingRecording && string.IsNullOrWhiteSpace(recordPath))
        {
            StatusText = "请选择录屏文件";
            return;
        }

        EnterBusy();
        var completedPath = current.RecordPath;
        StatusText = isStoppingRecording
            ? $"正在停止 {serial} 的录制并恢复普通镜像…"
            : $"正在为 {serial} 开始录制，镜像会短暂重启…";
        try
        {
            var profile = MirrorRecordingProfile.Create(current, isStoppingRecording ? null : recordPath);
            var restarted = await _mirrorSessions.RestartAsync(serial, profile, _lifetimeToken);
            if (_sessionFailures.TryRemove(current.Id, out var failure))
            {
                StatusText = $"镜像已重启，上一段录屏异常结束：{failure}";
            }
            else if (isStoppingRecording)
            {
                if (!string.IsNullOrWhiteSpace(completedPath)
                    && string.Equals(RecordingPath, completedPath, StringComparison.OrdinalIgnoreCase))
                {
                    RecordingPath = string.Empty;
                }
                StatusText = $"{serial} 的录制已停止并保存到 {completedPath}";
            }
            else
            {
                StatusText = $"{serial} 已开始录制到 {restarted.RecordPath}";
            }
            StatusText += AudioPlaybackNotice(restarted);
        }
        catch (Exception exception)
        {
            StatusText = $"切换录制状态失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    private static string AudioPlaybackNotice(MirrorSession session) =>
        session.Profile?.AudioEnabled == true && !session.AudioPlaybackEnabled
            ? "；本机无默认音频输出，已关闭本机声音播放"
            : string.Empty;

    public async Task RunDiagnosticsAsync()
    {
        if (_disposed || _diagnosticsRunning) return;
        _diagnosticsRunning = true;
        EnterBusy();
        StatusText = "正在运行环境诊断…";
        try
        {
            var results = await _diagnosticsService.RunAsync(_lifetimeToken);
            if (_disposed) return;
            Diagnostics.Clear();
            foreach (var result in results)
            {
                Diagnostics.Add(new DiagnosticItemViewModel(result));
            }

            var errors = results.Count(item => item.Severity == DiagnosticSeverity.Error);
            var warnings = results.Count(item => item.Severity == DiagnosticSeverity.Warning);
            StatusText = errors > 0
                ? $"诊断完成：{errors} 项错误，{warnings} 项警告"
                : $"诊断完成：没有错误，{warnings} 项警告";
        }
        catch (OperationCanceledException)
        {
            StatusText = "诊断已取消";
        }
        catch (Exception exception)
        {
            StatusText = $"诊断失败：{exception.Message}";
        }
        finally
        {
            _diagnosticsRunning = false;
            ExitBusy();
        }
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_disposed || _checkingUpdates || IsUpdateDownloading) return;
        _checkingUpdates = true;
        EnterBusy();
        UpdateStatusText = "正在检查 GitHub Release…";
        try
        {
            var update = await _updates.CheckAsync(_lifetimeToken);
            if (_disposed) return;
            _availableUpdate = update;
            UpdateAvailable = update.IsUpdateAvailable;
            OnPropertyChanged(nameof(LatestUpdateVersion));
            OnPropertyChanged(nameof(CanDownloadAndInstallUpdate));
            UpdateStatusText = update.IsUpdateAvailable
                ? update.Installer is not null
                    ? $"发现新版本 {update.LatestVersion}，可直接下载并安装"
                    : $"发现新版本 {update.LatestVersion}，但没有可验证的 Windows 安装包"
                : $"当前已是最新版本 {update.CurrentVersion}";
        }
        catch (Exception exception)
        {
            _availableUpdate = null;
            UpdateAvailable = false;
            OnPropertyChanged(nameof(LatestUpdateVersion));
            OnPropertyChanged(nameof(CanDownloadAndInstallUpdate));
            UpdateStatusText = $"检查更新失败：{exception.Message}";
        }
        finally
        {
            _checkingUpdates = false;
            ExitBusy();
        }
    }

    public async Task<string?> DownloadAndVerifyUpdateAsync(AppUpdateInfo update)
    {
        if (_disposed || update.Installer is null || !update.IsUpdateAvailable || IsBusy || IsUpdateDownloading) return null;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _updateDownloadCancellation = cancellation;
        IsUpdateDownloading = true;
        UpdateDownloadProgress = 0;
        EnterBusy();
        UpdateStatusText = $"正在下载 {update.LatestVersion} 安装包…";
        try
        {
            var progress = new Progress<UpdateDownloadProgress>(item =>
            {
                if (_disposed || !ReferenceEquals(_updateDownloadCancellation, cancellation)) return;
                UpdateDownloadProgress = item.Percentage;
                UpdateStatusText = $"正在下载 {update.LatestVersion}：{item.Percentage:F0}%";
            });
            var directory = Path.Combine(DataDirectory, "Updates", update.LatestVersion);
            var path = await _updates.DownloadInstallerAsync(update, directory, progress, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            UpdateDownloadProgress = 100;
            UpdateStatusText = $"{update.LatestVersion} 下载完成，大小和 SHA256 校验通过";
            return path;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            UpdateStatusText = "更新下载已取消";
            return null;
        }
        catch (Exception exception)
        {
            UpdateStatusText = $"更新下载失败：{exception.Message}";
            return null;
        }
        finally
        {
            if (ReferenceEquals(_updateDownloadCancellation, cancellation))
            {
                _updateDownloadCancellation = null;
            }
            cancellation.Dispose();
            IsUpdateDownloading = false;
            ExitBusy();
        }
    }

    public void CancelUpdateDownload() => _updateDownloadCancellation?.Cancel();

    public Task<IDisposable> AcquireVerifiedInstallerAsync(AppUpdateInfo update, string path) =>
        _updates.AcquireVerifiedInstallerAsync(update, path, _lifetimeToken);

    public async Task<DeviceDetails?> GetDeviceDetailsAsync(string? serial)
    {
        if (_disposed) return null;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return null;
        }
        EnterBusy();
        StatusText = $"正在读取 {serial} 的设备信息…";
        try
        {
            var details = await _adb.GetDeviceDetailsAsync(serial, _lifetimeToken);
            if (_disposed) return null;
            StatusText = $"已读取 {serial} 的设备信息";
            return details;
        }
        catch (Exception exception)
        {
            StatusText = $"读取设备信息失败：{exception.Message}";
            return null;
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task CaptureScreenshotAsync(string? serial, string localPath)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        EnterBusy();
        StatusText = $"正在截取 {serial} 的屏幕…";
        try
        {
            var result = await _adb.CaptureScreenshotAsync(serial, localPath, _lifetimeToken);
            StatusText = $"截图已保存到 {result}";
        }
        catch (Exception exception)
        {
            StatusText = $"截图失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task ExportLogcatAsync(string? serial, string localPath)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        EnterBusy();
        StatusText = $"正在导出 {serial} 的 Logcat…";
        try
        {
            var content = await _adb.GetLogcatSnapshotAsync(serial, 2000, _lifetimeToken);
            await File.WriteAllTextAsync(localPath, content, System.Text.Encoding.UTF8, _lifetimeToken);
            StatusText = $"Logcat 已保存到 {localPath}";
        }
        catch (Exception exception)
        {
            StatusText = $"Logcat 导出失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task PullRemoteFileAsync(string? serial)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        EnterBusy();
        StatusText = $"正在从 {serial} 下载 {RemoteFilePath}…";
        try
        {
            var result = await _adb.PullFileAsync(serial, RemoteFilePath, LocalDownloadDirectory, _lifetimeToken);
            StatusText = string.IsNullOrWhiteSpace(result) ? "设备文件下载完成" : $"下载完成：{result}";
        }
        catch (Exception exception)
        {
            StatusText = $"设备文件下载失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task SendDeviceKeyAsync(int keyCode)
    {
        if (_disposed) return;
        var serial = SelectedDeviceSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        try
        {
            await _adb.SendKeyEventAsync(serial, keyCode, _lifetimeToken);
            StatusText = "设备控制指令已发送";
        }
        catch (Exception exception)
        {
            StatusText = $"设备控制失败：{exception.Message}";
        }
    }

    public async Task RefreshInstalledAppsAsync(bool includeSystemApps)
    {
        if (_disposed) return;
        var version = ++_appsRequestVersion;
        var serial = SelectedDeviceSerial;
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        EnterBusy();
        try
        {
            var apps = await _adb.GetInstalledAppsAsync(serial, includeSystemApps, _lifetimeToken);
            if (_disposed || version != _appsRequestVersion) return;
            if (!string.Equals(serial, SelectedDeviceSerial, StringComparison.Ordinal))
            {
                StatusText = $"{serial} 的应用列表读取完成；当前目标设备已切换，结果未显示";
                return;
            }
            InstalledApps.Clear();
            foreach (var app in apps) InstalledApps.Add(new InstalledAppViewModel(app));
            SelectedAppPackage = InstalledApps.FirstOrDefault()?.PackageName;
            StatusText = $"已读取 {apps.Count} 个应用";
        }
        catch (Exception exception)
        {
            if (!_disposed && version == _appsRequestVersion)
                StatusText = $"读取应用失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task RunAppActionAsync(string action, string? serial, string? packageName)
    {
        if (_disposed) return;
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(packageName))
        {
            StatusText = "请选择设备和应用";
            return;
        }
        EnterBusy();
        try
        {
            switch (action)
            {
                case "launch": await _adb.LaunchAppAsync(serial, packageName, _lifetimeToken); break;
                case "stop": await _adb.ForceStopAppAsync(serial, packageName, _lifetimeToken); break;
                case "uninstall": await _adb.UninstallAppAsync(serial, packageName, _lifetimeToken); break;
                default: throw new ArgumentOutOfRangeException(nameof(action));
            }
            if (action == "uninstall" && string.Equals(serial, SelectedDeviceSerial, StringComparison.Ordinal))
            {
                await RefreshInstalledAppsAsync(includeSystemApps: false);
            }
            StatusText = $"已在 {serial} 完成应用操作：{packageName}";
        }
        catch (Exception exception)
        {
            StatusText = $"应用操作失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task UninstallPackageByNameAsync(string? serial, string packageName)
    {
        if (_disposed) return;
        packageName = packageName.Trim();
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        if (string.IsNullOrWhiteSpace(packageName))
        {
            StatusText = "请输入需要卸载的应用包名";
            return;
        }

        EnterBusy();
        try
        {
            await _adb.UninstallAppAsync(serial, packageName, _lifetimeToken);
            if (string.Equals(PackageNameInput.Trim(), packageName, StringComparison.Ordinal)) PackageNameInput = string.Empty;
            if (string.Equals(serial, SelectedDeviceSerial, StringComparison.Ordinal))
                await RefreshInstalledAppsAsync(includeSystemApps: false);
            StatusText = $"已从 {serial} 卸载 {packageName}";
        }
        catch (Exception exception)
        {
            StatusText = $"按包名卸载失败：{exception.Message}";
        }
        finally
        {
            ExitBusy();
        }
    }

    public async Task RunDeviceShellCommandAsync(string? serial, string command)
    {
        if (_disposed) return;
        command = command.Trim();
        if (string.IsNullOrWhiteSpace(serial))
        {
            StatusText = "请选择一台目标设备";
            return;
        }
        if (string.IsNullOrWhiteSpace(command))
        {
            StatusText = "请输入需要在设备上执行的命令";
            return;
        }
        if (_shellCommandCancellation is not null || IsBusy) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeToken);
        _shellCommandCancellation = cancellation;
        IsShellCommandRunning = true;
        EnterBusy();
        StatusText = $"正在 {serial} 上执行 ADB Shell 命令…";
        ShellOutput = $"$ {command}{Environment.NewLine}正在执行…";
        try
        {
            var output = await _adb.RunShellCommandAsync(serial, command, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            ShellOutput = $"$ {command}{Environment.NewLine}{output}";
            StatusText = $"{serial} 的设备命令执行完成";
        }
        catch (OperationCanceledException)
        {
            ShellOutput = $"$ {command}{Environment.NewLine}（命令已取消）";
            StatusText = "设备命令已取消";
        }
        catch (Exception exception)
        {
            ShellOutput = $"$ {command}{Environment.NewLine}[错误] {exception.Message}";
            StatusText = $"设备命令失败：{exception.Message}";
        }
        finally
        {
            if (ReferenceEquals(_shellCommandCancellation, cancellation))
            {
                _shellCommandCancellation = null;
            }
            cancellation.Dispose();
            IsShellCommandRunning = false;
            ExitBusy();
        }
    }

    public void CancelDeviceShellCommand() => _shellCommandCancellation?.Cancel();

    public void ClearShellOutput() => ShellOutput = "尚未执行设备命令。";

    public async Task ArrangeMirrorWindowsAsync(MirrorWindowLayout layout)
    {
        if (_disposed) return;
        try
        {
            var count = await _mirrorSessions.ArrangeWindowsAsync(layout, _lifetimeToken);
            StatusText = count == 0 ? "没有可排列的镜像窗口" : $"已排列 {count} 个镜像窗口";
        }
        catch (Exception exception)
        {
            StatusText = $"窗口排列失败：{exception.Message}";
        }
    }

    private async Task SaveSettingsSafelyAsync()
    {
        try
        {
            await _settingsStore.SaveAsync(_settings, _lifetimeToken);
        }
        catch (OperationCanceledException) when (_disposed) { }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            StatusText = $"设置暂时无法保存：{exception.Message}";
        }
    }

    private async Task RememberEndpointsAsync(IEnumerable<string> endpoints)
    {
        if (_disposed) return;
        var updated = _settings.RememberedEndpoints;
        foreach (var endpoint in endpoints)
        {
            try
            {
                updated = ConnectionHistory.Add(updated, AdbEndpoint.Normalize(endpoint));
            }
            catch (ArgumentException)
            {
                // Ignore malformed serials reported by third-party ADB implementations.
            }
        }

        if (updated.SequenceEqual(_settings.RememberedEndpoints, StringComparer.OrdinalIgnoreCase)) return;
        _settings = _settings with
        {
            SchemaVersion = AppSettings.CurrentSchemaVersion,
            LastEndpoint = updated.FirstOrDefault() ?? string.Empty,
            AutoReconnect = false,
            HasConnectedBefore = updated.Length > 0,
            RememberedEndpoints = updated
        };
        ReplaceRememberedEndpoints(updated);
        await SaveSettingsSafelyAsync();
    }

    private void ReplaceRememberedEndpoints(IEnumerable<string> endpoints)
    {
        RememberedEndpoints.Clear();
        foreach (var endpoint in endpoints) RememberedEndpoints.Add(endpoint);
        OnPropertyChanged(nameof(HasRememberedEndpoints));
    }

    private static string[] NormalizeConnectionEndpoints(IEnumerable<string> endpoints)
    {
        var normalized = new List<string>();
        foreach (var endpoint in endpoints)
        {
            try
            {
                normalized.Add(AdbEndpoint.Normalize(endpoint));
            }
            catch (ArgumentException)
            {
                // Ignore invalid entries left by older settings files.
            }
        }
        return ConnectionHistory.Normalize(normalized);
    }

    private void EnterBusy()
    {
        if (Interlocked.Increment(ref _busyCount) == 1) IsBusy = true;
    }

    private void ExitBusy()
    {
        var remaining = Interlocked.Decrement(ref _busyCount);
        if (remaining <= 0)
        {
            Interlocked.Exchange(ref _busyCount, 0);
            IsBusy = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _mirrorSessions.SessionChanged -= OnSessionChanged;
        var refreshCancellation = Interlocked.Exchange(ref _refreshCancellation, null);
        refreshCancellation?.Cancel();
        _transferCancellation?.Cancel();
        _updateDownloadCancellation?.Cancel();
        _shellCommandCancellation?.Cancel();
        _performanceCancellation?.Cancel();
        _refreshCoordinator.InvalidatePendingRefreshes();
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
    }

    private void OnSessionChanged(object? sender, MirrorSession session)
    {
        if (_disposed) return;
        if (session.State == MirrorSessionState.Failed)
            _sessionFailures[session.Id] = session.Error ?? "镜像或录屏异常退出";
        _uiContext.Post(_ =>
        {
            if (_disposed) return;
            var card = Devices.FirstOrDefault(device => device.Serial == session.DeviceSerial);
            var existing = Sessions.FirstOrDefault(item => item.DeviceSerial == session.DeviceSerial);
            var isActiveState = session.State is MirrorSessionState.Starting or MirrorSessionState.Running or MirrorSessionState.Stopping;
            var currentSession = _mirrorSessions.ActiveSessions.FirstOrDefault(item => item.DeviceSerial == session.DeviceSerial);
            if (isActiveState && (currentSession is null || currentSession.Id != session.Id || currentSession.State != session.State)) return;
            var isStaleTerminalEvent = !isActiveState
                && ((existing is not null && existing.Session.Id != session.Id)
                    || (currentSession is not null && currentSession.Id != session.Id));
            if (!isStaleTerminalEvent)
            {
                if (card is not null) card.IsMirroring = session.State == MirrorSessionState.Running;
                if (existing is not null) Sessions.Remove(existing);
                if (isActiveState) Sessions.Add(new MirrorSessionCardViewModel(session, card?.DisplayName));
                OnPropertyChanged(nameof(HasNoSessions));
                OnPropertyChanged(nameof(CanArrangeMirrorWindows));
                OnPropertyChanged(nameof(SelectedDeviceIsMirroring));
                OnPropertyChanged(nameof(CanStartSelectedMirror));
                OnPropertyChanged(nameof(CanStopSelectedMirror));
            }
            if (!string.IsNullOrWhiteSpace(session.RecordPath))
            {
                var recording = Recordings.FirstOrDefault(item => item.SessionId == session.Id);
                if (recording is null)
                {
                    recording = new RecordingItemViewModel(session);
                    Recordings.Insert(0, recording);
                }
                else
                {
                    recording.Update(session);
                }
                OnPropertyChanged(nameof(HasNoRecordings));
            }
            if (session.State == MirrorSessionState.Failed && !isStaleTerminalEvent)
            {
                StatusText = string.IsNullOrWhiteSpace(session.Error)
                    ? $"{session.DeviceSerial} 的镜像或录屏异常退出"
                    : $"镜像或录屏失败：{session.Error}";
            }
        }, null);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (_disposed || EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (!_disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class DeviceCardViewModel(DeviceInfo device, bool isMirroring) : INotifyPropertyChanged
{
    private bool _isMirroring = isMirroring;
    public string Serial => device.Serial;
    public string DisplayName => device.DisplayName;
    public string Model => device.Model;
    internal bool Represents(DeviceInfo other) => device.Serial == other.Serial && device.Model == other.Model
        && device.Product == other.Product && device.State == other.State && device.ConnectionKind == other.ConnectionKind;
    public DeviceState State => device.State;
    public string StateLabel => device.State switch
    {
        DeviceState.Online => "在线",
        DeviceState.Offline => "离线",
        DeviceState.Unauthorized => "未授权",
        _ => device.State.ToString()
    };
    public string ConnectionLabel => device.ConnectionKind == ConnectionKind.TcpIp ? "Wi-Fi" : "USB";
    public Brush StateIndicatorBrush => new SolidColorBrush(device.State switch
    {
        DeviceState.Online => ColorHelper.FromArgb(255, 48, 200, 117),
        DeviceState.Unauthorized => ColorHelper.FromArgb(255, 234, 163, 0),
        DeviceState.Error => ColorHelper.FromArgb(255, 232, 93, 93),
        _ => ColorHelper.FromArgb(255, 138, 138, 138)
    });
    public bool IsOnline => device.State == DeviceState.Online;
    public bool CanDisconnect => device.ConnectionKind == ConnectionKind.TcpIp;
    public string MirrorButtonText => IsMirroring ? "运行中" : "打开镜像";

    public bool IsMirroring
    {
        get => _isMirroring;
        set
        {
            if (_isMirroring == value) return;
            _isMirroring = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMirroring)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MirrorButtonText)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record MirrorSessionCardViewModel(MirrorSession Session, string? DisplayName = null)
{
    public string DeviceSerial => Session.DeviceSerial;
    public string DeviceDisplayName => string.IsNullOrWhiteSpace(DisplayName) ? DeviceSerial : DisplayName;
    public string StateLabel => Session.State switch
    {
        MirrorSessionState.Starting => "正在启动",
        MirrorSessionState.Running => "运行中",
        MirrorSessionState.Stopping => "正在停止",
        _ => Session.State.ToString()
    };
    public string StartedAtLabel => Session.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string PerformanceLabel => $"{Session.ProfileName} · {Session.VideoCodec.ToUpperInvariant()} · {Session.MaxSize}px · {Session.MaxFps} FPS · {Session.VideoBitRateMbps} Mbps";
    public string RecordingLabel => string.IsNullOrWhiteSpace(Session.RecordPath) ? "未录制" : $"录制：{System.IO.Path.GetFileName(Session.RecordPath)}";
    public bool IsRecording => !string.IsNullOrWhiteSpace(Session.RecordPath);
    public string RecordingButtonText => IsRecording ? "停止录制" : "开始录制";
}

public sealed record DiagnosticItemViewModel(DiagnosticItem Item)
{
    public string Title => Item.Title;
    public string Detail => Item.Detail;
    public string StateLabel => Item.Severity switch
    {
        DiagnosticSeverity.Success => "正常",
        DiagnosticSeverity.Warning => "警告",
        DiagnosticSeverity.Error => "错误",
        _ => "未知"
    };
    public string Glyph => Item.Severity switch
    {
        DiagnosticSeverity.Success => "\uE930",
        DiagnosticSeverity.Warning => "\uE7BA",
        DiagnosticSeverity.Error => "\uEA39",
        _ => "\uE946"
    };
}

public sealed record InstalledAppViewModel(InstalledApp App)
{
    public string PackageName => App.PackageName;
}

public sealed class RecordingItemViewModel : INotifyPropertyChanged
{
    private string _status;
    private string _sizeLabel = "—";
    public RecordingItemViewModel(MirrorSession session)
    {
        SessionId = session.Id;
        DeviceSerial = session.DeviceSerial;
        Path = session.RecordPath ?? string.Empty;
        StartedAt = session.StartedAt;
        _status = StateLabel(session.State);
        Update(session);
    }
    public string SessionId { get; }
    public string DeviceSerial { get; }
    public string Path { get; }
    public DateTimeOffset StartedAt { get; }
    public string FileName => System.IO.Path.GetFileName(Path);
    public string StartedAtLabel => StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeLabel { get => _sizeLabel; private set { if (_sizeLabel == value) return; _sizeLabel = value; PropertyChanged?.Invoke(this, new(nameof(SizeLabel))); } }
    public string Status { get => _status; private set { if (_status == value) return; _status = value; PropertyChanged?.Invoke(this, new(nameof(Status))); } }
    public void Update(MirrorSession session)
    {
        Status = StateLabel(session.State);
        if (session.State == MirrorSessionState.Failed && !string.IsNullOrWhiteSpace(session.Error))
        {
            Status = $"失败：{session.Error}";
        }
        if (session.State == MirrorSessionState.Exited)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    Status = "文件未生成";
                    return;
                }
                var bytes = new FileInfo(Path).Length;
                SizeLabel = bytes < 1024 * 1024 ? $"{bytes / 1024d:F1} KB" : $"{bytes / 1024d / 1024d:F1} MB";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                SizeLabel = "暂不可用";
            }
        }
    }
    private static string StateLabel(MirrorSessionState state) => state switch
    {
        MirrorSessionState.Running => "录制中",
        MirrorSessionState.Stopping => "正在结束",
        MirrorSessionState.Exited => "已完成",
        MirrorSessionState.Failed => "失败",
        _ => "准备中"
    };
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class TransferItemViewModel : INotifyPropertyChanged
{
    private string _status = "等待中";
    private bool _isComplete;
    public TransferItemViewModel(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        var length = new FileInfo(path).Length;
        SizeLabel = length switch
        {
            < 1024 => $"{length} B",
            < 1024 * 1024 => $"{length / 1024d:F1} KB",
            _ => $"{length / 1024d / 1024d:F1} MB"
        };
    }
    public string Path { get; }
    public string FileName { get; }
    public string SizeLabel { get; }
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }
    public bool IsComplete
    {
        get => _isComplete;
        set => _isComplete = value;
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
