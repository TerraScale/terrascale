using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

#pragma warning disable CS1591

namespace TerraScale.DotNet;

/// <summary>The CDN-first HTTP transport for an authorized reactive view.</summary>
public sealed class TerraScaleReactiveHttpClient
{
    internal const int MaxMessageBytes = 8 * 1024 * 1024;
    internal const int MaxStringBytes = 262_144;
    internal const int MaxBinaryBytes = 1_048_576;
    internal const int MaxArrayItems = 10_000;

    private readonly HttpClient httpClient;
    private readonly TerraScaleReactiveHttpClientOptions options;

    /// <summary>Creates a reactive HTTP client over a caller-owned <see cref="HttpClient"/>.</summary>
    public TerraScaleReactiveHttpClient(HttpClient httpClient, TerraScaleReactiveHttpClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
        this.options = (options ?? new TerraScaleReactiveHttpClientOptions()) with { };
        this.options.Validate();
        this.httpClient.BaseAddress ??= this.options.BaseAddress;
    }

    /// <summary>
    /// Attaches to a shared authorized view, downloads its immutable snapshot,
    /// and returns a subscription positioned at the attach cut.
    /// </summary>
    public async Task<TerraScaleReactiveSubscription> SubscribeAsync(
        string query,
        string scope,
        long entitlementEpoch = 0,
        TerraScaleViewCheckpoint? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        ValidateText(query, nameof(query), MaxStringBytes);
        ValidateText(scope, nameof(scope), 256);
        if (entitlementEpoch < 0) throw new ArgumentOutOfRangeException(nameof(entitlementEpoch));

        var requestBody = new ReactiveAttachRequest(query, scope, entitlementEpoch, checkpoint?.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve("/api/v1/reactive/views"))
        {
            Content = JsonContent.Create(requestBody, ReactiveJsonContext.Default.ReactiveAttachRequest),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await SendWithRetryAsync(request, retryPost: false, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        var attach = await response.Content.ReadFromJsonAsync(
            ReactiveJsonContext.Default.ReactiveAttachResponse,
            cancellationToken).ConfigureAwait(false)
            ?? throw new TerraScaleReactiveProtocolException("The reactive attach response was empty.");

        var attachment = CreateAttachment(attach);
        var snapshot = await DownloadSnapshotAsync(attachment, cancellationToken).ConfigureAwait(false);
        return new TerraScaleReactiveSubscription(this, attachment, snapshot, query, scope, entitlementEpoch);
    }

    /// <summary>Alias for <see cref="SubscribeAsync"/> for callers that prefer transport terminology.</summary>
    public Task<TerraScaleReactiveSubscription> AttachAsync(
        string query,
        string scope,
        long entitlementEpoch = 0,
        TerraScaleViewCheckpoint? checkpoint = null,
        CancellationToken cancellationToken = default) =>
        SubscribeAsync(query, scope, entitlementEpoch, checkpoint, cancellationToken);

    internal async Task<TerraScaleReactivePollResult?> PollAsync(
        TerraScaleReactiveSubscription subscription,
        CancellationToken cancellationToken)
    {
        var uri = AppendOffset(subscription.Attachment.Log, subscription.Offset);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/msgpack"));
        request.Headers.TryAddWithoutValidation(subscription.Attachment.CapabilityHeader, subscription.Attachment.Capability);
        await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);

        using var response = await SendWithRetryAsync(request, retryPost: false, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotModified) return null;
        if (response.StatusCode == HttpStatusCode.Gone || response.StatusCode == HttpStatusCode.Conflict)
        {
            var reason = response.StatusCode == HttpStatusCode.Gone
                ? TerraScaleReactiveResetReason.HistoryExpired
                : TerraScaleReactiveResetReason.Reconnect;
            var fresh = await ReattachAsync(subscription, reason, cancellationToken).ConfigureAwait(false);
            return fresh;
        }
        if (!response.IsSuccessStatusCode) throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);

        var bytes = await ReadBoundedBytesAsync(response.Content, options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0 || IsNoStore(response)) return null;
        var etag = response.Headers.ETag?.Tag;
        var frame = DecodeFrame(bytes, subscription.Attachment, subscription.Offset);
        if (frame is null) return null;
        if (etag is not null) subscription.RememberEtag(subscription.Offset, etag);
        return subscription.Accept(frame, etag);
    }

    private async Task<TerraScaleReactivePollResult> ReattachAsync(
        TerraScaleReactiveSubscription subscription,
        TerraScaleReactiveResetReason reason,
        CancellationToken cancellationToken)
    {
        var replacement = await SubscribeAsync(
            subscription.Query,
            subscription.Scope,
            subscription.EntitlementEpoch,
            checkpoint: null,
            cancellationToken).ConfigureAwait(false);
        subscription.Replace(replacement.Attachment, replacement.CurrentSnapshot);
        return TerraScaleReactivePollResult.Reset(reason, replacement.CurrentSnapshot);
    }

    private async Task<TerraScaleReactiveSnapshot> DownloadSnapshotAsync(
        TerraScaleReactiveAttachment attachment,
        CancellationToken cancellationToken)
    {
        var rows = new List<TerraScaleReactiveItem>();
        ulong revision = attachment.Offset;
        string? digest = null;
        foreach (var chunk in attachment.SnapshotChunks)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, chunk);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/msgpack"));
            request.Headers.TryAddWithoutValidation(attachment.CapabilityHeader, attachment.Capability);
            await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            using var response = await SendWithRetryAsync(request, retryPost: false, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedBytesAsync(response.Content, options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (response.Headers.ETag is null) throw new TerraScaleReactiveProtocolException("An immutable snapshot response must carry an ETag.");
            var snapshot = DecodeSnapshot(bytes, attachment);
            if (snapshot is null) continue;
            revision = snapshot.Revision;
            digest = snapshot.Digest;
            rows.AddRange(snapshot.Items);
            if (rows.Count > options.MaxItems) throw new TerraScaleReactiveProtocolException("The snapshot exceeds the configured item bound.");
        }

        var checkpoint = attachment.Checkpoint;
        return new TerraScaleReactiveSnapshot(
            attachment.Generation,
            authorizationGeneration: 0,
            revision,
            rows,
            digest,
            checkpoint);
    }

