namespace TerraBase.Engine.Actors;

/// <summary>
/// Routes keys to logical actors and serializes callbacks within each actor. This preparatory transport does not
/// replace storage, implement replication, or pin threads to cores. Callbacks explicitly carry their dependencies;
/// request ExecutionContext is not propagated. Callbacks must not wait for work on their own actor.
/// </summary>
public interface IActorRouter : IAsyncDisposable
{
    /// <summary>The fixed number of actors for this router's lifetime.</summary>
    int ActorCount { get; }

    /// <summary>
    /// Selects an actor using SHA-256 of length-prefixed UTF-8 identity fields (organization, project, database,
    /// collection, id), modulo <see cref="ActorCount"/>. Invalid UTF-16 and identities exceeding 4096 encoded bytes
    /// including framing are refused. Changing the actor count requires rebuilding placement, not an in-place resize.
    /// </summary>
    int GetActor(ActorKey key);

    /// <summary>
    /// Admits a callback to one actor, or throws <see cref="StorageBusyException"/> immediately when its item or byte
    /// budget is full. Both budgets include executing work. <paramref name="retainedBytes"/> is the nonnegative byte
    /// size of captured payloads; callers reserve payload memory before allocating it and retain ownership until this
    /// completes. Fixed mailbox bookkeeping is bounded by the item budget. Cancellation is checked before execution
    /// and passed to the callback; queued cancelled work retains admission until consumed. Callback errors are isolated.
    /// Disposal refuses new work and drains every accepted callback before returning.
    /// </summary>
    /// <typeparam name="T">The callback's result.</typeparam>
    /// <param name="actor">The actor index, from zero to <see cref="ActorCount"/> minus one.</param>
    /// <param name="retainedBytes">Bytes retained by this callback's payload.</param>
    /// <param name="work">The actor-owned operation.</param>
    /// <param name="cancellationToken">Cancellation for this operation.</param>
    ValueTask<T> InvokeAsync<T>(int actor, int retainedBytes, Func<CancellationToken, ValueTask<T>> work, CancellationToken cancellationToken);
}
