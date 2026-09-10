using AdbMirrorStudio.Infrastructure.Scrcpy;

namespace AdbMirrorStudio.UnitTests;

public sealed class ScrcpyErrorSummaryTests
{
    [Fact]
    public void SummarizeError_PreservesAudioDeviceFailureBeforeGenericDemuxerError()
    {
        const string cause = "ERROR: Could not open audio device: No default audio device available";
        var stderr = $"scrcpy-server: 1 file pushed\r\n{cause}\r\nERROR: Demuxer error\r\n";

        var error = MirrorSessionManager.SummarizeError(stderr, "INFO: Texture: 1920x1080");

        Assert.Equal(cause, error);
    }

    [Fact]
    public void SummarizeError_FindsServerCauseAcrossStreams()
    {
        const string cause = "[server] ERROR: Could not create video encoder";

        var error = MirrorSessionManager.SummarizeError("ERROR: Demuxer error", $"{cause}\nDEBUG: Server terminated");

        Assert.Equal(cause, error);
    }

    [Theory]
    [InlineData("ERROR: Demuxer error", "ERROR: Demuxer error")]
    [InlineData("ERROR: Could not open recording file\nINFO: Stopped", "ERROR: Could not open recording file")]
    [InlineData("\r\n WARN: Device disconnected \r\n", "WARN: Device disconnected")]
    [InlineData("\r\n  \r\n", null)]
    public void SummarizeError_KeepsUsefulFallbacks(string output, string? expected)
    {
        Assert.Equal(expected, MirrorSessionManager.SummarizeError(output));
    }
}