    private TerraScaleReactiveAttachment CreateAttachment(ReactiveAttachResponse response)
    {
        ValidateText(response.View, nameof(response.View), 256);
        if (response.Generation < 1 || response.Offset < 0) throw new TerraScaleReactiveProtocolException("The attach response has an invalid view generation or offset.");
        if (string.IsNullOrWhiteSpace(response.Capability)) throw new TerraScaleReactiveProtocolException("The attach response did not carry a view capability.");
        var header = string.IsNullOrWhiteSpace(response.CapabilityHeader) ? "X-TerraScale-Capability" : response.CapabilityHeader;
        ValidateText(header, nameof(response.CapabilityHeader), 128);
        var chunks = response.SnapshotChunks?.Select(Resolve).ToArray() ?? [];
        if (chunks.Length == 0 || string.IsNullOrWhiteSpace(response.Log) || string.IsNullOrWhiteSpace(response.Checkpoint))
            throw new TerraScaleReactiveProtocolException("The attach response must carry snapshot chunks, a log URL, and a checkpoint.");
        return new TerraScaleReactiveAttachment(
            response.View,
            checked((ulong)response.Generation),
            chunks,
            Resolve(response.Log),
            new TerraScaleViewCheckpoint(response.Checkpoint),
            header,
            response.Capability,
            checked((ulong)response.Offset));
    }

