using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Application.Mirroring;
using AdbMirrorStudio.Domain.Mirroring;
using AdbMirrorStudio.Infrastructure.Processes;

namespace AdbMirrorStudio.Infrastructure.Scrcpy;

public sealed class MirrorSessionManager : IMirrorSessionManager
{
    private readonly IAdbService _adbService;
    private readonly string _scrcpyPath;
    private readonly ICommandRunner _commandRunner;
    private readonly Func<ProcessStartInfo, Process> _createProcess;
    private readonly ConcurrentDictionary<string, ManagedSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionLocks = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IReadOnlySet<string>> _codecCache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _recordingOwners = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _disposeLock = new();
    private Task? _disposeTask;
    private volatile bool _disposed;

    public MirrorSessionManager(IAdbService adbService, string scrcpyPath)
        : this(adbService, scrcpyPath, new ProcessCommandRunner(),
            startInfo => new Process { StartInfo = startInfo, EnableRaisingEvents = true })
    {
    }

    internal MirrorSessionManager(
        IAdbService adbService,
        string scrcpyPath,
        ICommandRunner commandRunner,
        Func<ProcessStartInfo, Process> createProcess)
    {
        _adbService = adbService;
        _scrcpyPath = scrcpyPath;
        _commandRunner = commandRunner;
        _createProcess = createProcess;
    }

    public event EventHandler<MirrorSession>? SessionChanged;

    public IReadOnlyCollection<MirrorSession> ActiveSessions =>
        _sessions.Values.Select(value => value.Session).ToArray();

