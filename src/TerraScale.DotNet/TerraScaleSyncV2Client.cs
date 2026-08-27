using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Configures a <see cref="TerraScaleSyncV2Client"/> against the TerraBase
/// C# API host: the backend-supplied context headers the host's local
/// gate requires (PD-2026-08-26-11 D1 — no authentication surface lives
/// on this boundary; customer auth lives exclusively at the TerraScale
/// Platform).
/// </summary>
public sealed record TerraScaleSyncV2ClientOptions
{
    /// <summary>The backend principal (the <c>X-TB-Principal</c> header).</summary>
    public string? Principal { get; init; }

    /// <summary>The backend-supplied tenant (the <c>X-TB-Tenant</c> header).</summary>
    public string? Tenant { get; init; }

    internal void Validate()
    {
        if (Present(Principal) && Present(Tenant))
        {
            return;
        }

        throw new ArgumentException(
            "TerraScaleSyncV2ClientOptions requires both Principal and Tenant: the API host's local gate refuses requests without backend-supplied context (X-TB-Principal, X-TB-Tenant).");

        static bool Present(string? value) =>
            value is { Length: > 0 and <= 128 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    }
}

/// <summary>
/// Creates native <c>terrascale.sync.v2</c> sync sessions (SE-10,
/// PD-2026-08-26-12): a fully managed client over the API host's
/// sync-session and stream endpoints — no native assets, no interop
/// boundary. The client is thread-safe and safe to reuse as a singleton;
/// it never disposes the caller-owned <see cref="HttpClient"/>.
/// </summary>
public sealed class TerraScaleSyncV2Client
{
    private readonly HttpClient http;
    private readonly TerraScaleSyncV2ClientOptions options;

    /// <summary>Initializes the client.</summary>
    /// <param name="httpClient">The HTTP transport (caller-owned; never disposed by the SDK).</param>
    /// <param name="options">The backend-supplied context options.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentException">The options do not validate.</exception>
    public TerraScaleSyncV2Client(HttpClient httpClient, TerraScaleSyncV2ClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        if (httpClient.BaseAddress is not { IsAbsoluteUri: true })
        {
            throw new ArgumentException(
                "The HttpClient must carry an absolute BaseAddress pointing at the TerraBase API host.",
                nameof(httpClient));
        }

        options.Validate();
        this.http = httpClient;
        this.options = options;
    }

    /// <summary>The backend-supplied context options.</summary>
    public TerraScaleSyncV2ClientOptions Options => options;

    /// <summary>
    /// Creates a stopped sync session. Call <see cref="TerraScaleSyncV2Session.SetupAsync"/>
    /// for explicit control, or enumerate
    /// <see cref="TerraScaleSyncV2Session.UpdatesAsync"/> to run the
    /// trigger-driven live loop.
    /// </summary>
    /// <param name="options">The session options.</param>
    /// <returns>The session.</returns>
    public TerraScaleSyncV2Session CreateSession(TerraScaleSyncV2SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new TerraScaleSyncV2Session(http, this.options, options);
    }
}

/// <summary>
/// Configures one explicit <c>terrascale.sync.v2</c> sync session over
/// the API host.
/// </summary>
public sealed record TerraScaleSyncV2SessionOptions
{
    /// <summary>Initializes the session options.</summary>
    /// <param name="Collection">The collection scope the session subscribes to.</param>
    public TerraScaleSyncV2SessionOptions(string Collection)
    {
        this.Collection = Collection;
    }

    /// <summary>The collection scope (a bounded identifier: ASCII letters, digits, underscore, hyphen, dot; at most 128 characters).</summary>
    public string Collection { get; }

    /// <summary>The durable checkpoint a resuming session presents; <c>null</c> opens a fresh session.</summary>
    public TerraScaleSyncV2Checkpoint? Resume { get; init; }

    /// <summary>The delay before the first reconnect of a dropped stream. The default is 250 milliseconds.</summary>
    public TimeSpan ReconnectDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The maximum exponential reconnect delay. The default is 10 seconds.</summary>
    public TimeSpan MaximumReconnectDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>The streams endpoint path. The default is <c>/v1/streams</c>.</summary>
    public string StreamsPath { get; init; } = "/v1/streams";

    /// <summary>The sync sessions endpoint path. The default is <c>/v1/sync/sessions</c>.</summary>
    public string SessionsPath { get; init; } = "/v1/sync/sessions";

    /// <summary>
    /// The largest one server-sent event (any single field line or the
    /// accumulated data of one event) may occupy. A larger event ends the
    /// stream — the live loop reconnects; it is never buffered or
    /// dispatched. The default is one mebibyte.
    /// </summary>
    public int MaxStreamEventBytes { get; init; } = 1024 * 1024;

    /// <summary>
    /// The largest host response body the session decodes (the host
    /// surface's counterpart of the frame layer's
    /// <see cref="TerraScaleSyncV2Protocol.MaxFrameBytes"/>; sized for the
    /// base64 of one maximal payload plus its JSON envelope). A larger
    /// body is the typed host failure. The default is 32 mebibytes.
    /// </summary>
    public long MaxHostBodyBytes { get; init; } = 32L * 1024 * 1024;

    internal void Validate()
    {
        if (Collection.Length is < 1 or > 128
            || Collection.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-' and not '.'))
        {
            throw new ArgumentException(
                "The session collection must be a bounded identifier (ASCII letters, digits, underscore, hyphen, dot; 1-128 characters).",
                nameof(Collection));
        }

        if (ReconnectDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReconnectDelay), "ReconnectDelay must be greater than or equal to zero.");
        }

