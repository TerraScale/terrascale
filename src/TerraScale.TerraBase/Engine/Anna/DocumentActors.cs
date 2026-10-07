using System.Collections.Concurrent;
using TerraBase.Configuration;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;

namespace TerraBase.Engine.Anna;

/// <summary>Addresses one document in the actors' memory: its database's version scope, its collection and its id.</summary>
/// <param name="Scope">The database's version scope.</param>
/// <param name="Collection">The collection.</param>
/// <param name="Id">The document id.</param>
internal readonly record struct DocumentAddress(Guid Scope, string Collection, string Id);

/// <summary>One written version of a document: its JSON, or a deletion with empty JSON.</summary>
/// <param name="Json">The document.</param>
/// <param name="Deleted">Whether the version deletes the document.</param>
internal readonly record struct DocumentValue(ReadOnlyMemory<byte> Json, bool Deleted);

/// <summary>What a step on a document leaves: the register to keep (<see langword="null"/> keeps the current one) and its answer.</summary>
/// <typeparam name="T">The answer.</typeparam>
/// <param name="Next">The document's new register, or <see langword="null"/>.</param>
/// <param name="Result">The answer.</param>
internal readonly record struct ActorStep<T>(MvRegister<DocumentValue>? Next, T Result);

/// <summary>
/// Per-core actors that own documents in memory (docs/design/anna-core.md section 3). Each document belongs to one
/// actor, which runs every step on it in order on its own thread, so no lock guards document state.
/// </summary>
internal interface IDocumentActors
{
    /// <summary>
    /// Runs <paramref name="step"/> on the actor that owns <paramref name="address"/>, with the document's register and the
    /// actor's replica id. <paramref name="bytes"/> counts against the actor's inbox. A step whose
    /// <paramref name="cancellationToken"/> is cancelled before it runs does not run; once it runs, it completes.
    /// </summary>
    /// <exception cref="StorageBusyException">The actor's inbox or the memory budget is full.</exception>
    Task<T> RunAsync<T>(
        DocumentAddress address, int bytes, Func<MvRegister<DocumentValue>, ulong, ActorStep<T>> step, CancellationToken cancellationToken);

    /// <summary>Forgets every document of <paramref name="collection"/> in <paramref name="scope"/>, on every actor.</summary>
    Task PurgeAsync(Guid scope, string collection, CancellationToken cancellationToken);
}

internal sealed class DocumentActors : IDocumentActors, IDisposable
{
    // ponytail: fixed inbox bounds; settings once a TS-606 run asks for them.
    private const int InboxCount = 1024;
    private const long InboxBytes = 64L << 20;

    // Bytes charged per stored document on top of its JSON: address, dot, context and map entry.
    private const int EntryOverhead = 256;

    private static readonly MvRegister<DocumentValue> Absent = new();

    private readonly Actor[] _actors;

    public DocumentActors(TerraBaseOptions options, IMemoryBudget memory)
    {
        ArgumentNullException.ThrowIfNull(options);
        _actors = [.. Enumerable.Range(0, options.Actors).Select(index => new Actor((ulong)index + 1, memory))];
    }

    public Task<T> RunAsync<T>(
        DocumentAddress address, int bytes, Func<MvRegister<DocumentValue>, ulong, ActorStep<T>> step, CancellationToken cancellationToken)
    {
        // The local ring (design section 5): a process-stable hash, since actors hold no state across restarts yet.
        var message = new Step<T>(address, step, cancellationToken);
        _actors[(uint)address.GetHashCode() % (uint)_actors.Length].Post(message, bytes);
        return message.Task;
    }

    public Task PurgeAsync(Guid scope, string collection, CancellationToken cancellationToken) =>
        Task.WhenAll(_actors.Select(actor =>
        {
            var message = new PurgeCollection(scope, collection, cancellationToken);
            actor.Post(message, 0);
            return message.Task;
        }));

    public void Dispose()
    {
        foreach (var actor in _actors)
        {
            actor.Dispose();
        }
    }

    // A request to an actor and its answer. A message cancelled before it runs does not run.
    private abstract class Message<T>(CancellationToken cancellationToken) : Message
    {
        private readonly TaskCompletionSource<T> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _done.Task;

        public sealed override void Run(Actor actor)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _done.TrySetCanceled(cancellationToken);
                return;
            }

            try
            {
                _done.TrySetResult(Answer(actor));
            }
            catch (Exception error)
            {
                // A failing request fails its caller, never the actor's thread.
                _done.TrySetException(error);
            }
        }

        protected abstract T Answer(Actor actor);
    }

    private abstract class Message
    {
        public int Bytes { get; set; }

        public abstract void Run(Actor actor);
    }

    private sealed class Step<T>(DocumentAddress address, Func<MvRegister<DocumentValue>, ulong, ActorStep<T>> step, CancellationToken cancellationToken)
        : Message<T>(cancellationToken)
    {
        protected override T Answer(Actor actor) => actor.Apply(address, step);
    }

    private sealed class PurgeCollection(Guid scope, string collection, CancellationToken cancellationToken) : Message<bool>(cancellationToken)
    {
        protected override bool Answer(Actor actor)
        {
            actor.Purge(scope, collection);
            return true;
        }
    }

    private sealed record Entry(MvRegister<DocumentValue> Register, IMemoryReservation Reservation);

    private sealed class Actor : IDisposable
    {
        private readonly BlockingCollection<Message> _inbox = new(new ConcurrentQueue<Message>(), InboxCount);
        private readonly Dictionary<DocumentAddress, Entry> _documents = [];
        private readonly IMemoryBudget _memory;
        private readonly ulong _replica;
        private readonly Thread _thread;
        private long _queuedBytes;

        public Actor(ulong replica, IMemoryBudget memory)
        {
            _replica = replica;
            _memory = memory;
            _thread = new Thread(Loop) { IsBackground = true, Name = $"terrabase-actor-{replica - 1}" };
            _thread.Start();
        }

        public void Post(Message message, int bytes)
        {
            message.Bytes = bytes;
            if (Interlocked.Add(ref _queuedBytes, bytes) > InboxBytes || !_inbox.TryAdd(message))
            {
                Interlocked.Add(ref _queuedBytes, -bytes);
                throw new StorageBusyException();
            }
        }

        public T Apply<T>(DocumentAddress address, Func<MvRegister<DocumentValue>, ulong, ActorStep<T>> step)
        {
            var entry = _documents.GetValueOrDefault(address);
            var (next, result) = step(entry?.Register ?? Absent, _replica);
            if (next is not null)
            {
                var bytes = EntryOverhead + next.Values.Values.Sum(value => (long)value.Json.Length);
                var reservation = _memory.TryReserve(MemoryLane.Foreground, bytes) ?? throw new StorageBusyException();
                entry?.Reservation.Dispose();
                _documents[address] = new Entry(next, reservation);
            }

            return result;
        }

        public void Purge(Guid scope, string collection)
        {
            foreach (var address in _documents.Keys.Where(address => address.Scope == scope && address.Collection == collection).ToList())
            {
                _documents.Remove(address, out var entry);
                entry!.Reservation.Dispose();
            }
        }

        public void Dispose()
        {
            _inbox.CompleteAdding();
            _thread.Join();
            foreach (var entry in _documents.Values)
            {
                entry.Reservation.Dispose();
            }

            _documents.Clear();
            _inbox.Dispose();
        }

        private void Loop()
        {
            foreach (var message in _inbox.GetConsumingEnumerable())
            {
                message.Run(this);
                Interlocked.Add(ref _queuedBytes, -message.Bytes);
            }
        }
    }
}
