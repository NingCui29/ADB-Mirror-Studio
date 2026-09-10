namespace AdbMirrorStudio.Domain.Mirroring;

public sealed record MirrorProfile(
    string Id,
    string Name,
    int MaxSize,
    int MaxFps,
    int VideoBitRateMbps,
    bool AudioEnabled,
    bool StayAwake,
    bool TurnScreenOff,
    bool Fullscreen,
    bool AlwaysOnTop,
    bool ReadOnly,
    string? RecordPath = null,
    string VideoCodec = "auto")
{
    public static MirrorProfile Performance { get; } = new(
        "performance", "流畅", 1280, 60, 4, false, true, false, false, false, false);
    public static MirrorProfile Balanced { get; } = new(
        "balanced", "均衡", 1920, 60, 8, true, true, false, false, false, false);
    public static MirrorProfile Quality { get; } = new(
        "quality", "高清", 2560, 60, 16, true, true, false, false, false, false);
    public static MirrorProfile Presentation { get; } = new(
        "presentation", "演示", 1920, 30, 8, true, true, false, false, false, false);

    public static IReadOnlyList<MirrorProfile> Presets { get; } =
        [Performance, Balanced, Quality, Presentation];
}

public enum MirrorSessionState
{
    Validating,
    Starting,
    Running,
    Stopping,
    Exited,
    Failed
}

public sealed record MirrorSession(
    string Id,
    string DeviceSerial,
    MirrorSessionState State,
    int? ProcessId,
    DateTimeOffset StartedAt,
    int? ExitCode = null,
    string? Error = null,
    string ProfileName = "—",
    string VideoCodec = "h264",
    int MaxSize = 0,
    int MaxFps = 0,
    int VideoBitRateMbps = 0,
    string? RecordPath = null,
    MirrorProfile? Profile = null,
    string? WindowTitle = null,
    bool AudioPlaybackEnabled = true);

public enum MirrorWindowLayout
{
    Grid,
    Horizontal,
    Vertical
}