    private Uri Resolve(string pathOrUrl) => Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute)
        ? absolute
        : new Uri(options.BaseAddress, pathOrUrl);

    private static Uri AppendOffset(Uri log, ulong offset)
    {
        var builder = new UriBuilder(log);
        var query = builder.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !part.StartsWith("offset=", StringComparison.OrdinalIgnoreCase))
            .ToList();
        query.Add("offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Query = string.Join('&', query);
        return builder.Uri;
    }

    private async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-TerraScale-Auth-Key", options.ApiKey.Trim());
            return;
        }
        var bearer = options.BearerToken;
        if (string.IsNullOrWhiteSpace(bearer) && options.BearerTokenProvider is { } provider)
            bearer = await provider(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Trim());
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, bool retryPost, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            using var copy = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await httpClient.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) when (attempt < options.MaxRetryAttempts && (!request.Method.Equals(HttpMethod.Post) || retryPost))
            {
                await DelayAsync(null, attempt++, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (!IsTransient(response.StatusCode) || attempt >= options.MaxRetryAttempts || (request.Method == HttpMethod.Post && !retryPost)) return response;
            var delay = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            await DelayAsync(delay, attempt++, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
    {
        var copy = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (source.Content is not null) copy.Content = new ByteArrayContent(await source.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        return copy;
    }

    private async Task DelayAsync(TimeSpan? retryAfter, int attempt, CancellationToken cancellationToken)
    {
        var delay = retryAfter.GetValueOrDefault();
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or (HttpStatusCode)425 or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;
    private static bool IsNoStore(HttpResponseMessage response) => response.Headers.CacheControl?.NoStore == true || response.Headers.TryGetValues("Cloudflare-CDN-Cache-Control", out var values) && values.Any(v => string.Equals(v, "no-store", StringComparison.OrdinalIgnoreCase));

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > limit) throw new TerraScaleReactiveProtocolException("The reactive response exceeds the configured byte bound.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var result = new MemoryStream(Math.Min(limit, 16 * 1024));
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (result.Length + read > limit) throw new TerraScaleReactiveProtocolException("The reactive response exceeds the configured byte bound.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    private static TerraScaleReactiveFrame? DecodeFrame(byte[] bytes, TerraScaleReactiveAttachment attachment, ulong offset)
    {
        if (bytes.Length == 0) return null;
        var reader = new ReactiveMsgpackReader(bytes);
        if (reader.PeekIsMap) return DecodeLegacyLog(ref reader, attachment, offset);
        var envelope = ReadEnvelope(ref reader);
        reader.End();
        return ConvertMessage(envelope, attachment);
    }

    private static TerraScaleReactiveSnapshot? DecodeSnapshot(byte[] bytes, TerraScaleReactiveAttachment attachment)
    {
        if (bytes.Length == 0) return null;
        var reader = new ReactiveMsgpackReader(bytes);
        if (reader.PeekIsMap) return DecodeLegacySnapshot(ref reader, attachment);
        var envelope = ReadEnvelope(ref reader);
        reader.End();
        return envelope.Message is ReactiveWireSnapshot wire ? ConvertSnapshot(wire.Snapshot, attachment) : throw new TerraScaleReactiveProtocolException("The snapshot response did not carry a snapshot frame.");
    }

    private static TerraScaleReactiveFrame DecodeLegacyLog(ref ReactiveMsgpackReader reader, TerraScaleReactiveAttachment attachment, ulong offset)
    {
        var map = reader.Map();
        ulong revision = 0;
        var deltas = new List<TerraScaleReactiveItem>();
        for (var i = 0; i < map; i++)
        {
            var key = reader.Text();
            if (key == "revision") revision = reader.U64();
            else if (key == "deltas")
            {
                var count = reader.Array();
                for (var j = 0; j < count; j++) deltas.Add(ReadLegacyItem(ref reader));
            }
            else reader.Skip();
        }
        reader.End();
        var ops = deltas.Select((item, i) => new TerraScaleReactiveInsert(item, i)).Cast<TerraScaleReactiveOperation>().ToArray();
        return new TerraScaleReactiveDelta(attachment.Generation, offset, revision, ops, null, attachment.Checkpoint);
    }

    private static TerraScaleReactiveSnapshot DecodeLegacySnapshot(ref ReactiveMsgpackReader reader, TerraScaleReactiveAttachment attachment)
    {
        var map = reader.Map();
        ulong cut = attachment.Offset;
        var docs = new List<TerraScaleReactiveItem>();
        for (var i = 0; i < map; i++)
        {
            var key = reader.Text();
            if (key == "cut") cut = reader.U64();
            else if (key == "docs")
            {
                var count = reader.Array();
                for (var j = 0; j < count; j++) docs.Add(ReadLegacyItem(ref reader));
            }
            else reader.Skip();
        }
        reader.End();
        return new TerraScaleReactiveSnapshot(attachment.Generation, 0, cut, docs, null, attachment.Checkpoint);
    }

    private static TerraScaleReactiveItem ReadLegacyItem(ref ReactiveMsgpackReader reader)
    {
        var map = reader.Map();
        string id = "";
        byte[] body = [];
        for (var i = 0; i < map; i++)
        {
            var key = reader.Text();
            if (key == "id") id = reader.Text();
            else if (key == "body") body = reader.Binary();
            else reader.Skip();
        }
        return new TerraScaleReactiveItem(id, [new TerraScaleReactiveField("$", body)]);
    }

    private static ReactiveWireEnvelope ReadEnvelope(ref ReactiveMsgpackReader reader)
    {
        if (reader.Array() != 7) throw new TerraScaleReactiveProtocolException("The reactive envelope has an invalid shape.");
        if (reader.U32() != 2 || reader.U32() != 1 || reader.U32() != 0) throw new TerraScaleReactiveProtocolException("The reactive frame protocol version is unsupported.");
        var tag = reader.U32();
        _ = reader.U64();
        _ = reader.U64();
        return new ReactiveWireEnvelope(tag, ReadMessage(ref reader, tag));
    }

    private static ReactiveWireMessage ReadMessage(ref ReactiveMsgpackReader reader, uint tag) => tag switch
    {
        3 => ReadSnapshotMessage(ref reader),
        4 => ReadDeltaMessage(ref reader),
        5 => ReadResetMessage(ref reader),
        6 => ReadHeartbeat(ref reader),
        8 => ReadAuthorizationChanged(ref reader),
        9 => ReadRegion(ref reader),
        10 => ReadError(ref reader),
        11 => ReadBackpressure(ref reader),
        _ => throw new TerraScaleReactiveProtocolException($"Reactive message tag {tag} is not valid for an HTTP delivery."),
    };

    private static ReactiveWireMessage ReadSnapshotMessage(ref ReactiveMsgpackReader r) { CheckArray(ref r, 2); var subscription = r.Text(); return new ReactiveWireSnapshot(subscription, ReadSnapshot(ref r)); }
    private static ReactiveWireMessage ReadDeltaMessage(ref ReactiveMsgpackReader r) { CheckArray(ref r, 7); var subscription = r.Text(); var g = r.U64(); var from = r.U64(); var to = r.U64(); var ops = ReadOperations(ref r); var digest = r.Text(); var checkpoint = new TerraScaleViewCheckpoint(Convert.ToBase64String(r.Binary())); return new ReactiveWireDelta(subscription, g, from, to, ops, digest, checkpoint); }
    private static ReactiveWireMessage ReadResetMessage(ref ReactiveMsgpackReader r) { CheckArray(ref r, 3); var subscription = r.Text(); var reason = ReadReason(ref r); return new ReactiveWireReset(subscription, reason, ReadSnapshot(ref r)); }
    private static ReactiveWireMessage ReadHeartbeat(ref ReactiveMsgpackReader r) { CheckArray(ref r, 0); return new ReactiveWireHeartbeat(); }
    private static ReactiveWireMessage ReadAuthorizationChanged(ref ReactiveMsgpackReader r)
    {
        CheckArray(ref r, 4);
        var subscription = r.Text();
        _ = r.U64();
        var removedCount = r.Array();
        for (var i = 0; i < removedCount; i++) _ = r.Text();
        return new ReactiveWireAuthorizationChanged(subscription, ReadSnapshot(ref r));
    }
    private static ReactiveWireMessage ReadRegion(ref ReactiveMsgpackReader r) { CheckArray(ref r, 1); return new ReactiveWireRegion(r.Text()); }
    private static ReactiveWireMessage ReadError(ref ReactiveMsgpackReader r) { CheckArray(ref r, 2); var code = r.U32(); var detail = r.NilOrText(); return new ReactiveWireError(code, detail); }
    private static ReactiveWireMessage ReadBackpressure(ref ReactiveMsgpackReader r) { CheckArray(ref r, 1); var mode = r.U32(); if (mode is not 1 and not 2) throw new TerraScaleReactiveProtocolException("The reactive backpressure mode is unknown."); return new ReactiveWireBackpressure(mode); }

    private static ReactiveWireSnapshotValue ReadSnapshot(ref ReactiveMsgpackReader r)
    {
        CheckArray(ref r, 6);
        var generation = r.U64(); var auth = r.U64(); var revision = r.U64();
        var itemCount = r.Array(); var items = new List<TerraScaleReactiveItem>(itemCount);
        for (var i = 0; i < itemCount; i++) items.Add(ReadItem(ref r));
        return new ReactiveWireSnapshotValue(generation, auth, revision, items, r.Text(), new TerraScaleViewCheckpoint(Convert.ToBase64String(r.Binary())));
    }

    private static TerraScaleReactiveItem ReadItem(ref ReactiveMsgpackReader r)
    {
        CheckArray(ref r, 2); var identity = r.Text(); var count = r.Array(); var fields = new List<TerraScaleReactiveField>(count);
        for (var i = 0; i < count; i++) { CheckArray(ref r, 2); fields.Add(new TerraScaleReactiveField(r.Text(), r.Binary())); }
        return new TerraScaleReactiveItem(identity, fields);
    }

    private static IReadOnlyList<TerraScaleReactiveOperation> ReadOperations(ref ReactiveMsgpackReader r)
    {
        var count = r.Array();
        if (count > 1024) throw new TerraScaleReactiveProtocolException("A delta may contain at most 1024 operations.");
        var operations = new List<TerraScaleReactiveOperation>(count);
        for (var i = 0; i < count; i++)
        {
            var kind = r.Array();
            if (kind < 2 || kind > 4) throw new TerraScaleReactiveProtocolException("The reactive operation has an invalid shape.");
            var discriminant = r.U32();
            switch (discriminant)
            {
                case 1: CheckArrayCount(kind, 4); operations.Add(ReadInsert(ref r)); break;
                case 2: CheckArrayCount(kind, 3); operations.Add(new TerraScaleReactiveUpdate(ReadItemWithIdentity(ref r))); break;
                case 3: CheckArrayCount(kind, 3); operations.Add(new TerraScaleReactivePatch(r.Text(), ReadChanges(ref r))); break;
                case 4: CheckArrayCount(kind, 2); operations.Add(new TerraScaleReactiveRemove(r.Text())); break;
                case 5: CheckArrayCount(kind, 3); operations.Add(new TerraScaleReactiveMove(r.Text(), checked((int)r.U32()))); break;
                default: throw new TerraScaleReactiveProtocolException("The reactive operation discriminant is unknown.");
            }
        }
        return operations;
    }

    private static TerraScaleReactiveItem ReadItemWithIdentity(ref ReactiveMsgpackReader r)
    {
        var identity = r.Text(); var count = r.Array(); var fields = new List<TerraScaleReactiveField>(count);
        for (var i = 0; i < count; i++) { CheckArray(ref r, 2); fields.Add(new TerraScaleReactiveField(r.Text(), r.Binary())); }
        return new TerraScaleReactiveItem(identity, fields);
    }
    private static TerraScaleReactiveInsert ReadInsert(ref ReactiveMsgpackReader r)
    {
        var identity = r.Text();
        var position = checked((int)r.U32());
        var count = r.Array();
        var fields = new List<TerraScaleReactiveField>(count);
        for (var i = 0; i < count; i++) { CheckArray(ref r, 2); fields.Add(new TerraScaleReactiveField(r.Text(), r.Binary())); }
        return new TerraScaleReactiveInsert(new TerraScaleReactiveItem(identity, fields), position);
    }
    private static IReadOnlyList<TerraScaleReactiveFieldChange> ReadChanges(ref ReactiveMsgpackReader r)
    {
        var count = r.Array();
        if (count > 1024) throw new TerraScaleReactiveProtocolException("A patch may contain at most 1024 changes.");
        var changes = new List<TerraScaleReactiveFieldChange>(count);
        for (var i = 0; i < count; i++) { CheckArray(ref r, 2); changes.Add(new TerraScaleReactiveFieldChange(r.Text(), r.NilOrBinary())); }
        return changes;
    }
    private static TerraScaleReactiveResetReason ReadReason(ref ReactiveMsgpackReader r) => r.U32() switch
    {
        1 => TerraScaleReactiveResetReason.CheckpointExpired,
        2 => TerraScaleReactiveResetReason.RetentionAdvanced,
        3 => TerraScaleReactiveResetReason.GenerationChanged,
        4 => TerraScaleReactiveResetReason.CheckpointNotCurrent,
        5 => TerraScaleReactiveResetReason.ViewDiverged,
        6 => TerraScaleReactiveResetReason.Oversized,
        7 => TerraScaleReactiveResetReason.InvalidOperations,
        8 => TerraScaleReactiveResetReason.InvalidCheckpoint,
        9 => TerraScaleReactiveResetReason.Backpressure,
        _ => throw new TerraScaleReactiveProtocolException("The reactive reset reason is unknown."),
    };
    private static TerraScaleReactiveFrame ConvertMessage(ReactiveWireEnvelope envelope, TerraScaleReactiveAttachment attachment) => envelope.Message switch
    {
        ReactiveWireSnapshot snapshot => ConvertSnapshotFrame(snapshot.Snapshot, attachment),
        ReactiveWireDelta delta when delta.Generation == attachment.Generation => new TerraScaleReactiveDelta(delta.Generation, delta.From, delta.To, delta.Operations, delta.Digest, delta.Checkpoint),
        ReactiveWireDelta => throw new TerraScaleReactiveProtocolException("The reactive delta belongs to another view generation."),
        ReactiveWireReset reset => new TerraScaleReactiveReset(reset.Reason, ConvertSnapshot(reset.Snapshot, attachment)),
        ReactiveWireAuthorizationChanged changed => new TerraScaleReactiveReset(TerraScaleReactiveResetReason.PermissionChanged, ConvertSnapshot(changed.Snapshot, attachment)),
        ReactiveWireHeartbeat => new TerraScaleReactiveHeartbeat(),
        ReactiveWireRegion region => new TerraScaleReactiveRouteChange(region.Region),
        ReactiveWireError error => new TerraScaleReactiveError(MapErrorCode(error.Code), error.Detail),
        ReactiveWireBackpressure backpressure => new TerraScaleReactiveBackpressure(backpressure.Mode == 1 ? TerraScaleReactiveBackpressureMode.Pause : TerraScaleReactiveBackpressureMode.Resume),
        _ => throw new TerraScaleReactiveProtocolException("The reactive frame is not recognized."),
    };
    private static TerraScaleReactiveSnapshot ConvertSnapshot(ReactiveWireSnapshotValue value, TerraScaleReactiveAttachment attachment)
    {
        if (value.Generation != attachment.Generation) throw new TerraScaleReactiveProtocolException("The reactive snapshot belongs to another view generation.");
        return new TerraScaleReactiveSnapshot(value.Generation, value.AuthorizationGeneration, value.Revision, value.Items, value.Digest, value.Checkpoint);
    }
    private static TerraScaleReactiveSnapshotFrame ConvertSnapshotFrame(ReactiveWireSnapshotValue value, TerraScaleReactiveAttachment attachment) => new(ConvertSnapshot(value, attachment));
    private static void CheckArray(ref ReactiveMsgpackReader reader, int expected) { if (reader.Array() != expected) throw new TerraScaleReactiveProtocolException("The reactive frame has an invalid shape."); }
    private static void CheckArrayCount(int actual, int expected) { if (actual != expected) throw new TerraScaleReactiveProtocolException("The reactive operation has an invalid shape."); }
    private static void ValidateText(string value, string name, int maxBytes) { if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maxBytes) throw new ArgumentException($"{name} must be non-empty and at most {maxBytes} UTF-8 bytes.", name); }
    private static TerraScaleReactiveErrorCode MapErrorCode(uint code) => code switch
    {
        1 => TerraScaleReactiveErrorCode.UnsupportedVersion,
        2 => TerraScaleReactiveErrorCode.MalformedFrame,
        3 => TerraScaleReactiveErrorCode.AuthenticationExpired,
        4 => TerraScaleReactiveErrorCode.PermissionDenied,
        5 => TerraScaleReactiveErrorCode.CheckpointRefused,
        6 => TerraScaleReactiveErrorCode.SlowConsumer,
        7 => TerraScaleReactiveErrorCode.Overloaded,
        8 => TerraScaleReactiveErrorCode.Unavailable,
        _ => throw new TerraScaleReactiveProtocolException("The reactive error code is unknown."),
    };

    private sealed record ReactiveWireEnvelope(uint Tag, ReactiveWireMessage Message);
    private abstract record ReactiveWireMessage;
    private sealed record ReactiveWireSnapshot(string Subscription, ReactiveWireSnapshotValue Snapshot) : ReactiveWireMessage;
    private sealed record ReactiveWireDelta(string Subscription, ulong Generation, ulong From, ulong To, IReadOnlyList<TerraScaleReactiveOperation> Operations, string Digest, TerraScaleViewCheckpoint Checkpoint) : ReactiveWireMessage;
    private sealed record ReactiveWireReset(string Subscription, TerraScaleReactiveResetReason Reason, ReactiveWireSnapshotValue Snapshot) : ReactiveWireMessage;
    private sealed record ReactiveWireAuthorizationChanged(string Subscription, ReactiveWireSnapshotValue Snapshot) : ReactiveWireMessage;
    private sealed record ReactiveWireHeartbeat : ReactiveWireMessage;
    private sealed record ReactiveWireRegion(string Region) : ReactiveWireMessage;
    private sealed record ReactiveWireError(uint Code, string? Detail) : ReactiveWireMessage;
    private sealed record ReactiveWireBackpressure(uint Mode) : ReactiveWireMessage;
    private sealed record ReactiveWireSnapshotValue(ulong Generation, ulong AuthorizationGeneration, ulong Revision, IReadOnlyList<TerraScaleReactiveItem> Items, string Digest, TerraScaleViewCheckpoint Checkpoint);
}

/// <summary>Options for the CDN-backed reactive HTTP transport.</summary>
public sealed record TerraScaleReactiveHttpClientOptions
{
    /// <summary>Reactive CDN origin.</summary>
    public Uri BaseAddress { get; set; } = new("https://sync.api.terrascale.tech", UriKind.Absolute);
    /// <summary>API key authentication for the attach request.</summary>
    public string? ApiKey { get; set; }
    /// <summary>Bearer authentication for the attach request.</summary>
    public string? BearerToken { get; set; }
    /// <summary>Bearer provider invoked per request.</summary>
    public Func<CancellationToken, ValueTask<string>>? BearerTokenProvider { get; set; }
    /// <summary>Transient retries after the initial GET.</summary>
    public int MaxRetryAttempts { get; set; } = 3;
    /// <summary>Initial retry delay.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    /// <summary>Maximum bytes read from one snapshot or log response.</summary>
    public int MaxResponseBytes { get; set; } = TerraScaleReactiveHttpClient.MaxMessageBytes;
    /// <summary>Maximum rows retained in one result snapshot.</summary>
    public int MaxItems { get; set; } = TerraScaleReactiveHttpClient.MaxArrayItems;
    internal void Validate()
    {
        if (!BaseAddress.IsAbsoluteUri) throw new ArgumentException("The reactive base address must be absolute.", nameof(BaseAddress));
        if (MaxRetryAttempts is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts));
        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (MaxResponseBytes is < 1 or > TerraScaleReactiveHttpClient.MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        if (MaxItems is < 1 or > TerraScaleReactiveHttpClient.MaxArrayItems) throw new ArgumentOutOfRangeException(nameof(MaxItems));
        if (!string.IsNullOrWhiteSpace(ApiKey) && (!string.IsNullOrWhiteSpace(BearerToken) || BearerTokenProvider is not null)) throw new ArgumentException("Configure one authentication mode.");
        if (!string.IsNullOrWhiteSpace(BearerToken) && BearerTokenProvider is not null) throw new ArgumentException("Configure one bearer authentication mode.");
    }
}

/// <summary>Immutable metadata returned when a view is attached.</summary>
public sealed record TerraScaleReactiveAttachment
{
    internal TerraScaleReactiveAttachment(string view, ulong generation, IReadOnlyList<Uri> chunks, Uri log, TerraScaleViewCheckpoint checkpoint, string header, string capability, ulong offset)
    { View = view; Generation = generation; SnapshotChunks = new ReadOnlyCollection<Uri>(chunks.ToArray()); Log = log; Checkpoint = checkpoint; CapabilityHeader = header; Capability = capability; Offset = offset; }
    /// <summary>Opaque shared view id.</summary><value> </value>
    public string View { get; }
    /// <summary>View generation.</summary>
    public ulong Generation { get; }
    /// <summary>Immutable snapshot chunk URLs.</summary>
    public IReadOnlyList<Uri> SnapshotChunks { get; }
    /// <summary>Canonical immutable delta-log URL.</summary>
    public Uri Log { get; }
    /// <summary>Opaque checkpoint at the attach cut.</summary>
    public TerraScaleViewCheckpoint Checkpoint { get; }
    /// <summary>Capability header name.</summary>
    public string CapabilityHeader { get; }
    /// <summary>Capability header value.</summary>
    public string Capability { get; }
    /// <summary>Journal offset represented by the snapshot.</summary>
    public ulong Offset { get; }
}

/// <summary>An immutable projected field value carried as canonical JSON bytes.</summary>
public sealed record TerraScaleReactiveField
{
    public TerraScaleReactiveField(string path, ReadOnlyMemory<byte> value) { if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 256) throw new ArgumentException("A field path is required and must be at most 256 UTF-8 bytes.", nameof(path)); if (value.Length > TerraScaleReactiveHttpClient.MaxBinaryBytes) throw new ArgumentOutOfRangeException(nameof(value)); Path = path; Value = value.ToArray(); }
    public string Path { get; }
    public ReadOnlyMemory<byte> Value { get; }
}
/// <summary>An immutable result row.</summary>
public sealed record TerraScaleReactiveItem
{
    public TerraScaleReactiveItem(string identity, IReadOnlyList<TerraScaleReactiveField> fields) { if (string.IsNullOrWhiteSpace(identity) || Encoding.UTF8.GetByteCount(identity) > 256) throw new ArgumentException("An item identity is required and must be at most 256 UTF-8 bytes.", nameof(identity)); if (fields is null || fields.Count > 1024) throw new ArgumentException("An item may contain at most 1024 fields.", nameof(fields)); Identity = identity; Fields = new ReadOnlyCollection<TerraScaleReactiveField>(fields.ToArray()); }
    public string Identity { get; }
    public IReadOnlyList<TerraScaleReactiveField> Fields { get; }
}
/// <summary>Immutable field patch step.</summary>
public sealed record TerraScaleReactiveFieldChange
{
    public TerraScaleReactiveFieldChange(string path, ReadOnlyMemory<byte>? value)
    {
        if (string.IsNullOrWhiteSpace(path) || Encoding.UTF8.GetByteCount(path) > 256) throw new ArgumentException("A field path must be non-empty and at most 256 UTF-8 bytes.", nameof(path));
        Path = path;
        Value = value is { } bytes ? bytes.ToArray() : null;
    }
    public string Path { get; }
    public ReadOnlyMemory<byte>? Value { get; }
}
/// <summary>Typed reset reasons from the generated reactive contract.</summary>
public enum TerraScaleReactiveResetReason { PermissionChanged, AuthExpired, RegionMoved, SchemaChanged, GenerationChanged, HistoryExpired, SlowConsumer, Reconnect, CheckpointExpired, RetentionAdvanced, CheckpointNotCurrent, ViewDiverged, Oversized, InvalidOperations, InvalidCheckpoint, Backpressure }
/// <summary>One ordered result operation.</summary>
public abstract record TerraScaleReactiveOperation(string Identity);
public sealed record TerraScaleReactiveInsert(TerraScaleReactiveItem Item, int Position) : TerraScaleReactiveOperation(Item.Identity);
public sealed record TerraScaleReactiveUpdate(TerraScaleReactiveItem Item) : TerraScaleReactiveOperation(Item.Identity);
public sealed record TerraScaleReactivePatch : TerraScaleReactiveOperation
{
    public TerraScaleReactivePatch(string itemIdentity, IReadOnlyList<TerraScaleReactiveFieldChange> changes) : base(itemIdentity) { if (changes is null || changes.Count > 1024) throw new ArgumentException("A patch may contain at most 1024 changes.", nameof(changes)); Changes = new ReadOnlyCollection<TerraScaleReactiveFieldChange>(changes.ToArray()); }
    public IReadOnlyList<TerraScaleReactiveFieldChange> Changes { get; }
}
public sealed record TerraScaleReactiveRemove(string ItemIdentity) : TerraScaleReactiveOperation(ItemIdentity);
public sealed record TerraScaleReactiveMove(string ItemIdentity, int Position) : TerraScaleReactiveOperation(ItemIdentity);
/// <summary>Immutable complete authorized result.</summary>
public sealed record TerraScaleReactiveSnapshot
{
    public TerraScaleReactiveSnapshot(ulong generation, ulong authorizationGeneration, ulong revision, IReadOnlyList<TerraScaleReactiveItem> items, string? digest, TerraScaleViewCheckpoint checkpoint) { if (items is null || items.Count > TerraScaleReactiveHttpClient.MaxArrayItems) throw new ArgumentException("A snapshot may contain at most 10000 items.", nameof(items)); if (digest is not null && Encoding.UTF8.GetByteCount(digest) > 64) throw new ArgumentException("A digest must be at most 64 UTF-8 bytes.", nameof(digest)); ViewGeneration = generation; AuthorizationGeneration = authorizationGeneration; Revision = revision; Items = new ReadOnlyCollection<TerraScaleReactiveItem>(items.ToArray()); Digest = digest; Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)); }
    public ulong ViewGeneration { get; }
    public ulong AuthorizationGeneration { get; }
    public ulong Revision { get; }
    public IReadOnlyList<TerraScaleReactiveItem> Items { get; }
    public string? Digest { get; }
    public TerraScaleViewCheckpoint Checkpoint { get; }
}
/// <summary>Immutable delta between two journal offsets.</summary>
public sealed record TerraScaleReactiveDelta : TerraScaleReactiveFrame
{
    public TerraScaleReactiveDelta(ulong viewGeneration, ulong fromRevision, ulong toRevision, IReadOnlyList<TerraScaleReactiveOperation> operations, string? digest, TerraScaleViewCheckpoint checkpoint)
    {
        if (toRevision < fromRevision) throw new ArgumentException("A delta cannot move its revision backwards.", nameof(toRevision));
        if (operations is null || operations.Count > 1024) throw new ArgumentException("A delta may contain at most 1024 operations.", nameof(operations));
        if (digest is not null && Encoding.UTF8.GetByteCount(digest) > 64) throw new ArgumentException("A digest must be at most 64 UTF-8 bytes.", nameof(digest));
        ViewGeneration = viewGeneration; FromRevision = fromRevision; ToRevision = toRevision; Operations = new ReadOnlyCollection<TerraScaleReactiveOperation>(operations.ToArray()); Digest = digest; Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
    }
    public ulong ViewGeneration { get; }
    public ulong FromRevision { get; }
    public ulong ToRevision { get; }
    public IReadOnlyList<TerraScaleReactiveOperation> Operations { get; }
    public string? Digest { get; }
    public TerraScaleViewCheckpoint Checkpoint { get; }
}
/// <summary>Immutable snapshot delivery.</summary>
public sealed record TerraScaleReactiveSnapshotFrame(TerraScaleReactiveSnapshot Snapshot) : TerraScaleReactiveFrame;
/// <summary>Typed reset delivery with a replacement authorized snapshot.</summary>
public sealed record TerraScaleReactiveReset(TerraScaleReactiveResetReason Reason, TerraScaleReactiveSnapshot Snapshot) : TerraScaleReactiveFrame;
public sealed record TerraScaleReactiveHeartbeat : TerraScaleReactiveFrame;
public sealed record TerraScaleReactiveRouteChange(string Region) : TerraScaleReactiveFrame;
/// <summary>Typed errors carried by the generated reactive protocol.</summary>
public enum TerraScaleReactiveErrorCode { UnsupportedVersion, MalformedFrame, AuthenticationExpired, PermissionDenied, CheckpointRefused, SlowConsumer, Overloaded, Unavailable }
public sealed record TerraScaleReactiveError(TerraScaleReactiveErrorCode Code, string? Detail) : TerraScaleReactiveFrame;
/// <summary>Bounded-consumer instruction carried by the generated reactive protocol.</summary>
public enum TerraScaleReactiveBackpressureMode { Pause, Resume }
public sealed record TerraScaleReactiveBackpressure(TerraScaleReactiveBackpressureMode Mode) : TerraScaleReactiveFrame;
/// <summary>HTTP poll result; an empty timeout returns <c>null</c>.</summary>
public sealed record TerraScaleReactivePollResult(TerraScaleReactiveFrame Frame, string? ETag)
{
    public static TerraScaleReactivePollResult Reset(TerraScaleReactiveResetReason reason, TerraScaleReactiveSnapshot snapshot) => new(new TerraScaleReactiveReset(reason, snapshot), null);
}
public abstract record TerraScaleReactiveFrame;

/// <summary>A cancellation-aware sequential reactive subscription.</summary>
public sealed class TerraScaleReactiveSubscription : IAsyncDisposable
{
    private readonly TerraScaleReactiveHttpClient client;
    private readonly Dictionary<ulong, string> etags = [];
    private bool disposed;
    internal TerraScaleReactiveSubscription(TerraScaleReactiveHttpClient client, TerraScaleReactiveAttachment attachment, TerraScaleReactiveSnapshot snapshot) { this.client = client; Attachment = attachment; CurrentSnapshot = snapshot; Offset = snapshot.Revision; Query = ""; Scope = ""; }
    internal TerraScaleReactiveSubscription(TerraScaleReactiveHttpClient client, TerraScaleReactiveAttachment attachment, TerraScaleReactiveSnapshot snapshot, string query, string scope, long epoch) : this(client, attachment, snapshot) { Query = query; Scope = scope; EntitlementEpoch = epoch; }
    public TerraScaleReactiveAttachment Attachment { get; private set; }
    public TerraScaleReactiveSnapshot CurrentSnapshot { get; private set; }
    public ulong Offset { get; private set; }
    public string? LastETag { get; private set; }
    internal string Query { get; set; }
    internal string Scope { get; set; }
    internal long EntitlementEpoch { get; set; }
    public async Task<TerraScaleReactivePollResult?> ReadNextAsync(CancellationToken cancellationToken = default) { ObjectDisposedException.ThrowIf(disposed, this); return await client.PollAsync(this, cancellationToken).ConfigureAwait(false); }
    public async IAsyncEnumerable<TerraScaleReactivePollResult> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) { while (true) { var result = await ReadNextAsync(cancellationToken).ConfigureAwait(false); if (result is not null) yield return result; } }
    internal TerraScaleReactivePollResult Accept(TerraScaleReactiveFrame frame, string? etag)
    {
        switch (frame)
        {
            case TerraScaleReactiveDelta delta when delta.FromRevision == Offset && delta.ToRevision > Offset:
                Offset = delta.ToRevision;
                CurrentSnapshot = new TerraScaleReactiveSnapshot(CurrentSnapshot.ViewGeneration, CurrentSnapshot.AuthorizationGeneration, Offset, CurrentSnapshot.Items, delta.Digest, delta.Checkpoint);
                break;
            case TerraScaleReactiveDelta delta when delta.ToRevision <= Offset: return new TerraScaleReactivePollResult(new TerraScaleReactiveHeartbeat(), etag);
            case TerraScaleReactiveSnapshotFrame snapshot: CurrentSnapshot = snapshot.Snapshot; Offset = snapshot.Snapshot.Revision; break;
            case TerraScaleReactiveReset reset: CurrentSnapshot = reset.Snapshot; Offset = reset.Snapshot.Revision; break;
            case TerraScaleReactiveDelta: throw new TerraScaleReactiveProtocolException("The reactive delta has a revision gap and requires a reset.");
        }
        LastETag = etag;
        return new TerraScaleReactivePollResult(frame, etag);
    }
    internal void RememberEtag(ulong offset, string etag) { if (etags.TryGetValue(offset, out var previous) && !string.Equals(previous, etag, StringComparison.Ordinal)) throw new TerraScaleReactiveProtocolException("An immutable reactive offset changed its ETag."); etags[offset] = etag; }
    internal void Replace(TerraScaleReactiveAttachment attachment, TerraScaleReactiveSnapshot snapshot) { Attachment = attachment; CurrentSnapshot = snapshot; Offset = snapshot.Revision; etags.Clear(); LastETag = null; }
    public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
}