        if (MaximumReconnectDelay < ReconnectDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumReconnectDelay), "MaximumReconnectDelay must be at least ReconnectDelay.");
        }

        if (StreamsPath.Length == 0 || !StreamsPath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("StreamsPath must be an absolute path.", nameof(StreamsPath));
        }

        if (SessionsPath.Length == 0 || !SessionsPath.StartsWith("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("SessionsPath must be an absolute path.", nameof(SessionsPath));
        }

        if (MaxStreamEventBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxStreamEventBytes), "MaxStreamEventBytes must be at least one byte.");
        }

        if (MaxHostBodyBytes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHostBodyBytes), "MaxHostBodyBytes must be at least one byte.");
        }
    }
}

/// <summary>The setup answer: the session identity, the server checkpoint, and the retained floor.</summary>
/// <param name="SessionId">The server-assigned session identity.</param>
/// <param name="ServerCheckpoint">The server's current checkpoint.</param>
/// <param name="RetainedFloor">The oldest checkpoint deltas still serve from.</param>
public sealed record TerraScaleSyncV2SessionAccepted(
    ulong SessionId,
    TerraScaleSyncV2Checkpoint ServerCheckpoint,
    TerraScaleSyncV2Checkpoint RetainedFloor);

/// <summary>The outcome kinds of one delta pull.</summary>
public enum TerraScaleSyncV2DeltaPullKind
{
    /// <summary>The server served one byte-budgeted delta batch.</summary>
    Delta,

    /// <summary>The session's checkpoint already names the current state; nothing was served.</summary>
    UpToDate,

    /// <summary>The session is behind the retained floor; it must adopt a full snapshot.</summary>
    ResyncRequired,
}

/// <summary>The answer of one delta pull from the session's checkpoint.</summary>
/// <param name="Kind">The outcome kind.</param>
/// <param name="Codec">The payload codec (always <c>loro-delta/1</c> on a delta).</param>
/// <param name="Payload">The encoded delta bytes.</param>
/// <param name="Next">The checkpoint after applying this batch.</param>
/// <param name="Commits">How many journal commits the batch carries.</param>
/// <param name="SingleCommitOversize">Whether one commit's delta alone exceeded the budget and rides alone.</param>
/// <param name="Floor">The retained floor a resync-required answer states.</param>
public sealed record TerraScaleSyncV2DeltaPull(
    TerraScaleSyncV2DeltaPullKind Kind,
    TerraScaleSyncV2PayloadCodec Codec,
    byte[] Payload,
    TerraScaleSyncV2Checkpoint? Next,
    ulong Commits,
    bool SingleCommitOversize,
    TerraScaleSyncV2Checkpoint? Floor);

/// <summary>The answer of one snapshot pull: the full state and its checkpoint.</summary>
/// <param name="Codec">The payload codec (always <c>loro-snapshot/1</c>).</param>
/// <param name="Payload">The encoded snapshot bytes.</param>
/// <param name="Checkpoint">The checkpoint the snapshot corresponds to.</param>
public sealed record TerraScaleSyncV2SnapshotPull(
    TerraScaleSyncV2PayloadCodec Codec,
    byte[] Payload,
    TerraScaleSyncV2Checkpoint Checkpoint);

/// <summary>A host-surface mutation refusal: the engine's typed reason name.</summary>
/// <param name="Reason">The refusal's stable reason name.</param>
public sealed record TerraScaleSyncV2MutationRefusal(string Reason) : TerraScaleSyncV2MutationOutcome
{
    /// <summary>The closed-vocabulary error code the reason maps to, or <c>null</c> for an engine-side reason outside the wire vocabulary.</summary>
    public TerraScaleSyncV2ErrorCode? Code => Reason switch
    {
        "unsupported_version" => TerraScaleSyncV2ErrorCode.UnsupportedVersion,
        "malformed_frame" => TerraScaleSyncV2ErrorCode.MalformedFrame,
        "checkpoint_refused" => TerraScaleSyncV2ErrorCode.CheckpointRefused,
        "scope_refused" => TerraScaleSyncV2ErrorCode.ScopeRefused,
        "session_closed" => TerraScaleSyncV2ErrorCode.SessionClosed,
        "overloaded" => TerraScaleSyncV2ErrorCode.Overloaded,
        _ => null,
    };
}

/// <summary>The update kinds the live loop yields.</summary>
public enum TerraScaleSyncV2SyncUpdateKind
{
    /// <summary>A fresh session adopted the server's full snapshot.</summary>
    SnapshotAdopted,

    /// <summary>A delta batch applied and advanced the session's checkpoint.</summary>
    DeltaApplied,

    /// <summary>An at-least-once re-delivered batch was detected as already absorbed.</summary>
    DuplicateDelta,

    /// <summary>A resync-required answer was resolved by adopting a full snapshot.</summary>
    ResyncAdopted,
}

/// <summary>One live update the trigger-driven loop yields.</summary>
/// <param name="Kind">The update kind.</param>
/// <param name="Checkpoint">The session's checkpoint after the update.</param>
/// <param name="Codec">The payload codec when a delivery payload rode the update.</param>
/// <param name="Payload">The opaque delivery payload bytes.</param>
/// <param name="Commits">How many journal commits the applied batch carried.</param>
public sealed record TerraScaleSyncV2SyncUpdate(
    TerraScaleSyncV2SyncUpdateKind Kind,
    TerraScaleSyncV2Checkpoint Checkpoint,
    TerraScaleSyncV2PayloadCodec? Codec,
    ReadOnlyMemory<byte> Payload,
    ulong Commits);

