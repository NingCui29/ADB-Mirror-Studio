using AdbMirrorStudio.Domain.Mirroring;

namespace AdbMirrorStudio.Application.Mirroring;

public static class ScrcpyArgumentBuilder
{
    public static IReadOnlyList<string> Build(
        string serial,
        MirrorProfile profile,
        string? windowTitle = null,
        bool audioPlaybackAvailable = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serial);
        Validate(profile);

        var arguments = new List<string>
        {
            $"--serial={serial}",
            $"--window-title={windowTitle ?? serial}"
        };

        if (profile.MaxSize > 0) arguments.Add($"--max-size={profile.MaxSize}");
        if (profile.MaxFps > 0) arguments.Add($"--max-fps={profile.MaxFps}");
        if (profile.VideoBitRateMbps > 0) arguments.Add($"--video-bit-rate={profile.VideoBitRateMbps}M");
        if (!profile.VideoCodec.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add($"--video-codec={profile.VideoCodec.ToLowerInvariant()}");
        }
        if (!profile.AudioEnabled) arguments.Add("--no-audio");
        else if (!audioPlaybackAvailable) arguments.Add("--no-audio-playback");
        if (profile.StayAwake && !profile.ReadOnly) arguments.Add("--stay-awake");
        if (profile.TurnScreenOff && !profile.ReadOnly) arguments.Add("--turn-screen-off");
        if (profile.Fullscreen) arguments.Add("--fullscreen");
        if (profile.AlwaysOnTop) arguments.Add("--always-on-top");
        if (profile.ReadOnly) arguments.Add("--no-control");
        if (!string.IsNullOrWhiteSpace(profile.RecordPath)) arguments.Add($"--record={Path.GetFullPath(profile.RecordPath)}");

        return arguments;
    }

    private static void Validate(MirrorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.MaxSize is < 0 or > 8192) throw new ArgumentOutOfRangeException(nameof(profile.MaxSize));
        if (profile.MaxFps is < 0 or > 240) throw new ArgumentOutOfRangeException(nameof(profile.MaxFps));
        if (profile.VideoBitRateMbps is < 0 or > 200) throw new ArgumentOutOfRangeException(nameof(profile.VideoBitRateMbps));
        if (profile.VideoCodec.ToLowerInvariant() is not ("auto" or "h264" or "h265" or "av1" or "vp8" or "vp9"))
        {
            throw new ArgumentOutOfRangeException(nameof(profile.VideoCodec));
        }
        if (!string.IsNullOrWhiteSpace(profile.RecordPath))
        {
            var extension = Path.GetExtension(profile.RecordPath);
            if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("录屏文件必须使用 .mp4 或 .mkv 扩展名。", nameof(profile.RecordPath));
            }
        }
    }
}
