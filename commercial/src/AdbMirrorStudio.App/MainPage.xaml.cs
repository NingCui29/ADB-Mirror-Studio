using AdbMirrorStudio.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;
using System.Diagnostics;

namespace AdbMirrorStudio.App;

public sealed partial class MainPage : Page
{
    private readonly DispatcherTimer _autoRefreshTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private bool _initializingSettings;
    private bool _shutdown;
    public MainViewModel? ViewModel { get; private set; }

    public MainPage()
    {
        InitializeComponent();
        _autoRefreshTimer.Tick += AutoRefreshTimer_Tick;
    }

    private void WorkspaceTabs_Loaded(object sender, RoutedEventArgs e) =>
        UpdateWorkspaceContentWidth(WorkspaceTabs.ActualWidth);

    private void WorkspaceTabs_SizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateWorkspaceContentWidth(e.NewSize.Width);

    private void UpdateWorkspaceContentWidth(double availableWidth)
    {
        if (OverviewScrollViewer is null) return;
        var contentWidth = Math.Max(0, Math.Min(1400, availableWidth));
        foreach (var scrollViewer in new[]
                 {
                     OverviewScrollViewer,
                     ScreenScrollViewer,
                     FilesScrollViewer,
                     AppsScrollViewer,
                     TerminalScrollViewer
                 })
        {
            scrollViewer.Width = contentWidth;
        }

        foreach (var content in new FrameworkElement[]
                 {
                     OverviewCards,
                     ScreenCards,
                     FilesCards,
                     AppsCards,
                     TerminalContent
                 })
        {
            content.Width = contentWidth;
        }
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not AppServices services) return;
        _initializingSettings = true;
        ViewModel = new MainViewModel(services);
        DataContext = ViewModel;
        try
        {
            await ViewModel.InitializeAsync();
            if (_shutdown) return;
            ApplyTheme(ViewModel.Theme, updateSelector: true);
            AutoRefreshToggle.IsOn = ViewModel.AutoRefresh;
            if (!ViewModel.FirstRunCompleted) await ShowFirstRunDialogAsync();
            if (!_shutdown) _autoRefreshTimer.Start();
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

    private async void Mirror_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || ViewModel.IsBusy || GetCommandSerial(sender) is not { } serial) return;
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
        FrameworkElement { Tag: string serial } => serial,
        _ => null
    };

    private async void RunDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null) await ViewModel.RunDiagnosticsAsync();
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        WorkspaceView.Visibility = Visibility.Collapsed;
        TasksView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;

        if (args.IsSettingsSelected)
        {
            SettingsView.Visibility = Visibility.Visible;
            ThemeSelector?.Focus(FocusState.Programmatic);
            if (ViewModel is not null && ViewModel.Diagnostics.Count == 0)
            {
                await ViewModel.RunDiagnosticsAsync();
            }
            return;
        }

        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag as string;
        switch (tag)
        {
            case "tasks":
                TasksView.Visibility = Visibility.Visible;
                break;
            default:
                WorkspaceView.Visibility = Visibility.Visible;
                RefreshDevicesButton?.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void OpenDevices_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = WorkspaceNavigationItem;
        WorkspaceView.Visibility = Visibility.Visible;
        TasksView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        RefreshDevicesButton?.Focus(FocusState.Programmatic);
    }

    private void OpenFilesTab_Click(object sender, RoutedEventArgs e) => WorkspaceTabs.SelectedIndex = 2;

    private async void OpenDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        Navigation.SelectedItem = Navigation.SettingsItem;
        WorkspaceView.Visibility = Visibility.Collapsed;
        TasksView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        ThemeSelector?.Focus(FocusState.Programmatic);
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
        var details = await ViewModel.GetDeviceDetailsAsync(ViewModel.SelectedDeviceSerial);
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

    private async void DeviceKey_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is not null && sender is FrameworkElement { Tag: string value } && int.TryParse(value, out var keyCode))
        {
            await ViewModel.SendDeviceKeyAsync(keyCode);
        }
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
        if (ViewModel is null) return;
        var serial = ViewModel.SelectedDeviceSerial;
        var command = ViewModel.ShellCommand.Trim();
        if (string.IsNullOrWhiteSpace(serial) || string.IsNullOrWhiteSpace(command))
        {
            await ViewModel.RunDeviceShellCommandAsync(serial, command);
            return;
        }

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
        if (ViewModel is null || Microsoft.UI.Xaml.Application.Current is not App { MainWindow: not null } app) return;
        var serial = ViewModel.SelectedDeviceSerial;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            SuggestedFileName = $"android-screen-{DateTime.Now:yyyyMMdd-HHmmss}"
        };
        picker.FileTypeChoices.Add("PNG 图片", [".png"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(app.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is not null && !_shutdown)
        {
            await ViewModel.CaptureScreenshotAsync(serial, file.Path);
        }
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

    private async void MirrorProfileSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingSettings || ViewModel is null || sender is not ComboBox { SelectedValue: string profileId }) return;
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

    private async void AutoRefreshTimer_Tick(object? sender, object e)
    {
        if (!_shutdown && ViewModel is { AutoRefresh: true, IsBusy: false }) await ViewModel.RefreshAsync(silent: true);
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
}
