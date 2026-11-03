using Microsoft.UI.Xaml.Controls;

namespace AdbMirrorStudio.UnitTests;

public sealed partial class MainViewModelBehaviorTests
{
    [Fact]
    public async Task Status_SuccessDismissesAfterFiveSecondsOnUiContext()
    {
        var clock = new StatusTestClock();
        var ui = new QueuedStatusContext();
        using var model = Create(new FakeAdb(), timeProvider: clock, uiContext: ui);
        await model.ClearRememberedEndpointsAsync();

        Assert.Equal(InfoBarSeverity.Success, model.StatusSeverity);
        clock.Advance(TimeSpan.FromSeconds(4));
        ui.Drain();
        Assert.True(model.IsStatusOpen);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(model.IsStatusOpen); // The timer may not mutate UI state directly.
        ui.Drain();

        Assert.False(model.IsStatusOpen);
        Assert.Equal("已清除连接历史；当前在线设备不会断开", model.StatusText);
    }

    [Fact]
    public async Task Status_RepeatedSuccessResetsTimeoutAndRejectsAlreadyQueuedDismissal()
    {
        var clock = new StatusTestClock();
        var ui = new QueuedStatusContext();
        using var model = Create(new FakeAdb(), timeProvider: clock, uiContext: ui);
        await model.ClearRememberedEndpointsAsync();
        clock.Advance(TimeSpan.FromSeconds(5));

        await model.ClearRememberedEndpointsAsync();
        ui.Drain();
        clock.Advance(TimeSpan.FromSeconds(4));
        ui.Drain();
        Assert.True(model.IsStatusOpen);
        clock.Advance(TimeSpan.FromSeconds(1));
        ui.Drain();
        Assert.False(model.IsStatusOpen);

        await model.ClearRememberedEndpointsAsync();
        Assert.True(model.IsStatusOpen);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("warning")]
    [InlineData("progress")]
    public async Task Status_SuccessTimerCannotDismissReplacementThatNeedsAttention(string replacement)
    {
        var clock = new StatusTestClock();
        var ui = new QueuedStatusContext();
        var pendingShell = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var adb = new FakeAdb
        {
            Shell = _ => replacement == "error"
                ? Task.FromException<string>(new IOException("读取失败"))
                : pendingShell.Task
        };
        using var model = Create(adb, timeProvider: clock, uiContext: ui);
        await model.ClearRememberedEndpointsAsync();
        clock.Advance(TimeSpan.FromSeconds(5));

        Task? operation = null;
        if (replacement == "warning") await model.GetDeviceDetailsAsync(null);
        else
        {
            operation = model.RunDeviceShellCommandAsync("device", "echo test");
            if (replacement == "error") await operation;
        }
        var expected = replacement switch
        {
            "error" => InfoBarSeverity.Error,
            "warning" => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Informational
        };
        Assert.Equal(expected, model.StatusSeverity);
        var message = model.StatusText;
        ui.Drain();
        clock.Advance(TimeSpan.FromMinutes(1));
        ui.Drain();

        Assert.True(model.IsStatusOpen);
        Assert.Equal(message, model.StatusText);
        Assert.Equal(0, clock.ActiveTimerCount);
        if (replacement == "progress")
        {
            pendingShell.SetResult("done");
            await operation!;
        }
    }

    [Fact]
    public async Task Status_ManualCloseCancelsTimeoutAndOldCallbackCannotCloseNextSuccess()
    {
        var clock = new StatusTestClock();
        var ui = new QueuedStatusContext();
        using var model = Create(new FakeAdb(), timeProvider: clock, uiContext: ui);
        await model.ClearRememberedEndpointsAsync();
        clock.Advance(TimeSpan.FromSeconds(5));
        model.IsStatusOpen = false;
        Assert.Equal(0, clock.ActiveTimerCount);
        Assert.False(model.IsStatusOpen);

        await model.ClearRememberedEndpointsAsync();
        ui.Drain();
        Assert.True(model.IsStatusOpen);
        clock.Advance(TimeSpan.FromSeconds(4));
        ui.Drain();
        Assert.True(model.IsStatusOpen);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_DisposeCancelsTimerAndRejectsQueuedCallbacks(bool alreadyQueued)
    {
        var clock = new StatusTestClock();
        var ui = new QueuedStatusContext();
        var model = Create(new FakeAdb(), timeProvider: clock, uiContext: ui);
        await model.ClearRememberedEndpointsAsync();
        if (alreadyQueued) clock.Advance(TimeSpan.FromSeconds(5));
        var changesAfterDispose = 0;
        model.PropertyChanged += (_, _) => changesAfterDispose++;

        model.Dispose();
        clock.Advance(TimeSpan.FromMinutes(1));
        ui.Drain();

        Assert.Equal(0, clock.ActiveTimerCount);
        Assert.Equal(0, changesAfterDispose);
        Assert.True(model.IsStatusOpen);
    }

    private sealed class QueuedStatusContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        public override void Post(SendOrPostCallback callback, object? state) => _callbacks.Enqueue((callback, state));
        public void Drain()
        {
            while (_callbacks.TryDequeue(out var work)) work.Callback(work.State);
        }
    }

    private sealed class StatusTestClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<StatusTimer> _timers = [];
        public int ActiveTimerCount => _timers.Count(timer => timer.DueAt is not null);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new StatusTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
        public void Advance(TimeSpan duration)
        {
            _now += duration;
            foreach (var timer in _timers.ToArray())
                if (timer.DueAt is { } due && due <= _now) timer.Fire();
        }

        private sealed class StatusTimer(StatusTestClock clock, TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;
            private TimeSpan _period;
            public DateTimeOffset? DueAt { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }
            public void Fire()
            {
                if (_disposed) return;
                DueAt = _period == Timeout.InfiniteTimeSpan ? null : clock._now + _period;
                callback(state);
            }
            public void Dispose() { _disposed = true; DueAt = null; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
