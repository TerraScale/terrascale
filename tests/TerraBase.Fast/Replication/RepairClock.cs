using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Replication;

namespace TerraBase.Fast.Replication;

internal sealed class RepairClock(DateTimeOffset started) : FakeTimeProvider(started)
{
    private readonly Channel<bool> _pauses = Channel.CreateBounded<bool>(1);

    public async Task PausedAsync(CancellationToken token) => _ = await _pauses.Reader.ReadAsync(token);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime == AntiEntropyWorker.ReconcileInterval && period == Timeout.InfiniteTimeSpan && !_pauses.Writer.TryWrite(true))
        {
            throw new InvalidOperationException("a repair pass pause was not consumed");
        }

        return timer;
    }
}
