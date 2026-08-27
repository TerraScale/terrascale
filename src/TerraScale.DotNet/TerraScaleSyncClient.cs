using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>
/// Creates explicit TerraScale Sync sessions.
/// </summary>
public sealed class TerraScaleSyncClient
{
    private readonly TerraScaleClient client;

    internal TerraScaleSyncClient(TerraScaleClient client) => this.client = client;

    /// <summary>
    /// Creates a stopped Sync session. Call <see cref="TerraScaleSyncSession.StartAsync"/>
    /// to open its WebSocket and background loops.
    /// </summary>
    public TerraScaleSyncSession CreateSession(TerraScaleSyncSessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!client.HasSyncAuthentication)
        {
            throw new InvalidOperationException(
                "Configure an API key, bearer token, or bearer-token provider before creating a Sync session.");
        }

        return new TerraScaleSyncSession(client, options);
    }
}

/// <summary>
/// Configures one explicit TerraScale Sync session.
/// </summary>
/// <param name="DatabaseId">The logical database ULID.</param>
/// <param name="EnvironmentId">The database environment ULID.</param>
public sealed record TerraScaleSyncSessionOptions(string DatabaseId, string EnvironmentId)
{
    /// <summary>
    /// Stable session ULID. The SDK creates one when omitted.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Delay before the first reconnect attempt.
    /// </summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Maximum exponential reconnect delay.
    /// </summary>
    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Interval between application-level heartbeat messages.
    /// </summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Maximum interval without an inbound frame before reconnecting.
    /// </summary>
    public TimeSpan HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(45);

    internal ValidatedSyncSessionOptions Validate()
    {
        TerraScaleSyncId databaseId;
        TerraScaleSyncId environmentId;
        TerraScaleSyncId sessionId;
        try
        {
            databaseId = new TerraScaleSyncId(DatabaseId);
            environmentId = new TerraScaleSyncId(EnvironmentId);
            sessionId = string.IsNullOrWhiteSpace(SessionId)
                ? TerraScaleSyncIdGenerator.Create()
                : new TerraScaleSyncId(SessionId.Trim());
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException(
                "DatabaseId, EnvironmentId, and SessionId must be canonical ULIDs.",
                nameof(DatabaseId),
                exception);
        }

        if (ReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ReconnectDelay),
                "ReconnectDelay must be greater than or equal to zero.");
        }

        if (MaximumReconnectDelay < ReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumReconnectDelay),
                "MaximumReconnectDelay must be at least ReconnectDelay.");
        }

        if (HeartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatInterval),
                "HeartbeatInterval must be greater than zero.");
        }

        if (HeartbeatTimeout <= HeartbeatInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(HeartbeatTimeout),
                "HeartbeatTimeout must be greater than HeartbeatInterval.");
        }

        return new(
            databaseId,
            environmentId,
            sessionId,
            ReconnectDelay,
            MaximumReconnectDelay,
            HeartbeatInterval,
            HeartbeatTimeout);
    }
}

/// <summary>
/// A live authoritative query registration.
/// </summary>
/// <param name="Query">The TQL query.</param>
/// <param name="Arguments">The query arguments.</param>
/// <param name="SchemaVersion">The expected schema version.</param>
/// <param name="Journal">Optional opaque application journal data.</param>
public sealed record TerraScaleSyncQuery(
    string Query,
    JsonElement Arguments,
    TerraScaleSyncUInt64 SchemaVersion,
    JsonElement? Journal = null);

/// <summary>
/// An idempotent Sync mutation and its optional optimistic query patches.
/// </summary>
/// <param name="IdempotencyKey">A stable idempotency key for this logical mutation.</param>
/// <param name="Mutation">The mutation text.</param>
/// <param name="Arguments">The mutation arguments.</param>
/// <param name="Journal">Optional opaque application journal data.</param>
/// <param name="OptimisticPatches">Ordered local query transformations.</param>
public sealed record TerraScaleSyncMutation(
    string IdempotencyKey,
    string Mutation,
    JsonElement Arguments,
    JsonElement? Journal = null,
    IReadOnlyList<TerraScaleSyncOptimisticPatch>? OptimisticPatches = null);

/// <summary>
/// The durable result of a Sync mutation.
/// </summary>
public sealed record TerraScaleSyncMutationResult(
    TerraScaleSyncId ClientRequestId,
    bool Succeeded,
    JsonElement? Result,
    TerraScaleSyncError? Error,
    TerraScaleSyncDatabaseRevision? CommittedRevision);

/// <summary>
/// One immutable typed live-query value.
/// </summary>
public sealed record TerraScaleSyncLiveQuerySnapshot<T>(
    TerraScaleSyncId QueryId,
    bool HasValue,
    T? Value,
    string? ResultHash,
    TerraScaleSyncDatabaseRevision? ExecutionRevision,
    TerraScaleSyncDatabaseRevision ValidatedThroughRevision,
    TerraScaleSyncError? Error,
    bool Optimistic);

