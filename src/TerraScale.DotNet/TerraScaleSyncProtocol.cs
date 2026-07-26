using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// A precision-safe unsigned integer in the TerraScale Sync v1 wire protocol.
/// </summary>
/// <param name="Value">The unsigned integer value.</param>
[JsonConverter(typeof(TerraScaleSyncUInt64JsonConverter))]
public readonly record struct TerraScaleSyncUInt64(ulong Value) : IComparable<TerraScaleSyncUInt64>
{
    /// <summary>
    /// The zero value.
    /// </summary>
    public static TerraScaleSyncUInt64 Zero { get; } = new(0);

    /// <inheritdoc />
    public int CompareTo(TerraScaleSyncUInt64 other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);

    /// <summary>Compares two values.</summary>
    public static bool operator <(TerraScaleSyncUInt64 left, TerraScaleSyncUInt64 right) =>
        left.Value < right.Value;

    /// <summary>Compares two values.</summary>
    public static bool operator <=(TerraScaleSyncUInt64 left, TerraScaleSyncUInt64 right) =>
        left.Value <= right.Value;

    /// <summary>Compares two values.</summary>
    public static bool operator >(TerraScaleSyncUInt64 left, TerraScaleSyncUInt64 right) =>
        left.Value > right.Value;

    /// <summary>Compares two values.</summary>
    public static bool operator >=(TerraScaleSyncUInt64 left, TerraScaleSyncUInt64 right) =>
        left.Value >= right.Value;
}

