namespace AdbMirrorStudio.Application.Commands;

public sealed record CommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    TimeSpan? Timeout = null,
    bool SensitiveArguments = false);

public sealed record CommandResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut,
    bool Cancelled)
{
    public const int CaptureCharacterLimit = 1_000_000;
    public OutputTruncation StandardOutputTruncation { get; init; }
    public OutputTruncation StandardErrorTruncation { get; init; }
    public bool IsSuccess => ExitCode == 0 && !TimedOut && !Cancelled;
}

public enum OutputTruncation { None, CharacterLimit, Interrupted }

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(CommandRequest request, CancellationToken cancellationToken = default);
}

