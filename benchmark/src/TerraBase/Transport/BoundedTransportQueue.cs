using System.Threading.Channels;

namespace TerraBase.Transport;

/// <summary>Admission outcome for a non-blocking queue write.</summary>
public enum TransportEnqueueResult
{
    Accepted,
    Full,
    Closed,
}

/// <summary>
/// A bounded FIFO queue used at the transport admission boundary. The channel
/// never grows beyond <see cref="Capacity"/>; callers can either apply
/// backpressure with <see cref="EnqueueAsync"/> or fail fast with
/// <see cref="TryEnqueue"/>.
/// </summary>
public sealed class BoundedTransportQueue<T> : IAsyncDisposable
{
    private readonly Channel<T> channel;
    private readonly int capacity;
    private int count;
    private int completed;

    public BoundedTransportQueue(int capacity)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));

        this.capacity = capacity;
        channel = Channel.CreateBounded<T>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>The maximum number of items that may be admitted.</summary>
    public int Capacity => capacity;

    /// <summary>A best-effort count of currently queued items.</summary>
    public int Count => Math.Max(0, Volatile.Read(ref count));

    /// <summary>Whether no more items can be admitted.</summary>
    public bool IsCompleted => Volatile.Read(ref completed) != 0;

    /// <summary>
    /// Waits for capacity and enqueues an item. Cancellation releases the
    /// waiter without changing queue state.
    /// </summary>
    public async ValueTask EnqueueAsync(T item, CancellationToken cancellationToken = default)
    {
        while (await channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
        {
            if (channel.Writer.TryWrite(item))
            {
                Interlocked.Increment(ref count);
                return;
            }
        }

        throw new ChannelClosedException();
    }

    /// <summary>Attempts one immediate enqueue without waiting for capacity.</summary>
    public TransportEnqueueResult TryEnqueue(T item)
    {
        if (IsCompleted) return TransportEnqueueResult.Closed;
        if (!channel.Writer.TryWrite(item))
            return IsCompleted ? TransportEnqueueResult.Closed : TransportEnqueueResult.Full;

        Interlocked.Increment(ref count);
        return TransportEnqueueResult.Accepted;
    }

    /// <summary>Attempts one immediate dequeue.</summary>
    public bool TryDequeue(out T item)
    {
        if (!channel.Reader.TryRead(out item!)) return false;
        Interlocked.Decrement(ref count);
        return true;
    }

    /// <summary>Reads admitted items until the queue is completed.</summary>
    public async IAsyncEnumerable<T> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Decrement(ref count);
            yield return item;
        }
    }

    /// <summary>Stops admission while allowing already queued items to drain.</summary>
    public void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref completed, 1) == 0)
            channel.Writer.TryComplete(error);
    }

    /// <summary>Completes the queue and releases its channel waiters.</summary>
    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }
}