/// <summary>
/// Serializes Sync unsigned integers as canonical decimal strings.
/// </summary>
public sealed class TerraScaleSyncUInt64JsonConverter : JsonConverter<TerraScaleSyncUInt64>
{
    /// <inheritdoc />
    public override TerraScaleSyncUInt64 Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Unsigned protocol integers must be decimal strings.");
        }

        ReadOnlySpan<byte> value;
        byte[]? rented = null;
        if (reader.HasValueSequence)
        {
            var length = checked((int)reader.ValueSequence.Length);
            rented = ArrayPool<byte>.Shared.Rent(length);
            reader.ValueSequence.CopyTo(rented);
            value = rented.AsSpan(0, length);
        }
        else
        {
            value = reader.ValueSpan;
        }

        try
        {
            if (value.IsEmpty ||
                value.Length > 20 ||
                value.Length > 1 && value[0] == (byte)'0' ||
                !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new JsonException(
                    "Unsigned protocol integers must use canonical UInt64 decimal strings.");
            }

            return new TerraScaleSyncUInt64(parsed);
        }
        finally
        {
            if (rented is not null)
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        TerraScaleSyncUInt64 value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// A canonical ULID used by TerraScale Sync v1.
/// </summary>
[JsonConverter(typeof(TerraScaleSyncIdJsonConverter))]
public readonly record struct TerraScaleSyncId
{
    /// <summary>
    /// Creates a validated Sync identifier.
    /// </summary>
    /// <param name="value">A canonical 26-character ULID.</param>
    /// <exception cref="ArgumentException">The value is not a canonical ULID.</exception>
    public TerraScaleSyncId(string value)
    {
        if (!IsCanonical(value))
        {
            throw new ArgumentException("Sync identifiers must be canonical ULIDs.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// The canonical ULID text.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Whether this value is the default empty identifier.
    /// </summary>
    public bool IsEmpty => string.IsNullOrEmpty(Value);

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    internal static bool IsCanonical(string? value)
    {
        if (value is not { Length: 26 } || value[0] is < '0' or > '7')
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'A' and <= 'H') and
                not (>= 'J' and <= 'K') and
                not (>= 'M' and <= 'N') and
                not (>= 'P' and <= 'T') and
                not (>= 'V' and <= 'Z'))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Serializes canonical Sync ULIDs as JSON strings.
/// </summary>
public sealed class TerraScaleSyncIdJsonConverter : JsonConverter<TerraScaleSyncId>
{
    /// <inheritdoc />
    public override TerraScaleSyncId Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var value = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        return TerraScaleSyncId.IsCanonical(value)
            ? new TerraScaleSyncId(value!)
            : throw new JsonException("Sync identifiers must be canonical ULID strings.");
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        TerraScaleSyncId value,
        JsonSerializerOptions options)
    {
        if (value.IsEmpty)
        {
            throw new JsonException("Sync identifiers must be canonical ULID strings.");
        }

        writer.WriteStringValue(value.Value);
    }
}

/// <summary>
/// A durable database revision.
/// </summary>
/// <param name="RevisionEpoch">The authority epoch.</param>
/// <param name="CommitSequence">The commit sequence within the epoch.</param>
public readonly record struct TerraScaleSyncDatabaseRevision(
    TerraScaleSyncUInt64 RevisionEpoch,
    TerraScaleSyncUInt64 CommitSequence) : IComparable<TerraScaleSyncDatabaseRevision>
{
    /// <summary>
    /// The zero revision.
    /// </summary>
    public static TerraScaleSyncDatabaseRevision Zero { get; } =
        new(TerraScaleSyncUInt64.Zero, TerraScaleSyncUInt64.Zero);

    /// <inheritdoc />
    public int CompareTo(TerraScaleSyncDatabaseRevision other)
    {
        var epochComparison = RevisionEpoch.CompareTo(other.RevisionEpoch);
        return epochComparison != 0 ? epochComparison : CommitSequence.CompareTo(other.CommitSequence);
    }

    /// <summary>Compares two revisions.</summary>
    public static bool operator <(
        TerraScaleSyncDatabaseRevision left,
        TerraScaleSyncDatabaseRevision right) =>
        left.CompareTo(right) < 0;

    /// <summary>Compares two revisions.</summary>
    public static bool operator <=(
        TerraScaleSyncDatabaseRevision left,
        TerraScaleSyncDatabaseRevision right) =>
        left.CompareTo(right) <= 0;

    /// <summary>Compares two revisions.</summary>
    public static bool operator >(
        TerraScaleSyncDatabaseRevision left,
        TerraScaleSyncDatabaseRevision right) =>
        left.CompareTo(right) > 0;

    /// <summary>Compares two revisions.</summary>
    public static bool operator >=(
        TerraScaleSyncDatabaseRevision left,
        TerraScaleSyncDatabaseRevision right) =>
        left.CompareTo(right) >= 0;
}

/// <summary>
/// The three-dimensional version of a Sync session state.
/// </summary>
/// <param name="QuerySetVersion">The registered query-set version.</param>
/// <param name="DatabaseRevision">The durable database revision.</param>
/// <param name="IdentityVersion">The authorization identity version.</param>
public readonly record struct TerraScaleSyncStateVersion(
    TerraScaleSyncUInt64 QuerySetVersion,
    TerraScaleSyncDatabaseRevision DatabaseRevision,
    TerraScaleSyncUInt64 IdentityVersion)
{
    /// <summary>
    /// The zero session version.
    /// </summary>
    public static TerraScaleSyncStateVersion Zero { get; } =
        new(TerraScaleSyncUInt64.Zero, TerraScaleSyncDatabaseRevision.Zero, TerraScaleSyncUInt64.Zero);

    /// <summary>
    /// Returns whether every version component is at least the corresponding component of
    /// <paramref name="other"/>.
    /// </summary>
    public bool IsAtLeast(TerraScaleSyncStateVersion other) =>
        QuerySetVersion >= other.QuerySetVersion &&
        DatabaseRevision >= other.DatabaseRevision &&
        IdentityVersion >= other.IdentityVersion;
}

/// <summary>
/// Supported TerraScale Sync v1 consistency modes.
/// </summary>
[JsonConverter(typeof(TerraScaleSyncConsistencyModeJsonConverter))]
public enum TerraScaleSyncConsistencyMode
{
    /// <summary>
    /// Results are authoritative snapshots validated through a durable database revision.
    /// </summary>
    AuthoritativeSnapshot
}

/// <summary>
/// Serializes Sync consistency modes using their exact v1 wire names.
/// </summary>
public sealed class TerraScaleSyncConsistencyModeJsonConverter :
    JsonConverter<TerraScaleSyncConsistencyMode>
{
    /// <inheritdoc />
    public override TerraScaleSyncConsistencyMode Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !string.Equals(
                reader.GetString(),
                nameof(TerraScaleSyncConsistencyMode.AuthoritativeSnapshot),
                StringComparison.Ordinal))
        {
            throw new JsonException("ConsistencyMode must be AuthoritativeSnapshot.");
        }

        return TerraScaleSyncConsistencyMode.AuthoritativeSnapshot;
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        TerraScaleSyncConsistencyMode value,
        JsonSerializerOptions options)
    {
        if (value != TerraScaleSyncConsistencyMode.AuthoritativeSnapshot)
        {
            throw new JsonException("ConsistencyMode must be AuthoritativeSnapshot.");
        }

        writer.WriteStringValue(nameof(TerraScaleSyncConsistencyMode.AuthoritativeSnapshot));
    }
}

/// <summary>
/// TerraScale Sync v1 message types.
/// </summary>
[JsonConverter(typeof(TerraScaleSyncMessageTypeJsonConverter))]
public enum TerraScaleSyncMessageType
{
    /// <summary>Starts or resumes a session.</summary>
    Connect,
    /// <summary>Replaces session authentication.</summary>
    Authenticate,
    /// <summary>Changes the active query set.</summary>
    ModifyQuerySet,
    /// <summary>Executes a mutation.</summary>
    ExecuteMutation,
    /// <summary>Acknowledges a complete transition.</summary>
    AcknowledgeTransition,
    /// <summary>Reports client telemetry.</summary>
    ClientTelemetry,
    /// <summary>Checks connection liveness.</summary>
    Ping,
    /// <summary>Carries an atomic state transition.</summary>
    Transition,
    /// <summary>Reports a mutation result.</summary>
    MutationResult,
    /// <summary>Reports an authentication error.</summary>
    AuthenticationError,
    /// <summary>Requires a full resynchronization.</summary>
    ResyncRequired,
    /// <summary>Reports a regional route change.</summary>
    RegionRouteChanged,
    /// <summary>Terminates the connection for a protocol error.</summary>
    FatalProtocolError
}

/// <summary>
/// Serializes Sync message types using their exact v1 wire names.
/// </summary>
public sealed class TerraScaleSyncMessageTypeJsonConverter : JsonConverter<TerraScaleSyncMessageType>
{
    /// <inheritdoc />
    public override TerraScaleSyncMessageType Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("MessageType is not a supported TerraScale Sync v1 message.");
        }

        return reader.GetString() switch
        {
            nameof(TerraScaleSyncMessageType.Connect) => TerraScaleSyncMessageType.Connect,
            nameof(TerraScaleSyncMessageType.Authenticate) => TerraScaleSyncMessageType.Authenticate,
            nameof(TerraScaleSyncMessageType.ModifyQuerySet) => TerraScaleSyncMessageType.ModifyQuerySet,
            nameof(TerraScaleSyncMessageType.ExecuteMutation) => TerraScaleSyncMessageType.ExecuteMutation,
            nameof(TerraScaleSyncMessageType.AcknowledgeTransition) =>
                TerraScaleSyncMessageType.AcknowledgeTransition,
            nameof(TerraScaleSyncMessageType.ClientTelemetry) => TerraScaleSyncMessageType.ClientTelemetry,
            nameof(TerraScaleSyncMessageType.Ping) => TerraScaleSyncMessageType.Ping,
            nameof(TerraScaleSyncMessageType.Transition) => TerraScaleSyncMessageType.Transition,
            nameof(TerraScaleSyncMessageType.MutationResult) => TerraScaleSyncMessageType.MutationResult,
            nameof(TerraScaleSyncMessageType.AuthenticationError) =>
                TerraScaleSyncMessageType.AuthenticationError,
            nameof(TerraScaleSyncMessageType.ResyncRequired) => TerraScaleSyncMessageType.ResyncRequired,
            nameof(TerraScaleSyncMessageType.RegionRouteChanged) =>
                TerraScaleSyncMessageType.RegionRouteChanged,
            nameof(TerraScaleSyncMessageType.FatalProtocolError) =>
                TerraScaleSyncMessageType.FatalProtocolError,
            _ => throw new JsonException(
                "MessageType is not a supported TerraScale Sync v1 message.")
        };
    }

    /// <inheritdoc />
    public override void Write(
        Utf8JsonWriter writer,
        TerraScaleSyncMessageType value,
        JsonSerializerOptions options)
    {
        var name = value switch
        {
            TerraScaleSyncMessageType.Connect => nameof(TerraScaleSyncMessageType.Connect),
            TerraScaleSyncMessageType.Authenticate => nameof(TerraScaleSyncMessageType.Authenticate),
            TerraScaleSyncMessageType.ModifyQuerySet => nameof(TerraScaleSyncMessageType.ModifyQuerySet),
            TerraScaleSyncMessageType.ExecuteMutation => nameof(TerraScaleSyncMessageType.ExecuteMutation),
            TerraScaleSyncMessageType.AcknowledgeTransition =>
                nameof(TerraScaleSyncMessageType.AcknowledgeTransition),
            TerraScaleSyncMessageType.ClientTelemetry => nameof(TerraScaleSyncMessageType.ClientTelemetry),
            TerraScaleSyncMessageType.Ping => nameof(TerraScaleSyncMessageType.Ping),
            TerraScaleSyncMessageType.Transition => nameof(TerraScaleSyncMessageType.Transition),
            TerraScaleSyncMessageType.MutationResult => nameof(TerraScaleSyncMessageType.MutationResult),
            TerraScaleSyncMessageType.AuthenticationError =>
                nameof(TerraScaleSyncMessageType.AuthenticationError),
            TerraScaleSyncMessageType.ResyncRequired => nameof(TerraScaleSyncMessageType.ResyncRequired),
            TerraScaleSyncMessageType.RegionRouteChanged =>
                nameof(TerraScaleSyncMessageType.RegionRouteChanged),
            TerraScaleSyncMessageType.FatalProtocolError =>
                nameof(TerraScaleSyncMessageType.FatalProtocolError),
            _ => throw new JsonException(
                "MessageType is not a supported TerraScale Sync v1 message.")
        };

        writer.WriteStringValue(name);
    }
}

