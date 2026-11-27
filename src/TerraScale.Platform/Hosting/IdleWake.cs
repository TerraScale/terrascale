using System.Threading.Channels;

namespace TerraScale.Platform.Hosting;

public sealed class IdleWake
{
    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public static TimeSpan Backoff(TimeSpan current, TimeSpan maximum) => TimeSpan.FromTicks(Math.Min(current.Ticks * 2, maximum.Ticks));

    public void Signal() => _ = _signals.Writer.TryWrite(true);

    public void Clear() => _ = _signals.Reader.TryRead(out _);

    public async Task WaitAsync(TimeSpan delay, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        using var rest = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await Task.WhenAny(Task.Delay(delay, timeProvider, rest.Token), _signals.Reader.WaitToReadAsync(rest.Token).AsTask());
        await rest.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
