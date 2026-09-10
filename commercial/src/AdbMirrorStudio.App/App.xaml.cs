using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Diagnostics;
using AdbMirrorStudio.Application.Mirroring;
using AdbMirrorStudio.Application.Settings;
using AdbMirrorStudio.Application.Updates;
using AdbMirrorStudio.Infrastructure.Adb;
using AdbMirrorStudio.Infrastructure.Diagnostics;
using AdbMirrorStudio.Infrastructure.Processes;
using AdbMirrorStudio.Infrastructure.Persistence;
using AdbMirrorStudio.Infrastructure.Scrcpy;
using AdbMirrorStudio.Infrastructure.Updates;
using Microsoft.Windows.AppLifecycle;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinRT.Interop;

namespace AdbMirrorStudio.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private const string MainInstanceKey = "ADB-Mirror-Studio.Main";
    private const int RestoreWindowCommand = 9;
    private MainWindow? _window;
    private AppInstance? _mainAppInstance;
    private IMirrorSessionManager? _mirrorSessions;
    private HttpClient? _httpClient;
    private string? _toolsDirectory;
    private bool _shuttingDown;
    private bool _shutdownComplete;
    private Task? _shutdownTask;
    private int _activationRequested;
    internal MainWindow? MainWindow => _window;

    public App()
    {
        UnhandledException += (_, eventArgs) => CrashLog.Write(eventArgs.Exception);
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var currentInstance = AppInstance.GetCurrent();
            _mainAppInstance = AppInstance.FindOrRegisterForKey(MainInstanceKey);
            if (!_mainAppInstance.IsCurrent)
            {
                await _mainAppInstance.RedirectActivationToAsync(currentInstance.GetActivatedEventArgs());
                Environment.Exit(0);
                return;
            }
            _mainAppInstance.Activated += OnAppInstanceActivated;

            var toolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");
            _toolsDirectory = toolsDirectory;
            var runner = new ProcessCommandRunner();
            var adbPath = Path.Combine(toolsDirectory, "adb.exe");
            var scrcpyPath = Path.Combine(toolsDirectory, "scrcpy.exe");
            IAdbService adb = new AdbService(runner, adbPath);
            _mirrorSessions = new MirrorSessionManager(adb, scrcpyPath);
            IDiagnosticsService diagnostics = new DiagnosticsService(runner, adbPath, scrcpyPath);
            _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            IUpdateService updates = new GitHubUpdateService(
                _httpClient,
                AppVersionInfo.ProductVersion,
                "NingCui29",
                "ADB-Mirror-Studio");
            IAppSettingsStore settings = new JsonAppSettingsStore(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AdbMirrorStudio",
                "settings.json"));

            _window = new MainWindow(new AppServices(adb, _mirrorSessions, settings, diagnostics, updates));
            _window.AppWindow.Closing += OnAppWindowClosing;
            _window.Activate();
            if (Interlocked.Exchange(ref _activationRequested, 0) != 0) ActivateExistingWindow();
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception);
            throw;
        }
    }

    private void OnAppInstanceActivated(object? sender, AppActivationArguments args)
    {
        Interlocked.Exchange(ref _activationRequested, 1);
        var window = _window;
        if (window is null) return;
        window.DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _activationRequested, 0);
            ActivateExistingWindow();
        });
    }

    private void ActivateExistingWindow()
    {
        if (_shuttingDown) return;
        var window = _window;
        if (window is null) return;
        var handle = WindowNative.GetWindowHandle(window);
        if (handle != IntPtr.Zero)
        {
            _ = ShowWindow(handle, RestoreWindowCommand);
            _ = SetForegroundWindow(handle);
        }
        window.Activate();
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_shutdownComplete) return;
        args.Cancel = true;
        await ShutdownAsync();
    }

    internal Task ShutdownAsync() => _shutdownTask ??= ShutdownCoreAsync();

    private async Task ShutdownCoreAsync()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        try
        {
            _window?.PrepareForShutdown();
            if (_mirrorSessions is not null) await _mirrorSessions.DisposeAsync();
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception);
        }
        finally
        {
            try
            {
                if (_toolsDirectory is not null) await Task.Run(() => StopBundledToolProcesses(_toolsDirectory));
            }
            catch (Exception exception) { CrashLog.Write(exception); }
            if (_mainAppInstance is not null) _mainAppInstance.Activated -= OnAppInstanceActivated;
            _httpClient?.Dispose();
            _shutdownComplete = true;
            _window?.Close();
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr windowHandle, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

    private static void StopBundledToolProcesses(string toolsDirectory)
    {
        var normalizedToolsDirectory = Path.GetFullPath(toolsDirectory)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            foreach (var processName in new[] { "adb", "scrcpy" })
            {
                foreach (var process in Process.GetProcessesByName(processName))
                {
                    using (process)
                    {
                        try
                        {
                            var executable = process.MainModule?.FileName;
                            if (string.IsNullOrWhiteSpace(executable)
                                || !Path.GetFullPath(executable).StartsWith(normalizedToolsDirectory, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            if (!process.HasExited) process.Kill(entireProcessTree: true);
                            process.WaitForExit(5000);
                        }
                        catch (Exception exception) when (exception is InvalidOperationException
                                                           or System.ComponentModel.Win32Exception
                                                           or NotSupportedException)
                        {
                            // A process may exit or become inaccessible while the app is closing.
                        }
                    }
                }
            }
            if (attempt < 2) Thread.Sleep(150);
        }
    }
}

public sealed record AppServices(
    IAdbService Adb,
    IMirrorSessionManager MirrorSessions,
    IAppSettingsStore Settings,
    IDiagnosticsService Diagnostics,
    IUpdateService Updates);

internal static class CrashLog
{
    public static void Write(Exception exception)
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AdbMirrorStudio",
                "Crash");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmssfff}.log"),
                exception.ToString());
        }
        catch
        {
            // Crash reporting must never mask the original failure.
        }
    }
}