    public async Task<MirrorSession> StartAsync(
        string deviceSerial,
        MirrorProfile profile,
        string? windowTitle = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        ArgumentNullException.ThrowIfNull(profile);

        var sessionLock = _sessionLocks.GetOrAdd(deviceSerial, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await StartCoreAsync(deviceSerial, profile, windowTitle, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    // The caller holds the device lock. Exit observation never takes this lock, so
    // startup/stop may await it and finish cleanup before a replacement is started.
    private async Task<MirrorSession> StartCoreAsync(
        string deviceSerial,
        MirrorProfile profile,
        string? windowTitle,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(_scrcpyPath)) throw new FileNotFoundException("未找到 scrcpy.exe。", _scrcpyPath);
        _ = ScrcpyArgumentBuilder.Build(deviceSerial, profile, windowTitle);
        var requestedProfile = profile with { RecordPath = NormalizeRecordPath(profile.RecordPath) };
        if (_sessions.TryGetValue(deviceSerial, out var existing))
        {
            if (IsRunning(existing.Process))
            {
                var sameConfiguration = existing.RequestedProfile == requestedProfile
                    && existing.Session.WindowTitle == windowTitle;
                if (sameConfiguration) return existing.Session;
                throw new InvalidOperationException("该设备的镜像已经运行。更改录制或采集配置前，请先停止现有会话。");
            }
            await existing.ExitTask.ConfigureAwait(false);
            CleanupSession(deviceSerial, existing);
        }

        if (!await _adbService.IsOnlineAsync(deviceSerial, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"设备 {deviceSerial} 当前不可达。");
        }
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var recordPath = requestedProfile.RecordPath;
        var resolvedCodec = profile.VideoCodec.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? await ResolveCodecAsync(deviceSerial, profile, cancellationToken).ConfigureAwait(false)
            : profile.VideoCodec.ToLowerInvariant();
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var resolvedProfile = profile with { VideoCodec = resolvedCodec, RecordPath = recordPath };
        var audioPlaybackEnabled = resolvedProfile.AudioEnabled && AudioPlaybackDeviceProbe.IsAvailable();
        var arguments = ScrcpyArgumentBuilder.Build(deviceSerial, resolvedProfile, windowTitle, audioPlaybackEnabled);
        var startInfo = new ProcessStartInfo
        {
            FileName = _scrcpyPath,
            WorkingDirectory = Path.GetDirectoryName(_scrcpyPath)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        startInfo.Environment["PATH"] = $"{startInfo.WorkingDirectory}{Path.PathSeparator}{Environment.GetEnvironmentVariable("PATH")}";

        var process = _createProcess(startInfo);
        var session = new MirrorSession(
            Guid.NewGuid().ToString("N"),
            deviceSerial,
            MirrorSessionState.Starting,
            null,
            DateTimeOffset.UtcNow,
            ProfileName: resolvedProfile.Name,
            VideoCodec: resolvedCodec,
            MaxSize: resolvedProfile.MaxSize,
            MaxFps: resolvedProfile.MaxFps,
            VideoBitRateMbps: resolvedProfile.VideoBitRateMbps,
            RecordPath: resolvedProfile.RecordPath,
            Profile: resolvedProfile,
            WindowTitle: windowTitle,
            AudioPlaybackEnabled: audioPlaybackEnabled);
        var managed = new ManagedSession(process, session, requestedProfile);
        if (recordPath is not null && !_recordingOwners.TryAdd(recordPath, session.Id))
        {
            process.Dispose();
            throw new InvalidOperationException("该录屏文件正被另一个镜像会话使用，请选择不同的文件名。");
        }
        _sessions[deviceSerial] = managed;

        try
        {
            _ = PrepareRecordPath(recordPath);
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!process.Start()) throw new InvalidOperationException("scrcpy 进程未能启动。");
        }
        catch (Exception exception)
        {
            managed.Session = session with { State = MirrorSessionState.Failed, Error = exception.Message };
            CleanupSession(deviceSerial, managed);
            RaiseChanged(managed.Session);
            throw;
        }

        managed.Session = session with { ProcessId = process.Id };
        managed.ExitTask = ObserveExitAsync(managed);
        _ = CleanupAfterExitAsync(deviceSerial, managed);
        RaiseChanged(managed.Session);
        try
        {
            await Task.WhenAny(managed.ExitTask, Task.Delay(TimeSpan.FromMilliseconds(750), cancellationToken))
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            await StopCoreAsync(deviceSerial).ConfigureAwait(false);
            throw;
        }
        if (managed.ExitTask.IsCompleted || !IsRunning(process))
        {
            await managed.ExitTask.ConfigureAwait(false);
            await StopCoreAsync(deviceSerial).ConfigureAwait(false);
            var detail = string.IsNullOrWhiteSpace(managed.Session.Error)
                ? "scrcpy 启动后立即退出，请检查设备编码器和录屏路径。"
                : managed.Session.Error;
            throw new InvalidOperationException(detail);
        }
        // Observation and the startup state transition share a small state lock;
        // a process exit must never be overwritten by a later Running event.
        MirrorSession started;
        lock (managed.StateLock)
        {
            if (managed.Session.State == MirrorSessionState.Starting)
            {
                managed.Session = managed.Session with { State = MirrorSessionState.Running };
                RaiseChanged(managed.Session);
            }
            started = managed.Session;
        }
        if (started.State != MirrorSessionState.Running)
        {
            await managed.ExitTask.ConfigureAwait(false);
            await StopCoreAsync(deviceSerial).ConfigureAwait(false);
            throw new InvalidOperationException(started.Error ?? "scrcpy 启动后立即退出，请检查设备编码器和录屏路径。");
        }
        return started;
    }

    public async Task StopAsync(string deviceSerial, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        var sessionLock = _sessionLocks.GetOrAdd(deviceSerial, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Cancellation may cancel waiting for the device, but once shutdown has
            // begun it must finish flushing the recording and releasing ownership.
            await StopCoreAsync(deviceSerial).ConfigureAwait(false);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private async Task StopCoreAsync(string deviceSerial)
    {
        if (!_sessions.TryGetValue(deviceSerial, out var managed)) return;
        lock (managed.StateLock)
        {
            if (managed.Session.State is MirrorSessionState.Starting or MirrorSessionState.Running)
            {
                managed.StopRequested = true;
                managed.Session = managed.Session with { State = MirrorSessionState.Stopping };
                RaiseChanged(managed.Session);
            }
        }

        try
        {
            if (IsRunning(managed.Process))
            {
                var closeRequested = TryCloseMainWindow(managed.Process);
                if (!closeRequested && !string.IsNullOrWhiteSpace(managed.Session.RecordPath))
                {
                    var handleDeadline = DateTime.UtcNow.AddSeconds(3);
                    while (IsRunning(managed.Process)
                           && TryGetWindowHandle(managed.Process) == 0
                           && DateTime.UtcNow < handleDeadline)
                    {
                        await Task.Delay(100).ConfigureAwait(false);
                    }
                    if (IsRunning(managed.Process)) closeRequested = TryCloseMainWindow(managed.Process);
                }
                if (closeRequested)
                {
                    try
                    {
                        var gracefulTimeout = string.IsNullOrWhiteSpace(managed.Session.RecordPath)
                            ? TimeSpan.FromSeconds(5)
                            : TimeSpan.FromSeconds(15);
                        await managed.Process.WaitForExitAsync()
                            .WaitAsync(gracefulTimeout)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                        // Fall through to the safety kill below if scrcpy does not react to WM_CLOSE.
                    }
                }

                if (IsRunning(managed.Process))
                {
                    try
                    {
                        lock (managed.StateLock)
                        {
                            managed.Process.Kill(entireProcessTree: true);
                            managed.ForcedTermination = true;
                        }
                    }
                    catch (Exception exception) when (
                        exception is InvalidOperationException or System.ComponentModel.Win32Exception
                        && !IsRunning(managed.Process))
                    {
                        // It exited between the running check and the kill request.
                    }
                }
                await managed.Process.WaitForExitAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            if (!IsRunning(managed.Process))
            {
                await managed.ExitTask.ConfigureAwait(false);
                CleanupSession(deviceSerial, managed);
            }
        }
    }

    public async Task<MirrorSession> RestartAsync(
        string deviceSerial,
        MirrorProfile profile,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceSerial);
        ArgumentNullException.ThrowIfNull(profile);
        var sessionLock = _sessionLocks.GetOrAdd(deviceSerial, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_sessions.TryGetValue(deviceSerial, out var current) || !IsRunning(current.Process))
            {
                throw new InvalidOperationException($"设备 {deviceSerial} 没有正在运行的镜像。");
            }

            var previousProfile = current.RequestedProfile;
            var windowTitle = current.Session.WindowTitle;
            var windowBounds = TryGetWindowBounds(current.Process);
            _ = ScrcpyArgumentBuilder.Build(deviceSerial, profile, windowTitle);
            if (!string.IsNullOrWhiteSpace(profile.RecordPath)) _ = PrepareRecordPath(profile.RecordPath);

            cancellationToken.ThrowIfCancellationRequested();
            await StopCoreAsync(deviceSerial).ConfigureAwait(false);
            try
            {
                var restarted = await StartCoreAsync(deviceSerial, profile, windowTitle, cancellationToken).ConfigureAwait(false);
                await RestoreWindowBoundsAsync(deviceSerial, windowBounds).ConfigureAwait(false);
                return restarted;
            }
            catch (Exception switchException) when (switchException is not OperationCanceledException)
            {
                if (previousProfile is null || !string.IsNullOrWhiteSpace(previousProfile.RecordPath)) throw;
                try
                {
                    _ = await StartCoreAsync(deviceSerial, previousProfile, windowTitle, cancellationToken).ConfigureAwait(false);
                    await RestoreWindowBoundsAsync(deviceSerial, windowBounds).ConfigureAwait(false);
                }
                catch (Exception rollbackException) when (rollbackException is not OperationCanceledException)
                {
                    throw new InvalidOperationException(
                        $"切换录制状态失败，且无法恢复原镜像：{rollbackException.Message}",
                        new AggregateException(switchException, rollbackException));
                }
                throw new InvalidOperationException($"切换录制状态失败，已恢复原镜像：{switchException.Message}", switchException);
            }
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private static bool TryCloseMainWindow(Process process)
    {
        try
        {
            return process.CloseMainWindow();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or System.ComponentModel.Win32Exception && !IsRunning(process))
        {
            return false;
        }
    }

    private static nint TryGetWindowHandle(Process process)
    {
        try
        {
            process.Refresh();
            return IsRunning(process) ? process.MainWindowHandle : 0;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static WindowBounds? TryGetWindowBounds(Process process)
    {
        try
        {
            process.Refresh();
            return process.MainWindowHandle != 0 && NativeMethods.GetWindowRect(process.MainWindowHandle, out var bounds)
                ? new WindowBounds(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)
                : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task RestoreWindowBoundsAsync(
        string deviceSerial,
        WindowBounds? bounds)
    {
        if (bounds is null || !_sessions.TryGetValue(deviceSerial, out var session)) return;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var handle = TryGetWindowHandle(session.Process);
        while (handle == 0 && IsRunning(session.Process) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100).ConfigureAwait(false);
            handle = TryGetWindowHandle(session.Process);
        }
        if (handle != 0)
        {
            NativeMethods.MoveWindow(
                handle,
                bounds.X,
                bounds.Y,
                Math.Max(320, bounds.Width),
                Math.Max(240, bounds.Height),
                true);
        }
    }

    private async Task ObserveExitAsync(ManagedSession managed)
    {
        try
        {
            using var outputDrain = new CancellationTokenSource();
            var stdoutTask = BoundedTextTailReader.ReadAsync(managed.Process.StandardOutput, cancellationToken: outputDrain.Token);
            var stderrTask = BoundedTextTailReader.ReadAsync(managed.Process.StandardError, cancellationToken: outputDrain.Token);
            await managed.Process.WaitForExitAsync().ConfigureAwait(false);
            // Only bound draining after scrcpy exits. A live mirror may stream logs
            // indefinitely; a child retaining its pipes must not block cleanup.
            outputDrain.CancelAfter(TimeSpan.FromSeconds(2));
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            var exitCode = managed.Process.ExitCode;
            lock (managed.StateLock)
            {
                var stoppedByUser = managed.StopRequested;
                var isRecording = !string.IsNullOrWhiteSpace(managed.Session.RecordPath);
                var interruptedRecording = managed.ForcedTermination && exitCode != 0 && isRecording;
                // A requested stop does not make encoder/muxer failures successful.
                // Recordings must have a clean exit before they can be marked complete.
                var successfulExit = exitCode == 0 || (stoppedByUser && !isRecording);
                var error = interruptedRecording
                    ? "录屏进程未能正常退出，已强制终止；录屏文件可能不完整。"
                    : successfulExit ? null
                        : SummarizeError(stderr, stdout) ?? $"scrcpy 异常退出（退出码 {exitCode}）。";
                managed.Session = managed.Session with
                {
                    State = successfulExit ? MirrorSessionState.Exited : MirrorSessionState.Failed,
                    ExitCode = exitCode,
                    Error = error
                };
                if (exitCode != 0 && !stoppedByUser) _codecCache.TryRemove(managed.Session.DeviceSerial, out _);
                RaiseChanged(managed.Session);
            }
        }
        catch (Exception exception)
        {
            lock (managed.StateLock)
            {
                managed.Session = managed.Session with { State = MirrorSessionState.Failed, Error = exception.Message };
                RaiseChanged(managed.Session);
            }
        }
    }

    private async Task CleanupAfterExitAsync(string serial, ManagedSession managed)
    {
        await managed.ExitTask.ConfigureAwait(false);
        var sessionLock = _sessionLocks.GetOrAdd(serial, _ => new SemaphoreSlim(1, 1));
        await sessionLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CleanupSession(serial, managed);
        }
        finally
        {
            sessionLock.Release();
        }
    }

    private void CleanupSession(string serial, ManagedSession managed)
    {
        if (managed.CleanedUp || IsRunning(managed.Process)) return;
        managed.CleanedUp = true;
        _sessions.TryRemove(new KeyValuePair<string, ManagedSession>(serial, managed));
        if (!string.IsNullOrWhiteSpace(managed.Session.RecordPath))
        {
            _recordingOwners.TryRemove(new KeyValuePair<string, string>(managed.Session.RecordPath, managed.Session.Id));
        }
        managed.Process.Dispose();
    }

    private static bool IsRunning(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException)
        {
            return false;
        }
    }

    internal static string? PrepareRecordPath(string? recordPath)
    {
        var fullPath = NormalizeRecordPath(recordPath);
        if (fullPath is null) return null;
        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("录屏文件必须使用 .mp4 或 .mkv 扩展名。", nameof(recordPath));
        }

        var directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("录屏保存目录不存在，请重新选择保存位置。");
        }
        if (Directory.Exists(fullPath)) throw new IOException("录屏文件路径指向了文件夹，请重新选择文件名。");

        try
        {
            if (File.Exists(fullPath))
            {
                // FileSavePicker may create a zero-byte placeholder. Never let
                // scrcpy silently truncate a recording which already contains data.
                using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (stream.Length > 0) throw new IOException("录屏文件已存在且包含内容，请选择不同的文件名。");
            }
            else
            {
                // CreateNew prevents deleting a file created concurrently. Only our
                // own probe is removed when this handle is closed.
                using var stream = new FileStream(fullPath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.None, 4096, FileOptions.DeleteOnClose);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"录屏文件无法写入：{exception.Message}", exception);
        }
        return fullPath;
    }

    private static string? NormalizeRecordPath(string? recordPath) =>
        string.IsNullOrWhiteSpace(recordPath) ? null : Path.GetFullPath(recordPath.Trim());

    private async Task<string> ResolveCodecAsync(string serial, MirrorProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_codecCache.TryGetValue(serial, out var cached)) return SelectCodec(profile, cached);
        try
        {
            var result = await _commandRunner.RunAsync(new CommandRequest(
                _scrcpyPath,
                [$"--serial={serial}", "--list-encoders"],
                Path.GetDirectoryName(_scrcpyPath),
                Timeout: TimeSpan.FromSeconds(20)), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Cancelled) throw new OperationCanceledException(cancellationToken);
            if (!result.IsSuccess) return "h264";
            var available = Regex.Matches($"{result.StandardOutput}\n{result.StandardError}", @"\b(h264|h265|av1|vp9|vp8)\b", RegexOptions.IgnoreCase)
                .Select(match => match.Value.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (available.Count > 0) _codecCache[serial] = available;
            return SelectCodec(profile, available);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "h264";
        }
    }

    private static string SelectCodec(MirrorProfile profile, IReadOnlySet<string> available) =>
        profile.Id == MirrorProfile.Quality.Id && available.Contains("h265")
            ? "h265"
            : new[] { "h264", "h265", "av1", "vp9", "vp8" }.FirstOrDefault(available.Contains) ?? "h264";

    public async Task<int> ArrangeWindowsAsync(MirrorWindowLayout layout, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var processes = _sessions.Values.Where(session => IsRunning(session.Process)).Select(session => session.Process).ToArray();
        if (processes.Length == 0) return 0;
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (processes.Any(process => IsRunning(process) && TryGetWindowHandle(process) == 0) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        var handles = processes.Select(TryGetWindowHandle).Where(handle => handle != 0).ToArray();
        var count = handles.Length;
        if (count == 0) return 0;
        if (!NativeMethods.SystemParametersInfo(0x0030, 0, out var workArea, 0)) return 0;
        var columns = layout switch
        {
            MirrorWindowLayout.Vertical => 1,
            MirrorWindowLayout.Horizontal => count,
            _ => (int)Math.Ceiling(Math.Sqrt(count))
        };
        var rows = (int)Math.Ceiling(count / (double)columns);
        var width = Math.Max(320, (workArea.Right - workArea.Left) / columns);
        var height = Math.Max(240, (workArea.Bottom - workArea.Top) / rows);
        var index = 0;
        foreach (var handle in handles)
        {
            var column = index % columns;
            var row = index / columns;
            NativeMethods.MoveWindow(handle, workArea.Left + column * width, workArea.Top + row * height, width, height, true);
            index++;
        }
        return count;
    }

    internal static string? SummarizeError(params string[] outputs)
    {
        var lines = outputs.SelectMany(output => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();
        var errors = lines.Where(line => line.Contains("ERROR", StringComparison.OrdinalIgnoreCase)).ToArray();
        return errors.LastOrDefault(line => !line.Equals("ERROR: Demuxer error", StringComparison.OrdinalIgnoreCase))
            ?? errors.LastOrDefault()
            ?? lines.LastOrDefault();
    }

    private void RaiseChanged(MirrorSession session)
    {
        if (SessionChanged is not { } handlers) return;
        foreach (EventHandler<MirrorSession> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(this, session);
            }
            catch (Exception exception)
            {
                // A UI notification failure must not interrupt process cleanup.
                Trace.TraceError($"Mirror session notification failed: {exception}");
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                _disposed = true;
                _disposeTask = DisposeCoreAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await Task.Yield();
        // Include starts still waiting for ADB/codec probing and not yet registered
        // in _sessions. Their device locks are registered before the disposed check.
        var stops = _sessionLocks.Keys.Select(serial => StopAsync(serial)).ToArray();
        await Task.WhenAll(stops).ConfigureAwait(false);
    }

    private sealed class ManagedSession(Process process, MirrorSession session, MirrorProfile requestedProfile)
    {
        public Process Process { get; } = process;
        public MirrorProfile RequestedProfile { get; } = requestedProfile;
        public object StateLock { get; } = new();
        public MirrorSession Session { get; set; } = session;
        public Task ExitTask { get; set; } = Task.CompletedTask;
        public bool CleanedUp { get; set; }
        public bool ForcedTermination { get; set; }
        public volatile bool StopRequested;
    }

    private sealed record WindowBounds(int X, int Y, int Width, int Height);

    private static partial class NativeMethods
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool SystemParametersInfo(uint action, uint parameter, out Rect value, uint flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool MoveWindow(nint window, int x, int y, int width, int height, bool repaint);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetWindowRect(nint window, out Rect value);
    }
}
