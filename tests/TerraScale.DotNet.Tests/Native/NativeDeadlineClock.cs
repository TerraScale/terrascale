namespace TerraScale.DotNet.Tests.Native;

// One request at a time: fixtures explicitly expire its latest deadline after
// the intended invocation stage starts. Real timeouts remain test hang guards.
internal sealed class NativeDeadlineClock : TimeProvider
{
    private ManualTimer? timer;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1790769600);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        timer = new ManualTimer(callback, state);
    internal void Expire() => timer!.Fire();

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private readonly object gate = new();
        private bool disposed;
        internal void Fire() { lock (gate) { if (!disposed) callback(state); } }
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (gate) return !disposed; }
        public void Dispose() { lock (gate) disposed = true; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