/// <summary>Protocol error caused by an invalid or unbounded reactive response.</summary>
public sealed class TerraScaleReactiveProtocolException(string message) : Exception(message);
/// <summary>Typed HTTP failure from the reactive public API.</summary>
public sealed class TerraScaleReactiveHttpException(HttpStatusCode statusCode, string message, string? code, TimeSpan? retryAfter) : HttpRequestException(message, null, statusCode)
{
    public string? ErrorCode { get; } = code;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public static async Task<TerraScaleReactiveHttpException> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null;
        try { var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false); if (body.TryGetProperty("error", out var error)) code = error.TryGetProperty("code", out var c) ? c.GetString() : null; }
        catch (JsonException) { }
        return new TerraScaleReactiveHttpException(response.StatusCode, $"TerraScale reactive HTTP request returned {(int)response.StatusCode} {response.ReasonPhrase}.", code, response.Headers.RetryAfter?.Delta);
    }
}

internal sealed record ReactiveAttachRequest(string Query, string Scope, long EntitlementEpoch, string? Checkpoint);
internal sealed record ReactiveAttachResponse(string View, long Generation, string[] SnapshotChunks, string Log, string Checkpoint, string Capability, string CapabilityHeader, long Offset);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(ReactiveAttachRequest))]
[JsonSerializable(typeof(ReactiveAttachResponse))]
internal partial class ReactiveJsonContext : JsonSerializerContext;

