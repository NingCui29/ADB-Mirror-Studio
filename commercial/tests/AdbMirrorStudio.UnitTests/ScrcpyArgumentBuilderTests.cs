using AdbMirrorStudio.Application.Mirroring;
using AdbMirrorStudio.Domain.Mirroring;

namespace AdbMirrorStudio.UnitTests;

public sealed class ScrcpyArgumentBuilderTests
{
    [Fact]
    public void Build_BalancedProfile_ProducesSafeIndividualArguments()
    {
        var arguments = ScrcpyArgumentBuilder.Build("192.168.1.8:37111", MirrorProfile.Balanced, "测试设备");

        Assert.Contains("--serial=192.168.1.8:37111", arguments);
        Assert.Contains("--window-title=测试设备", arguments);
        Assert.Contains("--max-size=1920", arguments);
        Assert.Contains("--max-fps=60", arguments);
        Assert.Contains("--video-bit-rate=8M", arguments);
        Assert.Contains("--stay-awake", arguments);
        Assert.DoesNotContain("--no-audio", arguments);
        Assert.DoesNotContain("--no-audio-playback", arguments);
    }

    [Fact]
    public void Build_NoAudioOutput_AllowsMirroringWithoutChangingTheAudioPreference()
    {
        var profile = MirrorProfile.Balanced;

        var arguments = ScrcpyArgumentBuilder.Build("device", profile, audioPlaybackAvailable: false);

        Assert.Contains("--no-audio-playback", arguments);
        Assert.DoesNotContain("--no-audio", arguments);
        Assert.DoesNotContain("--no-video", arguments);
        Assert.DoesNotContain("--no-playback", arguments);
        Assert.True(profile.AudioEnabled);
        Assert.DoesNotContain("--no-audio-playback",
            ScrcpyArgumentBuilder.Build("device", profile, audioPlaybackAvailable: true));
    }

