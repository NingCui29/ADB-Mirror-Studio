using System.Diagnostics;
using System.Text;
using AdbMirrorStudio.Application.Commands;

namespace AdbMirrorStudio.Infrastructure.Processes;

public sealed class ProcessCommandRunner : ICommandRunner
{
    private const int MaxCapturedCharacters = 1_000_000;

    public async Task<CommandResult> RunAsync(
        CommandRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        cancellationToken.ThrowIfCancellationRequested();
        var timeout = request.Timeout ?? TimeSpan.FromSeconds(30);
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.FileName) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.Environment is not null)
        {
            foreach (var (key, value) in request.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stopwatch = Stopwatch.StartNew();

        if (!process.Start())
        {
            throw new InvalidOperationException($"无法启动进程：{request.FileName}");
        }

        var stdoutTask = ReadCappedAsync(process.StandardOutput, linkedCts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, linkedCts.Token);

        var timedOut = false;
        var cancelled = false;

        try
        {
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            // A child can keep inherited output pipes open after the main process exits.
            await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(linkedCts.Token).ConfigureAwait(false);
            linkedCts.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            cancelled = cancellationToken.IsCancellationRequested;
            TryKillProcessTree(process);
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(cleanupTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Do not hang the caller indefinitely if Windows denies process termination.
            }
        }
        catch
        {
            TryKillProcessTree(process);
            linkedCts.Cancel();
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        stopwatch.Stop();

        return new CommandResult(
            process.HasExited ? process.ExitCode : -1,
            stdout,
            stderr,
            stopwatch.Elapsed,
            timedOut,
            cancelled);
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var result = new StringBuilder();

        var truncated = false;
        try
        {
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (count == 0) break;

                var remaining = MaxCapturedCharacters - result.Length;
                truncated |= count > remaining;
                if (remaining > 0)
                {
                    result.Append(buffer, 0, Math.Min(count, remaining));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Return output already received when the overall command is cancelled or times out.
        }

        if (truncated)
        {
            result.AppendLine().Append("[输出已截断]");
        }

        return result.ToString();
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited or became inaccessible between HasExited and Kill.
        }
    }
}