internal ref struct ReactiveMsgpackReader
{
    private readonly ReadOnlySpan<byte> bytes;
    private int position;
    internal ReactiveMsgpackReader(byte[] bytes) { if (bytes.Length > TerraScaleReactiveHttpClient.MaxMessageBytes) throw new TerraScaleReactiveProtocolException("The reactive message exceeds the maximum size."); this.bytes = bytes; position = 0; }
    internal bool PeekIsMap => position < bytes.Length && (bytes[position] is >= 0x80 and <= 0x8f or 0xde or 0xdf);
    internal int Array() { var code = Take(); return code is >= 0x90 and <= 0x9f ? code & 15 : code switch { 0xdc => checked((int)U16()), 0xdd => checked((int)U32Raw()), _ => throw Invalid() }; }
    internal int Map() { var code = Take(); return code is >= 0x80 and <= 0x8f ? code & 15 : code switch { 0xde => checked((int)U16()), 0xdf => checked((int)U32Raw()), _ => throw Invalid() }; }
    internal uint U32() { var value = U64(); if (value > uint.MaxValue) throw Invalid(); return (uint)value; }
    internal ulong U64() { var code = Take(); return code switch { <= 0x7f => code, >= 0xe0 => throw Invalid(), 0xcc => Take(), 0xcd => U16(), 0xce => U32Raw(), 0xcf => U64Raw(), _ => throw Invalid() }; }
    internal string Text() { var length = Length(Take(), text: true); var value = Encoding.UTF8.GetString(Read(length)); if (Encoding.UTF8.GetByteCount(value) != length) throw Invalid(); return value; }
    internal byte[] Binary() { var length = Length(Take(), text: false); return Read(length).ToArray(); }
    internal byte[]? NilOrBinary() { if (position < bytes.Length && bytes[position] == 0xc0) { position++; return null; } return Binary(); }
    internal string? NilOrText() { if (position < bytes.Length && bytes[position] == 0xc0) { position++; return null; } return Text(); }
    internal bool Nil() { if (position < bytes.Length && bytes[position] == 0xc0) { position++; return true; } return false; }
    internal void Skip() { var code = Take(); if (code is <= 0x7f or >= 0xe0 or 0xc0) return; if (code is >= 0xa0 and <= 0xbf) { _ = Read(code & 31); return; } if (code is >= 0x90 and <= 0x9f) { for (var i = 0; i < (code & 15); i++) Skip(); return; } if (code is >= 0x80 and <= 0x8f) { for (var i = 0; i < (code & 15) * 2; i++) Skip(); return; } var bytesToRead = code switch { 0xcc => 1, 0xcd => 2, 0xce => 4, 0xcf => 8, 0xd0 => 1, 0xd1 => 2, 0xd2 => 4, 0xd3 => 8, 0xca => 4, 0xcb => 8, 0xd9 => 1, 0xda => 2, 0xdb => 4, 0xc4 => 1, 0xc5 => 2, 0xc6 => 4, 0xdc => 2, 0xdd => 4, 0xde => 2, 0xdf => 4, _ => throw Invalid() }; var length = (code is 0xd9 or 0xc4) ? Take() : code is 0xda or 0xc5 or 0xdc ? U16() : code is 0xdb or 0xc6 or 0xdd or 0xdf ? U32Raw() : (uint)bytesToRead; if (code is 0xdc or 0xdd) for (var i = 0; i < length; i++) Skip(); else if (code is 0xde or 0xdf) for (var i = 0; i < length * 2; i++) Skip(); else _ = Read(checked((int)length)); }
    internal void End() { if (position != bytes.Length) throw Invalid(); }
    private byte Take() { if (position >= bytes.Length) throw Invalid(); return bytes[position++]; }
    private ReadOnlySpan<byte> Read(int length) { if (length < 0 || position > bytes.Length - length) throw Invalid(); var value = bytes.Slice(position, length); position += length; return value; }
    private uint U16() { return BinaryPrimitives.ReadUInt16BigEndian(Read(2)); }
    private uint U32Raw() { return BinaryPrimitives.ReadUInt32BigEndian(Read(4)); }
    private ulong U64Raw() { return BinaryPrimitives.ReadUInt64BigEndian(Read(8)); }
    private int Length(byte code, bool text) { var length = code switch { >= 0xa0 and <= 0xbf => code & 31, 0xd9 or 0xc4 => (int)Take(), 0xda or 0xc5 => checked((int)U16()), 0xdb or 0xc6 => checked((int)U32Raw()), _ => throw Invalid() }; var max = text ? TerraScaleReactiveHttpClient.MaxStringBytes : TerraScaleReactiveHttpClient.MaxBinaryBytes; if (length > max) throw Invalid(); return length; }
    private static TerraScaleReactiveProtocolException Invalid() => new("The reactive MessagePack frame is malformed or exceeds a declared bound.");
}
