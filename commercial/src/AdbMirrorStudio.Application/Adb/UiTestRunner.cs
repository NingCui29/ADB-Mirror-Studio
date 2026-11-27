namespace AdbMirrorStudio.Application.Adb;

public sealed class UiTestRunner(IAdbService adb)
{
    public async Task RunAsync(string serial, IReadOnlyList<UiTestStep> steps, string outputDirectory,
        Action<string> report, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        var plan = steps.ToArray();
        if (plan.Length == 0) throw new ArgumentException("请先添加测试步骤。");
        cancellationToken.ThrowIfCancellationRequested();
        var runDirectory = Directory.CreateDirectory(Path.Combine(Path.GetFullPath(outputDirectory),
            $"ui-test-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}")).FullName;
        var reportPath = Path.Combine(runDirectory, "report.txt");
        await using var writer = new StreamWriter(reportPath);
        writer.AutoFlush = true;

        async Task RecordAsync(string line)
        {
            await writer.WriteLineAsync(line);
            report(line);
        }

        await RecordAsync($"设备 {serial} · {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        await RecordAsync($"报告 {reportPath}");
        for (var i = 0; i < plan.Length; i++)
        {
            var step = plan[i];
            var label = $"{i + 1}/{plan.Length} {step.ActionLabel}";
            if (step.Action is UiTestAction.Launch or UiTestAction.Tap or UiTestAction.AssertVisible)
                label += $" · {step.Value.ReplaceLineEndings(" ")}";
            string? artifact = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (step.Action)
                {
                    case UiTestAction.Screenshot:
                        artifact = $"{i + 1:D3}-screen.png";
                        await adb.CaptureScreenshotAsync(serial, Path.Combine(runDirectory, artifact), cancellationToken);
                        break;
                    case UiTestAction.Logcat:
                        artifact = $"{i + 1:D3}-logcat.txt";
                        var log = await adb.GetLogcatSnapshotAsync(serial, 500, cancellationToken);
                        cancellationToken.ThrowIfCancellationRequested();
                        await File.WriteAllTextAsync(Path.Combine(runDirectory, artifact), log);
                        break;
                    default:
                        await adb.ExecuteUiStepAsync(serial, step, cancellationToken);
                        break;
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RecordAsync($"已取消 · {label}");
                return;
            }
            catch (Exception ex)
            {
                await RecordAsync($"失败 · {label} · {ex.Message}");
                return;
            }

            await RecordAsync(artifact is null ? $"通过 · {label}" : $"通过 · {label} · {artifact}");
        }

        await RecordAsync($"测试通过，共 {plan.Length} 步。");
    }
}