/// <summary>
/// A TerraScale Sync v1 message envelope.
/// </summary>
/// <typeparam name="TPayload">The strongly typed message payload.</typeparam>
/// <param name="Protocol">The protocol version.</param>
/// <param name="MessageType">The message type.</param>
/// <param name="ConnectionSequence">The connection sequence.</param>
/// <param name="MessageSequence">The message sequence.</param>
/// <param name="Payload">The message payload.</param>
public sealed record TerraScaleSyncEnvelope<TPayload>(
    string Protocol,
    TerraScaleSyncMessageType MessageType,
    TerraScaleSyncUInt64 ConnectionSequence,
    TerraScaleSyncUInt64 MessageSequence,
    TPayload Payload)
{
    /// <summary>
    /// The exact TerraScale Sync v1 protocol identifier.
    /// </summary>
    public const string ProtocolVersion = "terrascale.sync.v1";
}

/// <summary>
/// A query to add to a Sync session.
/// </summary>
public sealed record TerraScaleSyncAddQuery(
    TerraScaleSyncId QueryId,
    string Query,
    JsonElement Arguments,
    TerraScaleSyncUInt64 SchemaVersion,
    TerraScaleSyncConsistencyMode ConsistencyMode,
    JsonElement? Journal);

/// <summary>
/// A query to remove from a Sync session.
/// </summary>
public sealed record TerraScaleSyncRemoveQuery(TerraScaleSyncId QueryId);

