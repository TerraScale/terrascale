using System.Collections.Concurrent;
using System.Net.Http;
using System.Net.Sockets;

namespace TerraBase.Transport;

/// <summary>Bounded retry settings for one inter-node RPC.</summary>
public sealed record InterNodeRetryPolicy
{
    public InterNodeRetryPolicy(
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeSpan? maxDelay = null,
        double backoffMultiplier = 2)
    {
        if (maxAttempts <= 0) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (backoffMultiplier < 1 || double.IsNaN(backoffMultiplier) || double.IsInfinity(backoffMultiplier))
            throw new ArgumentOutOfRangeException(nameof(backoffMultiplier));

        MaxAttempts = maxAttempts;
        InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(10);
        MaxDelay = maxDelay ?? TimeSpan.FromSeconds(1);
        if (InitialDelay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(initialDelay));
        if (MaxDelay < InitialDelay) throw new ArgumentOutOfRangeException(nameof(maxDelay));
        BackoffMultiplier = backoffMultiplier;
    }

    /// <summary>The total number of calls including the first call.</summary>
    public int MaxAttempts { get; }

    /// <summary>The delay before the second attempt.</summary>
    public TimeSpan InitialDelay { get; }

    /// <summary>The largest delay between attempts.</summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>The multiplier applied after each failed attempt.</summary>
    public double BackoffMultiplier { get; }

    internal TimeSpan DelayBeforeAttempt(int attempt)
    {
        // attempt is one-based and names the failed attempt. Saturating arithmetic
        // keeps a very large policy from overflowing into a negative delay.
        var delay = InitialDelay.TotalMilliseconds * Math.Pow(BackoffMultiplier, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(MaxDelay.TotalMilliseconds, Math.Max(0, delay)));
    }
}

/// <summary>Retry execution shared by MagicOnion adapters and test transports.</summary>
public static class InterNodeRetry
{
    /// <summary>
    /// Executes a call with one deadline budget. A mutation is retried only when
    /// its context carries an idempotency key; reads may opt in through
    /// <see cref="InterNodeRequestContext.IsIdempotent"/>.
    /// </summary>
    public static async ValueTask<T> ExecuteAsync<T>(
        InterNodeRequestContext context,
        InterNodeRetryPolicy policy,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(operation);

        using var deadlineCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var deadline = context.DeadlineUtc;
        if (deadline is { } deadlineUtc)
        {
            var remaining = deadlineUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
                throw DeadlineExceeded(context);
            deadlineCancellation.CancelAfter(remaining);
        }

        var maxAttempts = context.IsRetrySafe ? policy.MaxAttempts : 1;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(deadlineCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                !cancellationToken.IsCancellationRequested &&
                deadlineCancellation.IsCancellationRequested &&
                context.DeadlineUtc is { } expired && expired <= DateTimeOffset.UtcNow)
            {
                throw DeadlineExceeded(context);
            }
            catch (Exception exception) when (
                attempt < maxAttempts &&
                IsRetryable(exception))
            {
                try
                {
                    await Task.Delay(policy.DelayBeforeAttempt(attempt), deadlineCancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested &&
                    context.DeadlineUtc is { } expired && expired <= DateTimeOffset.UtcNow)
                {
                    throw DeadlineExceeded(context);
                }
            }
        }
    }

    /// <summary>Returns whether a failure is safe to retry at the transport seam.</summary>
    public static bool IsRetryable(Exception exception) => exception switch
    {
        InterNodeTransportException transport => transport.IsTransient,
        TimeoutException => true,
        HttpRequestException => true,
        SocketException => true,
        IOException => true,
        OperationCanceledException => false,
        _ => false,
    };

    private static InterNodeTransportException DeadlineExceeded(InterNodeRequestContext context) =>
        new(
            InterNodeTransportErrorCode.DeadlineExceeded,
            $"The deadline for {context.Service}/{context.Method} elapsed.");
}

/// <summary>Retrying adapter around a strongly typed inter-node transport.</summary>
public sealed class RetryingInterNodeTransport : IInterNodeTransport, IMagicOnionInterNodeTransport
{
    private readonly IInterNodeTransport inner;
    private readonly InterNodeRetryPolicy policy;

