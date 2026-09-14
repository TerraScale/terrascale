using System.Collections.ObjectModel;
using TerraBase.Replication;

namespace TerraBase.Transport;

/// <summary>Stable failure categories exposed by the private member transport.</summary>
public enum InterNodeTransportErrorCode
{
    Unavailable,
    DeadlineExceeded,
    Cancelled,
    QueueFull,
    ProtocolViolation,
    AuthenticationFailed,
    IdempotencyConflict,
    ResumeUnavailable,
    InvalidRequest,
}

/// <summary>An error returned by an inter-node operation.</summary>
public class InterNodeTransportException : Exception
{
    public InterNodeTransportException(
        InterNodeTransportErrorCode code,
        string message,
        bool isTransient = false,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        IsTransient = isTransient;
    }

    /// <summary>The stable error category.</summary>
    public InterNodeTransportErrorCode Code { get; }

    /// <summary>Whether retrying the same idempotent request may make progress.</summary>
    public bool IsTransient { get; }
}

/// <summary>Raised when an idempotency key is reused for another request.</summary>
public sealed class IdempotencyConflictException : InterNodeTransportException
{
    public IdempotencyConflictException(string idempotencyKey)
        : base(
            InterNodeTransportErrorCode.IdempotencyConflict,
            $"Idempotency key '{idempotencyKey}' was already used with another request digest.")
    {
    }
}

/// <summary>Raised when a stream's requested prefix is no longer retained.</summary>
public sealed class ReplicationResumeException : InterNodeTransportException
{
    public ReplicationResumeException(string streamId, long firstAvailableSequence)
        : base(
            InterNodeTransportErrorCode.ResumeUnavailable,
            $"Replication stream '{streamId}' no longer retains the requested sequence.")
    {
        FirstAvailableSequence = firstAvailableSequence;
    }

    /// <summary>The first sequence that can still be resumed.</summary>
    public long FirstAvailableSequence { get; }
}

