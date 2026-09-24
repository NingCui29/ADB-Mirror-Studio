using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Domain.Devices;

namespace AdbMirrorStudio.Infrastructure.Adb;

public sealed class AdbService(ICommandRunner commandRunner, string adbPath) : IAdbService
{
    public async Task<IReadOnlyList<DeviceInfo>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["devices", "-l"], TimeSpan.FromSeconds(15), cancellationToken);
        return AdbOutputParser.ParseDevices(result.StandardOutput);
    }

    public async Task<IReadOnlyList<MdnsService>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var result = await ExecuteAsync(["mdns", "services"], TimeSpan.FromSeconds(10), cancellationToken);
        return AdbOutputParser.ParseMdnsServices(result.StandardOutput);
    }

    public async Task<string> ConnectAsync(string endpoint, CancellationToken cancellationToken = default)
    {
        var normalized = AdbEndpoint.Normalize(endpoint);
        var result = await ExecuteAsync(["connect", normalized], TimeSpan.FromSeconds(30), cancellationToken);
        EnsureAcknowledged(result, "ADB 未确认连接成功。", "connected to ", "already connected to ");
        return FirstOutput(result);
    }

    public async Task<string> PairAsync(string endpoint, string pairingCode, CancellationToken cancellationToken = default)
    {
        var normalized = AdbEndpoint.Normalize(endpoint);
        var normalizedCode = pairingCode?.Trim();
        if (normalizedCode is null || normalizedCode.Length != 6 || normalizedCode.Any(character => character is < '0' or > '9'))
            throw new ArgumentException("请输入设备显示的六位数字配对码。", nameof(pairingCode));
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(adbPath)) throw new FileNotFoundException("未找到 adb.exe。", adbPath);

        var request = new CommandRequest(
            adbPath,
            ["pair", normalized, normalizedCode],
            Path.GetDirectoryName(adbPath),
            Timeout: TimeSpan.FromSeconds(30),
            SensitiveArguments: true);
        var result = await commandRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result);
        EnsureAcknowledged(result, "ADB 未确认配对成功。", "Successfully paired to ");
        return FirstOutput(result);
    }

    public async Task DisconnectAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        _ = await ExecuteAsync(["disconnect", serial.Trim()], TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task RebootAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        _ = await ExecuteAsync(["-s", serial.Trim(), "reboot"], TimeSpan.FromSeconds(30), cancellationToken);
    }

    public async Task<string> EnableTcpIpAsync(string serial, int port = 5555, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "端口必须在 1 到 65535 之间。");
        var result = await ExecuteAsync(["-s", serial.Trim(), "tcpip", port.ToString()], TimeSpan.FromSeconds(30), cancellationToken);
        return FirstOutput(result);
    }

    public async Task<string> InstallApkAsync(
        string serial,
        string apkPath,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidateLocalFile(apkPath, ".apk");
        var result = await ExecuteAsync(
            ["-s", serial.Trim(), "install", "-r", Path.GetFullPath(apkPath)],
            TimeSpan.FromMinutes(3),
            cancellationToken);
        return FirstOutput(result);
    }

    public async Task<string> PushFileAsync(
        string serial,
        string localPath,
        string remoteDirectory = "/sdcard/Download/",
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidateLocalFile(localPath);
        ValidateRemotePath(remoteDirectory, nameof(remoteDirectory));
        var normalizedDirectory = remoteDirectory.Trim().TrimEnd('/');
        var remotePath = $"{normalizedDirectory}/{Path.GetFileName(localPath)}";
        var result = await ExecuteAsync(
            ["-s", serial.Trim(), "push", Path.GetFullPath(localPath), remotePath],
            TimeSpan.FromMinutes(5),
            cancellationToken);
        return FirstOutput(result);
    }

    public async Task<bool> IsOnlineAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(adbPath)) throw new FileNotFoundException("未找到 adb.exe。", adbPath);
        var result = await commandRunner.RunAsync(
            new CommandRequest(adbPath, ["-s", serial.Trim(), "get-state"], Path.GetDirectoryName(adbPath), Timeout: TimeSpan.FromSeconds(10)),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.Cancelled) throw new OperationCanceledException("ADB 操作已取消。", cancellationToken);
        return result.IsSuccess && result.StandardOutput.Trim() == "device";
    }

    public async Task<DeviceDetails> GetDeviceDetailsAsync(string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        var normalizedSerial = serial.Trim();
        var androidTask = ExecuteAsync(
            ["-s", normalizedSerial, "shell", "getprop", "ro.build.version.release"],
            TimeSpan.FromSeconds(10), cancellationToken);
        var apiTask = ExecuteAsync(
            ["-s", normalizedSerial, "shell", "getprop", "ro.build.version.sdk"],
            TimeSpan.FromSeconds(10), cancellationToken);
        var sizeTask = ExecuteAsync(
            ["-s", normalizedSerial, "shell", "wm", "size"],
            TimeSpan.FromSeconds(10), cancellationToken);
        var batteryTask = ExecuteAsync(
            ["-s", normalizedSerial, "shell", "dumpsys", "battery"],
            TimeSpan.FromSeconds(10), cancellationToken);
        var storageTask = ExecuteAsync(
            ["-s", normalizedSerial, "shell", "df", "-h", "/data"],
            TimeSpan.FromSeconds(15), cancellationToken);

        await Task.WhenAll(androidTask, apiTask, sizeTask, batteryTask, storageTask).ConfigureAwait(false);
        var android = FirstOutput(await androidTask.ConfigureAwait(false));
        var api = FirstOutput(await apiTask.ConfigureAwait(false));
        var sizeOutput = FirstOutput(await sizeTask.ConfigureAwait(false));
        var batteryOutput = FirstOutput(await batteryTask.ConfigureAwait(false));
        var storageOutput = FirstOutput(await storageTask.ConfigureAwait(false));

        return new DeviceDetails(
            normalizedSerial,
            EmptyFallback(android),
            EmptyFallback(api),
            ParseResolution(sizeOutput),
            ParseIntField(batteryOutput, "level"),
            ParseBatteryStatus(ParseIntField(batteryOutput, "status")),
            ParseStorage(storageOutput));
    }

    public async Task<DevicePerformanceCounters> GetPerformanceCountersAsync(
        string serial,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        const string command =
            "head -n 1 /proc/stat; cat /proc/meminfo; " +
            "for p in /sys/class/devfreq/*gpu*/load /sys/class/misc/mali0/device/utilization; do " +
            "if [ -r \"$p\" ]; then v=$(cat \"$p\" 2>/dev/null); " +
            "if [ -n \"$v\" ]; then printf \"GPU_PATH=%s\\nGPU_VALUE=%s\\n\" \"$p\" \"$v\"; break; fi; fi; done; " +
            "for z in /sys/class/thermal/thermal_zone*; do " +
            "[ -r \"$z/type\" ] && [ -r \"$z/temp\" ] || continue; " +
            "IFS= read -r t < \"$z/type\"; IFS= read -r v < \"$z/temp\"; " +
            "printf \"THERMAL=%s|%s|%s\\n\" \"$z\" \"$t\" \"$v\"; done";
        var result = await ExecuteAsync(
            ["-s", serial.Trim(), "shell", command],
            TimeSpan.FromSeconds(8),
            cancellationToken).ConfigureAwait(false);
        return DevicePerformanceParser.Parse(result.StandardOutput);
    }

    public async Task<string> CaptureScreenshotAsync(
        string serial,
        string localPath,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        if (string.IsNullOrWhiteSpace(localPath)) throw new ArgumentException("请选择截图保存位置。", nameof(localPath));
        if (!string.Equals(Path.GetExtension(localPath), ".png", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("截图必须保存为 PNG 文件。", nameof(localPath));
        }

        var fullPath = Path.GetFullPath(localPath);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var remotePath = $"/data/local/tmp/adb-mirror-{Guid.NewGuid():N}.png";
        var temporaryPath = Path.Combine(Path.GetDirectoryName(fullPath)!, $".adb-mirror-{Guid.NewGuid():N}.png");
        try
        {
            await ExecuteAsync(["-s", serial.Trim(), "shell", "screencap", "-p", remotePath], TimeSpan.FromSeconds(30), cancellationToken);
            await ExecuteAsync(["-s", serial.Trim(), "pull", remotePath, temporaryPath], TimeSpan.FromMinutes(2), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
            return fullPath;
        }
        finally
        {
            try
            {
                await ExecuteAsync(["-s", serial.Trim(), "shell", "rm", "-f", remotePath], TimeSpan.FromSeconds(10), CancellationToken.None);
            }
            catch
            {
                // A temporary screenshot must not hide the primary result.
            }
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the capture result if another process holds the local temporary file.
            }
        }
    }

    public async Task<string> GetLogcatSnapshotAsync(
        string serial,
        int maxLines = 500,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        if (maxLines is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxLines));
        var result = await ExecuteAsync(
            ["-s", serial.Trim(), "logcat", "-d", "-t", maxLines.ToString()],
            TimeSpan.FromSeconds(30), cancellationToken);
        return result.StandardOutput;
    }

    public async Task<string> PullFileAsync(
        string serial,
        string remotePath,
        string localDirectory,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidateRemotePath(remotePath, nameof(remotePath));
        if (string.IsNullOrWhiteSpace(localDirectory)) throw new ArgumentException("请选择本地保存目录。", nameof(localDirectory));
        cancellationToken.ThrowIfCancellationRequested();
        var fullDirectory = Path.GetFullPath(localDirectory);
        var remoteParts = remotePath.Trim().Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (remoteParts.Any(part => part is "." or ".."))
            throw new ArgumentException("设备下载路径不能包含 . 或 .. 路径段。", nameof(remotePath));
        var normalizedRemote = "/" + string.Join('/', remoteParts);
        var rootName = remoteParts.LastOrDefault() ?? "root";
        ValidateWindowsFileName(rootName);
        var destination = Path.Combine(fullDirectory, rootName);
        var normalizedSerial = serial.Trim();
        var quotedRemote = QuoteRemoteShellArgument(normalizedRemote);
        var kind = await ExecuteAsync(
            ["-s", normalizedSerial, "shell", $"if [ -d {quotedRemote} ]; then printf directory; else printf file; fi"],
            TimeSpan.FromSeconds(15), cancellationToken);

        if (kind.StandardOutput.Trim() == "file")
        {
            ValidateDownloadTarget(fullDirectory, destination, isDirectory: false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(fullDirectory);
            await PullSingleFileAsync(normalizedSerial, normalizedRemote, destination, cancellationToken);
            return $"已下载至 {destination}";
        }
        if (kind.StandardOutput.Trim() != "directory") throw new AdbCommandException("无法识别设备下载路径的类型。");

        // Windows adb's CRT basename/dirname can corrupt UTF-8. Enumerate on Android, then
        // create every directory in .NET and pull each file to an explicit local filename.
        var directories = await EnumerateRemotePathsAsync(normalizedSerial, quotedRemote, "d", cancellationToken);
        var files = await EnumerateRemotePathsAsync(normalizedSerial, quotedRemote, "f", cancellationToken);
        if (directories.Count + files.Count > 10000)
            throw new AdbCommandException("设备目录超过 10000 个条目，请分批下载。");
        if (!directories.Contains(normalizedRemote, StringComparer.Ordinal))
            throw new AdbCommandException("设备目录列表不完整，请刷新后重试。");

        var targets = new Dictionary<string, (string RemotePath, bool IsDirectory)>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in directories.Select(path => (Path: path, IsDirectory: true))
            .Concat(files.Select(path => (Path: path, IsDirectory: false))))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = entry.Path == normalizedRemote ? string.Empty
                : entry.Path.StartsWith(normalizedRemote.TrimEnd('/') + "/", StringComparison.Ordinal)
                    ? entry.Path[(normalizedRemote.TrimEnd('/').Length + 1)..]
                    : throw new AdbCommandException("设备目录列表包含下载根目录以外的路径。");
            var components = relative.Length == 0 ? [] : relative.Split('/');
            foreach (var component in components) ValidateWindowsFileName(component);
            var localPath = components.Aggregate(destination, Path.Combine);
            ValidateDownloadTarget(fullDirectory, localPath, entry.IsDirectory);
            if (!targets.TryAdd(localPath, (entry.Path, entry.IsDirectory)))
                throw new AdbCommandException("设备目录存在 Windows 无法区分的同名条目，请分别下载。");
        }

        foreach (var target in targets.Where(item => !string.Equals(item.Key, destination, StringComparison.OrdinalIgnoreCase)))
        {
            if (!targets.TryGetValue(Path.GetDirectoryName(target.Key)!, out var parent) || !parent.IsDirectory)
                throw new AdbCommandException("设备目录结构在枚举过程中发生变化，请重试下载。");
        }
        foreach (var target in targets.Where(item => item.Value.IsDirectory).OrderBy(item => item.Key.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDownloadTarget(fullDirectory, target.Key, isDirectory: true);
            Directory.CreateDirectory(target.Key);
        }
        foreach (var target in targets.Where(item => !item.Value.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateDownloadTarget(fullDirectory, target.Key, isDirectory: false);
            await PullSingleFileAsync(normalizedSerial, target.Value.RemotePath, target.Key, cancellationToken);
        }
        return $"已下载 {files.Count} 个文件至 {destination}";
    }

    private async Task<IReadOnlyList<string>> EnumerateRemotePathsAsync(
        string serial, string quotedRemote, string type, CancellationToken cancellationToken)
    {
        const string terminator = "\0ADB_MIRROR_LIST_END\0";
        var result = await ExecuteAsync(
            ["-s", serial, "shell", $"find -L {quotedRemote} -type {type} -print0 && printf '\\0ADB_MIRROR_LIST_END\\0'"],
            TimeSpan.FromSeconds(30), cancellationToken);
        if (result.StandardOutput.Length > 1_000_000 || !result.StandardOutput.EndsWith(terminator, StringComparison.Ordinal))
            throw new AdbCommandException("设备目录列表过大或输出不完整，请分批下载。");
        var paths = result.StandardOutput[..^terminator.Length].Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (paths.Length > 10000) throw new AdbCommandException("设备目录超过 10000 个条目，请分批下载。");
        return paths;
    }

    private async Task PullSingleFileAsync(string serial, string remotePath, string destination, CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetDirectoryName(destination)!, $".adb-mirror-{Guid.NewGuid():N}.download");
        try
        {
            // An existing regular target also makes adb reject a source that changed into a
            // directory since enumeration, instead of recursively creating an unchecked tree.
            using (new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            await ExecuteAsync(["-s", serial, "pull", remotePath, temporaryPath], TimeSpan.FromMinutes(5), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the transfer failure if a scanner is holding the partial download.
            }
        }
    }

    private static string QuoteRemoteShellArgument(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static void ValidateWindowsFileName(string name)
    {
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (string.IsNullOrEmpty(name) || name.Length > 255 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
            || name.Any(character => char.IsControl(character) || "<>:\"/\\|?*".Contains(character))
            || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
            || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                && "123456789¹²³".Contains(stem[3])))
            throw new AdbCommandException("设备目录包含 Windows 无法保存的文件名，请先在设备上重命名后重试。");
    }

    private static void ValidateDownloadTarget(string downloadRoot, string target, bool isDirectory)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(downloadRoot));
        var fullTarget = Path.GetFullPath(target);
        var rootPrefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullTarget.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new AdbCommandException("下载文件路径超出本地保存目录。");
        for (var current = fullTarget; current.Length >= fullRoot.Length; current = Path.GetDirectoryName(current)!)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new AdbCommandException("本地下载路径包含符号链接或目录联接，请选择普通保存目录。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            if (current == fullRoot) break;
        }
        if (isDirectory ? File.Exists(fullTarget) : Directory.Exists(fullTarget))
            throw new AdbCommandException("本地已有同名的文件或目录，与设备目录结构冲突。");
    }

    public async Task SendKeyEventAsync(string serial, int keyCode, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        if (keyCode is < 0 or > 999) throw new ArgumentOutOfRangeException(nameof(keyCode));
        _ = await ExecuteAsync(["-s", serial.Trim(), "shell", "input", "keyevent", keyCode.ToString()], TimeSpan.FromSeconds(10), cancellationToken);
    }

    public async Task<IReadOnlyList<InstalledApp>> GetInstalledAppsAsync(
        string serial,
        bool includeSystemApps = false,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        var arguments = new List<string> { "-s", serial.Trim(), "shell", "pm", "list", "packages" };
        if (!includeSystemApps) arguments.Add("-3");
        var result = await ExecuteAsync(arguments, TimeSpan.FromSeconds(30), cancellationToken);
        var systemPackages = new HashSet<string>(StringComparer.Ordinal);
        if (includeSystemApps)
        {
            var systemResult = await ExecuteAsync(
                ["-s", serial.Trim(), "shell", "pm", "list", "packages", "-s"],
                TimeSpan.FromSeconds(30), cancellationToken);
            systemPackages.UnionWith(ParsePackageNames(systemResult.StandardOutput));
        }
        return ParsePackageNames(result.StandardOutput)
            .Select(package => new InstalledApp(package, systemPackages.Contains(package)))
            .OrderBy(app => app.PackageName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task LaunchAppAsync(string serial, string packageName, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidatePackageName(packageName);
        _ = await ExecuteAsync(["-s", serial.Trim(), "shell", "monkey", "-p", packageName.Trim(), "-c", "android.intent.category.LAUNCHER", "1"], TimeSpan.FromSeconds(20), cancellationToken);
    }

    public async Task ForceStopAppAsync(string serial, string packageName, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidatePackageName(packageName);
        _ = await ExecuteAsync(["-s", serial.Trim(), "shell", "am", "force-stop", packageName.Trim()], TimeSpan.FromSeconds(15), cancellationToken);
    }

    public async Task UninstallAppAsync(string serial, string packageName, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidatePackageName(packageName);
        _ = await ExecuteAsync(["-s", serial.Trim(), "uninstall", packageName.Trim()], TimeSpan.FromMinutes(2), cancellationToken);
    }

    public async Task<string> RunShellCommandAsync(
        string serial,
        string command,
        CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ValidateShellCommand(command);
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(adbPath)) throw new FileNotFoundException("未找到 adb.exe。", adbPath);
        var result = await commandRunner.RunAsync(
            new CommandRequest(
                adbPath,
                // adb joins shell arguments with spaces; an extra sh -c would lose the command's arguments.
                ["-s", serial.Trim(), "shell", command.Trim()],
                Path.GetDirectoryName(adbPath),
                Timeout: TimeSpan.FromMinutes(1),
                SensitiveArguments: true),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result);

        var output = result.StandardOutput.TrimEnd();
        var error = result.StandardError.TrimEnd();
        if (string.IsNullOrWhiteSpace(output)) return string.IsNullOrWhiteSpace(error) ? "（命令没有输出）" : error;
        return string.IsNullOrWhiteSpace(error) ? output : $"{output}{Environment.NewLine}[stderr]{Environment.NewLine}{error}";
    }

    private async Task<CommandResult> ExecuteAsync(
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(adbPath)) throw new FileNotFoundException("未找到 adb.exe。", adbPath);
        var result = await commandRunner.RunAsync(
            new CommandRequest(adbPath, arguments, Path.GetDirectoryName(adbPath), Timeout: timeout),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSuccess(result);
        return result;
    }

    private static void EnsureSuccess(CommandResult result)
    {
        if (result.Cancelled) throw new OperationCanceledException("ADB 操作已取消。");
        if (result.TimedOut) throw new AdbCommandException("ADB 操作超时。");
        if (result.ExitCode != 0)
        {
            throw new AdbCommandException(ErrorOutput(result), result.ExitCode);
        }
    }

    private static void EnsureAcknowledged(CommandResult result, string fallback, params string[] prefixes)
    {
        var output = result.StandardOutput.Trim();
        if (!prefixes.Any(prefix => output.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            var details = ErrorOutput(result);
            throw new AdbCommandException(string.IsNullOrWhiteSpace(details) ? fallback : details, result.ExitCode);
        }
    }

    private static IEnumerable<string> ParsePackageNames(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("package:", StringComparison.Ordinal) && line.Length > 8)
            .Select(line => line[8..])
            .Distinct(StringComparer.Ordinal);

    private static void ValidateRemotePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.Trim().StartsWith('/') || path.Any(char.IsControl))
            throw new ArgumentException("设备路径必须是以 / 开头且不含控制字符的绝对路径。", parameterName);
    }

    private static void ValidateSerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Any(char.IsControl))
        {
            throw new ArgumentException("请选择目标设备。", nameof(serial));
        }
    }

    private static void ValidatePackageName(string packageName)
    {
        var normalized = packageName?.Trim();
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.Length > 255
            || normalized.StartsWith('.')
            || normalized.EndsWith('.')
            || normalized.Contains("..", StringComparison.Ordinal)
            || normalized.Any(character => !(char.IsLetterOrDigit(character) || character is '.' or '_')))
        {
            throw new ArgumentException("应用包名格式无效。", nameof(packageName));
        }
    }

    private static void ValidateShellCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("设备命令不能为空。", nameof(command));
        }
        if (command.Length > 4096)
        {
            throw new ArgumentException("设备命令不能超过 4096 个字符。", nameof(command));
        }
        if (command.Any(character => char.IsControl(character) && character != '\t'))
        {
            throw new ArgumentException("设备命令不能包含换行或其他控制字符。", nameof(command));
        }
    }

    private static void ValidateLocalFile(string path, string? requiredExtension = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("请选择本地文件。", nameof(path));
        }
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("所选文件不存在。", path);
        }
        if (requiredExtension is not null && !string.Equals(Path.GetExtension(path), requiredExtension, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"请选择 {requiredExtension} 文件。", nameof(path));
        }
    }

    private static string FirstOutput(CommandResult result) =>
        (string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput).Trim();

    private static string ErrorOutput(CommandResult result)
    {
        var error = result.StandardError.Trim();
        var output = result.StandardOutput.Trim();
        if (string.IsNullOrWhiteSpace(error)) return string.IsNullOrWhiteSpace(output)
            ? result.ExitCode == 0 ? string.Empty : $"ADB 操作失败（退出码 {result.ExitCode}）。"
            : output;
        return string.IsNullOrWhiteSpace(output) ? error : $"{error}{Environment.NewLine}{output}";
    }

    private static string EmptyFallback(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();

    private static int? ParseIntField(string output, string name)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(':', 2);
            if (parts.Length == 2 && parts[0].Trim().Equals(name, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1].Trim(), out var value)) return value;
        }
        return null;
    }

    private static string ParseResolution(string output)
    {
        var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var line = lines.FirstOrDefault(value => value.Contains("Override size:", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault(value => value.Contains("Physical size:", StringComparison.OrdinalIgnoreCase))
            ?? lines.FirstOrDefault(value => value.Contains("size:", StringComparison.OrdinalIgnoreCase));
        return line is null ? "—" : line[(line.IndexOf(':') + 1)..].Trim();
    }

    private static string ParseBatteryStatus(int? status) => status switch
    {
        2 => "充电中",
        3 => "放电中",
        4 => "未充电",
        5 => "已充满",
        _ => "未知"
    };

    private static string ParseStorage(string output)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (line is null) return "—";
        var columns = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return columns.Length >= 5 ? $"可用 {columns[3]} / 总计 {columns[1]}（已用 {columns[4]}）" : line.Trim();
    }
}
