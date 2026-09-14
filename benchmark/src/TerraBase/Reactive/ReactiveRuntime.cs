using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace TerraBase.Reactive;

/// <summary>Materialized value and dependencies produced by one query run.</summary>
public sealed record ReactiveMaterialization<TResult>(TResult Result, ReactiveDependencies Dependencies);

/// <summary>Runs typed queries against an optional document source.</summary>
public sealed class ReactiveQueryExecutor
{
    private readonly IDocumentStore? documents;

    /// <summary>Creates an executor.</summary>
    public ReactiveQueryExecutor(IDocumentStore? documents = null) => this.documents = documents;

    /// <summary>Executes a typed query under one authorization scope.</summary>
    public async ValueTask<TResult> ExecuteAsync<TParameters, TResult>(
        ReactiveQuery<TParameters, TResult> query,
        TParameters parameters,
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(authorization);
        cancellationToken.ThrowIfCancellationRequested();
        var context = new ReactiveQueryContext(authorization, documents);
        if (query.DependencySelector is not null)
        {
            context.AddDependencies(query.DependencySelector(parameters));
        }

        return await query.ExecuteAsync(parameters, context, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Options for one subscription request.</summary>
public sealed record ReactiveSubscriptionOptions
{
    /// <summary>Per-subscriber queue and retention policy.</summary>
    public ReactiveOptions Delivery { get; init; } = new();

    /// <summary>Opaque checkpoint received from a previous connection.</summary>
    public string? ResumeFrom { get; init; }
}

/// <summary>Thrown when a bounded subscriber cannot accept another frame.</summary>
public sealed class ReactiveBackpressureException : Exception
{
    /// <summary>Creates a typed backpressure failure.</summary>
    public ReactiveBackpressureException(long droppedThrough)
        : base($"The reactive subscriber fell behind through sequence {droppedThrough}.") => DroppedThrough = droppedThrough;

    /// <summary>The last sequence that was discarded.</summary>
    public long DroppedThrough { get; }
}

/// <summary>Thrown when a resume checkpoint no longer has retained history.</summary>
public sealed class ReactiveCheckpointExpiredException : Exception
{
    /// <summary>Creates a checkpoint-expiry failure.</summary>
    public ReactiveCheckpointExpiredException()
        : base("The reactive checkpoint is outside the retained history.")
    {
    }
}

/// <summary>A live, bounded stream of typed result frames.</summary>
public sealed class ReactiveSubscription<TResult> : IAsyncEnumerable<ReactiveFrame<TResult>>, IAsyncDisposable
{
    private readonly ChannelReader<ReactiveFrame<TResult>> reader;
    private readonly Func<ValueTask> release;
    private int disposed;

    internal ReactiveSubscription(ChannelReader<ReactiveFrame<TResult>> reader, Func<ValueTask> release)
    {
        this.reader = reader;
        this.release = release;
    }

    /// <summary>Reads frames until the subscription is disposed or revoked.</summary>
    public IAsyncEnumerator<ReactiveFrame<TResult>> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

    /// <summary>Reads one frame without allocating an enumerator.</summary>
    public ValueTask<ReactiveFrame<TResult>> ReadAsync(CancellationToken cancellationToken = default) => reader.ReadAsync(cancellationToken);

    /// <summary>Releases the shared subscription lease.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            await release().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// Shares equivalent typed queries, evaluates affected queries once per commit,
/// and gives each consumer a bounded resumable stream.
/// </summary>
public sealed class ReactiveSubscriptionHub : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, ISharedSubscription> shared = new(StringComparer.Ordinal);
    private readonly IReactiveAuthorizer authorizer;
    private readonly ReactiveOptions options;
    private long sequence;
    private int disposed;

    /// <summary>Creates a subscription hub.</summary>
    public ReactiveSubscriptionHub(
        IReactiveAuthorizer? authorizer = null,
        ReactiveOptions? options = null)
    {
        this.authorizer = authorizer ?? new AllowAuthenticatedReactiveAuthorizer();
        this.options = options ?? new ReactiveOptions();
        this.options.Validate();
    }

    /// <summary>The latest committed publication sequence.</summary>
    public long CurrentSequence => Interlocked.Read(ref sequence);

    /// <summary>Subscribes to a typed query and receives a consistent initial snapshot.</summary>
    public async ValueTask<ReactiveSubscription<TResult>> SubscribeAsync<TParameters, TResult>(
        ReactiveQuery<TParameters, TResult> query,
        TParameters parameters,
        ReactiveAuthorization authorization,
        ReactiveSubscriptionOptions? subscriptionOptions = null,
        IDocumentStore? documents = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(authorization);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        var decision = await authorizer.AuthorizeQueryAsync(query.Id, authorization, cancellationToken).ConfigureAwait(false);
        if (!decision.Allowed)
        {
            throw new ReactiveAuthorizationException(decision.Reason ?? "The query is not authorized.");
        }

        var key = SubscriptionKey<TParameters, TResult>(query, parameters, authorization);
        ISharedSubscription sharedSubscription;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (!shared.TryGetValue(key, out sharedSubscription!))
            {
                sharedSubscription = new SharedSubscription<TParameters, TResult>(
                    this,
                    key,
                    query,
                    parameters,
                    authorization,
                    documents,
                    options,
                    authorizer);
                shared.Add(key, sharedSubscription);
            }
        }

        try
        {
            return await ((SharedSubscription<TParameters, TResult>)sharedSubscription)
                .SubscribeAsync(subscriptionOptions, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            RemoveIfUnused(key, sharedSubscription);
            throw;
        }
    }

    /// <summary>Publishes one committed change set to affected shared queries.</summary>
    public async ValueTask PublishAsync(ReactiveChangeSet changeSet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changeSet);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
        {
            if (changeSet.Sequence <= sequence)
            {
                throw new ArgumentException("Publication sequences must be strictly increasing.", nameof(changeSet));
            }

            sequence = changeSet.Sequence;
        }

        ISharedSubscription[] subscriptions;
        lock (gate)
        {
            subscriptions = shared.Values.ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await subscription.PublishAsync(changeSet, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Publishes a local change and assigns the next sequence.</summary>
    public ValueTask PublishAsync(IEnumerable<ReactiveChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        var next = checked(Interlocked.Read(ref sequence) + 1);
        return PublishAsync(new ReactiveChangeSet(next, changes), cancellationToken);
    }

    /// <summary>Rechecks subscriptions for an authorization generation change.</summary>
    public async ValueTask RefreshAuthorizationAsync(
        ReactiveAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ISharedSubscription[] subscriptions;
        lock (gate)
        {
            subscriptions = shared.Values
                .Where(subscription => subscription.MatchesTenant(authorization.Tenant))
                .ToArray();
        }

        foreach (var subscription in subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await subscription.RefreshAuthorizationAsync(authorization, cancellationToken).ConfigureAwait(false);
        }
    }

    internal void RemoveIfUnused(string key, ISharedSubscription expected)
    {
        lock (gate)
        {
            if (shared.TryGetValue(key, out var current) && ReferenceEquals(current, expected) && current.IsUnused)
            {
                shared.Remove(key);
            }
        }
    }

    /// <summary>Releases all streams and retained result histories.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        ISharedSubscription[] subscriptions;
        lock (gate)
        {
            subscriptions = shared.Values.ToArray();
            shared.Clear();
        }

        foreach (var subscription in subscriptions)
        {
            await subscription.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static string SubscriptionKey<TParameters, TResult>(
        ReactiveQuery<TParameters, TResult> query,
        TParameters parameters,
        ReactiveAuthorization authorization)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(parameters);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return $"{query.Id.Value}\u001f{typeof(TParameters).AssemblyQualifiedName}\u001f{typeof(TResult).AssemblyQualifiedName}\u001f{digest}\u001f{authorization.ScopeKey()}";
    }

    private interface ISharedSubscription
    {
        bool IsUnused { get; }

        bool MatchesTenant(TenantId tenant);

        ValueTask PublishAsync(ReactiveChangeSet changeSet, CancellationToken cancellationToken);

        ValueTask RefreshAuthorizationAsync(ReactiveAuthorization authorization, CancellationToken cancellationToken);

        ValueTask DisposeAsync();
    }

    private sealed class SharedSubscription<TParameters, TResult> : ISharedSubscription
    {
        private readonly ReactiveSubscriptionHub hub;
        private readonly string key;
        private readonly ReactiveQuery<TParameters, TResult> query;
        private readonly TParameters parameters;
        private readonly ReactiveOptions options;
        private readonly IReactiveAuthorizer authorizer;
        private readonly IDocumentStore? documents;
        private readonly ReactiveAuthorization authorization;
        private readonly object subscriptionGate = new();
        private readonly Dictionary<long, ReactiveFrame<TResult>> history = new();
        private readonly Dictionary<long, Subscriber> subscribers = new();
        private readonly TaskCompletionSource<bool> initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ReactiveDependencies dependencies = new();
        private TResult? result;
        private string fingerprint = string.Empty;
        private long revision;
        private long subscriberId;
        private int disposed;

        public SharedSubscription(
            ReactiveSubscriptionHub hub,
            string key,
            ReactiveQuery<TParameters, TResult> query,
            TParameters parameters,
            ReactiveAuthorization authorization,
            IDocumentStore? documents,
            ReactiveOptions options,
            IReactiveAuthorizer authorizer)
        {
            this.hub = hub;
            this.key = key;
            this.query = query;
            this.parameters = parameters;
            this.authorization = authorization;
            this.documents = documents;
            this.options = options;
            this.authorizer = authorizer;
            _ = InitializeAsync();
        }

        public bool IsUnused
        {
            get
            {
                lock (subscriptionGate)
                {
                    return subscribers.Count == 0;
                }
            }
        }

        public bool MatchesTenant(TenantId tenant) => authorization.Tenant == tenant;

        public async ValueTask<ReactiveSubscription<TResult>> SubscribeAsync(
            ReactiveSubscriptionOptions? subscriptionOptions,
            CancellationToken cancellationToken)
        {
            await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var delivery = subscriptionOptions?.Delivery ?? options;
            delivery.Validate();
            var channel = Channel.CreateBounded<ReactiveFrame<TResult>>(new BoundedChannelOptions(delivery.SubscriberBufferSize)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            var subscriber = new Subscriber(channel, delivery.Backpressure);
            lock (subscriptionGate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                var id = ++subscriberId;
                subscribers.Add(id, subscriber);
                EnqueueResume(subscriber, subscriptionOptions?.ResumeFrom);
                return new ReactiveSubscription<TResult>(channel.Reader, () => ReleaseAsync(id));
            }
        }

        public async ValueTask PublishAsync(ReactiveChangeSet changeSet, CancellationToken cancellationToken)
        {
            await initialized.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (!dependencies.IsAffectedBy(changeSet.Changes))
            {
                return;
            }

            var decision = await authorizer.AuthorizeQueryAsync(query.Id, authorization, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                Revoke(decision.Reason ?? "Authorization changed.");
                return;
            }

            var context = new ReactiveQueryContext(authorization, documents);
            if (query.DependencySelector is not null)
            {
                context.AddDependencies(query.DependencySelector(parameters));
            }

            var next = await query.ExecuteAsync(parameters, context, cancellationToken).ConfigureAwait(false);
            var nextFingerprint = Fingerprint(next);
            lock (subscriptionGate)
            {
                if (changeSet.Sequence <= revision || string.Equals(nextFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    revision = Math.Max(revision, changeSet.Sequence);
                    dependencies = context.Dependencies;
                    return;
                }

                revision = changeSet.Sequence;
                result = next;
                fingerprint = nextFingerprint;
                dependencies = context.Dependencies;
                var frame = ReactiveFrame<TResult>.Delta(revision, Checkpoint(revision), next, changeSet.Changes);
                Retain(frame);
                foreach (var subscriber in subscribers.Values)
                {
                    subscriber.Enqueue(frame, options.Backpressure);
                }
            }
        }

        public async ValueTask RefreshAuthorizationAsync(ReactiveAuthorization refreshed, CancellationToken cancellationToken)
        {
            if (refreshed.Tenant != authorization.Tenant || refreshed.Subject != authorization.Subject)
            {
                return;
            }

            var decision = await authorizer.AuthorizeQueryAsync(query.Id, refreshed, cancellationToken).ConfigureAwait(false);
            if (!decision.Allowed || refreshed.Generation != authorization.Generation)
            {
                Revoke(decision.Reason ?? "Authorization generation changed.");
            }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return ValueTask.CompletedTask;
            }

            lock (subscriptionGate)
            {
                foreach (var subscriber in subscribers.Values)
                {
                    subscriber.Complete();
                }

                subscribers.Clear();
                history.Clear();
            }

            return ValueTask.CompletedTask;
        }

        private async Task InitializeAsync()
        {
            try
            {
                var context = new ReactiveQueryContext(authorization, documents);
                if (query.DependencySelector is not null)
                {
                    context.AddDependencies(query.DependencySelector(parameters));
                }

                var value = await query.ExecuteAsync(parameters, context, CancellationToken.None).ConfigureAwait(false);
                lock (subscriptionGate)
                {
                    result = value;
                    fingerprint = Fingerprint(value);
                    dependencies = context.Dependencies;
                    revision = hub.CurrentSequence;
                    var frame = ReactiveFrame<TResult>.Snapshot(revision, Checkpoint(revision), value);
                    Retain(frame);
                }

                initialized.SetResult(true);
            }
            catch (Exception exception)
            {
                initialized.SetException(exception);
                hub.RemoveIfUnused(key, this);
            }
        }

        private void EnqueueResume(Subscriber subscriber, string? encodedCheckpoint)
        {
            if (result is null && typeof(TResult).IsValueType)
            {
                // A value type result can legitimately be the default value; the
                // initialization task is still the source of truth for readiness.
            }

            if (string.IsNullOrEmpty(encodedCheckpoint) || !ReactiveCheckpoint.TryDecode(encodedCheckpoint, key, out var checkpoint))
            {
                subscriber.Enqueue(ReactiveFrame<TResult>.Snapshot(revision, Checkpoint(revision), result!), options.Backpressure);
                return;
            }

            lock (subscriptionGate)
            {
                var oldest = history.Count == 0 ? revision : history.Keys.Min();
                if (checkpoint.Sequence > revision || checkpoint.Sequence < oldest - 1)
                {
                    subscriber.Enqueue(new ReactiveFrame<TResult>(
                        ReactiveFrameKind.Reset,
                        revision,
                        Checkpoint(revision),
                        result,
                        Array.Empty<ReactiveChange>(),
                        Reason: nameof(ReactiveCheckpointExpiredException)), options.Backpressure);
                    return;
                }

                foreach (var frame in history.Values.Where(frame => frame.Sequence > checkpoint.Sequence).OrderBy(frame => frame.Sequence))
                {
                    subscriber.Enqueue(frame, options.Backpressure);
                }
            }
        }

        private ValueTask ReleaseAsync(long id)
        {
            lock (subscriptionGate)
            {
                if (subscribers.Remove(id, out var subscriber))
                {
                    subscriber.Complete();
                }
            }

            hub.RemoveIfUnused(key, this);
            return ValueTask.CompletedTask;
        }

        private void Revoke(string reason)
        {
            lock (subscriptionGate)
            {
                var frame = new ReactiveFrame<TResult>(
                    ReactiveFrameKind.AuthorizationRevoked,
                    revision,
                    Checkpoint(revision),
                    default,
                    Array.Empty<ReactiveChange>(),
                    Reason: reason);
                foreach (var subscriber in subscribers.Values)
                {
                    subscriber.Enqueue(frame, ReactiveBackpressureMode.Disconnect);
                    subscriber.Complete();
                }

                subscribers.Clear();
            }

            hub.RemoveIfUnused(key, this);
        }

        private void Retain(ReactiveFrame<TResult> frame)
        {
            history[frame.Sequence] = frame;
            while (history.Count > options.RetainedRevisions)
            {
                history.Remove(history.Keys.Min());
            }
        }

        private string Checkpoint(long sequence) => new ReactiveCheckpoint(key, sequence).Encode();

        private static string Fingerprint(TResult value)
        {
            try
            {
                return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));
            }
            catch (NotSupportedException)
            {
                return value?.GetHashCode().ToString() ?? "null";
            }
        }

        private sealed class Subscriber
        {
            private readonly Channel<ReactiveFrame<TResult>> channel;
            private readonly ReactiveBackpressureMode mode;
            private long droppedThrough;

            public Subscriber(Channel<ReactiveFrame<TResult>> channel, ReactiveBackpressureMode mode)
            {
                this.channel = channel;
                this.mode = mode;
            }

            public void Enqueue(ReactiveFrame<TResult> frame, ReactiveBackpressureMode fallback)
            {
                if (channel.Writer.TryWrite(frame))
                {
                    return;
                }

                var behavior = mode;
                if (behavior == ReactiveBackpressureMode.Disconnect || fallback == ReactiveBackpressureMode.Disconnect)
                {
                    channel.Writer.TryComplete(new ReactiveBackpressureException(frame.Sequence));
                    return;
                }

                while (channel.Reader.TryRead(out var removed))
                {
                    droppedThrough = Math.Max(droppedThrough, removed.Sequence);
                }

                var reset = frame with
                {
                    Kind = ReactiveFrameKind.Backpressure,
                    DroppedThrough = droppedThrough,
                    Reason = "subscriber_buffer_full",
                };
                channel.Writer.TryWrite(reset);
            }

            public void Complete(Exception? error = null) => channel.Writer.TryComplete(error);
        }
    }
}

/// <summary>Typed authorization failure for reactive attach.</summary>
public sealed class ReactiveAuthorizationException : UnauthorizedAccessException
{
    /// <summary>Creates a typed authorization failure.</summary>
    public ReactiveAuthorizationException(string message)
        : base(message)
    {
    }
}

internal static class ReactiveQueryContextExtensions
{
    internal static void AddDependencies(this ReactiveQueryContext context, ReactiveDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        foreach (var dependency in dependencies)
        {
            context.AddDependency(dependency);
        }
    }

    private static void AddDependency(this ReactiveQueryContext context, ReactiveDependency dependency) =>
        context.SetDependencies(context.Dependencies.Add(dependency));

    private static void SetDependencies(this ReactiveQueryContext context, ReactiveDependencies dependencies) =>
        context.ReplaceDependencies(dependencies);
}