/// <summary>
/// Starts or resumes a Sync session.
/// </summary>
public sealed record TerraScaleSyncConnectMessage(
    TerraScaleSyncId SessionId,
    TerraScaleSyncId DatabaseId,
    TerraScaleSyncId EnvironmentId,
    TerraScaleSyncStateVersion? LastAppliedVersion,
    TerraScaleSyncStateVersion? LastAcknowledgedVersion,
    TerraScaleSyncDatabaseRevision MaximumObservedRevision,
    IReadOnlyList<TerraScaleSyncAddQuery> ActiveQueries,
    IReadOnlyList<TerraScaleSyncId> PendingMutationIds,
    string? PreviousIngressRegion,
    string? PreviousServingRegion,
    string? PreviousDatabaseRegion);

/// <summary>
/// Replaces the authentication attached to a Sync session.
/// </summary>
public sealed record TerraScaleSyncAuthenticateMessage(string BearerToken);

/// <summary>
/// Atomically changes the active query set.
/// </summary>
public sealed record TerraScaleSyncModifyQuerySetMessage(
    TerraScaleSyncUInt64 BaseQuerySetVersion,
    TerraScaleSyncUInt64 NewQuerySetVersion,
    IReadOnlyList<TerraScaleSyncAddQuery> AddQuery,
    IReadOnlyList<TerraScaleSyncRemoveQuery> RemoveQuery);

