using AdbMirrorStudio.Application.Commands;
using AdbMirrorStudio.Application.Diagnostics;

namespace AdbMirrorStudio.Infrastructure.Diagnostics;

public sealed class DiagnosticsService(
    ICommandRunner commandRunner,
    string adbPath,
    string scrcpyPath) : IDiagnosticsService
{
    public async Task<IReadOnlyList<DiagnosticItem>> RunAsync(CancellationToken cancellationToken = default)
    {
        var checks = new[]
        {
            CheckBinaryAsync("adb-file", "ADB 组件", adbPath, cancellationToken),
            CheckBinaryAsync("scrcpy-file", "scrcpy 组件", scrcpyPath, cancellationToken),
            CheckCommandAsync("adb-version", "ADB 版本", adbPath, ["version"], cancellationToken),
            CheckCommandAsync("adb-server", "ADB 服务", adbPath, ["devices", "-l"], cancellationToken),
            CheckCommandAsync("mdns", "无线发现 mDNS", adbPath, ["mdns", "check"], cancellationToken),
            CheckCommandAsync("scrcpy-version", "scrcpy 版本", scrcpyPath, ["--version"], cancellationToken),
            Task.FromResult(CheckOtherAdbCopies())
        };

        return await Task.WhenAll(checks).ConfigureAwait(false);
    }

    private async Task<DiagnosticItem> CheckBinaryAsync(
        string id,
        string title,
        string path,
        CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            return new DiagnosticItem(id, title, $"缺少文件：{Path.GetFileName(path)}", DiagnosticSeverity.Error);
        }

        try
        {
            var size = new FileInfo(path).Length;
            return size == 0
                ? new DiagnosticItem(id, title, "组件文件为空，请重新安装", DiagnosticSeverity.Error)
                : new DiagnosticItem(id, title, $"文件存在，{size:N0} 字节", DiagnosticSeverity.Success);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new DiagnosticItem(id, title, exception.Message, DiagnosticSeverity.Error);
        }
    }

    private async Task<DiagnosticItem> CheckCommandAsync(
        string id,
        string title,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(executable))
        {
            return new DiagnosticItem(id, title, "无法检查：组件缺失", DiagnosticSeverity.Error);
        }

        try
        {
            var result = await commandRunner.RunAsync(
                new CommandRequest(
                    executable,
                    arguments,
                    Path.GetDirectoryName(executable),
                    Timeout: TimeSpan.FromSeconds(12)),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            if (result.Cancelled)
            {
                throw new OperationCanceledException("诊断检查已取消。", cancellationToken);
            }

            if (result.TimedOut)
            {
                return new DiagnosticItem(id, title, "检查超时", DiagnosticSeverity.Error);
            }

            var output = FirstMeaningfulLine(result.StandardOutput, result.StandardError);
            return result.IsSuccess
                ? new DiagnosticItem(id, title, output ?? "检查通过", DiagnosticSeverity.Success)
                : new DiagnosticItem(id, title, output ?? $"退出码 {result.ExitCode}", DiagnosticSeverity.Error);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DiagnosticItem(id, title, exception.Message, DiagnosticSeverity.Error);
        }
    }

    private DiagnosticItem CheckOtherAdbCopies()
    {
        var bundled = Path.GetFullPath(adbPath);
        var copies = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.Combine(path, "adb.exe"))
            .Where(File.Exists)
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, bundled, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];

        return copies.Length == 0
            ? new DiagnosticItem("adb-copies", "ADB 版本冲突", "未在 PATH 中发现其他 adb.exe", DiagnosticSeverity.Success)
            : new DiagnosticItem(
                "adb-copies",
                "ADB 版本冲突",
                $"发现 {copies.Length} 个其他 ADB；仅在连接异常时考虑统一版本",
                DiagnosticSeverity.Warning);
    }

    private static string? FirstMeaningfulLine(params string[] outputs) =>
        outputs.SelectMany(output => output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !line.StartsWith('*'));
}
