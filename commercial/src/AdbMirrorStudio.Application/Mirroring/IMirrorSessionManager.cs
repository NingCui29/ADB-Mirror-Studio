using AdbMirrorStudio.Domain.Mirroring;

namespace AdbMirrorStudio.Application.Mirroring;

public interface IMirrorSessionManager : IAsyncDisposable
{
    IReadOnlyCollection<MirrorSession> ActiveSessions { get; }
    event EventHandler<MirrorSession>? SessionChanged;
    nint GetWindowHandle(MirrorSession session) => 0;
    Task<MirrorSession> StartAsync(
        string deviceSerial,
        MirrorProfile profile,
        string? windowTitle = null,
        CancellationToken cancellationToken = default);
    Task StopAsync(string deviceSerial, CancellationToken cancellationToken = default);
    async Task<MirrorSession> RestartAsync(
        string deviceSerial,
        MirrorProfile profile,
        CancellationToken cancellationToken = default)
    {
        await StopAsync(deviceSerial, cancellationToken).ConfigureAwait(false);
        return await StartAsync(deviceSerial, profile, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    Task<int> ArrangeWindowsAsync(MirrorWindowLayout layout, CancellationToken cancellationToken = default) => Task.FromResult(0);
}