    [Theory]
    [InlineData("mp4")]
    [InlineData("mkv")]
    public void Build_NoAudioOutput_PreservesRecordingAudio(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), $"recording.{extension}");
        var profile = MirrorProfile.Balanced with { RecordPath = path };

        var arguments = ScrcpyArgumentBuilder.Build("device", profile, audioPlaybackAvailable: false);

        Assert.Contains("--no-audio-playback", arguments);
        Assert.DoesNotContain("--no-audio", arguments);
        Assert.Contains($"--record={path}", arguments);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_AudioCaptureDisabled_RespectsTheProfile(bool audioPlaybackAvailable)
    {
        var arguments = ScrcpyArgumentBuilder.Build("device", MirrorProfile.Performance,
            audioPlaybackAvailable: audioPlaybackAvailable);

        Assert.Contains("--no-audio", arguments);
        Assert.DoesNotContain("--no-audio-playback", arguments);
    }

    [Fact]
    public void Build_RejectsUnsafeRanges()
    {
        var profile = MirrorProfile.Balanced with { MaxFps = 1000 };
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrcpyArgumentBuilder.Build("device", profile));
    }

    [Fact]
    public void Build_RecordingProfile_UsesSafeWindowedPresentationOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), "镜像 录制.mkv");
        var profile = MirrorProfile.Presentation with { RecordPath = path };

        var arguments = ScrcpyArgumentBuilder.Build("device", profile);

        Assert.Contains($"--record={Path.GetFullPath(path)}", arguments);
        Assert.DoesNotContain("--fullscreen", arguments);
        Assert.DoesNotContain("--always-on-top", arguments);
        Assert.Contains("--stay-awake", arguments);
        Assert.DoesNotContain("--turn-screen-off", arguments);
        Assert.DoesNotContain("--no-control", arguments);
    }

    [Fact]
    public void Presets_NeverForceFullscreenOrAlwaysOnTop()
    {
        foreach (var profile in MirrorProfile.Presets)
        {
            Assert.False(profile.Fullscreen);
            Assert.False(profile.AlwaysOnTop);
        }
    }

    [Fact]
    public void Build_ReadOnlyProfile_DropsOptionsThatRequireControl()
    {
        var profile = MirrorProfile.Balanced with { ReadOnly = true, TurnScreenOff = true };

        var arguments = ScrcpyArgumentBuilder.Build("device", profile);

        Assert.Contains("--no-control", arguments);
        Assert.DoesNotContain("--stay-awake", arguments);
        Assert.DoesNotContain("--turn-screen-off", arguments);
    }

    [Fact]
    public void Build_RejectsUnsupportedRecordingContainer()
    {
        var profile = MirrorProfile.Balanced with { RecordPath = "recording.avi" };
        Assert.Throws<ArgumentException>(() => ScrcpyArgumentBuilder.Build("device", profile));
    }

    [Fact]
    public void Build_AcceptsUppercaseRecordingContainer()
    {
        var profile = MirrorProfile.Balanced with { RecordPath = "recording.MP4" };

        var arguments = ScrcpyArgumentBuilder.Build("device", profile);

        Assert.Contains($"--record={Path.GetFullPath("recording.MP4")}", arguments);
    }

    [Fact]
    public void Build_AddsResolvedVideoCodec()
    {
        var profile = MirrorProfile.Quality with { VideoCodec = "h265" };

        var arguments = ScrcpyArgumentBuilder.Build("device", profile);

        Assert.Contains("--video-codec=h265", arguments);
    }

    [Fact]
    public void Build_RejectsUnknownVideoCodec()
    {
        var profile = MirrorProfile.Balanced with { VideoCodec = "unknown" };
        Assert.Throws<ArgumentOutOfRangeException>(() => ScrcpyArgumentBuilder.Build("device", profile));
    }

    [Fact]
    public void MirrorRecordingProfile_StartsRecordingWithoutFullscreenOrAlwaysOnTop()
    {
        var activeProfile = MirrorProfile.Quality with
        {
            Fullscreen = true,
            AlwaysOnTop = true,
            VideoCodec = "h265"
        };
        var session = new MirrorSession(
            "session",
            "device",
            MirrorSessionState.Running,
            42,
            DateTimeOffset.UtcNow,
            ProfileName: activeProfile.Name,
            VideoCodec: activeProfile.VideoCodec,
            MaxSize: activeProfile.MaxSize,
            MaxFps: activeProfile.MaxFps,
            VideoBitRateMbps: activeProfile.VideoBitRateMbps,
            Profile: activeProfile);
        var path = Path.Combine(Path.GetTempPath(), "运行中开始录制.mkv");

        var recordingProfile = MirrorRecordingProfile.Create(session, path);

        Assert.Equal(Path.GetFullPath(path), recordingProfile.RecordPath);
        Assert.False(recordingProfile.Fullscreen);
        Assert.False(recordingProfile.AlwaysOnTop);
        Assert.Equal("h265", recordingProfile.VideoCodec);
    }

    [Fact]
    public void MirrorRecordingProfile_StopsRecordingAndPreservesCaptureSettings()
    {
        var activeProfile = MirrorProfile.Presentation with
        {
            RecordPath = Path.Combine(Path.GetTempPath(), "active.mp4"),
            VideoCodec = "h264"
        };
        var session = new MirrorSession(
            "session",
            "device",
            MirrorSessionState.Running,
            42,
            DateTimeOffset.UtcNow,
            ProfileName: activeProfile.Name,
            VideoCodec: activeProfile.VideoCodec,
            MaxSize: activeProfile.MaxSize,
            MaxFps: activeProfile.MaxFps,
            VideoBitRateMbps: activeProfile.VideoBitRateMbps,
            RecordPath: activeProfile.RecordPath,
            Profile: activeProfile);

        var mirrorProfile = MirrorRecordingProfile.Create(session, null);

        Assert.Null(mirrorProfile.RecordPath);
        Assert.Equal(activeProfile.MaxSize, mirrorProfile.MaxSize);
        Assert.Equal(activeProfile.MaxFps, mirrorProfile.MaxFps);
        Assert.Equal(activeProfile.VideoBitRateMbps, mirrorProfile.VideoBitRateMbps);
        Assert.Equal(activeProfile.VideoCodec, mirrorProfile.VideoCodec);
    }
}