/// <summary>
/// A typed failure of the API host surface: the HTTP status, the host's
/// error code, the bounded detail, the resync marker a checkpoint
/// refusal carries, the retained floor such a refusal states, and the
/// retryability derived from the status.
/// </summary>
public sealed class TerraScaleSyncV2HostException : Exception
{
    /// <summary>Initializes the typed host failure.</summary>
    /// <param name="statusCode">The HTTP status.</param>
    /// <param name="code">The host's error code.</param>
    /// <param name="detail">The bounded detail text.</param>
    /// <param name="resyncRequired">Whether the failure demands a snapshot resync.</param>
    /// <param name="floor">The retained floor the host stated, when it sent one.</param>
    public TerraScaleSyncV2HostException(
        HttpStatusCode statusCode,
        string code,
        string? detail,
        bool resyncRequired = false,
        TerraScaleSyncV2Checkpoint? floor = null)
        : base(detail is { Length: > 0 } ? $"{code}: {detail}" : code)
    {
        StatusCode = statusCode;
        Code = code;
        Detail = detail;
        ResyncRequired = resyncRequired;
        Floor = floor;
    }

    /// <summary>The HTTP status.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>The host's typed error code (for example <c>checkpoint-refused</c>, <c>session-ended</c>).</summary>
    public string Code { get; }

    /// <summary>The bounded detail text, when the host sent one.</summary>
    public string? Detail { get; }

    /// <summary>Whether the failure carries the host's resync-required marker.</summary>
    public bool ResyncRequired { get; }

    /// <summary>The retained floor the host stated in its refusal body, or <c>null</c> when it did not send one.</summary>
    public TerraScaleSyncV2Checkpoint? Floor { get; }

    /// <summary>Whether the failure is transient (retry after re-resolving): unavailable, gateway, or throttling statuses.</summary>
    public bool Retryable => StatusCode is HttpStatusCode.ServiceUnavailable
        or HttpStatusCode.BadGateway
        or HttpStatusCode.GatewayTimeout
        or (HttpStatusCode)429;
}

/// <summary>
/// One <c>terrascale.sync.v2</c> sync session over the API host (SE-10):
/// checkpoint-resumed setup, trigger-driven delta pulls (push the hint,
/// pull the delta — PD-2026-08-26-13 D3), full-snapshot resync, and
/// server-ack-only mutations (PD-2026-08-26-12 D2: no optimistic patches;
/// a mutation reaches the session's state only through the delivery
/// path). The live loop runs only while the consumer enumerates
/// <see cref="UpdatesAsync"/> — there is no hidden background work.
/// </summary>
public sealed class TerraScaleSyncV2Session : IAsyncDisposable
{
    /// <summary>
    /// How many times the live loop re-opens an ended session before it
    /// gives up with the typed host failure. The counter resets on a
    /// successful pull; every re-open pays the reconnect backoff.
    /// </summary>
    internal const int MaxSessionReopens = 3;

    private readonly HttpClient http;
    private readonly TerraScaleSyncV2ClientOptions clientOptions;
    private readonly TerraScaleSyncV2SessionOptions options;
    private readonly CancellationTokenSource disposed = new();
    private TerraScaleSyncV2SessionAccepted? accepted;
    private ulong lastEventId;
    private TimeSpan reconnectDelay;

    internal TerraScaleSyncV2Session(
        HttpClient http,
        TerraScaleSyncV2ClientOptions clientOptions,
        TerraScaleSyncV2SessionOptions options)
    {
        this.http = http;
        this.clientOptions = clientOptions;
        this.options = options;
        State = options.Resume is null
            ? new TerraScaleSyncV2ClientState()
            : new TerraScaleSyncV2ClientState(options.Resume);
    }

    /// <summary>The session's client-side receive state.</summary>
    public TerraScaleSyncV2ClientState State { get; }

    /// <summary>The setup answer, or <c>null</c> before <see cref="SetupAsync"/>.</summary>
    public TerraScaleSyncV2SessionAccepted? Accepted => accepted;

    /// <summary>The session identity, or <c>0</c> before setup.</summary>
    public ulong SessionId => accepted?.SessionId ?? 0;