/// <summary>
/// A reference-counted live-query listener. Dispose it to release its network registration.
/// </summary>
public sealed class TerraScaleSyncLiveQuery<T> :
    IAsyncEnumerable<TerraScaleSyncLiveQuerySnapshot<T>>,
    IAsyncDisposable
{
    private readonly IAsyncEnumerable<TerraScaleSyncQuerySnapshot> snapshots;
    private readonly JsonTypeInfo<T>? typeInfo;
    private readonly Func<ValueTask> release;
    private int enumerated;
    private int disposed;

    internal TerraScaleSyncLiveQuery(
        TerraScaleSyncId queryId,
        IAsyncEnumerable<TerraScaleSyncQuerySnapshot> snapshots,
        JsonTypeInfo<T>? typeInfo,
        Func<ValueTask> release)
    {
        QueryId = queryId;
        this.snapshots = snapshots;
        this.typeInfo = typeInfo;
        this.release = release;
    }

    /// <summary>The shared network query identifier.</summary>
    public TerraScaleSyncId QueryId { get; }

    /// <inheritdoc />
    public IAsyncEnumerator<TerraScaleSyncLiveQuerySnapshot<T>> GetAsyncEnumerator(
        CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref enumerated, 1) != 0)
        {
            throw new InvalidOperationException(
                "A live-query handle supports one enumeration. Acquire another handle for another listener.");
        }

        return ReadAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    private async IAsyncEnumerable<TerraScaleSyncLiveQuerySnapshot<T>> ReadAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var snapshot in snapshots.WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return new(
                    snapshot.QueryId,
                    snapshot.Result is not null,
                    Convert(snapshot.Result),
                    snapshot.ResultHash,
                    snapshot.ExecutionRevision,
                    snapshot.ValidatedThroughRevision,
                    snapshot.Error,
                    snapshot.Optimistic);
            }
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    private T? Convert(JsonElement? result)
    {
        if (result is null)
        {
            return default;
        }

        if (typeof(T) == typeof(JsonElement))
        {
            return (T)(object)result.Value.Clone();
        }

        var resolvedTypeInfo = typeInfo
            ?? throw new InvalidOperationException("A JSON type descriptor is required for a typed live query.");
        return result.Value.Deserialize(resolvedTypeInfo);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref disposed, 1) == 0 ? release() : ValueTask.CompletedTask;
}

/// <summary>
/// The lifecycle state of a Sync session.
/// </summary>
public enum TerraScaleSyncConnectionState
{
    /// <summary>The session has not started or is between attempts.</summary>
    Disconnected,
    /// <summary>The first WebSocket is connecting.</summary>
    Connecting,
    /// <summary>The WebSocket is active.</summary>
    Connected,
    /// <summary>A resumable reconnect is in progress.</summary>
    Reconnecting,
    /// <summary>The server required a full authoritative resynchronization.</summary>
    Resynchronizing,
    /// <summary>A terminal authentication or protocol error stopped the session.</summary>
    Faulted,
    /// <summary>The session was disposed.</summary>
    Disposed
}

/// <summary>
/// A bounded, non-sensitive diagnostic snapshot for one Sync session.
/// </summary>
public sealed record TerraScaleSyncConnectionDiagnostics(
    TerraScaleSyncConnectionState ConnectionState,
    TerraScaleSyncUInt64 ConnectionSequence,
    TerraScaleSyncStateVersion StateVersion,
    TerraScaleSyncStateVersion? LastAcknowledgedVersion,
    TerraScaleSyncDatabaseRevision MaximumObservedRevision,
    string? IngressRegion,
    string? ServingRegion,
    string? DatabaseRegion,
    TerraScaleSyncUInt64 RouteGeneration,
    int ActiveQueries,
    int PendingMutations,
    int? LastRoundTripMilliseconds,
    string? ErrorCode);

/// <summary>
/// Event data for a Sync connection-state change.
/// </summary>
public sealed class TerraScaleSyncConnectionStateChangedEventArgs(
    TerraScaleSyncConnectionDiagnostics diagnostics) : EventArgs
{
    /// <summary>The current diagnostic snapshot.</summary>
    public TerraScaleSyncConnectionDiagnostics Diagnostics { get; } = diagnostics;
}

/// <summary>
/// A structured TerraScale Sync error.
/// </summary>
public sealed class TerraScaleSyncException : Exception
{
    internal TerraScaleSyncException(string code, string message, bool retryable = false)
        : base(message)
    {
        Code = code;
        Retryable = retryable;
    }

    /// <summary>The stable protocol error code.</summary>
    public string Code { get; }

    /// <summary>Whether the server reported that the operation may be retried.</summary>
    public bool Retryable { get; }
}

internal sealed record ValidatedSyncSessionOptions(
    TerraScaleSyncId DatabaseId,
    TerraScaleSyncId EnvironmentId,
    TerraScaleSyncId SessionId,
    TimeSpan ReconnectDelay,
    TimeSpan MaximumReconnectDelay,
    TimeSpan HeartbeatInterval,
    TimeSpan HeartbeatTimeout);

internal static class TerraScaleSyncIdGenerator
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static TerraScaleSyncId Create()
    {
        Span<byte> bytes = stackalloc byte[16];
        var milliseconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(milliseconds >> 40);
        bytes[1] = (byte)(milliseconds >> 32);
        bytes[2] = (byte)(milliseconds >> 24);
        bytes[3] = (byte)(milliseconds >> 16);
        bytes[4] = (byte)(milliseconds >> 8);
        bytes[5] = (byte)milliseconds;
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes[6..]);

        Span<char> chars = stackalloc char[26];
        for (var characterIndex = 0; characterIndex < chars.Length; characterIndex++)
        {
            var value = 0;
            for (var bit = 0; bit < 5; bit++)
            {
                value <<= 1;
                var bitIndex = characterIndex * 5 + bit - 2;
                if (bitIndex >= 0)
                {
                    value |= bytes[bitIndex / 8] >> (7 - bitIndex % 8) & 1;
                }
            }

            chars[characterIndex] = Alphabet[value];
        }

        return new TerraScaleSyncId(new string(chars));
    }
}
