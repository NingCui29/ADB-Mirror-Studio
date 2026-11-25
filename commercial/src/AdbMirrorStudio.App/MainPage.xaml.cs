using AdbMirrorStudio.App.ViewModels;
using AdbMirrorStudio.App.Mirroring;
using AdbMirrorStudio.Domain.Mirroring;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using System.Diagnostics;
using Windows.System;

namespace AdbMirrorStudio.App;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _performanceTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private MirrorToolbarHost? _mirrorToolbars;
    private bool _initializingSettings;
    private bool _shutdown;
    private bool _synchronizingNavigation;
    private string _selectedSection = "screen";
    public MainViewModel? ViewModel { get; private set; }

    public MainPage()
    {
        InitializeComponent();
        SelectSection("screen");
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
        _performanceTimer.Tick += PerformanceTimer_Tick;
        AddHandler(KeyDownEvent, new KeyEventHandler(MainPage_KeyDown), true);
    }

    // WinUI cannot await event handlers. Application.UnhandledException is the
    // global barrier for the async void handlers below; periodic sources are guarded
    // locally as well so one failing device operation cannot flood the crash log.
    private void Guard(Func<Task> operation)
    {
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await operation();
            }
            catch (Exception exception)
            {
                if (!_shutdown) CrashLog.Write(exception);
            }
        }
    }

    private void SelectSection(string section)
    {
        if (PageViews is null || ScreenScrollViewer is null) return;
        if (section is not ("screen" or "files" or "apps" or "terminal" or "tasks" or "settings"))
            section = "screen";

        _selectedSection = section;
        _synchronizingNavigation = true;
        try
        {
            Navigation.SelectedItem = Navigation.Items.OfType<ListViewItem>()
                .FirstOrDefault(item => item.Tag as string == section);
            FooterNavigation.SelectedItem = FooterNavigation.Items.OfType<ListViewItem>()
                .FirstOrDefault(item => item.Tag as string == section);
        }
        finally
        {
            _synchronizingNavigation = false;
        }

        WorkspaceView.Visibility = section is "tasks" or "settings" ? Visibility.Collapsed : Visibility.Visible;
        TasksView.Visibility = section == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;
        ScreenScrollViewer.Visibility = section == "screen" ? Visibility.Visible : Visibility.Collapsed;
        FilesScrollViewer.Visibility = section == "files" ? Visibility.Visible : Visibility.Collapsed;
        AppsScrollViewer.Visibility = section == "apps" ? Visibility.Visible : Visibility.Collapsed;
        TerminalScrollViewer.Visibility = section == "terminal" ? Visibility.Visible : Visibility.Collapsed;
        (WorkspaceTitle.Text, WorkspaceDescription.Text) = section switch
        {
            "files" => ("文件管理", "在电脑与当前设备之间发送和获取文件。"),
            "apps" => ("应用管理", "安装应用，管理当前设备上的应用程序。"),
            "terminal" => ("终端", "向当前设备发送 Shell 命令，查看和导出输出。"),
            _ => ("屏幕镜像", "查看画面，直接用键盘和鼠标控制。")
        };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not AppServices services) return;
        _initializingSettings = true;
        ViewModel = new MainViewModel(services);
        DataContext = ViewModel;
        _mirrorToolbars = new MirrorToolbarHost(DispatcherQueue, services.MirrorSessions, RunMirrorToolbarAction);
        try
        {
            await ViewModel.InitializeAsync();
            if (_shutdown) return;
            ApplyTheme(ViewModel.Theme, updateSelector: true);
            AutoRefreshToggle.IsOn = ViewModel.AutoRefresh;
            PerformanceMonitoringToggle.IsOn = ViewModel.PerformanceMonitoringEnabled;
            if (!ViewModel.FirstRunCompleted) await ShowFirstRunDialogAsync();
            if (!_shutdown)
            {
                _autoRefreshTimer.Start();
                UpdatePerformanceMonitoring();
            }
        }
        catch (Exception exception)
        {
            if (_shutdown) return;
            CrashLog.Write(exception);
            var root = XamlRoot;
            if (_shutdown || root is null) return;
            await new ContentDialog
            {
                XamlRoot = root,
                Title = "初始化失败",
                Content = exception.Message,
                CloseButtonText = "关闭"
            }.ShowAsync();
        }
        finally
        {
            _initializingSettings = false;
        }
    }

    internal void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;
        IsEnabled = false;
        _autoRefreshTimer.Stop();
        _autoRefreshTimer.Tick -= AutoRefreshTimer_Tick;
        _performanceTimer.Stop();
        _performanceTimer.Tick -= PerformanceTimer_Tick;
        _mirrorToolbars?.Dispose();
        ViewModel?.Dispose();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.RefreshAsync();
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        ViewModel.Endpoint = EndpointBox.Text?.Trim() ?? string.Empty;
        await ViewModel.ConnectAsync();
    }

    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.ClearRememberedEndpointsAsync();
    }

    private async void MirrorAction_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || GetCommandSerial(sender) is not { } serial) return;
        if (ViewModel.Sessions.Any(session => session.DeviceSerial == serial))
            await ViewModel.StopMirrorAsync(serial);
        else
            await ViewModel.StartMirrorAsync(serial);
    }

    private void PairingCodeBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is PasswordBox passwordBox)
        {
            ViewModel.PairingCode = passwordBox.Password;
        }
    }

    private async void Pair_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        await ViewModel.PairAsync();
        PairingCodeBox.Password = string.Empty;
    }

    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || GetCommandSerial(sender) is not { } serial) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认断开设备",
            Content = $"将断开设备 {ViewModel.GetDeviceLabel(serial)}，当前镜像和传输可能中断。",
            PrimaryButtonText = "断开连接",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_shutdown)
            await ViewModel.DisconnectAsync(serial);
    }

    private async void Reboot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || GetCommandSerial(sender) is not { } serial) return;
        var target = ViewModel.GetDeviceLabel(serial);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认重启设备",
            Content = $"设备 {target} 将立即重新启动，当前镜像和传输会中断。",
            PrimaryButtonText = "重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) await ViewModel.RebootAsync(serial);
    }

    private async void EnableTcpIp_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || GetCommandSerial(sender) is not { } serial) return;
        var portBox = new NumberBox
        {
            Header = "监听端口",
            Value = 5555,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "启用 ADB TCP/IP",
            Content = portBox,
            PrimaryButtonText = "启用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !double.IsNaN(portBox.Value))
        {
            await ViewModel.EnableTcpIpAsync(serial, (int)portBox.Value);
        }
    }

    private async void StopMirror_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || GetCommandSerial(sender) is not { } serial) return;
        await ViewModel.StopMirrorAsync(serial);
    }

    private async void ToggleSessionRecording_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || sender is not Button { DataContext: MirrorSessionCardViewModel session }) return;
        if (session.IsRecording)
        {
            await ViewModel.ToggleMirrorRecordingAsync(session.DeviceSerial);
            return;
        }

        var file = await PickRecordingFileAsync();
        if (file is not null) await ViewModel.ToggleMirrorRecordingAsync(session.DeviceSerial, file.Path);
    }

    private async void ArrangeWindows_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { Tag: string tag }) return;
        var layout = tag switch
        {
            "horizontal" => AdbMirrorStudio.Domain.Mirroring.MirrorWindowLayout.Horizontal,
            "vertical" => AdbMirrorStudio.Domain.Mirroring.MirrorWindowLayout.Vertical,
            _ => AdbMirrorStudio.Domain.Mirroring.MirrorWindowLayout.Grid
        };
        await ViewModel.ArrangeMirrorWindowsAsync(layout);
    }

    private static string? GetCommandSerial(object sender) => sender switch
    {
        Button { CommandParameter: string serial } => serial,
        MenuFlyoutItem { CommandParameter: string serial } => serial,
        FrameworkElement { Tag: string serial } => serial,
        _ => null
    };

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.RunDiagnosticsAsync();
    }

    private async void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_synchronizingNavigation || PageViews is null || sender is not ListView list) return;
        if (list.SelectedItem is not ListViewItem { Tag: string section })
        {
            SelectSection(_selectedSection);
            return;
        }
        SelectSection(section);
        if (section == "settings" && ViewModel is not null && ViewModel.Diagnostics.Count == 0)
        {
            await ViewModel.RunDiagnosticsAsync();
        }
    }

    private void OpenDevices_Click(object sender, RoutedEventArgs e)
    {
        SelectSection("screen");
        RefreshDevicesButton?.Focus(FocusState.Programmatic);
    }

    private void OpenAddDevice_Click(object sender, RoutedEventArgs e) =>
        AddDeviceButton.Flyout?.ShowAt(AddDeviceButton);

    private void OpenTasks_Click(object sender, RoutedEventArgs e)
    {
        MirrorSettingsButton.Flyout?.Hide();
        SelectSection("tasks");
        TasksNavigationItem.Focus(FocusState.Programmatic);
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        SelectSection("terminal");
        TerminalNavigationItem.Focus(FocusState.Programmatic);
    }

    private async void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        SelectSection("settings");
        SettingsView.UpdateLayout();
        DiagnosticsCard.StartBringIntoView();
        SettingsNavigationItem.Focus(FocusState.Programmatic);
        if (ViewModel is not null && ViewModel.Diagnostics.Count == 0)
        {
            await ViewModel.RunDiagnosticsAsync();
        }
    }

    private async void ChooseApk_Click(object sender, RoutedEventArgs e) =>
        await ChooseFileAsync([".apk"]);

    private async void ChooseFile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
        var files = await picker.PickMultipleFilesAsync();
        if (_shutdown || files.Count == 0) return;
        ViewModel.SetTransferFiles(files.Select(file => file.Path));
    }

    private async Task ChooseFileAsync(IReadOnlyList<string> filters)
    {
        if (ViewModel is null || Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.Downloads
        };
        foreach (var filter in filters) picker.FileTypeFilter.Add(filter);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file is not null && !_shutdown) ViewModel.ApkFilePath = file.Path;
    }

    private async void InstallApk_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.InstallApkAsync(ViewModel.SelectedDeviceSerial);
    }

    private async void PushFile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.PushFileAsync(ViewModel.SelectedDeviceSerial);
    }

    private void CancelTransfer_Click(object sender, RoutedEventArgs e) => ViewModel?.CancelTransfer();

    private async void DeviceDetails_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var serial = GetCommandSerial(sender) ?? ViewModel.SelectedDeviceSerial;
        var details = await ViewModel.GetDeviceDetailsAsync(serial);
        if (details is null || _shutdown) return;

        var battery = details.BatteryLevel is null
            ? details.BatteryStatus
            : $"{details.BatteryLevel}%（{details.BatteryStatus}）";
        var content = new TextBlock
        {
            Text = $"序列号：{details.Serial}\nAndroid：{details.AndroidVersion}（API {details.ApiLevel}）\n" +
                   $"分辨率：{details.Resolution}\n电池：{battery}\n存储：{details.StorageSummary}",
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "设备详情",
            Content = content,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }

    private async void RefreshApps_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.RefreshInstalledAppsAsync(includeSystemApps: false);
    }

    private async void AppAction_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not FrameworkElement { Tag: string action }) return;
        var serial = ViewModel.SelectedDeviceSerial;
        var packageName = ViewModel.SelectedAppPackage;
        if (action == "uninstall")
        {
            var target = ViewModel.GetDeviceLabel(serial);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认卸载应用",
                Content = $"将从设备 {target} 卸载 {packageName} 并删除其应用数据。",
                PrimaryButtonText = "卸载",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        if (!_shutdown) await ViewModel.RunAppActionAsync(action, serial, packageName);
    }

    private async void UninstallPackage_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var packageName = ViewModel.PackageNameInput.Trim();
        var serial = ViewModel.SelectedDeviceSerial;
        if (string.IsNullOrWhiteSpace(packageName) || string.IsNullOrWhiteSpace(serial))
        {
            await ViewModel.UninstallPackageByNameAsync(serial, packageName);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认按包名卸载",
            Content = $"将从设备 {ViewModel.GetDeviceLabel(serial)} 卸载 {packageName}，并删除该应用的设备数据。",
            PrimaryButtonText = "卸载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_shutdown)
        {
            await ViewModel.UninstallPackageByNameAsync(serial, packageName);
        }
    }

    private async void RunShellCommand_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy) return;
        var serial = ViewModel.SelectedDeviceSerial;
        var command = ViewModel.ShellCommand.Trim();
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(command))
        {
            await ViewModel.RunDeviceShellCommandAsync(serial, command);
            return;
        }
        if (!ViewModel.TryValidateDeviceShellCommand(command)) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "确认执行 ADB Shell",
            Content = $"目标设备：{ViewModel.GetDeviceLabel(serial)}\n\n命令：{command}\n\n设备端 sh 将解释此命令，可能修改或删除设备数据。",
            PrimaryButtonText = "执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !_shutdown)
        {
            await ViewModel.RunDeviceShellCommandAsync(serial, command);
        }
    }

    private void CancelShellCommand_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.CancelDeviceShellCommand();

    private void ClearShellOutput_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.ClearShellOutput();

    private void CopyStatus_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || string.IsNullOrWhiteSpace(ViewModel.StatusText)) return;
        var data = new DataPackage();
        data.SetText(ViewModel.StatusText);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    private async void SaveScreenshot_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not { } model || Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        var serial = model.SelectedDeviceSerial;
        var file = await PickScreenshotPathAsync(WindowNative.GetWindowHandle(app.MainWindow));
        if (file is not null && !_shutdown) await model.CaptureScreenshotAsync(serial, file.Path);
    }

    private void RunMirrorToolbarAction(MirrorSession session, nint toolbarWindow, int? keyCode) => Guard(async () =>
    {
        if (_shutdown || ViewModel is not { } model) return;
        if (keyCode is { } key)
        {
            await model.SendMirrorKeyAsync(session, key);
        }
        else if (model.CanUseMirrorSession(session))
        {
            var file = await PickScreenshotPathAsync(toolbarWindow);
            if (file is not null && !_shutdown) await model.CaptureMirrorScreenshotAsync(session, file.Path);
        }
    });

    private static async Task<StorageFile?> PickScreenshotPathAsync(nint ownerWindow)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"android-screen-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("PNG 图片", [".png"]);
        InitializeWithWindow.Initialize(picker, ownerWindow);
        return await picker.PickSaveFileAsync();
    }

    private async void ExportLogcat_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        var serial = ViewModel.SelectedDeviceSerial;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"android-logcat-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("文本日志", [".txt"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is not null && !_shutdown)
        {
            await ViewModel.ExportLogcatAsync(serial, file.Path);
        }
    }

    private async void ChooseDownloadDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.Downloads };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ViewModel.LocalDownloadDirectory = folder.Path;
    }

    private async void PullRemoteFile_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null)
        {
            await ViewModel.PullRemoteFileAsync(ViewModel.SelectedDeviceSerial);
        }
    }

    private void FileDrop_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "添加到传输队列";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void FileDrop_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        ViewModel.SetTransferFiles(items.OfType<StorageFile>().Select(file => file.Path));
    }

    private void ScreenDrop_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "直接安装 APK 或发送文件到设备";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    private async void ScreenDrop_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var files = items.OfType<StorageFile>().Select(file => file.Path).ToList();
        if (files.Count == 0) return;

        var apks = files.Where(file => file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();
        var otherFiles = files.Where(file => !file.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).ToList();

        if (otherFiles.Count > 0)
        {
            ViewModel.SetTransferFiles(otherFiles);
        }

        if (apks.Count > 0)
        {
            ViewModel.ApkFilePath = apks[0];
            if (ViewModel.CanUseSelectedDevice)
            {
                await ViewModel.InstallApkAsync(ViewModel.SelectedDeviceSerial);
                if (otherFiles.Count > 0)
                {
                    await ViewModel.PushFileAsync(ViewModel.SelectedDeviceSerial);
                }
            }
            else
            {
                ViewModel.StatusText = otherFiles.Count > 0
                    ? $"已就绪 APK 与 {otherFiles.Count} 个传输文件，请先选择目标设备"
                    : "已就绪 APK，请先选择目标设备";
            }
        }
        else
        {
            ViewModel.SetTransferFiles(otherFiles);
            if (ViewModel.CanUseSelectedDevice)
            {
                await ViewModel.PushFileAsync(ViewModel.SelectedDeviceSerial);
            }
            else
            {
                ViewModel.StatusText = $"已添加 {otherFiles.Count} 个文件到传输队列，请选择目标设备";
            }
        }
    }

    private async void MirrorProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || ViewModel is null || sender is not Selector selector) return;
        if (selector.SelectedValue is not string profileId)
        {
            selector.SelectedValue = ViewModel.SelectedMirrorProfileId;
            return;
        }
        await ViewModel.SetMirrorProfileAsync(profileId);
    }

    private async void ChooseRecordingPath_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var file = await PickRecordingFileAsync();
        if (file is not null) ViewModel.RecordingPath = file.Path;
    }

    private static async Task<StorageFile?> PickRecordingFileAsync()
    {
        if (Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return null;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.VideosLibrary,
            SuggestedFileName = $"adb-mirror-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("Matroska 视频", [".mkv"]);
        picker.FileTypeChoices.Add("MP4 视频", [".mp4"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
        return await picker.PickSaveFileAsync();
    }

    private void ClearRecordingPath_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) ViewModel.RecordingPath = string.Empty;
    }

    private void OpenRecordingDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { CommandParameter: string path } || string.IsNullOrWhiteSpace(path)) return;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        }
    }

    private async void AutoRefreshToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializingSettings || ViewModel is null || sender is not ToggleSwitch toggle) return;
        await ViewModel.SetAutoRefreshAsync(toggle.IsOn);
    }

    private void AutoRefreshTimer_Tick(object? sender, object e)
    {
        if (!_shutdown && ViewModel is { AutoRefresh: true, IsBusy: false })
            Guard(() => ViewModel.RefreshAsync(silent: true));
    }

    private void PerformanceTimer_Tick(object? sender, object e)
    {
        if (!_shutdown && ViewModel is { PerformanceMonitoringEnabled: true })
            Guard(() => ViewModel.RefreshPerformanceAsync());
    }

    private async void PerformanceMonitoringToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_shutdown || _initializingSettings || ViewModel is null || sender is not ToggleSwitch toggle) return;
        var save = ViewModel.SetPerformanceMonitoringAsync(toggle.IsOn);
        UpdatePerformanceMonitoring();
        await save;
    }

    private void UpdatePerformanceMonitoring()
    {
        var enabled = !_shutdown && ViewModel is { PerformanceMonitoringEnabled: true };
        PerformanceMetricsPanel.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        PerformanceDisabledHint.Visibility = enabled ? Visibility.Collapsed : Visibility.Visible;
        if (enabled)
        {
            _performanceTimer.Start();
            Guard(() => ViewModel!.RefreshPerformanceAsync());
        }
        else
        {
            _performanceTimer.Stop();
        }
    }

    private async Task ShowFirstRunDialogAsync()
    {
        if (ViewModel is null) return;
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "开始前请确认以下事项：",
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "• 本应用通过 ADB 控制你主动授权的 Android 设备。\n• 首次连接必须在手机上确认 USB 调试授权。\n• 文件传输、APK 安装、重启和关闭屏幕只会在你点击相应操作后执行。\n• 设置和崩溃日志仅保存在本机，不会自动上传。",
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "欢迎使用 ADB Mirror Studio",
            Content = content,
            PrimaryButtonText = "同意并开始",
            DefaultButton = ContentDialogButton.Primary
        };
        await dialog.ShowAsync();
        await ViewModel.CompleteFirstRunAsync();
    }

    private void OpenDataDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        Directory.CreateDirectory(ViewModel.DataDirectory);
        Process.Start(new ProcessStartInfo(ViewModel.DataDirectory) { UseShellExecute = true });
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.CheckForUpdatesAsync();
    }

    private async void DownloadAndInstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || !ViewModel.CanDownloadAndInstallUpdate) return;
        var update = ViewModel.AvailableUpdate;
        if (update is null) return;
        var result = await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"安装 {update.LatestVersion}",
            Content = "应用将从官方 GitHub Release 下载 Windows x64 安装包，核对文件大小和 SHA256 后启动安装程序，并关闭当前应用。安装程序尚未配置代码签名，Windows 可能显示“未知发布者”。",
            PrimaryButtonText = "下载并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        }.ShowAsync();
        if (result != ContentDialogResult.Primary || _shutdown) return;

        var installerPath = await ViewModel.DownloadAndVerifyUpdateAsync(update);
        if (string.IsNullOrWhiteSpace(installerPath) || _shutdown) return;
        try
        {
            using var verifiedInstaller = await ViewModel.AcquireVerifiedInstallerAsync(update, installerPath);
            if (_shutdown) return;
            using var installer = Process.Start(new ProcessStartInfo(installerPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(installerPath)
            });
            if (installer is null) throw new InvalidOperationException("Windows 未能启动安装程序。");
            if (Microsoft.UI.Xaml.Application.Current is App { MainWindow: not null } app)
            {
                await app.ShutdownAsync();
            }
        }
        catch (Exception exception)
        {
            if (_shutdown) return;
            CrashLog.Write(exception);
            await new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "无法启动安装程序",
                Content = exception.Message,
                CloseButtonText = "关闭"
            }.ShowAsync();
        }
    }

    private void CancelUpdateDownload_Click(object sender, RoutedEventArgs e) =>
        ViewModel?.CancelUpdateDownload();

    private async void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || ViewModel is null) return;
        var theme = ThemeSelector.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System"
        };
        ApplyTheme(theme, updateSelector: false);
        await ViewModel.SetThemeAsync(theme);
    }

    private void ApplyTheme(string theme, bool updateSelector)
    {
        if (Microsoft.UI.Xaml.Application.Current is App { MainWindow: not null } app)
        {
            app.MainWindow.ApplyTheme(theme);
        }
        else
        {
            RequestedTheme = theme switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default
            };
        }

        if (updateSelector)
        {
            ThemeSelector.SelectedIndex = theme switch
            {
                "Light" => 1,
                "Dark" => 2,
                _ => 0
            };
        }
    }

    private void QuickCommand_Click(object sender, RoutedEventArgs e) =>
        ShowCommandPalette();

    private void CommandPaletteAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleCommandPalette();
        args.Handled = true;
    }

    private void MainPage_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrlDown = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (ctrlDown && e.Key == VirtualKey.K)
        {
            ToggleCommandPalette();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && CommandPaletteOverlay.Visibility == Visibility.Visible)
        {
            HideCommandPalette();
            e.Handled = true;
        }
    }

    private void ToggleCommandPalette()
    {
        if (CommandPaletteOverlay.Visibility == Visibility.Visible)
            HideCommandPalette();
        else
            ShowCommandPalette();
    }

    private void ShowCommandPalette()
    {
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        CommandSearchBox.Text = string.Empty;
        FilterCommands(string.Empty);
        CommandSearchBox.Focus(FocusState.Programmatic);
    }

    private void HideCommandPalette()
    {
        CommandPaletteOverlay.Visibility = Visibility.Collapsed;
    }

    private void CommandPaletteOverlay_Tapped(object sender, TappedRoutedEventArgs e) =>
        HideCommandPalette();

    private void CommandPaletteCard_Tapped(object sender, TappedRoutedEventArgs e) =>
        e.Handled = true;

    private void CommandSearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        FilterCommands(CommandSearchBox.Text);

    private void CommandSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HideCommandPalette();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter)
        {
            if (CommandListView.SelectedItem is CommandPaletteItem selected)
            {
                ExecuteCommand(selected);
            }
            else if (CommandListView.Items.Count > 0 && CommandListView.Items[0] is CommandPaletteItem first)
            {
                ExecuteCommand(first);
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Down)
        {
            if (CommandListView.SelectedIndex < CommandListView.Items.Count - 1)
            {
                CommandListView.SelectedIndex++;
                CommandListView.ScrollIntoView(CommandListView.SelectedItem);
            }
            else if (CommandListView.SelectedIndex < 0 && CommandListView.Items.Count > 0)
            {
                CommandListView.SelectedIndex = 0;
                CommandListView.ScrollIntoView(CommandListView.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up)
        {
            if (CommandListView.SelectedIndex > 0)
            {
                CommandListView.SelectedIndex--;
                CommandListView.ScrollIntoView(CommandListView.SelectedItem);
            }
            e.Handled = true;
        }
    }

    private void CommandListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommandPaletteItem item)
        {
            ExecuteCommand(item);
        }
    }

    private readonly List<CommandPaletteItem> _allCommands =
    [
        new() { Id = "screenshot", Title = "捕获屏幕截图", Subtitle = "截取当前设备屏幕并保存为图片文件", Glyph = "\uE722", Shortcut = "工具栏" },
        new() { Id = "mirror", Title = "启动 / 停止屏幕镜像", Subtitle = "在独立窗口中控制当前设备画面", Glyph = "\uE7F4" },
        new() { Id = "back", Title = "发送返回按键 (Back)", Subtitle = "模拟 Android 物理返回键 (KeyCode 4)", Glyph = "\uE72B", Shortcut = "伴随栏" },
        new() { Id = "home", Title = "发送主屏按键 (Home)", Subtitle = "返回 Android 设备主屏幕 (KeyCode 3)", Glyph = "\uE80F", Shortcut = "伴随栏" },
        new() { Id = "recents", Title = "发送多任务按键 (Recents)", Subtitle = "打开最近运行应用列表 (KeyCode 187)", Glyph = "\uE7C4", Shortcut = "伴随栏" },
        new() { Id = "power", Title = "电源键 / 息屏唤醒", Subtitle = "熄灭或唤醒设备屏幕 (KeyCode 26)", Glyph = "\uE7E8", Shortcut = "伴随栏" },
        new() { Id = "vol_up", Title = "增加音量 (Volume+)", Subtitle = "调高设备媒体音量 (KeyCode 24)", Glyph = "\uE767" },
        new() { Id = "vol_down", Title = "减少音量 (Volume-)", Subtitle = "降低设备媒体音量 (KeyCode 25)", Glyph = "\uE74F" },
        new() { Id = "refresh", Title = "刷新设备列表", Subtitle = "重新扫描 USB 与网络 ADB 连接", Glyph = "\uE72C" },
        new() { Id = "export_logcat", Title = "导出 Logcat 日志", Subtitle = "抓取当前设备运行日志快照", Glyph = "\uE8A5" },
        new() { Id = "tasks", Title = "打开任务中心", Subtitle = "查看后台传输队列与录屏会话", Glyph = "\uE9D9" },
        new() { Id = "settings", Title = "打开设置与诊断", Subtitle = "系统健康检查、更新与外观配置", Glyph = "\uE713" },
        new() { Id = "terminal", Title = "打开终端命令行", Subtitle = "向当前设备发送交互式 Shell 指令", Glyph = "\uE756" },
        new() { Id = "files", Title = "打开文件管理", Subtitle = "在电脑与设备间互传文件", Glyph = "\uE8B7" },
        new() { Id = "apps", Title = "打开应用管理", Subtitle = "安装 APK 或管理已安装应用", Glyph = "\uE74C" }
    ];

    private void FilterCommands(string query)
    {
        var trimmed = query.Trim();
        var filtered = string.IsNullOrEmpty(trimmed)
            ? _allCommands
            : _allCommands.Where(cmd =>
                cmd.Title.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                cmd.Subtitle.Contains(trimmed, StringComparison.OrdinalIgnoreCase) ||
                cmd.Id.Contains(trimmed, StringComparison.OrdinalIgnoreCase)).ToList();

        CommandListView.ItemsSource = filtered;
        if (filtered.Count > 0)
        {
            CommandListView.SelectedIndex = 0;
        }
    }

    private async void ExecuteCommand(CommandPaletteItem item)
    {
        HideCommandPalette();
        switch (item.Id)
        {
            case "screenshot":
                SaveScreenshot_Click(this, new RoutedEventArgs());
                break;
            case "mirror":
                if (ViewModel is not null && !string.IsNullOrEmpty(ViewModel.SelectedDeviceSerial))
                {
                    MirrorAction_Click(MirrorActionButton, new RoutedEventArgs());
                }
                break;
            case "back":
                if (ViewModel is not null) await ViewModel.SendDeviceKeyAsync(4);
                break;
            case "home":
                if (ViewModel is not null) await ViewModel.SendDeviceKeyAsync(3);
                break;
            case "recents":
                if (ViewModel is not null) await ViewModel.SendDeviceKeyAsync(187);
                break;
            case "power":
                if (ViewModel is not null) await ViewModel.SendDeviceKeyAsync(26);
                break;
            case "vol_up":
                if (ViewModel is not null) await ViewModel.SendDeviceKeyAsync(24);
                break;
            case "vol_down":
                if (ViewModel is not null) await ViewModel.SendDeviceKeyAsync(25);
                break;
            case "refresh":
                Refresh_Click(this, new RoutedEventArgs());
                break;
            case "export_logcat":
                ExportLogcat_Click(this, new RoutedEventArgs());
                break;
            case "tasks":
                SelectSection("tasks");
                TasksNavigationItem.Focus(FocusState.Programmatic);
                break;
            case "settings":
                SelectSection("settings");
                SettingsNavigationItem.Focus(FocusState.Programmatic);
                break;
            case "terminal":
                SelectSection("terminal");
                TerminalNavigationItem.Focus(FocusState.Programmatic);
                break;
            case "files":
                SelectSection("files");
                FilesNavigationItem.Focus(FocusState.Programmatic);
                break;
            case "apps":
                SelectSection("apps");
                AppsNavigationItem.Focus(FocusState.Programmatic);
                break;
        }
    }
}

public sealed class CommandPaletteItem
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Glyph { get; init; }
    public string? Shortcut { get; init; }
    public bool HasShortcut => !string.IsNullOrEmpty(Shortcut);
}
