using System.Diagnostics;
using System.Text;
using AdbMirrorStudio.Application.Commands;

namespace AdbMirrorStudio.Infrastructure.Processes;

public sealed class ProcessCommandRunner : ICommandRunner
{
    private const int MaxCapturedCharacters = 1_000_000;
    private static readonly TimeSpan MaxPipeDrainGrace = TimeSpan.FromSeconds(2);

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
        // Output readers use their own token. Bounding a pipe drain must never be
        // reported as a command timeout.
        using var outputCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

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

        var stdoutTask = ReadCappedAsync(process.StandardOutput, outputCts.Token);
        var stderrTask = ReadCappedAsync(process.StandardError, outputCts.Token);
        // Keep the aggregate task so every exit path can observe its exceptions.
        var drainTask = Task.WhenAll(stdoutTask, stderrTask);

        var timedOut = false;
        var cancelled = false;

        try
        {
            // This wait alone represents the command lifetime. Once the main process
            // has exited, the command has finished, even if a descendant process
            // still keeps the inherited output pipes open.
            await process.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
            if (cancellationToken.IsCancellationRequested) cancelled = true;
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
            outputCts.Cancel();
            ObserveDrain(drainTask);
            throw;
        }

        // A child can keep inherited output pipes open after the main process exits.
        // Bound the drain separately: a slow drain truncates output, it does not turn
        // a successfully completed command into a timeout.
        if (!drainTask.IsCompleted)
        {
            var grace = timeout < MaxPipeDrainGrace ? timeout : MaxPipeDrainGrace;
            using var drainGrace = new CancellationTokenSource(grace);
            try
            {
                await drainTask.WaitAsync(drainGrace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                outputCts.Cancel();
                ObserveDrain(drainTask);
                // Some Windows anonymous pipes ignore cancellation. Closing the read
                // end unblocks a pending ReadAsync so the caller cannot hang forever.
                TryCloseReader(process.StandardOutput);
                TryCloseReader(process.StandardError);
            }
        }

        string stdout;
        string stderr;
        try
        {
            stdout = await stdoutTask.ConfigureAwait(false);
            stderr = await stderrTask.ConfigureAwait(false);
        }
        catch
        {
            ObserveDrain(drainTask);
            throw;
        }

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
            // Return output already received when the drain is cancelled or bounded.
            truncated = true;
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The read end was closed while a read was pending, for example after
            // the process tree was killed. Keep whatever was already captured.
            truncated = true;
        }

        if (truncated)
        {
            result.AppendLine().Append("[输出已截断]");
        }

        return result.ToString();
    }

    private static void TryCloseReader(StreamReader reader)
    {
        try
        {
            reader.Dispose();
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The reader may already be closed or owned by a completed read.
        }
    }

    private static void ObserveDrain(Task drainTask) =>
        _ = drainTask.ContinueWith(
            static task => _ = task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            // Kill(entireProcessTree) only walks descendants while the root is alive.
            // Orphaned descendants lose their inherited pipe handles when the drain
            // above closes the read end.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // The process exited or became inaccessible between HasExited and Kill.
        }
    }
}
