using System.Buffers.Text;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using static TerraScale.DotNet.TerraBase.TerraBaseShapeProtocol;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// Client of TerraBase shape reads protocol v1 (<c>…/collections/{collection}/shape</c>) over a caller-owned
/// <see cref="HttpClient"/> that it never disposes. Thread-safe; each subscription owns its own state and does no work
/// outside its enumeration. Request deadlines (30 seconds), the 8 MiB response bound and retries are fixed.
/// </summary>
public sealed partial class TerraBaseShapeClient
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxRequestLine = 8 * 1024 - 64;
    private const int MaxRetries = 6;
    private const string ResumeHeader = "TB-Shape-Resume";
    private static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly ImmutableDictionary<string, TerraBaseShapeDocument> NoDocuments = ImmutableDictionary.Create<string, TerraBaseShapeDocument>(StringComparer.Ordinal);

    private readonly TerraBaseConnection connection;
    private readonly TimeProvider timeProvider;

    /// <summary>
    /// Creates a client and validates <paramref name="options"/>. The caller-owned HttpClient must disable automatic
    /// redirects; use <see cref="TerraBaseHttp.CreateHandler"/>. Redirects are followed only to the same origin.
    /// </summary>
    public TerraBaseShapeClient(HttpClient httpClient, TerraBaseShapeClientOptions options)
        : this(httpClient, options, TimeProvider.System)
    {
    }

    internal TerraBaseShapeClient(HttpClient httpClient, TerraBaseShapeClientOptions options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        connection = new TerraBaseConnection(httpClient, options.BaseAddress, options.EndpointResolver, options.ApiKey, nameof(options.ApiKey), TerraBaseConnection.RequestDeadline, MaxResponseBytes,
            options.AccessTokenProvider, timeProvider: timeProvider);
        this.timeProvider = timeProvider;
    }

    /// <summary>
    /// Syncs one collection, optionally filtered, and yields the complete local state after each applied response:
    /// snapshot pages, then log chunks, then live long-polls, forever until cancelled. Each response is validated
    /// whole before it is applied; an invalid response throws <see cref="TerraBaseException"/> (<c>invalid_response</c>)
    /// and applies nothing. Repeated events are applied, never deduplicated. <c>409 must_refetch</c> restarts from an
    /// empty state, except for one resume attempt that keeps it after a lineage change; any other 409 is thrown. <c>429 shape_busy</c>,
    /// <c>503 unavailable</c> and <c>503 shape_too_large</c> are retried with bounded backoff, honoring Retry-After; a Retry-After over 60 seconds is thrown instead.
    /// </summary>
    /// <param name="projectId">Project id.</param>
    /// <param name="databaseName">Database name.</param>
    /// <param name="collection">Collection name.</param>
    /// <param name="where">Optional query v1 JSON filter, sent as written; for example <c>{"eq":["/read",false]}</c>.</param>
    /// <param name="cancellationToken">Stops the subscription, its active request and any retry wait.</param>
    /// <exception cref="ArgumentException">A name is empty or <paramref name="where"/> is outside the filter grammar or limits.</exception>
    public IAsyncEnumerable<TerraBaseShapeState> SubscribeAsync(string projectId, string databaseName, string collection, string? where = null,
        CancellationToken cancellationToken = default)
    {
        var path = ShapePath(projectId, databaseName, collection);
        if (where is not null) ValidateWhere(where);
        return SubscribeCoreAsync(path, where, cancellationToken);
    }

    /// <summary>
    /// Fetches the epoch key <c>K_e</c> of a handle for TBS1 objects (<c>shape/keys</c>, <c>no-store</c>). The returned
    /// 32 bytes are a secret: keep them only in memory. Unretained pairs throw <see cref="TerraBaseConflictException"/>.
    /// </summary>
    /// <param name="projectId">Project id.</param>
    /// <param name="databaseName">Database name.</param>
    /// <param name="collection">Collection name.</param>
    /// <param name="handle">The shape handle (<c>h1-…</c>).</param>
    /// <param name="epoch">The object's epoch <c>E</c>.</param>
    /// <param name="keyId">The object's 26-character data key id.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<byte[]> GetEpochKeyAsync(string projectId, string databaseName, string collection, string handle, ulong epoch, string keyId,
        CancellationToken cancellationToken = default)
    {
        var path = ShapePath(projectId, databaseName, collection) + "/keys";
        if (!IsValidHandle(handle)) throw new ArgumentException("Handle must be a shape handle: h1- and 16 lowercase hexadecimal digits.", nameof(handle));
        if (epoch > long.MaxValue) throw new ArgumentOutOfRangeException(nameof(epoch), "Epoch must be at most 9223372036854775807.");
        if (keyId is not { Length: 26 } || !keyId.All(char.IsAsciiLetterOrDigit)) throw new ArgumentException("KeyId must be 26 ASCII letters or digits.", nameof(keyId));
        var epochText = epoch.ToString(CultureInfo.InvariantCulture);
        return connection.ExchangeAsync(path + TerraBaseConnection.Query(("handle", handle), ("epoch", epochText), ("key_id", keyId)), null,
            headers => headers.CacheControl = new CacheControlHeaderValue { NoStore = true }, async opened =>
            {
                var response = opened.Response;
                if (response.StatusCode != HttpStatusCode.OK)
                    throw (int)response.StatusCode >= 400 ? await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false) : Invalid(response.StatusCode, "unexpected status");
                if (response.Headers.CacheControl is not { NoStore: true }) throw Invalid(response.StatusCode, "key response is cacheable");
                if (!IsJson(response)) throw Invalid(response.StatusCode, "not JSON");
                var key = Deserialize(await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false), TerraBaseShapeJsonContext.Default.ShapeEpochKey);
                return key.Handle == handle && key.Epoch == epochText && key.KeyId == keyId && TryDecodeKey(key.Key, out var bytes)
                    ? bytes
                    : throw Invalid(response.StatusCode, "key does not match the request");
            }, cancellationToken);
    }

    private async IAsyncEnumerable<TerraBaseShapeState> SubscribeCoreAsync(string path, string? where, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var documents = NoDocuments;
        string? handle = null, shapeId = null, usableCursor = null, liveCursor = null, resumeToken = null;
        var offset = "-1";
        Uri? followUp = null;
        bool live = false, resuming = false, resumeInHeader = false, refetchedWithoutProgress = false;
        var failures = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target;
            Uri? origin = null;
            var sendResumeHeader = false;
            if (followUp is not null)
            {
                // The single 307 follow-up goes to the validated Location; HTTP clients keep custom headers on it.
                origin = new Uri(followUp.GetLeftPart(UriPartial.Authority));
                target = followUp.PathAndQuery;
                sendResumeHeader = resumeInHeader;
            }
            else if (handle is null)
            {
                target = path + TerraBaseConnection.Query(("where", where), ("offset", "-1"), ("cursor", resumeToken));
                resumeInHeader = resumeToken is not null && "GET ".Length + target.Length + " HTTP/1.1".Length > MaxRequestLine;
                if (resumeInHeader) target = path + TerraBaseConnection.Query(("where", where), ("offset", "-1"));
                sendResumeHeader = resumeInHeader;
            }
            else
            {
                target = path + TerraBaseConnection.Query(("where", where), ("handle", handle), ("offset", offset), ("live", live ? "true" : null), ("cursor", live ? liveCursor : null));
            }

            var requestLive = followUp is null && handle is not null && live;
            var token = resumeToken;
            var reply = await connection.ExchangeAsync(target, origin, sendResumeHeader ? headers => headers.TryAddWithoutValidation(ResumeHeader, token) : null,
                ReadReplyAsync, cancellationToken).ConfigureAwait(false);

            if (reply.Status == HttpStatusCode.OK)
            {
                if (handle is null) throw Invalid(reply.Status, "bootstrap must redirect");
                var body = ReadBody(reply, handle, offset, requestLive, shapeId);
                var next = documents.ToBuilder();
                foreach (var operation in body.Changes.SelectMany(change => change.Ops))
                {
                    if (operation.Op == "upsert") next[operation.Id] = new TerraBaseShapeDocument(operation.Version!, operation.Document!.Value);
                    else next.Remove(operation.Id);
                }

                documents = next.ToImmutable();
                (handle, shapeId, offset, live) = (body.Handle, body.ShapeId, body.NextOffset, body.UpToDate);
                // A snapshot cursor proves nothing until the snapshot's last page is applied.
                usableCursor = TryParseDecimal(offset, out _) ? body.CursorResume : null;
                if (requestLive) liveCursor = reply.Cursor;
                (followUp, resuming, resumeToken, failures, refetchedWithoutProgress) = (null, false, null, 0, false);
                yield return new TerraBaseShapeState { Documents = documents, Handle = handle, Offset = offset, CursorResume = usableCursor, UpToDate = body.UpToDate };
                continue;
            }

            if (reply.Status == HttpStatusCode.TemporaryRedirect)
            {
                if (handle is not null || followUp is not null) throw Invalid(reply.Status, "only a bootstrap or resume may redirect, once");
                (followUp, handle, offset) = ReadRedirect(reply, where is not null, resumeToken is not null);
                (shapeId, live) = (null, false);
                continue;
            }

            if (reply.Status == HttpStatusCode.Conflict && reply.Error?.ErrorCode == "must_refetch")
            {
                // Section 7 rule 6: one resume attempt when a continuation lands on another lineage with a proven cursor.
                if (handle is not null && followUp is null && !resuming && usableCursor is not null && IsValidHandle(reply.Handle) && reply.Handle != handle)
                {
                    (resuming, resumeToken, handle, offset, shapeId, live) = (true, usableCursor, null, "-1", null, false);
                    continue;
                }

                if (refetchedWithoutProgress) await RetryDelayAsync(++failures, reply.Error!, cancellationToken).ConfigureAwait(false);
                refetchedWithoutProgress = true;
                (documents, handle, offset, shapeId, usableCursor, liveCursor, resumeToken, resuming, followUp, live) =
                    (NoDocuments, null, "-1", null, null, null, null, false, null, false);
                continue;
            }

            if (reply.Error is { } error && IsRetryable(error))
            {
                await RetryDelayAsync(++failures, error, cancellationToken).ConfigureAwait(false);
                continue;
            }

            throw (int)reply.Status >= 400 && reply.Error is not null ? reply.Error : Invalid(reply.Status, "unexpected status");
        }
    }

    private async Task RetryDelayAsync(int failures, TerraBaseException error, CancellationToken cancellationToken)
    {
        // A Retry-After beyond the fixed wait is surfaced instead of retrying before the server allows it.
        if (failures > MaxRetries || error.RetryAfter > MaxRetryDelay) throw error;
        var backoff = Math.Min(MaxBackoff.TotalMilliseconds, FirstBackoff.TotalMilliseconds * Math.Pow(2, failures - 1)) * (0.5 + Random.Shared.NextDouble() / 2);
        var delay = TimeSpan.FromMilliseconds(Math.Max(backoff, error.RetryAfter?.TotalMilliseconds ?? 0));
        await Task.Delay(delay, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsRetryable(TerraBaseException error) => ((int?)error.StatusCode, error.ErrorCode) is
        (429, "shape_busy") or (503, "unavailable") or (503, "auth_unavailable") or (503, "shape_too_large");

    private async Task<Reply> ReadReplyAsync(TerraBaseConnection.OpenResponse opened)
    {
        var response = opened.Response;
        byte[]? body = null;
        TerraBaseException? error = null;
        if (response.StatusCode == HttpStatusCode.OK) body = await connection.ReadBodyAsync(response, opened.Token).ConfigureAwait(false);
        else if (response.StatusCode != HttpStatusCode.TemporaryRedirect) error = await TerraBaseException.FromResponseAsync(response, opened.Token).ConfigureAwait(false);
        return new Reply(response.StatusCode, opened.RequestUri, IsJson(response), response.Headers.Location, Header(response, "TB-Shape-Handle"),
            Header(response, "TB-Shape-Next-Offset"), Header(response, "TB-Shape-Up-To-Date"), Header(response, "TB-Shape-Cursor"), body, error);
    }

    // A 307 must stay on the request's origin and route; its query is exactly [where,]handle,offset (section 3).
    private static (Uri Target, string Handle, string Offset) ReadRedirect(Reply reply, bool hasWhere, bool resume)
    {
        if (reply.Location is not { } location) throw Invalid(reply.Status, "redirect without Location");
        var target = location.IsAbsoluteUri ? location : new Uri(reply.RequestUri, location);
        if (target.UserInfo.Length != 0 || target.Fragment.Length != 0
            || !string.Equals(target.GetLeftPart(UriPartial.Authority), reply.RequestUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
            || target.AbsolutePath != reply.RequestUri.AbsolutePath)
            throw Invalid(reply.Status, "redirect leaves the shape route");
        var query = target.Query.TrimStart('?').Split('&').Select(pair => pair.Split('=')).ToArray();
        string[] names = hasWhere ? ["where", "handle", "offset"] : ["handle", "offset"];
        if (query.Length != names.Length || query.Where((pair, i) => pair is not [var name, { Length: > 0 }] || name != names[i]).Any())
            throw Invalid(reply.Status, "redirect query is not [where,]handle,offset");
        var handle = Uri.UnescapeDataString(query[^2][1]);
        var offset = Uri.UnescapeDataString(query[^1][1]);
        var validOffset = resume ? TryParseDecimal(offset, out _) : TryParseSnapshotOffset(offset, out _, out var page) && page == "0";
        return IsValidHandle(handle) && handle == reply.Handle && validOffset ? (target, handle, offset) : throw Invalid(reply.Status, "redirect target is invalid");
    }

    private static ShapeBody ReadBody(Reply reply, string handle, string offset, bool live, string? shapeId)
    {
        if (!reply.Json) throw Invalid(reply.Status, "not JSON");
        var body = Deserialize(reply.Body!, TerraBaseShapeJsonContext.Default.ShapeBody);
        return IsValidBody(body, reply, handle, offset, live, shapeId) ? body : throw Invalid(reply.Status, "body breaks the shape protocol");
    }

    private static bool IsValidBody(ShapeBody body, Reply reply, string handle, string offset, bool live, string? shapeId)
    {
        if (!IsValidShapeId(body.ShapeId) || (shapeId is not null && body.ShapeId != shapeId) || body.Handle != handle || reply.Handle != handle
            || body.Offset != offset || reply.NextOffset != body.NextOffset || reply.UpToDate != (body.UpToDate ? "true" : "false")
            || !IsBase64UrlToken(body.CursorResume) || !TryParseDecimal(body.CoveredOffset, out var covered) || !TryParseDecimal(body.ValuesAt, out var valuesAt)
            || (live && !TryParseDecimal(reply.Cursor, out _)))
            return false;
        foreach (var change in body.Changes)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (!change.Ops.All(op => op.Id.Length > 0 && ids.Add(op.Id) && op switch
            {
                { Op: "upsert", Version.Length: > 0, Document.ValueKind: JsonValueKind.Object } => true,
                { Op: "delete", Version: null, Document: null } => true,
                _ => false,
            }))
                return false;
        }

        if (TryParseSnapshotOffset(offset, out var snapshot, out _))
        {
            // Section 6: one change at S with upserts only; a page advances within S or ends at decimal S.
            return !live && covered == snapshot && valuesAt == snapshot && !body.UpToDate && body.NextOffset != offset
                && body.Changes is [{ } only] && TryParseDecimal(only.Offset, out var at) && at == snapshot && only.Ops.All(op => op.Op == "upsert")
                && ((TryParseSnapshotOffset(body.NextOffset, out var nextSnapshot, out _) && nextSnapshot == snapshot)
                    || (TryParseDecimal(body.NextOffset, out var end) && end == snapshot));
        }

        if (!TryParseDecimal(offset, out var from) || !TryParseDecimal(body.NextOffset, out var next) || next < from || covered != next || valuesAt < next) return false;
        if (body.UpToDate ? !live && (next != from || body.Changes.Count != 0) : next == from) return false;
        var previous = from;
        foreach (var change in body.Changes)
        {
            if (!TryParseDecimal(change.Offset, out var commit) || commit <= previous || commit > next) return false;
            previous = commit;
        }

        return true;
    }

    private static T Deserialize<T>(byte[] body, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
        TerraBaseConnection.ReadResponse(body, typeInfo, HttpStatusCode.OK);

    private static bool TryDecodeKey(string key, out byte[] bytes)
    {
        bytes = [];
        if (!IsBase64UrlToken(key)) return false;
        try
        {
            bytes = Base64Url.DecodeFromChars(key);
        }
        catch (FormatException)
        {
            return false;
        }

        return bytes.Length == 32 && Base64Url.EncodeToString(bytes) == key;
    }

    private static bool IsJson(HttpResponseMessage response) =>
        string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase);

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) && values.ToArray() is [var value] ? value : null;

    private static string ShapePath(string projectId, string databaseName, string collection) =>
        $"/v1/projects/{TerraBaseConnection.Segment(projectId, nameof(projectId))}/databases/{TerraBaseConnection.Segment(databaseName, nameof(databaseName))}"
        + $"/collections/{TerraBaseConnection.Segment(collection, nameof(collection))}/shape";

    private static TerraBaseException Invalid(HttpStatusCode status, string reason) =>
        new(status, $"TerraBase returned an invalid shape response: {reason}.", "invalid_response", null);

    private sealed record Reply(HttpStatusCode Status, Uri RequestUri, bool Json, Uri? Location, string? Handle, string? NextOffset, string? UpToDate, string? Cursor,
        byte[]? Body, TerraBaseException? Error);
}
