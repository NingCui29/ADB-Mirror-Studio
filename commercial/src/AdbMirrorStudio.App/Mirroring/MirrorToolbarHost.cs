using AdbMirrorStudio.Application.Mirroring;
using AdbMirrorStudio.Domain.Mirroring;
using Microsoft.UI.Dispatching;

namespace AdbMirrorStudio.App.Mirroring;

internal sealed class MirrorToolbarHost : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly IMirrorSessionManager _sessions;
    private readonly Action<MirrorSession, nint, int?> _invoke;
    private readonly DispatcherQueueTimer _timer;
    private readonly Dictionary<string, MirrorToolbarWindow> _windows = new(StringComparer.Ordinal);
    private bool _disposed;

    public MirrorToolbarHost(DispatcherQueue dispatcher, IMirrorSessionManager sessions,
        Action<MirrorSession, nint, int?> invoke)
    {
        _dispatcher = dispatcher;
        _sessions = sessions;
        _invoke = invoke;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += OnTick;
        _sessions.SessionChanged += OnSessionChanged;
        Synchronize();
    }

    private void OnTick(DispatcherQueueTimer sender, object args) => Synchronize();

    private void OnSessionChanged(object? sender, MirrorSession session) => _dispatcher.TryEnqueue(Synchronize);

    private void Synchronize()
    {
        if (_disposed) return;
        var running = _sessions.ActiveSessions.Where(session => session.State == MirrorSessionState.Running).ToArray();
        foreach (var id in _windows.Keys.ToArray())
        {
            if (!running.Any(session => session.Id == id)) RemoveWindow(id);
        }

        foreach (var session in running)
        {
            if (_windows.TryGetValue(session.Id, out var toolbar))
            {
                if (MirrorWindowNative.BelongsToProcess(toolbar.Owner, session.ProcessId))
                {
                    toolbar.Follow(MirrorWindowNative.GetPlacement(toolbar.Owner));
                    continue;
                }
                RemoveWindow(session.Id);
            }

            var owner = _sessions.GetWindowHandle(session);
            if (!MirrorWindowNative.BelongsToProcess(owner, session.ProcessId)
                || MirrorWindowNative.GetPlacement(owner) is not { } placement) continue;
            toolbar = new MirrorToolbarWindow(session, owner, _invoke);
            _windows.Add(session.Id, toolbar);
            toolbar.Follow(placement);
        }

        if (running.Length == 0) _timer.Stop();
        else if (!_timer.IsRunning) _timer.Start();
    }

    private void RemoveWindow(string id)
    {
        if (_windows.Remove(id, out var window) && !window.IsClosed) window.Close();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessions.SessionChanged -= OnSessionChanged;
        _timer.Stop();
        _timer.Tick -= OnTick;
        foreach (var id in _windows.Keys.ToArray()) RemoveWindow(id);
    }
}