/// <summary>
/// Metadata sent with one RPC. The request identity and digest are deliberately
/// separate: an idempotency key identifies a retry, while the digest binds it to
/// the exact request bytes chosen by the caller.
/// </summary>
public sealed record InterNodeRequestContext
{
    public InterNodeRequestContext(
        string service,
        string method,
        string? idempotencyKey = null,
        string? requestDigest = null,
        DateTimeOffset? deadlineUtc = null,
        bool isIdempotent = false)
    {
        if (string.IsNullOrWhiteSpace(service)) throw new ArgumentException("A service is required.", nameof(service));
        if (string.IsNullOrWhiteSpace(method)) throw new ArgumentException("A method is required.", nameof(method));
        if (idempotencyKey is not null && string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ArgumentException("An idempotency key must not be empty.", nameof(idempotencyKey));
        if (idempotencyKey is not null && string.IsNullOrWhiteSpace(requestDigest))
            throw new ArgumentException("An idempotent request requires a request digest.", nameof(requestDigest));
        if (deadlineUtc is { } deadline && deadline.Offset != TimeSpan.Zero)
            deadlineUtc = deadline.ToUniversalTime();

        Service = service;
        Method = method;
        IdempotencyKey = idempotencyKey;
        RequestDigest = requestDigest;
        DeadlineUtc = deadlineUtc;
        IsIdempotent = isIdempotent;
    }

    /// <summary>The MagicOnion service contract name.</summary>
    public string Service { get; }

    /// <summary>The MagicOnion method name.</summary>
    public string Method { get; }

    /// <summary>The stable identity used for duplicate delivery.</summary>
    public string? IdempotencyKey { get; }

    /// <summary>The canonical request digest bound to <see cref="IdempotencyKey"/>.</summary>
    public string? RequestDigest { get; }

    /// <summary>The absolute UTC deadline, when one was supplied.</summary>
    public DateTimeOffset? DeadlineUtc { get; }

    /// <summary>Whether this request can be retried without an idempotency key.</summary>
    public bool IsIdempotent { get; }

    /// <summary>Whether retrying this context is safe.</summary>
    public bool IsRetrySafe => IsIdempotent || IdempotencyKey is not null;

    /// <summary>Creates a context for a read-only, naturally idempotent method.</summary>
    public static InterNodeRequestContext Read(string service, string method, DateTimeOffset? deadlineUtc = null) =>
        new(service, method, deadlineUtc: deadlineUtc, isIdempotent: true);

    /// <summary>Creates a context for a mutation with an explicit retry identity.</summary>
    public static InterNodeRequestContext Mutation(
        string service,
        string method,
        string idempotencyKey,
        string requestDigest,
        DateTimeOffset? deadlineUtc = null) =>
        new(service, method, idempotencyKey, requestDigest, deadlineUtc);
}

/// <summary>One unary response and whether it came from an idempotency replay.</summary>
public sealed record InterNodeResponse<TResponse>(TResponse Value, bool Replayed = false, string? CommitId = null)
{
    internal InterNodeResponse<TResponse> AsReplay() => this with { Replayed = true };
}

/// <summary>One ordered item delivered by a streaming RPC.</summary>
public sealed record InterNodeStreamItem<TResponse>(long Sequence, TResponse Value, bool IsTerminal = false);

/// <summary>
/// Generic RPC boundary matching MagicOnion's strongly typed service model. The
/// repository intentionally keeps the interface free of a MagicOnion package so
/// implementations can bind it to the package when that dependency is adopted.
/// </summary>
public interface IInterNodeTransport
{
    ValueTask<InterNodeResponse<TResponse>> InvokeAsync<TRequest, TResponse>(
        InterNodeRequestContext context,
        TRequest request,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<InterNodeStreamItem<TResponse>> StreamAsync<TRequest, TResponse>(
        InterNodeRequestContext context,
        TRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Compatibility name for adapters that use MagicOnion terminology.</summary>
public interface IMagicOnionInterNodeTransport : IInterNodeTransport
{
}

/// <summary>A per-origin causal cursor used to resume replication.</summary>
public sealed record ReplicationCursor
{
    public ReplicationCursor(IReadOnlyDictionary<string, ulong> frontier)
    {
        ArgumentNullException.ThrowIfNull(frontier);
        if (frontier.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("A replication cursor contains an empty origin.", nameof(frontier));

        Frontier = new ReadOnlyDictionary<string, ulong>(
            new Dictionary<string, ulong>(frontier, StringComparer.Ordinal));
    }

    /// <summary>The highest contiguous sequence observed for each origin.</summary>
    public IReadOnlyDictionary<string, ulong> Frontier { get; }

    /// <summary>An empty cursor used for a fresh replication stream.</summary>
    public static ReplicationCursor Empty { get; } = new(new Dictionary<string, ulong>(StringComparer.Ordinal));

    /// <summary>Returns whether this cursor includes a replication dot.</summary>
    public bool Contains(ReplicationDot dot) =>
        Frontier.TryGetValue(dot.Origin, out var sequence) && sequence >= dot.Sequence;
}

/// <summary>Bounds for one resumable replication stream request.</summary>
public sealed record ReplicationStreamRequest
{
    public ReplicationStreamRequest(
        string streamId,
        ReplicationCursor? cursor = null,
        int maxRecords = 256,
        int maxChunkBytes = 4 * 1024 * 1024,
        long resumeAfterSequence = -1)
    {
        if (string.IsNullOrWhiteSpace(streamId)) throw new ArgumentException("A stream id is required.", nameof(streamId));
        if (maxRecords <= 0) throw new ArgumentOutOfRangeException(nameof(maxRecords));
        if (maxChunkBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunkBytes));
        if (resumeAfterSequence < -1) throw new ArgumentOutOfRangeException(nameof(resumeAfterSequence));

        StreamId = streamId;
        Cursor = cursor ?? ReplicationCursor.Empty;
        MaxRecords = maxRecords;
        MaxChunkBytes = maxChunkBytes;
        ResumeAfterSequence = resumeAfterSequence;
    }

    /// <summary>The retained stream identity.</summary>
    public string StreamId { get; }

    /// <summary>The receiver's causal frontier at stream start.</summary>
    public ReplicationCursor Cursor { get; }

    /// <summary>The maximum number of records in one chunk.</summary>
    public int MaxRecords { get; }

    /// <summary>The maximum estimated decoded bytes in one chunk.</summary>
    public int MaxChunkBytes { get; }

    /// <summary>The last fully applied chunk sequence, or -1 for a fresh stream.</summary>
    public long ResumeAfterSequence { get; }
}

/// <summary>An immutable, checksum protected replication chunk.</summary>
public sealed record ReplicationStreamChunk(
    string StreamId,
    long Sequence,
    IReadOnlyList<ReplicationRecord> Records,
    ReplicationCursor Cursor,
    bool IsFinal,
    int EstimatedBytes,
    string Checksum);

/// <summary>Replication-specific transport operations.</summary>
public interface IReplicationTransport
{
    IAsyncEnumerable<ReplicationStreamChunk> StreamReplicationAsync(
        ReplicationStreamRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Result metadata for a successful stream resume.</summary>
public sealed record ReplicationResumeResult(
    string StreamId,
    long FirstAvailableSequence,
    long LastAvailableSequence,
    bool IsComplete);