/// <summary>
/// Executes one idempotent Sync mutation.
/// </summary>
public sealed record TerraScaleSyncExecuteMutationMessage(
    TerraScaleSyncId ClientRequestId,
    string IdempotencyKey,
    string Mutation,
    JsonElement Arguments,
    JsonElement? Journal);

/// <summary>
/// Acknowledges an applied transition.
/// </summary>
public sealed record TerraScaleSyncAcknowledgeTransitionMessage(
    TerraScaleSyncId TransitionId,
    TerraScaleSyncStateVersion EndVersion);

/// <summary>
/// Reports connection observations that help regional routing.
/// </summary>
public sealed record TerraScaleSyncClientTelemetryMessage(
    int? RoundTripMilliseconds,
    string? ObservedIngressRegion,
    string? PreviousServingRegion,
    string? PreviousDatabaseRegion);

/// <summary>
/// A liveness message.
/// </summary>
public sealed record TerraScaleSyncPingMessage(
    TerraScaleSyncUInt64 Nonce,
    DateTimeOffset SentAt);

/// <summary>
/// A Sync query or mutation error.
/// </summary>
public sealed record TerraScaleSyncError(string Code, string Message, bool Retryable);

/// <summary>
/// A successful authoritative query result.
/// </summary>
public sealed record TerraScaleSyncQueryUpdated(
    TerraScaleSyncId QueryId,
    string QueryFingerprint,
    JsonElement Result,
    string ResultHash,
    TerraScaleSyncDatabaseRevision ExecutionRevision,
    TerraScaleSyncDatabaseRevision ValidatedThroughRevision,
    JsonElement? Journal);

/// <summary>
/// A failed authoritative query result.
/// </summary>
public sealed record TerraScaleSyncQueryFailed(
    TerraScaleSyncId QueryId,
    TerraScaleSyncError Error,
    TerraScaleSyncDatabaseRevision ValidatedThroughRevision);

/// <summary>
/// A query removed from authoritative state.
/// </summary>
public sealed record TerraScaleSyncQueryRemoved(TerraScaleSyncId QueryId);

/// <summary>
/// Server work attributed to one transition.
/// </summary>
public sealed record TerraScaleSyncServerTiming(
    int QueryMilliseconds,
    int ValidationMilliseconds,
    int SerializationMilliseconds);

