using System.Globalization;
using System.Xml;
using AdbMirrorStudio.Application.Adb;
using AdbMirrorStudio.Application.Commands;

namespace AdbMirrorStudio.Infrastructure.Adb;

public sealed partial class AdbService
{
    public async Task<IReadOnlyList<UiTestNode>> InspectUiAsync(
        string serial, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ReadUiTreeOnceAsync(serial.Trim(), cancellationToken);
            }
            catch (AdbCommandException ex) when (attempt < 3 && IsUiTransitionError(ex.Message))
            {
                // A window transition may briefly leave UIAutomator with no active root.
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt), cancellationToken);
            }
        }
    }

    private static bool IsUiTransitionError(string message) =>
        !message.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal)
        && (message.Contains("ERROR: null root node returned", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ERROR: could not get idle state", StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<UiTestNode>> ReadUiTreeOnceAsync(string serial, CancellationToken cancellationToken)
    {
        // ADB shell may have no controlling terminal. Dump to a private, per-call file instead.
        var path = $"/data/local/tmp/adb-mirror-ui-{Guid.NewGuid():N}.xml";
        var command = $"umask 077; trap 'rm -f {path}' 0; "
            + $"message=$(uiautomator dump {path} 2>&1); status=$?; "
            + $"if [ \"$status\" -ne 0 ] || [ ! -s {path} ]; then printf '%s\\n' \"$message\" >&2; exit 1; fi; cat {path}";
        var result = await ExecuteAsync(["-s", serial, "shell", command], TimeSpan.FromSeconds(30), cancellationToken);
        if (result.StandardOutputTruncation == OutputTruncation.Interrupted)
            throw new AdbCommandException("ADB 输出中断，未能完整读取界面层级 XML。请检查设备连接并重试。");
        if (result.StandardOutputTruncation == OutputTruncation.CharacterLimit
            || result.StandardOutput.Length > CommandResult.CaptureCharacterLimit)
            throw new AdbCommandException($"界面层级 XML 超过 {CommandResult.CaptureCharacterLimit:N0} 个字符的读取上限。请在控件较少的页面重试。");
        // Older ADB servers can merge diagnostics into stdout and return exit code zero.
        if (IsUiTransitionError(result.StandardError) || IsUiTransitionError(result.StandardOutput))
            throw new AdbCommandException(ErrorOutput(result));
        try
        {
            using var text = new StringReader(result.StandardOutput);
            using var reader = XmlReader.Create(text, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = CommandResult.CaptureCharacterLimit
            });
            if (reader.MoveToContent() != XmlNodeType.Element || reader.Name != "hierarchy")
                throw new AdbCommandException("设备未返回 Android 界面层级。");

            var nodes = new List<UiTestNode>();
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.Name != "node"
                    || reader.GetAttribute("visible-to-user") == "false") continue;

                nodes.Add(new UiTestNode(
                    reader.GetAttribute("text") ?? "",
                    reader.GetAttribute("resource-id") ?? "",
                    reader.GetAttribute("content-desc") ?? "",
                    reader.GetAttribute("class") ?? "",
                    reader.GetAttribute("bounds") ?? "",
                    reader.GetAttribute("enabled") != "false",
                    reader.GetAttribute("clickable") == "true",
                    reader.GetAttribute("scrollable") == "true") { Depth = reader.Depth - 1 });
            }
            return nodes;
        }
        catch (XmlException)
        {
            throw new AdbCommandException("设备返回的界面层级 XML 格式无效，请重新读取。");
        }
    }

    public async Task ExecuteUiStepAsync(string serial, UiTestStep step, CancellationToken cancellationToken = default)
    {
        ValidateSerial(serial);
        ArgumentNullException.ThrowIfNull(step);
        cancellationToken.ThrowIfCancellationRequested();
        serial = serial.Trim();
        switch (step.Action)
        {
            case UiTestAction.Launch:
                await LaunchUiTestAppAsync(serial, step.Value, cancellationToken);
                return;
            case UiTestAction.Back:
                await SendKeyEventAsync(serial, 4, cancellationToken);
                return;
            case UiTestAction.InputText:
                if (step.ValueError is { } error) throw new ArgumentException(error);
                var input = QuoteRemoteShellArgument(step.Value.Replace(" ", "%s", StringComparison.Ordinal));
                await ExecuteAsync(["-s", serial, "shell", "input", "text", input],
                    TimeSpan.FromSeconds(15), cancellationToken, sensitiveArguments: true);
                return;
            case UiTestAction.SwipeUp:
            case UiTestAction.SwipeDown:
                var screen = await InspectUiAsync(serial, cancellationToken);
                var region = FindSwipeRegion(screen, scrollableOnly: true) ?? FindSwipeRegion(screen, scrollableOnly: false)
                    ?? throw new AdbCommandException("界面层级中未找到可滑动的有效区域。");
                await SwipeRegionAsync(serial, region, step.Action == UiTestAction.SwipeUp, cancellationToken);
                return;
            case UiTestAction.Tap:
            case UiTestAction.AssertVisible:
                await ExecuteTargetStepAsync(serial, step, cancellationToken);
                return;
            default:
                throw new ArgumentException("此步骤不属于设备界面操作。", nameof(step));
        }
    }

    private async Task LaunchUiTestAppAsync(string serial, string packageName, CancellationToken cancellationToken)
    {
        ValidatePackageName(packageName);
        packageName = packageName.Trim();
        const string main = "android.intent.action.MAIN";
        const string launcher = "android.intent.category.LAUNCHER";
        string? component = null;
        try
        {
            var resolved = await ExecuteAsync(
                ["-s", serial, "shell", "cmd", "package", "resolve-activity", "--brief", "-a", main, "-c", launcher, "-p", packageName],
                TimeSpan.FromSeconds(15), cancellationToken);
            // Older ADB shell protocols can return exit code zero for an unavailable command.
            if (!IsResolverUnavailable(resolved.StandardOutput) && !IsResolverUnavailable(resolved.StandardError))
            {
                component = resolved.StandardOutput.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
                if (component is null || !component.StartsWith(packageName + "/", StringComparison.Ordinal)
                    || component.Length == packageName.Length + 1
                    || component[(packageName.Length + 1)..].Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '_' or '$')))
                    throw new AdbCommandException($"无法解析 {packageName} 的启动 Activity，请确认应用已安装且有桌面入口。");
            }
        }
        catch (AdbCommandException ex) when (ex.ExitCode.HasValue && IsResolverUnavailable(ex.Message))
        {
            // On Android versions without cmd package, am resolves the package-scoped intent.
        }

        var launched = await ExecuteAsync(
            ["-s", serial, "shell", "am", "start", "-W", "-a", main, "-c", launcher,
                component is null ? "-p" : "-n", component is null ? packageName : QuoteRemoteShellArgument(component)],
            TimeSpan.FromSeconds(45), cancellationToken);
        var status = launched.StandardOutput.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!status.Contains("Status: ok", StringComparer.OrdinalIgnoreCase)
            || (component is null && !status.Any(line => line.StartsWith($"Activity: {packageName}/", StringComparison.Ordinal))))
            throw new AdbCommandException($"设备未确认应用 {packageName} 启动成功。{Environment.NewLine}{ErrorOutput(launched)}");
    }

    private static bool IsResolverUnavailable(string message) =>
        message.Contains("cmd: not found", StringComparison.OrdinalIgnoreCase)
        || message.Contains("cmd: inaccessible or not found", StringComparison.OrdinalIgnoreCase)
        || message.Contains("Unknown command: resolve-activity", StringComparison.OrdinalIgnoreCase);

    private async Task ExecuteTargetStepAsync(string serial, UiTestStep step, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(step.Value)) throw new ArgumentException("请输入控件的文字、资源 ID 或无障碍描述。");
        var nodes = await InspectUiAsync(serial, cancellationToken);
        var target = FindTarget(nodes, step.Value);
        if (target is null && step.ScrollIfMissing && FindSwipeRegion(nodes, scrollableOnly: true) is { } region)
        {
            await SwipeRegionAsync(serial, region, up: true, cancellationToken);
            nodes = await InspectUiAsync(serial, cancellationToken);
            target = FindTarget(nodes, step.Value);
        }
        if (target is null) throw new AdbCommandException($"当前界面未找到可见控件“{step.Value}”。");
        if (step.Action == UiTestAction.AssertVisible) return;
        var (node, bounds) = target.Value;
        if (!node.Enabled) throw new AdbCommandException($"控件“{step.Value}”已停用。");
        await ExecuteAsync(["-s", serial, "shell", "input", "tap", Coordinate(bounds.CenterX), Coordinate(bounds.CenterY)],
            TimeSpan.FromSeconds(15), cancellationToken);
    }

    private static (UiTestNode Node, UiTestBounds Bounds)? FindTarget(IReadOnlyList<UiTestNode> nodes, string selector)
    {
        var match = UiTestSelector.FindUnique(nodes, selector, out var ambiguous);
        if (ambiguous) throw new AdbCommandException($"多个可见控件匹配“{selector}”，请使用唯一的文字、资源 ID 或描述。");
        return match;
    }

    private static UiTestBounds? FindSwipeRegion(IReadOnlyList<UiTestNode> nodes, bool scrollableOnly)
    {
        UiTestBounds? largest = null;
        foreach (var node in nodes)
        {
            if (scrollableOnly && !node.Scrollable) continue;
            if (UiTestBounds.Parse(node.Bounds) is not { } bounds || bounds.Height < 4) continue;
            if (largest is null || bounds.Area > largest.Value.Area) largest = bounds;
        }
        return largest;
    }

    private async Task SwipeRegionAsync(string serial, UiTestBounds region, bool up, CancellationToken cancellationToken)
    {
        var x = Coordinate(region.CenterX);
        var top = Coordinate(region.Top + region.Height / 4);
        var bottom = Coordinate(region.Bottom - region.Height / 4);
        await ExecuteAsync(["-s", serial, "shell", "input", "swipe", x, up ? bottom : top, x, up ? top : bottom, "350"],
            TimeSpan.FromSeconds(15), cancellationToken);
    }

    private static string Coordinate(int value) => value.ToString(CultureInfo.InvariantCulture);
}
