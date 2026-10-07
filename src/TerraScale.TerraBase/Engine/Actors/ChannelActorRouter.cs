using System.Buffers.Binary;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace TerraBase.Engine.Actors;

internal sealed class ChannelActorRouter : IActorRouter
{
    private const int MaxKeyBytes = 4096;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Mailbox[] _actors;
    private readonly Counter<long> _rejected;
    private int _closed;

    internal ChannelActorRouter(int actorCount, int maxItems, int maxBytes, Meter meter)
    {
        _rejected = meter.CreateCounter<long>("terrabase.actors.rejected", description: "Callbacks refused by actor admission.");
        var completed = meter.CreateCounter<long>("terrabase.actors.completed", description: "Actor callbacks completed, including cancellations and failures.");
        _actors = [.. Enumerable.Range(0, actorCount).Select(index => new Mailbox(index, maxItems, maxBytes, completed))];
    }

    public int ActorCount => _actors.Length;

    public int GetActor(ActorKey key)
    {
        ArgumentNullException.ThrowIfNull(key.Database);
        // A bounded stack buffer avoids allocating encoded identities on this routing path. Length prefixes make
        // field boundaries unambiguous, including Unicode and identities containing separator characters.
        Span<byte> identity = stackalloc byte[MaxKeyBytes];
        var used = 0;
        Append(key.Database.OrganizationId, identity, ref used);
        Append(key.Database.ProjectId, identity, ref used);
        Append(key.Database.DatabaseId, identity, ref used);
        Append(key.Collection, identity, ref used);
        Append(key.Id, identity, ref used);
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(identity[..used], hash);
        return (int)(BinaryPrimitives.ReadUInt64LittleEndian(hash) % (ulong)ActorCount);
    }

    private static void Append(string? value, Span<byte> identity, ref int used)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);
        // Check characters first so oversized caller input is refused before traversing or encoding it.
        if (value.Length > MaxKeyBytes - used - sizeof(int))
        {
            throw new ArgumentException("an actor key exceeds 4096 encoded bytes", nameof(value));
        }

        var length = Utf8.GetByteCount(value);
        if (length > MaxKeyBytes - used - sizeof(int))
        {
            throw new ArgumentException("an actor key exceeds 4096 encoded bytes", nameof(value));
        }

        BinaryPrimitives.WriteInt32LittleEndian(identity[used..], length);
        used += sizeof(int);
        used += Utf8.GetBytes(value, identity[used..]);
    }

    public async ValueTask<T> InvokeAsync<T>(int actor, int retainedBytes, Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(actor);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(actor, ActorCount);
        ArgumentOutOfRangeException.ThrowIfNegative(retainedBytes);
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
        var mailbox = _actors[actor];
        if (!mailbox.Reserve(retainedBytes))
        {
            _rejected.Add(1, new KeyValuePair<string, object?>("actor", actor));
            throw new StorageBusyException();
        }

        Work<T> item;
        try
        {
            item = new Work<T>(retainedBytes, work, cancellationToken);
            ObjectDisposedException.ThrowIf(!mailbox.Queue.Writer.TryWrite(item), this);
        }
        catch
        {
            mailbox.Release(retainedBytes);
            throw;
        }

        return await item.Completion.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _closed, 1);
        foreach (var actor in _actors)
        {
            actor.Queue.Writer.TryComplete();
        }

        foreach (var actor in _actors)
        {
            await actor.Consumer.ConfigureAwait(false);
        }
    }

    private sealed class Mailbox
    {
        // One CAS reserves both budgets: the high 32 bits count items, the low 32 bits count bytes.
        // Each actor has its own counter. Releasing before completing the caller makes capacity immediately reusable.
        private const long Item = 1L << 32;
        private readonly int _maxItems;
        private readonly int _maxBytes;
        private readonly int _index;
        private readonly Counter<long> _completed;
        private long _reserved;

        internal Mailbox(int index, int maxItems, int maxBytes, Counter<long> completed)
        {
            _index = index;
            _maxItems = maxItems;
            _maxBytes = maxBytes;
            _completed = completed;
            Queue = Channel.CreateBounded<IWork>(new BoundedChannelOptions(maxItems)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false,
            });
            // A router resolved by a request must not retain that request's ambient tenant or HttpContext.
            if (ExecutionContext.IsFlowSuppressed())
            {
                Consumer = Task.Run(ConsumeAsync);
            }
            else
            {
                using var flow = ExecutionContext.SuppressFlow();
                Consumer = Task.Run(ConsumeAsync);
            }
        }

        internal Channel<IWork> Queue { get; }

        internal Task Consumer { get; }

        internal bool Reserve(int bytes)
        {
            var current = Volatile.Read(ref _reserved);
            while ((current >> 32) < _maxItems && bytes <= _maxBytes - (uint)current)
            {
                var observed = Interlocked.CompareExchange(ref _reserved, current + Item + bytes, current);
                if (observed == current)
                {
                    return true;
                }

                current = observed;
            }

            return false;
        }

        internal void Release(int bytes) => Interlocked.Add(ref _reserved, -Item - bytes);

        private async Task ConsumeAsync()
        {
            await foreach (var item in Queue.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await item.ExecuteAsync().ConfigureAwait(false);
                Release(item.Bytes);
                _completed.Add(1, new KeyValuePair<string, object?>("actor", _index));
                item.Complete();
            }
        }
    }

    private interface IWork
    {
        int Bytes { get; }

        ValueTask ExecuteAsync();

        void Complete();
    }

    private sealed class Work<T>(int bytes, Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken) : IWork
    {
        private T? _result;
        private Exception? _error;

        internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Bytes => bytes;

        public async ValueTask ExecuteAsync()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                _result = await work(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                _error = error;
            }
        }

        public void Complete()
        {
            if (_error is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                Completion.TrySetCanceled(cancellationToken);
            }
            else if (_error is { } error)
            {
                Completion.TrySetException(error);
            }
            else
            {
                Completion.TrySetResult(_result!);
            }
        }
    }
}