/// <summary>
/// The base type for complete and chunked transition frames.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "FrameType")]
[JsonDerivedType(typeof(TerraScaleSyncTransition), typeDiscriminator: "Complete")]
[JsonDerivedType(typeof(TerraScaleSyncTransitionChunk), typeDiscriminator: "Chunk")]
public abstract record TerraScaleSyncTransitionFrame;

/// <summary>
/// One complete, atomic Sync state transition.
/// </summary>
public sealed record TerraScaleSyncTransition(
    TerraScaleSyncId TransitionId,
    TerraScaleSyncStateVersion StartVersion,
    TerraScaleSyncStateVersion EndVersion,
    IReadOnlyList<TerraScaleSyncQueryUpdated> QueryUpdated,
    IReadOnlyList<TerraScaleSyncQueryFailed> QueryFailed,
    IReadOnlyList<TerraScaleSyncQueryRemoved> QueryRemoved,
    TerraScaleSyncServerTiming? ServerTiming,
    string IngressRegion,
    string ServingRegion,
    string DatabaseRegion,
    TerraScaleSyncUInt64 RouteGeneration) : TerraScaleSyncTransitionFrame;

/// <summary>
/// One bounded piece of a chunked Sync transition.
/// </summary>
public sealed record TerraScaleSyncTransitionChunk(
    TerraScaleSyncId TransitionId,
    int ChunkIndex,
    int ChunkCount,
    int UncompressedLength,
    string Sha256,
    byte[] Data) : TerraScaleSyncTransitionFrame;

/// <summary>
/// Reports the durable result of a mutation.
/// </summary>
public sealed record TerraScaleSyncMutationResultMessage(
    TerraScaleSyncId ClientRequestId,
    bool Succeeded,
    JsonElement? Result,
    TerraScaleSyncError? Error,
    TerraScaleSyncDatabaseRevision? CommittedRevision);

/// <summary>
/// Reports an authentication failure.
/// </summary>
public sealed record TerraScaleSyncAuthenticationErrorMessage(TerraScaleSyncError Error);

/// <summary>
/// Instructs the client to discard its resume position and resynchronize.
/// </summary>
public sealed record TerraScaleSyncResyncRequiredMessage(
    string Reason,
    TerraScaleSyncDatabaseRevision EarliestRetainedRevision,
    TerraScaleSyncStateVersion? LastAcknowledgedVersion);

/// <summary>
/// Reports the route currently serving the session.
/// </summary>
public sealed record TerraScaleSyncRegionRouteChangedMessage(
    string IngressRegion,
    string ServingRegion,
    string DatabaseRegion,
    TerraScaleSyncUInt64 RouteGeneration,
    bool ReconnectRequired);

/// <summary>
/// A validated Sync protocol error.
/// </summary>
public sealed record TerraScaleSyncValidationError(string Code, string Message);

/// <summary>
/// Terminates a session after a fatal protocol error.
/// </summary>
public sealed record TerraScaleSyncFatalProtocolErrorMessage(
    TerraScaleSyncValidationError Error,
    TerraScaleSyncStateVersion? ResumeVersion);

/// <summary>
/// Source-generated, strict TerraScale Sync v1 JSON metadata.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified,
    PropertyNameCaseInsensitive = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncConnectMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncAuthenticateMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncModifyQuerySetMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncExecuteMutationMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncAcknowledgeTransitionMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncClientTelemetryMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncPingMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncTransitionFrame>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncMutationResultMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncAuthenticationErrorMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncResyncRequiredMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncRegionRouteChangedMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<TerraScaleSyncFatalProtocolErrorMessage>))]
[JsonSerializable(typeof(TerraScaleSyncEnvelope<JsonElement>))]
[JsonSerializable(typeof(TerraScaleSyncTransitionFrame))]
[JsonSerializable(typeof(TerraScaleSyncTransition))]
public sealed partial class TerraScaleSyncJsonContext : JsonSerializerContext;