    /// <summary>
    /// Opens the session: posts the setup with the collection scope and
    /// (when resuming) the session's durable checkpoint, and records the
    /// server's checkpoint and retained floor.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The setup answer.</returns>
    /// <exception cref="TerraScaleSyncV2HostException">The host refused the setup.</exception>
    public async Task<TerraScaleSyncV2SessionAccepted> SetupAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed.IsCancellationRequested, this);
        var resume = State.Checkpoint;
        var fresh = options.Resume is null && resume.CompareTo(TerraScaleSyncV2Checkpoint.Initial) == 0;
        var answer = await PostSetupAsync(fresh ? null : resume, cancellationToken).ConfigureAwait(false);
        accepted = answer;
        State.ObserveSetup(answer.ServerCheckpoint, answer.RetainedFloor);
        return answer;
    }

    /// <summary>
    /// Pulls one delta batch from the session's checkpoint and applies
    /// it. The answer is a delta, an up-to-date marker, or a
    /// resync-required demand (the caller then adopts a snapshot through
    /// <see cref="PullSnapshotAsync"/>). The caller acknowledges through
    /// <see cref="AcknowledgeCheckpointAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The pull answer.</returns>
    /// <exception cref="TerraScaleSyncV2HostException">The host refused the pull.</exception>
    public async Task<TerraScaleSyncV2DeltaPull> PullDeltaAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed.IsCancellationRequested, this);
        var current = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        var answer = await PostDeltaAsync(current, State.Checkpoint, cancellationToken).ConfigureAwait(false);
        if (answer.Kind == TerraScaleSyncV2DeltaPullKind.Delta)
        {
            _ = State.ApplyDelta(new TerraScaleSyncV2DeltaBatchBody(
                answer.Codec,
                answer.Payload,
                answer.Next!,
                WithinBudget: true,
                SingleCommitOversize: answer.SingleCommitOversize,
                Commits: answer.Commits));
        }

        return answer;
    }

    /// <summary>
    /// Pulls the full state and adopts it: the snapshot replaces the
    /// session's position (the resync path — a session behind the
    /// retained floor adopts rather than merges).
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The snapshot answer.</returns>
    /// <exception cref="TerraScaleSyncV2HostException">The host refused the pull.</exception>
    public async Task<TerraScaleSyncV2SnapshotPull> PullSnapshotAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed.IsCancellationRequested, this);
        var current = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = NewRequest(HttpMethod.Post, $"{options.SessionsPath}/{current.SessionId}/snapshot");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var snapshot = await ReadAsync(response, static body => new TerraScaleSyncV2SnapshotPull(
            CodecOf(body.RootElement.GetProperty("payloadCodec").GetString()!),
            Convert.FromBase64String(body.RootElement.GetProperty("payload").GetString()!),
            TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(body.RootElement.GetProperty("checkpoint").GetString()!))),
            cancellationToken).ConfigureAwait(false);
        State.AdoptSnapshot(snapshot.Codec, snapshot.Payload, snapshot.Checkpoint);
        return snapshot;
    }

    /// <summary>
    /// Acknowledges the session's durable checkpoint: retransmission is
    /// bounded by the acknowledged position.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The acknowledgement task.</returns>
    /// <exception cref="TerraScaleSyncV2HostException">The host refused the acknowledgement.</exception>
    public async Task AcknowledgeCheckpointAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed.IsCancellationRequested, this);
        var current = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = NewRequest(HttpMethod.Post, $"{options.SessionsPath}/{current.SessionId}/ack");
        request.Content = JsonContent(
            new SyncV2AckRequest { Checkpoint = Convert.ToBase64String(State.Checkpoint.ToBytes()) });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        _ = await ReadAsync(response, static body => body.RootElement.GetProperty("sessionId").GetUInt64(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Submits one server-ack-only mutation: the typed write plus the
    /// caller's idempotency key (the at-least-once retry identity — a
    /// retried key answers the recorded receipt). Nothing applies
    /// locally; the change reaches the session's state through the
    /// delivery path.
    /// </summary>
    /// <param name="document">The target document.</param>
    /// <param name="write">The typed write.</param>
    /// <param name="idempotencyKey">The stable retry identity of this mutation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The applied receipt or the typed refusal.</returns>
    /// <exception cref="TerraScaleSyncV2HostException">The host refused the request itself.</exception>
    public async Task<TerraScaleSyncV2MutationOutcome> MutateAsync(
        string document,
        TerraScaleSyncV2DocumentWrite write,
        string idempotencyKey,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed.IsCancellationRequested, this);
        ArgumentNullException.ThrowIfNull(write);
        _ = new TerraScaleSyncV2IdempotencyKey(idempotencyKey);
        var current = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = NewRequest(HttpMethod.Post, $"{options.SessionsPath}/{current.SessionId}/mutations");
        request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        request.Content = JsonContent(new SyncV2MutationRequest
        {
            Document = document,
            Change = write is TerraScaleSyncV2DocumentWrite.Put put
                ? new SyncV2ChangeRequest
                {
                    Put = put.Body.OrderBy(field => field.Key, Utf8OrdinalComparer.Instance)
                        .Select(field => new SyncV2FieldRequest { Name = field.Key, Value = field.Value })
                        .ToArray(),
                }
                : new SyncV2ChangeRequest { Delete = true },
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var outcome = await ReadAsync(response, static body =>
        {
            var element = body.RootElement.GetProperty("outcome");
            return element.GetProperty("outcome").GetString() switch
            {
                "applied" => (TerraScaleSyncV2MutationOutcome)new TerraScaleSyncV2MutationApplied(
                    element.GetProperty("receipt").GetProperty("sequence").GetUInt64(),
                    element.GetProperty("receipt").GetProperty("operationId").GetString()!),
                "rejected" => new TerraScaleSyncV2MutationRefusal(element.GetProperty("reason").GetString()!),
                var other => throw new TerraScaleSyncV2HostException(
                    HttpStatusCode.InternalServerError,
                    "host-failure",
                    $"unknown mutation outcome {other}"),
            };
        }, cancellationToken).ConfigureAwait(false);
        State.CountMutation(outcome);
        return outcome;
    }

    /// <summary>
    /// Closes the session on the host. The session is unusable afterwards.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The close task.</returns>
    public async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed.IsCancellationRequested, this);
        var current = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
        using var request = NewRequest(HttpMethod.Post, $"{options.SessionsPath}/{current.SessionId}/close");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        _ = await ReadAsync(response, static body => body.RootElement.GetProperty("sessionId").GetUInt64(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The trigger-driven live loop: opens the session (adopting the
    /// server snapshot when fresh), subscribes to the stream, and answers
    /// every content-free trigger by pulling delta batches from the
    /// checkpoint until current, acknowledging as it goes — push the
    /// hint, pull the delta; there is no polling. A dropped stream
    /// reconnects with the last observed event id; a transiently refused
    /// stream open (an engine blip) reconnects the same way; an ended
    /// session is re-opened by resuming from the durable checkpoint,
    /// bounded to <see cref="MaxSessionReopens"/> backed-off attempts
    /// before the typed failure surfaces; a resync demand adopts a full
    /// snapshot. The loop runs only while the caller enumerates it.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The live updates.</returns>
    public async IAsyncEnumerable<TerraScaleSyncV2SyncUpdate> UpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, disposed.Token);
        var fresh = accepted is null;
        if (fresh)
        {
            _ = await SetupAsync(linked.Token).ConfigureAwait(false);
        }

        if (fresh && options.Resume is null)
        {
            var snapshot = await PullSnapshotAsync(linked.Token).ConfigureAwait(false);
            await AcknowledgeCheckpointAsync(linked.Token).ConfigureAwait(false);
            yield return new TerraScaleSyncV2SyncUpdate(
                TerraScaleSyncV2SyncUpdateKind.SnapshotAdopted,
                snapshot.Checkpoint,
                snapshot.Codec,
                snapshot.Payload,
                Commits: 0);
        }
        else if (fresh)
        {
            foreach (var update in await PullUntilCurrentAsync(linked.Token).ConfigureAwait(false))
            {
                yield return update;
            }
        }

        while (!linked.IsCancellationRequested)
        {
            // The stream reader ends (instead of throwing) on a dropped
            // connection; a drop is answered by the reconnect below.
            await foreach (var (id, name, data) in ReadStreamEventsAsync(linked.Token).ConfigureAwait(false))
            {
                if (id is { } eventId)
                {
                    lastEventId = eventId;
                }

                if (name != "trigger")
                {
                    continue;
                }

                try
                {
                    var trigger = JsonSerializer.Deserialize(data, SyncV2JsonContext.Default.SyncV2TriggerEvent);
                    if (trigger is not null)
                    {
                        State.ObserveTrigger(trigger.Collection, trigger.Sequence, trigger.OperationId);
                    }
                }
                catch (JsonException)
                {
                    // A malformed hint is content-free noise: count nothing
                    // and still run the pull — the pull is authoritative.
                }

                foreach (var update in await PullWithRecoveryAsync(linked.Token).ConfigureAwait(false))
                {
                    yield return update;
                }
            }

            if (linked.IsCancellationRequested)
            {
                yield break;
            }

            reconnectDelay = reconnectDelay == TimeSpan.Zero
                ? options.ReconnectDelay
                : TimeSpan.FromTicks(Math.Min(reconnectDelay.Ticks * 2, options.MaximumReconnectDelay.Ticks));
            try
            {
                await Task.Delay(reconnectDelay, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
        }
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        try
        {
            disposed.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed: disposal is idempotent.
        }

        disposed.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task<TerraScaleSyncV2SessionAccepted> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        if (accepted is { } current)
        {
            return current;
        }

        return await SetupAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<TerraScaleSyncV2SyncUpdate>> PullWithRecoveryAsync(CancellationToken cancellationToken)
    {
        var attempts = 0;
        var reopens = 0;
        while (true)
        {
            try
            {
                var updates = await PullUntilCurrentAsync(cancellationToken).ConfigureAwait(false);
                reopens = 0;
                return updates;
            }
            catch (TerraScaleSyncV2HostException failure) when (failure.Retryable && attempts < 3)
            {
                // A transient refusal (an unavailable engine): retry the
                // pull a bounded number of times before surfacing.
                attempts++;
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
                continue;
            }
            catch (TerraScaleSyncV2HostException failure)
            {
                if (failure.StatusCode == HttpStatusCode.Gone)
                {
                    if (reopens >= MaxSessionReopens)
                    {
                        // A host that ends every re-opened session is given
                        // up on: the typed failure surfaces instead of an
                        // unbounded re-open loop.
                        throw;
                    }

                    // The session ended with the host's generation: re-open
                    // it by resuming from the durable checkpoint, paying the
                    // reconnect backoff between attempts.
                    reopens++;
                    reconnectDelay = reconnectDelay == TimeSpan.Zero
                        ? options.ReconnectDelay
                        : TimeSpan.FromTicks(Math.Min(reconnectDelay.Ticks * 2, options.MaximumReconnectDelay.Ticks));
                    await Task.Delay(reconnectDelay, cancellationToken).ConfigureAwait(false);
                    accepted = null;
                    _ = await SetupAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (failure.StatusCode == HttpStatusCode.Conflict && failure.ResyncRequired)
                {
                    var snapshot = await PullSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    await AcknowledgeCheckpointAsync(cancellationToken).ConfigureAwait(false);
                    return
                    [
                        new TerraScaleSyncV2SyncUpdate(
                            TerraScaleSyncV2SyncUpdateKind.ResyncAdopted,
                            snapshot.Checkpoint,
                            snapshot.Codec,
                            snapshot.Payload,
                            Commits: 0),
                    ];
                }

                throw;
            }
        }
    }

    private async Task<List<TerraScaleSyncV2SyncUpdate>> PullUntilCurrentAsync(CancellationToken cancellationToken)
    {
        var updates = new List<TerraScaleSyncV2SyncUpdate>();
        while (true)
        {
            var before = State.Checkpoint;
            var answer = await PullDeltaAsync(cancellationToken).ConfigureAwait(false);
            switch (answer.Kind)
            {
                case TerraScaleSyncV2DeltaPullKind.UpToDate:
                    return updates;
                case TerraScaleSyncV2DeltaPullKind.ResyncRequired:
                    var snapshot = await PullSnapshotAsync(cancellationToken).ConfigureAwait(false);
                    await AcknowledgeCheckpointAsync(cancellationToken).ConfigureAwait(false);
                    updates.Add(new TerraScaleSyncV2SyncUpdate(
                        TerraScaleSyncV2SyncUpdateKind.ResyncAdopted,
                        snapshot.Checkpoint,
                        snapshot.Codec,
                        snapshot.Payload,
                        Commits: 0));
                    return updates;
                default:
                    await AcknowledgeCheckpointAsync(cancellationToken).ConfigureAwait(false);
                    updates.Add(new TerraScaleSyncV2SyncUpdate(
                        State.Checkpoint.CompareTo(before) > 0
                            ? TerraScaleSyncV2SyncUpdateKind.DeltaApplied
                            : TerraScaleSyncV2SyncUpdateKind.DuplicateDelta,
                        answer.Next!,
                        answer.Codec,
                        answer.Payload,
                        answer.Commits));
                    break;
            }
        }
    }

    private async Task<TerraScaleSyncV2SessionAccepted> PostSetupAsync(TerraScaleSyncV2Checkpoint? resume, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, options.SessionsPath);
        request.Content = JsonContent(new SyncV2SetupRequest
        {
            Collection = options.Collection,
            Resume = resume is null ? null : Convert.ToBase64String(resume.ToBytes()),
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, static body => new TerraScaleSyncV2SessionAccepted(
            body.RootElement.GetProperty("sessionId").GetUInt64(),
            TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(body.RootElement.GetProperty("checkpoint").GetString()!)),
            TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(body.RootElement.GetProperty("retainedFloor").GetString()!))),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TerraScaleSyncV2DeltaPull> PostDeltaAsync(TerraScaleSyncV2SessionAccepted current, TerraScaleSyncV2Checkpoint from, CancellationToken cancellationToken)
    {
        using var request = NewRequest(HttpMethod.Post, $"{options.SessionsPath}/{current.SessionId}/delta");
        request.Content = JsonContent(new SyncV2DeltaRequest
        {
            From = Convert.ToBase64String(from.ToBytes()),
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var failure = await HostFailureAsync(response, cancellationToken).ConfigureAwait(false);
            if (failure.Code == "checkpoint-refused" && failure.ResyncRequired)
            {
                // The host states its retained floor in the refusal body;
                // carry it into the pull (the documented fallback when the
                // host sent none is the initial checkpoint).
                return new TerraScaleSyncV2DeltaPull(
                    TerraScaleSyncV2DeltaPullKind.ResyncRequired,
                    TerraScaleSyncV2PayloadCodec.LoroDelta1,
                    [],
                    null,
                    0,
                    false,
                    failure.Floor ?? TerraScaleSyncV2Checkpoint.Initial);
            }

            throw failure;
        }

        return await ReadAsync(response, static body =>
        {
            var root = body.RootElement;
            var commits = root.GetProperty("commits").GetUInt64();
            var next = TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(root.GetProperty("next").GetString()!));
            var payload = Convert.FromBase64String(root.GetProperty("payload").GetString()!);
            return new TerraScaleSyncV2DeltaPull(
                commits == 0 ? TerraScaleSyncV2DeltaPullKind.UpToDate : TerraScaleSyncV2DeltaPullKind.Delta,
                CodecOf(root.GetProperty("payloadCodec").GetString()!),
                payload,
                next,
                commits,
                root.GetProperty("singleCommitOversize").GetBoolean(),
                null);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<(ulong? Id, string Name, string Data)> ReadStreamEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = NewRequest(
            HttpMethod.Get,
            $"{options.StreamsPath}?collections={Uri.EscapeDataString(options.Collection)}");
        request.Headers.Accept.Add(new("text/event-stream"));
        if (lastEventId != 0)
        {
            request.Headers.TryAddWithoutValidation(
                "Last-Event-ID",
                lastEventId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        HttpResponseMessage? response = null;
        Stream? stream = null;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await HostFailureAsync(response, cancellationToken).ConfigureAwait(false);
                response.Dispose();
                throw failure;
            }

            stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or SocketException
            or (TerraScaleSyncV2HostException { Retryable: true }))
        {
            // The connection did not open, or the host refused it
            // transiently (an engine blip answers 503/502/504/429): the
            // stream ends and the live loop reconnects. Non-retryable
            // refusals keep propagating, and the response is disposed on
            // every exit.
            response?.Dispose();
            yield break;
        }

        reconnectDelay = TimeSpan.Zero;
        var maxEvent = options.MaxStreamEventBytes;
        await using (stream!)
        using (response!)
        using (var reader = new StreamReader(stream!, new UTF8Encoding(false)))
        {
        var lines = new SyncV2BoundedLineReader(reader);
        ulong? eventId = null;
        var eventName = "message";
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            try
            {
                line = await lines.ReadLineAsync(maxEvent, cancellationToken).ConfigureAwait(false);
            }
            catch (SyncV2OversizeEventException)
            {
                // A field line past the event bound: bounded reading ends
                // the stream; the live loop reconnects.
                yield break;
            }
            catch (Exception failure) when (failure is HttpRequestException or IOException or SocketException or ObjectDisposedException)
            {
                // The connection dropped: the stream ends and the live loop reconnects.
                yield break;
            }

            if (line is null)
            {
                yield break;
            }

            if (line.Length == 0)
            {
                if (data.Length > 0)
                {
                    yield return (eventId, eventName, data.ToString());
                }

                eventId = null;
                eventName = "message";
                data.Clear();
                continue;
            }

            if (line.StartsWith("id:", StringComparison.Ordinal))
            {
                if (ulong.TryParse(FieldValue(line, 3).Trim(), out var parsed))
                {
                    eventId = parsed;
                }
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = FieldValue(line, 6);
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                {
                    _ = data.Append('\n');
                }

                _ = data.Append(FieldValue(line, 5));
                if (data.Length > maxEvent)
                {
                    // The accumulated event is past the bound: end the
                    // stream instead of buffering it without limit.
                    yield break;
                }
            }
        }
        }
    }

    /// <summary>
    /// Reads newline-terminated lines (an optional trailing carriage
    /// return is stripped) with a hard per-line bound: a line that grows
    /// past the bound throws instead of buffering without limit. Chars
    /// read past the first line's terminator are retained for the next
    /// line, so one chunked read never loses the events it carried.
    /// </summary>
    private sealed class SyncV2BoundedLineReader
    {
        private readonly StreamReader reader;
        private string pending = string.Empty;

        /// <summary>Initializes the reader.</summary>
        /// <param name="reader">The decoded stream reader.</param>
        public SyncV2BoundedLineReader(StreamReader reader)
        {
            this.reader = reader;
        }

        /// <summary>Reads the next line, or <c>null</c> at the end of the stream.</summary>
        /// <param name="maxChars">The hard per-line bound in characters.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The line, or <c>null</c>.</returns>
        public async Task<string?> ReadLineAsync(int maxChars, CancellationToken cancellationToken)
        {
            while (true)
            {
                var terminator = this.pending.IndexOf('\n');
                if (terminator >= 0)
                {
                    var line = this.pending[..terminator];
                    this.pending = this.pending[(terminator + 1)..];
                    return line.EndsWith('\r') ? line[..^1] : line;
                }

                var buffer = new char[256];
                var read = await this.reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    var rest = this.pending;
                    this.pending = string.Empty;
                    return rest.Length == 0 ? null : rest;
                }

                this.pending += new string(buffer, 0, read);
                if (this.pending.IndexOf('\n') < 0 && this.pending.Length > maxChars)
                {
                    throw new SyncV2OversizeEventException();
                }
            }
        }
    }

    /// <summary>The SSE field value: the text after the field name, with at most one leading space stripped.</summary>
    /// <param name="line">The field line.</param>
    /// <param name="prefixLength">The field name's length (including the colon).</param>
    /// <returns>The field value.</returns>
    private static string FieldValue(string line, int prefixLength) =>
        line.Length > prefixLength && line[prefixLength] == ' '
            ? line[(prefixLength + 1)..]
            : line[prefixLength..];

    private HttpRequestMessage NewRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("X-TB-Principal", clientOptions.Principal);
        request.Headers.TryAddWithoutValidation("X-TB-Tenant", clientOptions.Tenant);
        return request;
    }

    private static HttpContent JsonContent<T>(T value) =>
        new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(value, typeof(T), SyncV2JsonContext.Default))
        {
            Headers = { ContentType = new("application/json") { CharSet = "utf-8" } },
        };

    private async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        Func<JsonDocument, T> select,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await HostFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var bytes = await ReadBodyAsync(response, options.MaxHostBodyBytes, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(bytes);
        return select(document);
    }

    /// <summary>
    /// Reads a host response body with a hard bound: the declared length
    /// is checked first and at most one byte past the bound is read, so a
    /// host body is never buffered without limit.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="cap">The bound in bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The body bytes (never longer than the bound).</returns>
    private static async Task<byte[]> ReadBodyAsync(
        HttpResponseMessage response,
        long cap,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > cap)
        {
            throw HostBodyTooLarge(response, cap);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffered = new MemoryStream((int)Math.Min(cap, 1024 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return buffered.ToArray();
            }

            if (buffered.Length + read > cap)
            {
                throw HostBodyTooLarge(response, cap);
            }

            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
    }

    private static TerraScaleSyncV2HostException HostBodyTooLarge(HttpResponseMessage response, long cap) => new(
        response.StatusCode,
        "host-failure",
        $"the response body exceeds the decode bound of {cap} bytes");

    private async Task<TerraScaleSyncV2HostException> HostFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        // Error bodies are bounded well below the full body bound: a
        // detail is diagnostic text, never a payload.
        var bytes = await ReadBodyAsync(response, Math.Min(options.MaxHostBodyBytes, 64 * 1024), cancellationToken).ConfigureAwait(false);
        var detail = Encoding.UTF8.GetString(bytes);
        string? code = null;
        string? message = null;
        var resync = false;
        TerraScaleSyncV2Checkpoint? floor = null;
        try
        {
            using var document = JsonDocument.Parse(detail);
            if (document.RootElement.TryGetProperty("code", out var codeElement))
            {
                code = codeElement.GetString();
            }

            if (document.RootElement.TryGetProperty("detail", out var detailElement))
            {
                message = detailElement.GetString();
            }

            if (document.RootElement.TryGetProperty("resyncRequired", out var resyncElement))
            {
                resync = resyncElement.GetBoolean();
            }

            if (document.RootElement.TryGetProperty("floor", out var floorElement) && floorElement.GetString() is { Length: > 0 } floorText)
            {
                try
                {
                    floor = TerraScaleSyncV2Checkpoint.FromBytes(Convert.FromBase64String(floorText));
                }
                catch (Exception failure) when (failure is FormatException or TerraScaleSyncV2ProtocolException)
                {
                    // A floor that does not decode is absent: the refusal
                    // still surfaces, without a stated floor.
                }
            }
        }
        catch (JsonException)
        {
            // Non-JSON failure bodies surface as the raw detail.
        }

        return new TerraScaleSyncV2HostException(
            response.StatusCode,
            code ?? "host-failure",
            message ?? (detail.Length == 0 ? null : detail),
            resync,
            floor);
    }

    private static TerraScaleSyncV2PayloadCodec CodecOf(string name) => name switch
    {
        "loro-delta/1" => TerraScaleSyncV2PayloadCodec.LoroDelta1,
        "loro-snapshot/1" => TerraScaleSyncV2PayloadCodec.LoroSnapshot1,
        var other => throw new TerraScaleSyncV2HostException(
            HttpStatusCode.InternalServerError,
            "host-failure",
            $"unknown payload codec {other}"),
    };
}

/// <summary>Internal JSON DTOs of the API host surface (camelCase, strict).</summary>
internal sealed record SyncV2SetupRequest
{
    public string Collection { get; init; } = "";

    public string? Resume { get; init; }
}

internal sealed record SyncV2DeltaRequest
{
    public string From { get; init; } = "";
}

internal sealed record SyncV2AckRequest
{
    public string Checkpoint { get; init; } = "";
}

internal sealed record SyncV2MutationRequest
{
    public string Document { get; init; } = "";

    public SyncV2ChangeRequest Change { get; init; } = new();
}

internal sealed record SyncV2ChangeRequest
{
    public SyncV2FieldRequest[]? Put { get; init; }

    public bool? Delete { get; init; }
}

internal sealed record SyncV2FieldRequest
{
    public string Name { get; init; } = "";

    [JsonConverter(typeof(SyncV2ValueJsonConverter))]
    public TerraScaleSyncV2Value Value { get; init; } = TerraScaleSyncV2Value.Null;
}

internal sealed class SyncV2ValueJsonConverter : JsonConverter<TerraScaleSyncV2Value>
{
    public override TerraScaleSyncV2Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("A sync value carries exactly one discriminator.");
        }

        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var discriminator = reader.GetString();
            reader.Read();
            var value = discriminator switch
            {
                "null" when reader.TokenType == JsonTokenType.True => TerraScaleSyncV2Value.Null,
                "boolean" when reader.TokenType is JsonTokenType.True or JsonTokenType.False => TerraScaleSyncV2Value.Of(reader.GetBoolean()),
                "integer" when reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var integer) => TerraScaleSyncV2Value.Of(integer),
                "number" when reader.TokenType == JsonTokenType.Number => TerraScaleSyncV2Value.Of(reader.GetDouble()),
                "string" when reader.TokenType == JsonTokenType.String => TerraScaleSyncV2Value.Of(reader.GetString()!),
                _ => throw new JsonException($"A sync value's discriminator '{discriminator}' does not name a scalar."),
            };

            // Consume the value object's end token.
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
            }

            return value;
        }

        throw new JsonException("A sync value carries exactly one discriminator.");
    }

    public override void Write(Utf8JsonWriter writer, TerraScaleSyncV2Value value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value.Tag)
        {
            case 0:
                writer.WriteBoolean("null", true);
                break;
            case 1:
                writer.WriteBoolean("boolean", value.Boolean);
                break;
            case 2:
                writer.WriteNumber("integer", value.Integer);
                break;
            case 3:
                writer.WriteNumber("number", value.Number);
                break;
            case 4:
                writer.WriteString("string", value.Text);
                break;
            default:
                throw new JsonException($"unknown value tag {value.Tag}");
        }

        writer.WriteEndObject();
    }
}

internal sealed record SyncV2TriggerEvent
{
    public ulong SubscriptionId { get; init; }

    public string Collection { get; init; } = "";

    public ulong Sequence { get; init; }

    public string OperationId { get; init; } = "";

    public string Kind { get; init; } = "";
}

/// <summary>One event-stream field line (or accumulated event) passed the configured event bound.</summary>
internal sealed class SyncV2OversizeEventException : Exception
{
    /// <summary>Initializes the bound marker.</summary>
    public SyncV2OversizeEventException()
        : base("the event-stream line exceeds the configured event bound")
    {
    }
}

/// <summary>Source-generated JSON metadata of the API host surface (camelCase).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SyncV2SetupRequest))]
[JsonSerializable(typeof(SyncV2DeltaRequest))]
[JsonSerializable(typeof(SyncV2AckRequest))]
[JsonSerializable(typeof(SyncV2MutationRequest))]
[JsonSerializable(typeof(SyncV2TriggerEvent))]
internal sealed partial class SyncV2JsonContext : JsonSerializerContext;