    public RetryingInterNodeTransport(IInterNodeTransport inner, InterNodeRetryPolicy? policy = null)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.policy = policy ?? new InterNodeRetryPolicy();
    }

    /// <inheritdoc />
    public ValueTask<InterNodeResponse<TResponse>> InvokeAsync<TRequest, TResponse>(
        InterNodeRequestContext context,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        InterNodeRetry.ExecuteAsync(
            context,
            policy,
            token => inner.InvokeAsync<TRequest, TResponse>(context, request, token),
            cancellationToken);

    /// <inheritdoc />
    public IAsyncEnumerable<InterNodeStreamItem<TResponse>> StreamAsync<TRequest, TResponse>(
        InterNodeRequestContext context,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        inner.StreamAsync<TRequest, TResponse>(context, request, cancellationToken);
}

/// <summary>A completed result returned by an idempotent execution.</summary>
public sealed record IdempotencyResult<T>(T Value, bool Replayed);

/// <summary>
/// Process-local idempotency coordinator. It coalesces concurrent duplicates,
/// rejects digest mismatches, and retains successful results in a bounded map.
/// A durable member adapter can implement the same contract around its WAL.
/// </summary>
public interface IIdempotencyStore
{
    ValueTask<IdempotencyResult<T>> ExecuteAsync<T>(
        string idempotencyKey,
        string requestDigest,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default);
}

/// <summary>In-memory implementation suitable for one member process or tests.</summary>
public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private sealed class Entry
    {
        public Entry(string digest)
        {
            Digest = digest;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public string Digest { get; }
        public TaskCompletionSource<object?> Completion { get; }
        public DateTimeOffset CompletedAt { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly int maximumEntries;
    private readonly TimeSpan retention;

    public InMemoryIdempotencyStore(int maximumEntries = 4_096, TimeSpan? retention = null)
    {
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.maximumEntries = maximumEntries;
        this.retention = retention ?? TimeSpan.FromHours(24);
        if (this.retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    /// <inheritdoc />
    public async ValueTask<IdempotencyResult<T>> ExecuteAsync<T>(
        string idempotencyKey,
        string requestDigest,
        Func<CancellationToken, ValueTask<T>> operation,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ArgumentException("An idempotency key is required.", nameof(idempotencyKey));
        if (string.IsNullOrWhiteSpace(requestDigest)) throw new ArgumentException("A request digest is required.", nameof(requestDigest));
        ArgumentNullException.ThrowIfNull(operation);
        Prune();

        var candidate = new Entry(requestDigest);
        var entry = entries.GetOrAdd(idempotencyKey, candidate);
        if (!ReferenceEquals(candidate, entry))
        {
            if (!string.Equals(entry.Digest, requestDigest, StringComparison.Ordinal))
                throw new IdempotencyConflictException(idempotencyKey);

            var replayed = await entry.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (replayed is not T typed)
                throw new InterNodeTransportException(
                    InterNodeTransportErrorCode.ProtocolViolation,
                    "An idempotency key was reused across incompatible response types.");
            return new IdempotencyResult<T>(typed, true);
        }

        try
        {
            var value = await operation(cancellationToken).ConfigureAwait(false);
            candidate.CompletedAt = DateTimeOffset.UtcNow;
            candidate.Completion.TrySetResult(value);
            EnforceBound();
            return new IdempotencyResult<T>(value, false);
        }
        catch (Exception exception)
        {
            // A failed call has no terminal outcome. Remove only our reservation;
            // a concurrent caller that already observed it receives the same
            // failure, while a later retry can discover the remote commit.
            entries.TryRemove(new KeyValuePair<string, Entry>(idempotencyKey, candidate));
            candidate.Completion.TrySetException(exception);
            throw;
        }
    }

    private void Prune()
    {
        var cutoff = DateTimeOffset.UtcNow - retention;
        foreach (var pair in entries)
            if (pair.Value.CompletedAt is { } completedAt && completedAt < cutoff)
                entries.TryRemove(new KeyValuePair<string, Entry>(pair.Key, pair.Value));
        EnforceBound();
    }

    private void EnforceBound()
    {
        while (entries.Count > maximumEntries)
        {
            var oldest = entries
                .Where(pair => pair.Value.CompletedAt != default)
                .OrderBy(pair => pair.Value.CompletedAt)
                .FirstOrDefault();
            if (oldest.Key is null || !entries.TryRemove(new KeyValuePair<string, Entry>(oldest.Key, oldest.Value)))
                break;
        }
    }
}

/// <summary>Idempotency adapter for unary inter-node calls.</summary>
public sealed class IdempotentInterNodeTransport : IInterNodeTransport, IMagicOnionInterNodeTransport
{
    private readonly IInterNodeTransport inner;
    private readonly IIdempotencyStore store;

    public IdempotentInterNodeTransport(IInterNodeTransport inner, IIdempotencyStore store)
    {
        this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    /// <inheritdoc />
    public async ValueTask<InterNodeResponse<TResponse>> InvokeAsync<TRequest, TResponse>(
        InterNodeRequestContext context,
        TRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.IdempotencyKey is null)
            return await inner.InvokeAsync<TRequest, TResponse>(context, request, cancellationToken).ConfigureAwait(false);

        var result = await store.ExecuteAsync(
            context.IdempotencyKey,
            context.RequestDigest!,
            token => inner.InvokeAsync<TRequest, TResponse>(context, request, token),
            cancellationToken).ConfigureAwait(false);
        return result.Replayed ? result.Value.AsReplay() : result.Value;
    }

    /// <inheritdoc />
    public IAsyncEnumerable<InterNodeStreamItem<TResponse>> StreamAsync<TRequest, TResponse>(
        InterNodeRequestContext context,
        TRequest request,
        CancellationToken cancellationToken = default) =>
        inner.StreamAsync<TRequest, TResponse>(context, request, cancellationToken);
}

