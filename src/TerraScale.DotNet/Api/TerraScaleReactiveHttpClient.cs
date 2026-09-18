using System.Buffers;
using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
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
    internal const int MaxCheckpointBytes = 4096;
    internal const int MaxArrayItems = 10_000;
    internal const int MaxFields = 1024;
    internal const string CapabilityHeaderName = "X-TerraScale-Capability";
    internal const string WebSocketSyncProtocol = "terrascale.sync.v1";
    private const int MaxErrorBytes = 64 * 1024;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    private readonly HttpClient httpClient;
    private readonly TerraScaleReactiveHttpClientOptions options;

    /// <summary>Creates a reactive HTTP client over a caller-owned <see cref="HttpClient"/>.</summary>
    /// <remarks>The caller-owned HTTP handler must disable automatic redirects to protect credentials and view capabilities.</remarks>
    public TerraScaleReactiveHttpClient(HttpClient httpClient, TerraScaleReactiveHttpClientOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        var configured = (options ?? new TerraScaleReactiveHttpClientOptions()) with { };
        configured.Validate();
        this.options = configured with
        {
            BaseAddress = NormalizeOrigin(configured.BaseAddress, nameof(configured.BaseAddress)),
            ApiBaseAddress = configured.ApiBaseAddress is null ? null : NormalizeOrigin(configured.ApiBaseAddress, nameof(configured.ApiBaseAddress)),
        };
        this.httpClient = httpClient;
        this.httpClient.BaseAddress ??= this.options.BaseAddress;
    }

    /// <summary>Attaches to a bounded live SQL result view.</summary>
    public Task<TerraScaleReactiveSubscription> SubscribeAsync(string databaseId, string subscription, string statement, ReadOnlyMemory<byte> parameters = default, TerraScaleViewCheckpoint? checkpoint = null, TerraScaleReactiveTransport transport = TerraScaleReactiveTransport.Http, CancellationToken cancellationToken = default) =>
        AttachCoreAsync(databaseId, subscription, statement, parameters, checkpoint, transport, cancellationToken);

    /// <summary>Attaches using the wire transport name from the public contract.</summary>
    public Task<TerraScaleReactiveSubscription> SubscribeAsync(string databaseId, string subscription, string statement, ReadOnlyMemory<byte> parameters, TerraScaleViewCheckpoint? checkpoint, string transport, CancellationToken cancellationToken = default) =>
        SubscribeAsync(databaseId, subscription, statement, parameters, checkpoint, ParseTransport(transport), cancellationToken);

    /// <summary>Alias for <see cref="SubscribeAsync(string,string,string,ReadOnlyMemory{byte},TerraScaleViewCheckpoint?,TerraScaleReactiveTransport,CancellationToken)"/>.</summary>
    public Task<TerraScaleReactiveSubscription> AttachAsync(string databaseId, string subscription, string statement, ReadOnlyMemory<byte> parameters = default, TerraScaleViewCheckpoint? checkpoint = null, TerraScaleReactiveTransport transport = TerraScaleReactiveTransport.Http, CancellationToken cancellationToken = default) =>
        AttachCoreAsync(databaseId, subscription, statement, parameters, checkpoint, transport, cancellationToken);

    /// <summary>Attaches using the wire transport name from the public contract.</summary>
    public Task<TerraScaleReactiveSubscription> AttachAsync(string databaseId, string subscription, string statement, ReadOnlyMemory<byte> parameters, TerraScaleViewCheckpoint? checkpoint, string transport, CancellationToken cancellationToken = default) =>
        AttachAsync(databaseId, subscription, statement, parameters, checkpoint, ParseTransport(transport), cancellationToken);

    private async Task<TerraScaleReactiveSubscription> AttachCoreAsync(string databaseId, string subscription, string statement, ReadOnlyMemory<byte> parameters, TerraScaleViewCheckpoint? checkpoint, TerraScaleReactiveTransport transport, CancellationToken cancellationToken)
    {
        ValidateText(databaseId, nameof(databaseId), 256);
        ValidateText(subscription, nameof(subscription), 128);
        ValidateText(statement, nameof(statement), MaxStringBytes);
        if (parameters.Length > MaxBinaryBytes) throw new ArgumentOutOfRangeException(nameof(parameters));
        if (!Enum.IsDefined(transport)) throw new ArgumentOutOfRangeException(nameof(transport));
        if (checkpoint is not null) ValidateCheckpointToken(checkpoint.Token);

        // Transport is a local delivery choice; it is not part of the
        // owner-published attach request and must never be serialized.
        var requestBody = new ReactiveAttachRequest(databaseId, statement, Convert.ToBase64String(parameters.ToArray()), checkpoint?.Token, subscription, 0);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(AttachOrigin(), "/api/v1/reactive/views"))
        {
            Content = JsonContent.Create(requestBody, ReactiveJsonContext.Default.ReactiveAttachRequest),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(options.RegionHint)) request.Headers.TryAddWithoutValidation("X-TerraScale-Region-Hint", options.RegionHint);
        await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
        using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.Created) throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
        var attach = await response.Content.ReadFromJsonAsync(ReactiveJsonContext.Default.ReactiveAttachResponse, cancellationToken).ConfigureAwait(false)
            ?? throw new TerraScaleReactiveProtocolException("The reactive attach response was empty.");
        var attachment = CreateAttachment(attach, databaseId, subscription, statement, parameters, transport);
        var snapshot = await DownloadSnapshotAsync(attachment, cancellationToken).ConfigureAwait(false);
        return new TerraScaleReactiveSubscription(this, attachment, snapshot);
    }

    internal async Task<TerraScaleReactivePollResult?> PollAsync(TerraScaleReactiveSubscription subscription, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(subscription.IsDisposed, subscription);
        if (subscription.NeedsResume) return await ReattachAsync(subscription, TerraScaleReactiveResetReason.RegionMoved, cancellationToken).ConfigureAwait(false);
        if (subscription.Attachment.Transport == TerraScaleReactiveTransport.WebSocket)
            return await PollWebSocketAsync(subscription, cancellationToken).ConfigureAwait(false);
        if (subscription.Attachment.Transport == TerraScaleReactiveTransport.ServerSentEvents)
            return await PollSseAsync(subscription, cancellationToken).ConfigureAwait(false);
        var request = new HttpRequestMessage(HttpMethod.Get, AppendOffset(subscription.Attachment.Log, subscription.Offset));
        using (request)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/msgpack"));
            request.Headers.TryAddWithoutValidation(subscription.Attachment.CapabilityHeader, subscription.Attachment.Capability);
            if (subscription.LastETag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", subscription.LastETag);
            await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.PollWindow);
            HttpResponseMessage response;
            try { response = await SendWithRetryAsync(request, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
            using (response)
            {
                if (response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified) return null;
                if (response.StatusCode == HttpStatusCode.Gone) return await ReattachAsync(subscription, TerraScaleReactiveResetReason.HistoryExpired, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Conflict) return await ReattachAsync(subscription, TerraScaleReactiveResetReason.Reconnect, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
                var bytes = await ReadBoundedBytesAsync(response.Content, options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0) return null;
                var etagHeader = response.Headers.ETag;
                var etag = etagHeader?.Tag;
                var frame = DecodeFrame(bytes, subscription.Attachment);
                if (frame is null) return null;
                if (frame is (TerraScaleReactiveDelta or TerraScaleReactiveSnapshotFrame) && !IsStrongEtag(etagHeader))
                    throw new TerraScaleReactiveProtocolException("A data-bearing reactive response must carry an ETag.");
                var immutableEtag = frame is TerraScaleReactiveDelta or TerraScaleReactiveSnapshotFrame ? etag : null;
                return subscription.AcceptHttpFrame(frame, immutableEtag);
            }
        }
    }

    private async Task<TerraScaleReactivePollResult?> PollWebSocketAsync(TerraScaleReactiveSubscription subscription, CancellationToken cancellationToken)
    {
        var socket = await subscription.EnsureWebSocketAsync(CreateWebSocketUri(subscription.Attachment.Log, subscription.Attachment.View, subscription.Attachment.Generation), subscription.Attachment.Capability, cancellationToken).ConfigureAwait(false);
        if (!subscription.WebSocketSubscribed)
        {
            var checkpoint = DecodeCheckpoint(subscription.CurrentSnapshot.Checkpoint.Token);
            // The wire identity is the canonical view issued by attach. The
            // caller's subscription name is local bookkeeping only.
            var subscribe = EncodeSubscribe(subscription.Attachment.View, subscription.Attachment.Statement, subscription.Attachment.Parameters.Span, checkpoint);
            await socket.SendAsync(subscribe, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
            subscription.MarkWebSocketSubscribed();
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.PollWindow);
        byte[]? bytes;
        try
        {
            bytes = await ReceiveWebSocketMessageAsync(socket, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        if (bytes is null)
        {
            subscription.MarkNeedsResume();
            return null;
        }

        var frame = DecodeFrame(bytes, subscription.Attachment);
        if (frame is null) return null;
        return subscription.Accept(frame, etag: null);
    }

    private static async Task<byte[]?> ReceiveWebSocketMessageAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        // The scratch buffer is pooled and grows by doubling; only the final
        // exact-size message copy is allocated per frame.
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            var length = 0;
            while (true)
            {
                var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), cancellationToken).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                {
                    if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None).ConfigureAwait(false);
                    return null;
                }

                if (received.MessageType != WebSocketMessageType.Binary)
                    throw new TerraScaleReactiveProtocolException("The reactive WebSocket delivered a non-binary frame.");
                length += received.Count;
                if (length > MaxMessageBytes)
                    throw new TerraScaleReactiveProtocolException("The reactive WebSocket message exceeds the maximum size.");
                if (received.EndOfMessage) return buffer.AsSpan(0, length).ToArray();
                if (length == buffer.Length)
                {
                    var grown = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(grown);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = grown;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task<TerraScaleReactivePollResult> ReattachAsync(TerraScaleReactiveSubscription subscription, TerraScaleReactiveResetReason reason, CancellationToken cancellationToken)
    {
        subscription.SetSessionState(TerraScaleViewSessionState.Resuming);
        var replacement = await AttachCoreAsync(subscription.Attachment.DatabaseId, subscription.Attachment.Subscription, subscription.Attachment.Statement, subscription.Attachment.Parameters, subscription.CurrentSnapshot.Checkpoint, subscription.Attachment.Transport, cancellationToken).ConfigureAwait(false);
        if (replacement.CurrentSnapshot.AuthorizationGeneration < subscription.CurrentSnapshot.AuthorizationGeneration)
            throw new TerraScaleReactiveProtocolException("The reattached snapshot moved to an older authorization generation.");
        subscription.Replace(replacement.Attachment, replacement.CurrentSnapshot);
        return TerraScaleReactivePollResult.Reset(reason, replacement.CurrentSnapshot);
    }

    private async Task<TerraScaleReactiveSnapshot> DownloadSnapshotAsync(TerraScaleReactiveAttachment attachment, CancellationToken cancellationToken)
    {
        var rows = new List<TerraScaleReactiveItem>();
        TerraScaleReactiveSnapshot? first = null;
        foreach (var chunk in attachment.SnapshotChunks)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, chunk);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/msgpack"));
            request.Headers.TryAddWithoutValidation(attachment.CapabilityHeader, attachment.Capability);
            await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            using var response = await SendWithRetryAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
            var bytes = await ReadBoundedBytesAsync(response.Content, options.MaxResponseBytes, cancellationToken).ConfigureAwait(false);
            if (!IsStrongEtag(response.Headers.ETag)) throw new TerraScaleReactiveProtocolException("An immutable snapshot response must carry a strong ETag.");
            var snapshot = DecodeSnapshot(bytes, attachment) ?? throw new TerraScaleReactiveProtocolException("A snapshot chunk was empty.");
            if (first is not null && (snapshot.ViewGeneration != first.ViewGeneration || snapshot.AuthorizationGeneration != first.AuthorizationGeneration || snapshot.Revision != first.Revision || !string.Equals(snapshot.Digest, first.Digest, StringComparison.Ordinal) || !string.Equals(snapshot.Checkpoint.Token, first.Checkpoint.Token, StringComparison.Ordinal)))
                throw new TerraScaleReactiveProtocolException("Snapshot chunks do not describe one immutable cut.");
            first ??= snapshot;
            rows.AddRange(snapshot.Items);
            if (rows.Count > options.MaxItems) throw new TerraScaleReactiveProtocolException("The snapshot exceeds the configured item bound.");
        }
        if (first is null) throw new TerraScaleReactiveProtocolException("The attach response carried no snapshot chunks.");
        attachment.ValidateCheckpoint(first.Checkpoint);
        return new TerraScaleReactiveSnapshot(first.ViewGeneration, first.AuthorizationGeneration, first.Revision, rows, first.Digest, first.Checkpoint);
    }

    private TerraScaleReactiveAttachment CreateAttachment(ReactiveAttachResponse response, string databaseId, string subscription, string statement, ReadOnlyMemory<byte> parameters, TerraScaleReactiveTransport transport)
    {
        ValidateText(response.View, nameof(response.View), 256);
        if (response.Generation < 1) throw new TerraScaleReactiveProtocolException("The attach response has an invalid view generation.");
        ValidateText(response.Capability, nameof(response.Capability), 4096);
        ValidateCheckpointToken(response.Checkpoint);
        if (string.IsNullOrWhiteSpace(response.Log)) throw new TerraScaleReactiveProtocolException("The attach response must carry a log URL.");
        if (response.SnapshotUrls is null || response.SnapshotUrls.Length == 0) throw new TerraScaleReactiveProtocolException("The attach response must carry snapshot chunk URLs.");
        if (response.SnapshotUrls.Length > options.MaxSnapshotChunks) throw new TerraScaleReactiveProtocolException("The attach response exceeds the snapshot chunk bound.");
        if (!string.Equals(response.CapabilityHeader, CapabilityHeaderName, StringComparison.OrdinalIgnoreCase))
            throw new TerraScaleReactiveProtocolException("The attach response declared an unsupported capability header.");
        var chunks = response.SnapshotUrls.Select(value => ResolveResourceUri(value, "snapshot chunk")).ToArray();
        var log = ResolveResourceUri(response.Log!, "log");
        return new TerraScaleReactiveAttachment(databaseId, response.View!, subscription, statement, parameters, transport, response.Generation, response.Offset, chunks, log, new TerraScaleViewCheckpoint(response.Checkpoint!), response.Capability!, CapabilityHeaderName);
    }

    private Uri ResolveResourceUri(string value, string resourceName)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var parsed))
            throw new TerraScaleReactiveProtocolException($"The attach response carried an invalid {resourceName} URL.");
        var resolved = parsed.IsAbsoluteUri ? parsed : new Uri(options.BaseAddress, parsed);
        if (!(resolved.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) || resolved.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) || resolved.UserInfo.Length != 0 || !SameOrigin(resolved, options.BaseAddress))
            throw new TerraScaleReactiveProtocolException($"The attach response carried an unsafe {resourceName} URL.");
        return resolved;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    private Uri AttachOrigin() => options.ApiBaseAddress ?? options.BaseAddress;

    private static Uri CreateWebSocketUri(Uri log, string view, ulong generation)
    {
        var builder = new UriBuilder(log)
        {
            Scheme = log.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = $"/api/v1/reactive/views/{Uri.EscapeDataString(view)}/{generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}/websocket",
            Query = string.Empty,
        };
        return builder.Uri;
    }

    private static Uri CreateSseUri(Uri log, string view, ulong generation, ulong offset)
    {
        var builder = new UriBuilder(log)
        {
            Path = $"/api/v1/reactive/views/{Uri.EscapeDataString(view)}/{generation.ToString(System.Globalization.CultureInfo.InvariantCulture)}/stream",
            Query = "offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return builder.Uri;
    }

    /// <summary>
    /// Reads the next frame from the subscription's server-sent-events
    /// stream. The stream stays open across polls; a dropped connection
    /// marks the subscription for reattach, mirroring the WebSocket path.
    /// </summary>
    private async Task<TerraScaleReactivePollResult?> PollSseAsync(TerraScaleReactiveSubscription subscription, CancellationToken cancellationToken)
    {
        var reader = await subscription.EnsureSseReaderAsync(
            CreateSseUri(subscription.Attachment.Log, subscription.Attachment.View, subscription.Attachment.Generation, subscription.Offset),
            subscription.Attachment.Capability,
            cancellationToken).ConfigureAwait(false);
        TerraScaleReactiveSseEvent? sseEvent;
        try
        {
            sseEvent = await ReadSseEventAsync(reader, options.PollWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (TerraScaleReactiveProtocolException)
        {
            subscription.MarkNeedsResume();
            throw;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (IOException)
        {
            subscription.MarkNeedsResume();
            return null;
        }
        catch (HttpRequestException)
        {
            subscription.MarkNeedsResume();
            return null;
        }

        if (sseEvent is null)
        {
            subscription.MarkNeedsResume();
            return null;
        }

        if (string.Equals(sseEvent.Event, "error", StringComparison.Ordinal))
        {
            subscription.MarkNeedsResume();
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(sseEvent.Data.ToString());
        }
        catch (FormatException)
        {
            subscription.MarkNeedsResume();
            throw new TerraScaleReactiveProtocolException("The reactive SSE event carried invalid base64 data.");
        }

        var frame = DecodeFrame(bytes, subscription.Attachment);
        if (frame is null) return null;
        return subscription.Accept(frame, etag: null);
    }

    private static async Task<TerraScaleReactiveSseEvent?> ReadSseEventAsync(System.IO.StreamReader reader, TimeSpan window, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(window);
        string? eventName = null;
        var data = new StringBuilder();
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (line is null)
            {
                return null;
            }

            if (line.Length == 0)
            {
                if (data.Length == 0)
                {
                    eventName = null;
                    continue;
                }

                return new TerraScaleReactiveSseEvent(eventName, data.ToString());
            }

            if (line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':');
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..].TrimStart(' ');
            if (string.Equals(field, "event", StringComparison.Ordinal))
            {
                eventName = value;
            }
            else if (string.Equals(field, "data", StringComparison.Ordinal))
            {
                data.Append(value);
            }
        }
    }

    private sealed record TerraScaleReactiveSseEvent(string? Event, string Data);

    private static byte[] DecodeCheckpoint(string token)
    {
        try { return Convert.FromBase64String(token); }
        catch (FormatException) { throw new TerraScaleReactiveProtocolException("The reactive checkpoint is not valid base64."); }
    }

    /// <summary>
    /// Opens the server-sent-events stream for one attached view. The caller
    /// owns the returned response and reader; the stream stays open until the
    /// server ends it (reset or failure), so one reader serves many polls.
    /// </summary>
    internal async Task<(HttpResponseMessage Response, System.IO.StreamReader Reader)> OpenSseStreamAsync(Uri uri, string capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ValidateText(capability, nameof(capability), MaxStringBytes);
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        try
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            request.Headers.TryAddWithoutValidation(CapabilityHeaderName, capability);
            await ApplyAuthenticationAsync(request, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(options.RegionHint))
                request.Headers.TryAddWithoutValidation("X-TerraScale-Region-Hint", options.RegionHint);
            var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                if (!response.IsSuccessStatusCode)
                    throw await TerraScaleReactiveHttpException.CreateAsync(response, cancellationToken).ConfigureAwait(false);
                var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return (response, new System.IO.StreamReader(stream, Encoding.UTF8));
            }
            catch
            {
                response.Dispose();
                throw;
            }
        }
        finally
        {
            request.Dispose();
        }
    }

    private static byte[] EncodeSubscribe(string wireView, string statement, ReadOnlySpan<byte> parameters, ReadOnlySpan<byte> checkpoint)
    {
        var writer = new ArrayBufferWriter<byte>();
        WriteArrayHeader(writer, 7);
        WriteUInt(writer, 1); WriteUInt(writer, 0); WriteUInt(writer, 0); WriteUInt(writer, 1); WriteUInt(writer, 1); WriteUInt(writer, 0);
        WriteArrayHeader(writer, 4);
        WriteString(writer, wireView); WriteString(writer, statement); WriteBinary(writer, parameters); WriteBinary(writer, checkpoint);
        return writer.WrittenMemory.ToArray();
    }

    private static void WriteArrayHeader(ArrayBufferWriter<byte> writer, int count) { writer.GetSpan(1)[0] = (byte)(0x90 | count); writer.Advance(1); }

    private static void WriteUInt(ArrayBufferWriter<byte> writer, ulong value)
    {
        if (value < 128) { writer.GetSpan(1)[0] = (byte)value; writer.Advance(1); return; }
        var span = writer.GetSpan(9); span[0] = 0xcf; BinaryPrimitives.WriteUInt64BigEndian(span[1..], value); writer.Advance(9);
    }

    private static void WriteString(ArrayBufferWriter<byte> writer, string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue) throw new TerraScaleReactiveProtocolException("The reactive subscribe string is too large.");
        if (bytes.Length < 32) { var span = writer.GetSpan(1); span[0] = (byte)(0xa0 | bytes.Length); writer.Advance(1); }
        else { var span = writer.GetSpan(3); span[0] = 0xda; BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)bytes.Length); writer.Advance(3); }
        writer.Write(bytes);
    }

    private static void WriteBinary(ArrayBufferWriter<byte> writer, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= byte.MaxValue) { var span = writer.GetSpan(2); span[0] = 0xc4; span[1] = (byte)bytes.Length; writer.Advance(2); }
        else if (bytes.Length <= ushort.MaxValue) { var span = writer.GetSpan(3); span[0] = 0xc5; BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)bytes.Length); writer.Advance(3); }
        else if (bytes.Length <= MaxBinaryBytes) { var span = writer.GetSpan(5); span[0] = 0xc6; BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)bytes.Length); writer.Advance(5); }
        else throw new TerraScaleReactiveProtocolException("The reactive subscribe binary value is too large.");
        writer.Write(bytes);
    }

    private static bool IsStrongEtag(EntityTagHeaderValue? value) => value is { Tag: not null, IsWeak: false } && value.Tag != "*";

    internal static ulong FrameRevision(TerraScaleReactiveFrame frame, ulong fallback) => frame switch
    {
        TerraScaleReactiveDelta delta => delta.ToRevision,
        TerraScaleReactiveSnapshotFrame snapshot => snapshot.Snapshot.Revision,
        TerraScaleReactiveReset reset => reset.Snapshot.Revision,
        _ => fallback,
    };

    private async Task ApplyAuthenticationAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) { request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey.Trim()); return; }
        var bearer = options.BearerToken;
        if (string.IsNullOrWhiteSpace(bearer) && options.BearerTokenProvider is { } provider) bearer = await provider(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(bearer)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Trim());
    }

    internal async Task<ClientWebSocket> ConnectWebSocketAsync(Uri uri, string capability, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ValidateText(capability, nameof(capability), MaxStringBytes);
        var socket = new ClientWebSocket();
        try
        {
            // The view capability is carried in the subprotocol offer so the
            // producer can authenticate the upgrade. It is also retained in
            // the header for regional callers and existing HTTP delivery.
            try
            {
                socket.Options.AddSubProtocol(WebSocketSyncProtocol);
                socket.Options.AddSubProtocol($"terrascale.cap.{capability}");
            }
            catch (ArgumentException)
            {
                throw new TerraScaleReactiveProtocolException("The reactive WebSocket capability is malformed.");
            }
            var bearer = !string.IsNullOrWhiteSpace(options.ApiKey) ? options.ApiKey : options.BearerToken;
            if (string.IsNullOrWhiteSpace(bearer) && options.BearerTokenProvider is { } provider)
                bearer = await provider(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(bearer))
                socket.Options.SetRequestHeader("Authorization", $"Bearer {bearer.Trim()}");
            socket.Options.SetRequestHeader(CapabilityHeaderName, capability);
            if (!string.IsNullOrWhiteSpace(options.RegionHint))
                socket.Options.SetRequestHeader("X-TerraScale-Region-Hint", options.RegionHint);
            try
            {
                await socket.ConnectAsync(uri, cancellationToken).ConfigureAwait(false);
            }
            catch (WebSocketException)
            {
                throw new TerraScaleReactiveProtocolException("The reactive WebSocket handshake was rejected.");
            }
            if (!string.Equals(socket.SubProtocol, WebSocketSyncProtocol, StringComparison.Ordinal))
                throw new TerraScaleReactiveProtocolException("The reactive WebSocket negotiated an unsupported subprotocol.");
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var attempt = 0;
        while (true)
        {
            using var copy = await CloneRequestAsync(request, cancellationToken).ConfigureAwait(false);
            HttpResponseMessage response;
            try { response = await httpClient.SendAsync(copy, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false); }
            // POST attach and SQL-like mutations are never retried: a network
            // failure does not prove that the server did not commit them.
            catch (HttpRequestException) when (attempt < options.MaxRetryAttempts && request.Method != HttpMethod.Post) { await DelayAsync(null, attempt++, cancellationToken).ConfigureAwait(false); continue; }
            if (!IsTransient(response.StatusCode) || attempt >= options.MaxRetryAttempts || request.Method == HttpMethod.Post) return response;
            var delay = RetryAfter(response);
            if (delay > TimeSpan.FromSeconds(30)) return response;
            response.Dispose(); await DelayAsync(delay, attempt++, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage source, CancellationToken cancellationToken)
    {
        var copy = new HttpRequestMessage(source.Method, source.RequestUri);
        foreach (var header in source.Headers) copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (source.Content is not null) { copy.Content = new ByteArrayContent(await source.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false)); foreach (var header in source.Content.Headers) copy.Content.Headers.TryAddWithoutValidation(header.Key, header.Value); }
        return copy;
    }

    private async Task DelayAsync(TimeSpan? retryAfter, int attempt, CancellationToken cancellationToken)
    {
        var delay = retryAfter.GetValueOrDefault();
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromMilliseconds(options.RetryDelay.TotalMilliseconds * Math.Pow(2, attempt));
        if (delay > TimeSpan.FromSeconds(30)) delay = TimeSpan.FromSeconds(30);
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter;
        if (value?.Delta is { } delta && delta >= TimeSpan.Zero) return delta;
        if (value?.Date is { } date) { var remaining = date - DateTimeOffset.UtcNow; return remaining >= TimeSpan.Zero ? remaining : TimeSpan.Zero; }
        return null;
    }

    private static bool IsTransient(HttpStatusCode status) => status is HttpStatusCode.RequestTimeout or (HttpStatusCode)425 or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError;

    private static async Task<byte[]> ReadBoundedBytesAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long declared && declared > limit) throw new TerraScaleReactiveProtocolException("The reactive response exceeds the configured byte bound.");
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        if (content.Headers.ContentLength is long expected && expected >= 0)
        {
            // A declared length sizes the destination exactly: one allocation,
            // no scratch buffer, and no intermediate growth.
            var payload = new byte[expected];
            var filled = 0;
            while (filled < payload.Length)
            {
                var read = await stream.ReadAsync(payload.AsMemory(filled), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                filled += read;
            }

            return filled == payload.Length ? payload : payload[..filled];
        }

        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var result = new MemoryStream(buffer.Length);
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                if (result.Length + read > limit) throw new TerraScaleReactiveProtocolException("The reactive response exceeds the configured byte bound.");
                result.Write(buffer, 0, read);
            }

            return result.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    internal static async Task<byte[]> ReadBoundedErrorBytesAsync(HttpContent content, CancellationToken cancellationToken) => await ReadBoundedBytesAsync(content, MaxErrorBytes, cancellationToken).ConfigureAwait(false);

    private static TerraScaleReactiveFrame? DecodeFrame(byte[] bytes, TerraScaleReactiveAttachment attachment)
    {
        if (bytes.Length == 0) return null;
        var reader = new ReactiveMsgpackReader(bytes); var envelope = ReadEnvelope(ref reader); reader.End(); return ConvertMessage(envelope, attachment);
    }

    private static TerraScaleReactiveSnapshot? DecodeSnapshot(byte[] bytes, TerraScaleReactiveAttachment attachment)
    {
        if (bytes.Length == 0) return null;
        var reader = new ReactiveMsgpackReader(bytes); var envelope = ReadEnvelope(ref reader); reader.End();
        return envelope.Message is ReactiveWireSnapshot wire ? ConvertSnapshot(wire.Subscription, wire.Snapshot, attachment) : throw new TerraScaleReactiveProtocolException("The snapshot response did not carry a snapshot frame.");
    }

    private static ReactiveWireEnvelope ReadEnvelope(ref ReactiveMsgpackReader reader)
    {
        CheckArray(ref reader, 7);
        if (reader.U32() != 1 || reader.U32() != 0 || reader.U32() != 0) throw new TerraScaleReactiveProtocolException("The reactive frame version or feature set is unsupported.");
        var tag = reader.U32(); var correlation = reader.U64(); var causation = reader.U64(); return new ReactiveWireEnvelope(tag, correlation, causation, ReadMessage(ref reader, tag));
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
    private static ReactiveWireMessage ReadSnapshotMessage(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 2); return new ReactiveWireSnapshot(reader.Text(1, 128), ReadSnapshot(ref reader)); }
    private static ReactiveWireMessage ReadDeltaMessage(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 7); var subscription = reader.Text(1, 128); var generation = reader.U64(); var from = reader.U64(); var to = reader.U64(); var operations = ReadOperations(ref reader); var digest = reader.Text(0, 64); var checkpoint = NewCheckpoint(reader.Binary(1, TerraScaleReactiveHttpClient.MaxCheckpointBytes)); return new ReactiveWireDelta(subscription, generation, from, to, operations, digest, checkpoint); }
    private static ReactiveWireMessage ReadResetMessage(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 3); return new ReactiveWireReset(reader.Text(1, 128), ReadReason(ref reader), ReadSnapshot(ref reader)); }
    private static ReactiveWireMessage ReadHeartbeat(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 0); return new ReactiveWireHeartbeat(); }
    private static ReactiveWireMessage ReadAuthorizationChanged(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 4); var subscription = reader.Text(1, 128); var authorizationGeneration = reader.U64(); var count = reader.Array(MaxArrayItems); for (var i = 0; i < count; i++) _ = reader.Text(1, 256); return new ReactiveWireAuthorizationChanged(subscription, authorizationGeneration, ReadSnapshot(ref reader)); }
    private static ReactiveWireMessage ReadRegion(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 1); return new ReactiveWireRegion(reader.Text(1, 64)); }
    private static ReactiveWireMessage ReadError(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 2); return new ReactiveWireError(reader.U32(), reader.NilOrText(256)); }
    private static ReactiveWireMessage ReadBackpressure(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 1); var mode = reader.U32(); if (mode is not 1 and not 2) throw new TerraScaleReactiveProtocolException("The reactive backpressure mode is unknown."); return new ReactiveWireBackpressure(mode); }

    private static ReactiveWireSnapshotValue ReadSnapshot(ref ReactiveMsgpackReader reader)
    {
        CheckArray(ref reader, 6); var generation = reader.U64(); var auth = reader.U64(); var revision = reader.U64(); var count = reader.Array(MaxArrayItems); var items = new List<TerraScaleReactiveItem>(count); for (var i = 0; i < count; i++) items.Add(ReadItem(ref reader)); var digest = reader.Text(0, 64); var checkpoint = NewCheckpoint(reader.Binary(1, MaxCheckpointBytes)); return new ReactiveWireSnapshotValue(generation, auth, revision, items, digest, checkpoint);
    }
    private static TerraScaleReactiveItem ReadItem(ref ReactiveMsgpackReader reader) { CheckArray(ref reader, 2); return new TerraScaleReactiveItem(reader.Text(1, 256), ReadFields(ref reader)); }
    private static IReadOnlyList<TerraScaleReactiveField> ReadFields(ref ReactiveMsgpackReader reader)
    {
        var count = reader.Array(MaxFields); var fields = new List<TerraScaleReactiveField>(count); var total = 0;
        for (var i = 0; i < count; i++) { CheckArray(ref reader, 2); var path = reader.Text(1, 256); var value = reader.Binary(0, MaxBinaryBytes); total = checked(total + value.Length); if (total > MaxBinaryBytes) throw new TerraScaleReactiveProtocolException("Projected field values exceed the item byte bound."); fields.Add(new TerraScaleReactiveField(path, value)); }
        return fields;
    }
    private static IReadOnlyList<TerraScaleReactiveOperation> ReadOperations(ref ReactiveMsgpackReader reader)
    {
        var count = reader.Array(1024); var operations = new List<TerraScaleReactiveOperation>(count);
        for (var i = 0; i < count; i++) { var shape = reader.Array(4); var discriminant = reader.U32(); switch (discriminant) { case 1: CheckArrayCount(shape, 4); operations.Add(ReadInsert(ref reader)); break; case 2: CheckArrayCount(shape, 3); operations.Add(new TerraScaleReactiveUpdate(ReadItemWithIdentity(ref reader))); break; case 3: CheckArrayCount(shape, 3); operations.Add(new TerraScaleReactivePatch(reader.Text(1, 256), ReadChanges(ref reader))); break; case 4: CheckArrayCount(shape, 2); operations.Add(new TerraScaleReactiveRemove(reader.Text(1, 256))); break; case 5: CheckArrayCount(shape, 3); operations.Add(new TerraScaleReactiveMove(reader.Text(1, 256), ReadPosition(ref reader))); break; default: throw new TerraScaleReactiveProtocolException("The reactive operation discriminant is unknown."); } }
        return operations;
    }
    private static TerraScaleReactiveItem ReadItemWithIdentity(ref ReactiveMsgpackReader reader) => new(reader.Text(1, 256), ReadFields(ref reader));
    private static TerraScaleReactiveInsert ReadInsert(ref ReactiveMsgpackReader reader)
    {
        var identity = reader.Text(1, 256);
        var position = ReadPosition(ref reader);
        return new TerraScaleReactiveInsert(new TerraScaleReactiveItem(identity, ReadFields(ref reader)), position);
    }
    private static int ReadPosition(ref ReactiveMsgpackReader reader)
    {
        var position = reader.U32();
        if (position > MaxArrayItems)
            throw new TerraScaleReactiveProtocolException("The reactive result position exceeds the item bound.");
        return (int)position;
    }
    private static IReadOnlyList<TerraScaleReactiveFieldChange> ReadChanges(ref ReactiveMsgpackReader reader)
    {
        var count = reader.Array(MaxFields); var changes = new List<TerraScaleReactiveFieldChange>(count); var total = 0;
        for (var i = 0; i < count; i++) { CheckArray(ref reader, 2); var path = reader.Text(1, 256); var value = reader.NilOrBinary(0, MaxBinaryBytes); if (value is { } bytes) total = checked(total + bytes.Length); if (total > MaxBinaryBytes) throw new TerraScaleReactiveProtocolException("Patch values exceed the byte bound."); changes.Add(new TerraScaleReactiveFieldChange(path, value)); }
        return changes;
    }
    private static TerraScaleReactiveResetReason ReadReason(ref ReactiveMsgpackReader reader) => reader.U32() switch { 1 => TerraScaleReactiveResetReason.CheckpointExpired, 2 => TerraScaleReactiveResetReason.RetentionAdvanced, 3 => TerraScaleReactiveResetReason.GenerationChanged, 4 => TerraScaleReactiveResetReason.CheckpointNotCurrent, 5 => TerraScaleReactiveResetReason.ViewDiverged, 6 => TerraScaleReactiveResetReason.Oversized, 7 => TerraScaleReactiveResetReason.InvalidOperations, 8 => TerraScaleReactiveResetReason.InvalidCheckpoint, 9 => TerraScaleReactiveResetReason.Backpressure, _ => throw new TerraScaleReactiveProtocolException("The reactive reset reason is unknown."), };

    private static TerraScaleReactiveFrame ConvertMessage(ReactiveWireEnvelope envelope, TerraScaleReactiveAttachment attachment) => envelope.Message switch
    {
        ReactiveWireSnapshot snapshot => ConvertSnapshotFrame(snapshot.Subscription, snapshot.Snapshot, attachment),
        ReactiveWireDelta delta when delta.Subscription == attachment.View && delta.Generation == attachment.Generation => new TerraScaleReactiveDelta(delta.Generation, delta.From, delta.To, delta.Operations, delta.Digest, delta.Checkpoint),
        ReactiveWireDelta => throw new TerraScaleReactiveProtocolException("The reactive delta belongs to another view or generation."),
        ReactiveWireReset reset when reset.Subscription == attachment.View => new TerraScaleReactiveReset(reset.Reason, ConvertSnapshot(reset.Subscription, reset.Snapshot, attachment)),
        ReactiveWireReset => throw new TerraScaleReactiveProtocolException("The reactive reset belongs to another subscription."),
        ReactiveWireAuthorizationChanged changed when changed.Subscription == attachment.View => new TerraScaleReactiveReset(TerraScaleReactiveResetReason.PermissionChanged, ConvertAuthorizationChanged(changed, attachment)),
        ReactiveWireAuthorizationChanged => throw new TerraScaleReactiveProtocolException("The authorization change belongs to another subscription."),
        ReactiveWireHeartbeat => new TerraScaleReactiveHeartbeat(),
        ReactiveWireRegion region => new TerraScaleReactiveRouteChange(region.Region),
        ReactiveWireError error => new TerraScaleReactiveError(MapErrorCode(error.Code), error.Detail),
        ReactiveWireBackpressure backpressure => new TerraScaleReactiveBackpressure(backpressure.Mode == 1 ? TerraScaleReactiveBackpressureMode.Pause : TerraScaleReactiveBackpressureMode.Resume),
        _ => throw new TerraScaleReactiveProtocolException("The reactive frame is not recognized."),
    };
    private static TerraScaleReactiveSnapshot ConvertSnapshot(string wireView, ReactiveWireSnapshotValue value, TerraScaleReactiveAttachment attachment) { if (wireView != attachment.View) throw new TerraScaleReactiveProtocolException("The reactive snapshot belongs to another view."); if (value.Generation != attachment.Generation) throw new TerraScaleReactiveProtocolException("The reactive snapshot belongs to another view generation."); attachment.ValidateCheckpoint(value.Checkpoint); return new TerraScaleReactiveSnapshot(value.Generation, value.AuthorizationGeneration, value.Revision, value.Items, value.Digest, value.Checkpoint); }
    private static TerraScaleReactiveSnapshot ConvertAuthorizationChanged(ReactiveWireAuthorizationChanged changed, TerraScaleReactiveAttachment attachment) { if (changed.AuthorizationGeneration != changed.Snapshot.AuthorizationGeneration) throw new TerraScaleReactiveProtocolException("The authorization change generation does not match its snapshot."); return ConvertSnapshot(changed.Subscription, changed.Snapshot, attachment); }
    private static TerraScaleReactiveSnapshotFrame ConvertSnapshotFrame(string wireView, ReactiveWireSnapshotValue value, TerraScaleReactiveAttachment attachment) => new(ConvertSnapshot(wireView, value, attachment));
    private static void CheckArray(ref ReactiveMsgpackReader reader, int expected) { if (reader.Array(expected) != expected) throw new TerraScaleReactiveProtocolException("The reactive frame has an invalid shape."); }
    private static void CheckArrayCount(int actual, int expected) { if (actual != expected) throw new TerraScaleReactiveProtocolException("The reactive operation has an invalid shape."); }
    private static TerraScaleReactiveErrorCode MapErrorCode(uint code) => code switch { 1 => TerraScaleReactiveErrorCode.UnsupportedVersion, 2 => TerraScaleReactiveErrorCode.MalformedFrame, 3 => TerraScaleReactiveErrorCode.AuthenticationExpired, 4 => TerraScaleReactiveErrorCode.PermissionDenied, 5 => TerraScaleReactiveErrorCode.CheckpointRefused, 6 => TerraScaleReactiveErrorCode.SlowConsumer, 7 => TerraScaleReactiveErrorCode.Overloaded, 8 => TerraScaleReactiveErrorCode.Unavailable, _ => throw new TerraScaleReactiveProtocolException("The reactive error code is unknown."), };

    internal static void ValidateText(string? value, string name, int maxBytes) { if (string.IsNullOrWhiteSpace(value) || StrictUtf8.GetByteCount(value) > maxBytes) throw new ArgumentException($"{name} must be non-empty and at most {maxBytes} UTF-8 bytes.", name); }
    internal static void ValidateIdentity(string? value) => ValidateText(value, nameof(value), 256);
    internal static void ValidateFieldPath(string? value) => ValidateText(value, nameof(value), 256);
    internal static void ValidateRegionHint(string value) => ValidateText(value, nameof(value), 64);
    private static TerraScaleReactiveTransport ParseTransport(string? value) =>
        string.Equals(value, "http", StringComparison.OrdinalIgnoreCase) ? TerraScaleReactiveTransport.Http :
        string.Equals(value, "websocket", StringComparison.OrdinalIgnoreCase) ? TerraScaleReactiveTransport.WebSocket :
        string.Equals(value, "sse", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "server-sent-events", StringComparison.OrdinalIgnoreCase) ? TerraScaleReactiveTransport.ServerSentEvents :
        throw new TerraScaleReactiveProtocolException("The reactive transport must be 'http', 'websocket', or 'sse'.");
    internal static string StrictDecode(ReadOnlySpan<byte> bytes) => StrictUtf8.GetString(bytes);
    internal static string? BoundText(string? value, int maxBytes)
    {
        if (value is null || StrictUtf8.GetByteCount(value) <= maxBytes) return value;
        var length = value.Length;
        while (length > 0 && StrictUtf8.GetByteCount(value.AsSpan(0, length)) > maxBytes) length--;
        return value[..length];
    }
    internal static void ValidateCheckpointToken(string? token) { ValidateText(token, nameof(token), 16 * 1024); if (StrictUtf8.GetByteCount(token!) is < 1 or > MaxCheckpointBytes) throw new TerraScaleReactiveProtocolException("The checkpoint exceeds its opaque byte bound."); }
    private static TerraScaleViewCheckpoint NewCheckpoint(byte[] bytes) => new(Convert.ToBase64String(bytes));
    private static Uri NormalizeOrigin(Uri uri, string name) { if (uri is null || !uri.IsAbsoluteUri || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/") throw new ArgumentException("The reactive origin must be an absolute URL without credentials, path, query, or fragment.", name); if (uri.Scheme is not ("http" or "https")) throw new ArgumentException("The reactive origin must use HTTP or HTTPS.", name); if (uri.Scheme == "http" && !IsLoopbackOrTestHost(uri.Host)) throw new ArgumentException("HTTP is allowed only for loopback or explicit test endpoints; production endpoints must use HTTPS.", name); return new UriBuilder(uri) { Path = string.Empty }.Uri; }
    private static bool IsLoopbackOrTestHost(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".test", StringComparison.OrdinalIgnoreCase) || System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);
    private static Uri AppendOffset(Uri log, ulong offset) => new UriBuilder(log) { Query = "offset=" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) }.Uri;

    internal sealed record ReactiveWireEnvelope(uint Tag, ulong Correlation, ulong Causation, ReactiveWireMessage Message);
    internal abstract record ReactiveWireMessage;
    internal sealed record ReactiveWireSnapshot(string Subscription, ReactiveWireSnapshotValue Snapshot) : ReactiveWireMessage;
    internal sealed record ReactiveWireDelta(string Subscription, ulong Generation, ulong From, ulong To, IReadOnlyList<TerraScaleReactiveOperation> Operations, string Digest, TerraScaleViewCheckpoint Checkpoint) : ReactiveWireMessage;
    internal sealed record ReactiveWireReset(string Subscription, TerraScaleReactiveResetReason Reason, ReactiveWireSnapshotValue Snapshot) : ReactiveWireMessage;
    internal sealed record ReactiveWireAuthorizationChanged(string Subscription, ulong AuthorizationGeneration, ReactiveWireSnapshotValue Snapshot) : ReactiveWireMessage;
    internal sealed record ReactiveWireHeartbeat : ReactiveWireMessage;
    internal sealed record ReactiveWireRegion(string Region) : ReactiveWireMessage;
    internal sealed record ReactiveWireError(uint Code, string? Detail) : ReactiveWireMessage;
    internal sealed record ReactiveWireBackpressure(uint Mode) : ReactiveWireMessage;
    internal sealed record ReactiveWireSnapshotValue(ulong Generation, ulong AuthorizationGeneration, ulong Revision, IReadOnlyList<TerraScaleReactiveItem> Items, string Digest, TerraScaleViewCheckpoint Checkpoint);
}

/// <summary>Transport selected for reactive delivery.</summary>
public enum TerraScaleReactiveTransport { Http, WebSocket, ServerSentEvents }
/// <summary>Options for the CDN-backed reactive HTTP transport.</summary>
public sealed record TerraScaleReactiveHttpClientOptions
{
    public Uri BaseAddress { get; set; } = new("https://sync.api.terrascale.tech", UriKind.Absolute);
    public Uri? ApiBaseAddress { get; set; }
    public string? ApiKey { get; set; }
    public string? BearerToken { get; set; }
    public Func<CancellationToken, ValueTask<string>>? BearerTokenProvider { get; set; }
    public string? RegionHint { get; set; }
    public int MaxRetryAttempts { get; set; } = 3;
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(250);
    public int MaxResponseBytes { get; set; } = TerraScaleReactiveHttpClient.MaxMessageBytes;
    public int MaxItems { get; set; } = TerraScaleReactiveHttpClient.MaxArrayItems;
    public int MaxSnapshotChunks { get; set; } = 1024;
    public TimeSpan PollWindow { get; set; } = TimeSpan.FromSeconds(25);
    internal void Validate()
    {
        if (BaseAddress is null || !BaseAddress.IsAbsoluteUri) throw new ArgumentException("The reactive base address must be absolute.", nameof(BaseAddress));
        if (ApiBaseAddress is not null && !ApiBaseAddress.IsAbsoluteUri) throw new ArgumentException("The reactive API base address must be absolute.", nameof(ApiBaseAddress));
        if (MaxRetryAttempts is < 0 or > 8) throw new ArgumentOutOfRangeException(nameof(MaxRetryAttempts));
        if (RetryDelay < TimeSpan.Zero || RetryDelay > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException(nameof(RetryDelay));
        if (MaxResponseBytes is < 1 or > TerraScaleReactiveHttpClient.MaxMessageBytes) throw new ArgumentOutOfRangeException(nameof(MaxResponseBytes));
        if (MaxItems is < 1 or > TerraScaleReactiveHttpClient.MaxArrayItems) throw new ArgumentOutOfRangeException(nameof(MaxItems));
        if (MaxSnapshotChunks is < 1 or > TerraScaleReactiveHttpClient.MaxArrayItems) throw new ArgumentOutOfRangeException(nameof(MaxSnapshotChunks));
        if (PollWindow <= TimeSpan.Zero || PollWindow > TimeSpan.FromSeconds(25)) throw new ArgumentOutOfRangeException(nameof(PollWindow));
        if (!string.IsNullOrWhiteSpace(ApiKey) && (!string.IsNullOrWhiteSpace(BearerToken) || BearerTokenProvider is not null)) throw new ArgumentException("Configure one authentication mode.");
        if (!string.IsNullOrWhiteSpace(BearerToken) && BearerTokenProvider is not null) throw new ArgumentException("Configure one bearer authentication mode.");
        if (RegionHint is not null) TerraScaleReactiveHttpClient.ValidateRegionHint(RegionHint);
    }
}

/// <summary>Immutable metadata returned when a view is attached.</summary>
public sealed record TerraScaleReactiveAttachment
{
    internal TerraScaleReactiveAttachment(string databaseId, string wireView, string clientSubscription, string statement, ReadOnlyMemory<byte> parameters, TerraScaleReactiveTransport transport, ulong generation, ulong offset, IReadOnlyList<Uri> chunks, Uri log, TerraScaleViewCheckpoint checkpoint, string capability, string capabilityHeader) { DatabaseId = databaseId; View = wireView; Subscription = clientSubscription; Statement = statement; Parameters = parameters.ToArray(); Transport = transport; Generation = generation; Offset = offset; SnapshotChunks = new ReadOnlyCollection<Uri>(chunks.ToArray()); Log = log; Checkpoint = checkpoint; Capability = capability; CapabilityHeader = capabilityHeader; }
    public string DatabaseId { get; }
    public string View { get; }
    public string Subscription { get; }
    public string Statement { get; }
    public ReadOnlyMemory<byte> Parameters { get; }
    public TerraScaleReactiveTransport Transport { get; }
    public ulong Generation { get; }
    public ulong Offset { get; }
    public IReadOnlyList<Uri> SnapshotChunks { get; }
    public Uri Log { get; }
    public TerraScaleViewCheckpoint Checkpoint { get; }
    public string Capability { get; }
    public string CapabilityHeader { get; }
    internal void ValidateCheckpoint(TerraScaleViewCheckpoint checkpoint) { TerraScaleReactiveHttpClient.ValidateCheckpointToken(checkpoint.Token); }
}

public sealed record TerraScaleReactiveField
{
    public TerraScaleReactiveField(string path, ReadOnlyMemory<byte> value) { TerraScaleReactiveHttpClient.ValidateFieldPath(path); if (value.Length > TerraScaleReactiveHttpClient.MaxBinaryBytes) throw new ArgumentOutOfRangeException(nameof(value)); Path = path; Value = value.ToArray(); }
    public string Path { get; }
    public ReadOnlyMemory<byte> Value { get; }
}
public sealed record TerraScaleReactiveItem
{
    public TerraScaleReactiveItem(string identity, IReadOnlyList<TerraScaleReactiveField> fields) { TerraScaleReactiveHttpClient.ValidateIdentity(identity); if (fields is null || fields.Count > TerraScaleReactiveHttpClient.MaxFields) throw new ArgumentException("An item may contain at most 1024 fields.", nameof(fields)); var copy = fields.ToArray(); if (copy.Any(field => field is null)) throw new ArgumentException("An item cannot contain null fields.", nameof(fields)); if (copy.Select(field => field.Path).Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("An item cannot contain duplicate fields.", nameof(fields)); Identity = identity; Fields = new ReadOnlyCollection<TerraScaleReactiveField>(copy); }
    public string Identity { get; }
    public IReadOnlyList<TerraScaleReactiveField> Fields { get; }
}
public sealed record TerraScaleReactiveFieldChange
{
    public TerraScaleReactiveFieldChange(string path, ReadOnlyMemory<byte>? value) { TerraScaleReactiveHttpClient.ValidateFieldPath(path); Path = path; Value = value?.ToArray(); }
    public string Path { get; }
    public ReadOnlyMemory<byte>? Value { get; }
}
public enum TerraScaleReactiveResetReason { PermissionChanged, AuthExpired, RegionMoved, SchemaChanged, GenerationChanged, HistoryExpired, SlowConsumer, Reconnect, CheckpointExpired, RetentionAdvanced, CheckpointNotCurrent, ViewDiverged, Oversized, InvalidOperations, InvalidCheckpoint, Backpressure }
public abstract record TerraScaleReactiveOperation(string Identity);
public sealed record TerraScaleReactiveInsert(TerraScaleReactiveItem Item, int Position) : TerraScaleReactiveOperation(Item.Identity);
public sealed record TerraScaleReactiveUpdate(TerraScaleReactiveItem Item) : TerraScaleReactiveOperation(Item.Identity);
public sealed record TerraScaleReactivePatch : TerraScaleReactiveOperation
{
    public TerraScaleReactivePatch(string itemIdentity, IReadOnlyList<TerraScaleReactiveFieldChange> changes) : base(itemIdentity) { TerraScaleReactiveHttpClient.ValidateIdentity(itemIdentity); if (changes is null || changes.Count > TerraScaleReactiveHttpClient.MaxFields) throw new ArgumentException("A patch may contain at most 1024 changes.", nameof(changes)); Changes = new ReadOnlyCollection<TerraScaleReactiveFieldChange>(changes.ToArray()); }
    public IReadOnlyList<TerraScaleReactiveFieldChange> Changes { get; }
}
public sealed record TerraScaleReactiveRemove(string ItemIdentity) : TerraScaleReactiveOperation(ItemIdentity);
public sealed record TerraScaleReactiveMove(string ItemIdentity, int Position) : TerraScaleReactiveOperation(ItemIdentity);
public sealed record TerraScaleReactiveSnapshot
{
    public TerraScaleReactiveSnapshot(ulong generation, ulong authorizationGeneration, ulong revision, IReadOnlyList<TerraScaleReactiveItem> items, string? digest, TerraScaleViewCheckpoint checkpoint) { if (items is null || items.Count > TerraScaleReactiveHttpClient.MaxArrayItems) throw new ArgumentException("A snapshot may contain at most 10000 items.", nameof(items)); if (digest is not null && Encoding.UTF8.GetByteCount(digest) > 64) throw new ArgumentException("A digest must be at most 64 UTF-8 bytes.", nameof(digest)); var copy = items.ToArray(); if (copy.Select(item => item.Identity).Distinct(StringComparer.Ordinal).Count() != copy.Length) throw new ArgumentException("A snapshot cannot contain duplicate identities.", nameof(items)); ViewGeneration = generation; AuthorizationGeneration = authorizationGeneration; Revision = revision; Items = new ReadOnlyCollection<TerraScaleReactiveItem>(copy); Digest = digest; Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)); }
    public ulong ViewGeneration { get; }
    public ulong AuthorizationGeneration { get; }
    public ulong Revision { get; }
    public IReadOnlyList<TerraScaleReactiveItem> Items { get; }
    public string? Digest { get; }
    public TerraScaleViewCheckpoint Checkpoint { get; }
}
public sealed record TerraScaleReactiveDelta : TerraScaleReactiveFrame
{
    public TerraScaleReactiveDelta(ulong viewGeneration, ulong fromRevision, ulong toRevision, IReadOnlyList<TerraScaleReactiveOperation> operations, string? digest, TerraScaleViewCheckpoint checkpoint) { if (toRevision < fromRevision) throw new ArgumentException("A delta cannot move its revision backwards.", nameof(toRevision)); if (operations is null || operations.Count > 1024) throw new ArgumentException("A delta may contain at most 1024 operations.", nameof(operations)); if (digest is not null && Encoding.UTF8.GetByteCount(digest) > 64) throw new ArgumentException("A digest must be at most 64 UTF-8 bytes.", nameof(digest)); ViewGeneration = viewGeneration; FromRevision = fromRevision; ToRevision = toRevision; Operations = new ReadOnlyCollection<TerraScaleReactiveOperation>(operations.ToArray()); Digest = digest; Checkpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)); }
    public ulong ViewGeneration { get; }
    public ulong FromRevision { get; }
    public ulong ToRevision { get; }
    public IReadOnlyList<TerraScaleReactiveOperation> Operations { get; }
    public string? Digest { get; }
    public TerraScaleViewCheckpoint Checkpoint { get; }
}
public sealed record TerraScaleReactiveSnapshotFrame(TerraScaleReactiveSnapshot Snapshot) : TerraScaleReactiveFrame;
public sealed record TerraScaleReactiveReset(TerraScaleReactiveResetReason Reason, TerraScaleReactiveSnapshot Snapshot) : TerraScaleReactiveFrame;
public sealed record TerraScaleReactiveHeartbeat : TerraScaleReactiveFrame;
public sealed record TerraScaleReactiveRouteChange(string Region) : TerraScaleReactiveFrame;
public enum TerraScaleReactiveErrorCode { UnsupportedVersion, MalformedFrame, AuthenticationExpired, PermissionDenied, CheckpointRefused, SlowConsumer, Overloaded, Unavailable }
public sealed record TerraScaleReactiveError(TerraScaleReactiveErrorCode Code, string? Detail) : TerraScaleReactiveFrame;
public enum TerraScaleReactiveBackpressureMode { Pause, Resume }
public sealed record TerraScaleReactiveBackpressure(TerraScaleReactiveBackpressureMode Mode) : TerraScaleReactiveFrame;
public sealed record TerraScaleReactivePollResult(TerraScaleReactiveFrame Frame, string? ETag) { public static TerraScaleReactivePollResult Reset(TerraScaleReactiveResetReason reason, TerraScaleReactiveSnapshot snapshot) => new(new TerraScaleReactiveReset(reason, snapshot), null); }
public abstract record TerraScaleReactiveFrame;

public sealed class TerraScaleReactiveSubscription : IAsyncDisposable
{
    private readonly TerraScaleReactiveHttpClient client;
    private (ulong Revision, string ETag)? retainedEtag;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object lifecycleGate = new();
    private readonly CancellationTokenSource lifetime = new();
    private Task? disposal;
    private ClientWebSocket? webSocket;
    private bool webSocketSubscribed;
    private HttpResponseMessage? sseResponse;
    private System.IO.StreamReader? sseReader;
    private volatile bool disposed;
    internal TerraScaleReactiveSubscription(TerraScaleReactiveHttpClient client, TerraScaleReactiveAttachment attachment, TerraScaleReactiveSnapshot snapshot) { this.client = client; Attachment = attachment; CurrentSnapshot = snapshot; if (snapshot.Revision != attachment.Offset) throw new TerraScaleReactiveProtocolException("The attach offset does not match the immutable snapshot revision."); Offset = attachment.Offset; SessionState = TerraScaleViewSessionState.Live; }
    public TerraScaleReactiveAttachment Attachment { get; private set; }
    public TerraScaleReactiveSnapshot CurrentSnapshot { get; private set; }
    public ulong Offset { get; private set; }
    public string? LastETag { get; private set; }
    public bool Paused { get; private set; }
    public TerraScaleViewSessionState SessionState { get; private set; }
    internal bool NeedsResume { get; private set; }
    internal bool IsDisposed => disposed;
    internal int RetainedEtagCount => retainedEtag is null ? 0 : 1;
    internal async Task<ClientWebSocket> EnsureWebSocketAsync(Uri uri, string capability, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (webSocket is { State: WebSocketState.Open } current)
            return current;

        DropWebSocket();
        var connected = await client.ConnectWebSocketAsync(uri, capability, cancellationToken).ConfigureAwait(false);
        if (disposed)
        {
            connected.Dispose();
            throw new ObjectDisposedException(nameof(TerraScaleReactiveSubscription));
        }

        webSocket = connected;
        webSocketSubscribed = false;
        return connected;
    }

    internal bool WebSocketSubscribed => webSocketSubscribed;
    internal void MarkWebSocketSubscribed() => webSocketSubscribed = true;

    /// <summary>
    /// Opens (or returns) the subscription's server-sent-events reader. The
    /// response stays open across polls; its stream ends only on reset or
    /// connection loss, which marks the subscription for reattach.
    /// </summary>
    internal async Task<System.IO.StreamReader> EnsureSseReaderAsync(Uri uri, string capability, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sseReader is { } current)
            return current;

        DropSse();
        var opened = await client.OpenSseStreamAsync(uri, capability, cancellationToken).ConfigureAwait(false);
        if (disposed)
        {
            opened.Reader.Dispose();
            opened.Response.Dispose();
            throw new ObjectDisposedException(nameof(TerraScaleReactiveSubscription));
        }

        sseResponse = opened.Response;
        sseReader = opened.Reader;
        return sseReader;
    }

    internal void MarkNeedsResume()
    {
        NeedsResume = true;
        SessionState = TerraScaleViewSessionState.Resuming;
        DropWebSocket();
        DropSse();
    }
    private void DropWebSocket()
    {
        var socket = Interlocked.Exchange(ref webSocket, null);
        webSocketSubscribed = false;
        socket?.Dispose();
    }

    private void DropSse()
    {
        var reader = Interlocked.Exchange(ref sseReader, null);
        var response = Interlocked.Exchange(ref sseResponse, null);
        reader?.Dispose();
        response?.Dispose();
    }
    public async Task<TerraScaleReactivePollResult?> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource linked;
        lock (lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        }

        using (linked)
        {
            await gate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                return await client.PollAsync(this, linked.Token).ConfigureAwait(false);
            }
            finally { gate.Release(); }
        }
    }
    public async IAsyncEnumerable<TerraScaleReactivePollResult> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken = default) { while (true) { var result = await ReadNextAsync(cancellationToken).ConfigureAwait(false); if (result is not null) yield return result; } }
    internal TerraScaleReactivePollResult Accept(TerraScaleReactiveFrame frame, string? etag)
    {
        switch (frame)
        {
            case TerraScaleReactiveDelta delta when delta.FromRevision == Offset && delta.ToRevision > Offset:
                Attachment.ValidateCheckpoint(delta.Checkpoint);
                var items = ApplyOperations(CurrentSnapshot.Items, delta.Operations);
                Offset = delta.ToRevision;
                CurrentSnapshot = new TerraScaleReactiveSnapshot(CurrentSnapshot.ViewGeneration, CurrentSnapshot.AuthorizationGeneration, Offset, items, delta.Digest, delta.Checkpoint);
                break;
            case TerraScaleReactiveDelta delta when delta.ToRevision == Offset:
                if (!string.Equals(delta.Digest, CurrentSnapshot.Digest, StringComparison.Ordinal))
                    throw new TerraScaleReactiveProtocolException("A same-revision reactive delta has a different result digest.");
                return new TerraScaleReactivePollResult(new TerraScaleReactiveHeartbeat(), etag);
            case TerraScaleReactiveDelta delta when delta.ToRevision < Offset: return new TerraScaleReactivePollResult(new TerraScaleReactiveHeartbeat(), etag);
            case TerraScaleReactiveDelta: throw new TerraScaleReactiveProtocolException("The reactive delta has a revision gap and requires a reset.");
            case TerraScaleReactiveSnapshotFrame snapshot: ValidateAuthorizationGeneration(snapshot.Snapshot); Attachment.ValidateCheckpoint(snapshot.Snapshot.Checkpoint); CurrentSnapshot = snapshot.Snapshot; Offset = snapshot.Snapshot.Revision; break;
            case TerraScaleReactiveReset reset: ValidateAuthorizationGeneration(reset.Snapshot); CurrentSnapshot = reset.Snapshot; Offset = reset.Snapshot.Revision; SessionState = TerraScaleViewSessionState.Resetting; break;
            case TerraScaleReactiveRouteChange: NeedsResume = true; break;
            case TerraScaleReactiveBackpressure backpressure: Paused = backpressure.Mode == TerraScaleReactiveBackpressureMode.Pause; break;
            case TerraScaleReactiveError { Code: TerraScaleReactiveErrorCode.AuthenticationExpired }: SessionState = TerraScaleViewSessionState.Resuming; break;
            case TerraScaleReactiveError { Code: TerraScaleReactiveErrorCode.PermissionDenied }: SessionState = TerraScaleViewSessionState.Resetting; break;
        }
        LastETag = etag; return new TerraScaleReactivePollResult(frame, etag);
    }
    private void ValidateAuthorizationGeneration(TerraScaleReactiveSnapshot snapshot)
    {
        if (snapshot.AuthorizationGeneration < CurrentSnapshot.AuthorizationGeneration)
            throw new TerraScaleReactiveProtocolException("The reactive snapshot moved to an older authorization generation.");
    }
    private static IReadOnlyList<TerraScaleReactiveItem> ApplyOperations(IReadOnlyList<TerraScaleReactiveItem> source, IReadOnlyList<TerraScaleReactiveOperation> operations)
    {
        var items = source.ToList();
        foreach (var operation in operations)
        {
            switch (operation)
            {
                case TerraScaleReactiveInsert insert:
                    if (items.Any(item => item.Identity == insert.Identity) || insert.Position < 0 || insert.Position > items.Count)
                        throw new TerraScaleReactiveProtocolException("The reactive insert does not describe a valid result position or identity.");
                    items.Insert(insert.Position, insert.Item);
                    break;
                case TerraScaleReactiveUpdate update:
                    ReplaceExisting(items, update.Identity, update.Item);
                    break;
                case TerraScaleReactivePatch patch:
                    var index = FindExisting(items, patch.Identity);
                    if (patch.Changes.Select(change => change.Path).Distinct(StringComparer.Ordinal).Count() != patch.Changes.Count)
                        throw new TerraScaleReactiveProtocolException("The reactive patch contains duplicate field paths.");
                    var fields = items[index].Fields.ToDictionary(field => field.Path, StringComparer.Ordinal);
                    foreach (var change in patch.Changes)
                    {
                        if (change.Value is { } value) fields[change.Path] = new TerraScaleReactiveField(change.Path, value);
                        else fields.Remove(change.Path);
                    }
                    items[index] = new TerraScaleReactiveItem(items[index].Identity, fields.Values.ToArray());
                    break;
                case TerraScaleReactiveRemove remove:
                    items.RemoveAt(FindExisting(items, remove.ItemIdentity));
                    break;
                case TerraScaleReactiveMove move:
                    var oldIndex = FindExisting(items, move.ItemIdentity);
                    var item = items[oldIndex];
                    items.RemoveAt(oldIndex);
                    if (move.Position < 0 || move.Position > items.Count) throw new TerraScaleReactiveProtocolException("The reactive move has an invalid result position.");
                    items.Insert(move.Position, item);
                    break;
                default:
                    throw new TerraScaleReactiveProtocolException("The reactive operation is not recognized.");
            }
        }
        if (items.Count > TerraScaleReactiveHttpClient.MaxArrayItems) throw new TerraScaleReactiveProtocolException("The reactive snapshot exceeds the item bound.");
        return items;
    }
    private static int FindExisting(IReadOnlyList<TerraScaleReactiveItem> items, string identity)
    {
        var index = items.ToList().FindIndex(item => item.Identity == identity);
        if (index < 0) throw new TerraScaleReactiveProtocolException("The reactive operation names an unknown identity.");
        return index;
    }
    private static void ReplaceExisting(List<TerraScaleReactiveItem> items, string identity, TerraScaleReactiveItem replacement) => items[FindExisting(items, identity)] = replacement;
    internal TerraScaleReactivePollResult AcceptHttpFrame(TerraScaleReactiveFrame frame, string? etag)
    {
        var revision = TerraScaleReactiveHttpClient.FrameRevision(frame, Offset);
        if (etag is not null)
        {
            // Polls are serialized and request the current checkpoint. The producer
            // returns a later revision or a reset, so historical data is never an
            // outstanding valid response. Reject it instead of trusting an evicted ETag.
            if (revision < Offset)
                throw new TerraScaleReactiveProtocolException("The reactive response precedes the current checkpoint and requires a reset.");
            if (retainedEtag is { } previous && previous.Revision == revision && !string.Equals(previous.ETag, etag, StringComparison.Ordinal))
                throw new TerraScaleReactiveProtocolException("An immutable reactive offset changed its ETag.");
        }

        var result = Accept(frame, etag);
        // Failed frames must not replace validation state for the accepted checkpoint.
        if (etag is not null) retainedEtag = (revision, etag);
        else if (frame is TerraScaleReactiveReset) retainedEtag = null;
        return result;
    }
    internal void Replace(TerraScaleReactiveAttachment attachment, TerraScaleReactiveSnapshot snapshot) { Attachment = attachment; CurrentSnapshot = snapshot; Offset = snapshot.Revision; retainedEtag = null; LastETag = null; NeedsResume = false; SessionState = TerraScaleViewSessionState.Live; }
    internal void SetSessionState(TerraScaleViewSessionState state) => SessionState = state;
    public ValueTask DisposeAsync()
    {
        lock (lifecycleGate)
        {
            if (disposal is null)
            {
                disposed = true;
                disposal = DisposeCoreAsync();
            }

            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            SessionState = TerraScaleViewSessionState.Closed;
            DropWebSocket();
            DropSse();
            lifetime.Dispose();
        }
        finally
        {
            // Queued reads may still be observing cancellation. Keep their semaphore
            // valid until they finish; it has no allocated wait handle.
            gate.Release();
        }
    }
}

public sealed class TerraScaleReactiveProtocolException(string message) : Exception(message);
public sealed class TerraScaleReactiveHttpException(HttpStatusCode statusCode, string message, string? code, string? detail, TimeSpan? retryAfter) : HttpRequestException(message, null, statusCode)
{
    public string? ErrorCode { get; } = code;
    public string? ErrorDetail { get; } = detail;
    public TimeSpan? RetryAfter { get; } = retryAfter;
    public static async Task<TerraScaleReactiveHttpException> CreateAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? code = null; string? detail = null;
        try { var bytes = await TerraScaleReactiveHttpClient.ReadBoundedErrorBytesAsync(response.Content, cancellationToken).ConfigureAwait(false); using var document = JsonDocument.Parse(bytes); if (document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object) { if (error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String) code = TerraScaleReactiveHttpClient.BoundText(c.GetString(), 128); if (error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String) detail = TerraScaleReactiveHttpClient.BoundText(m.GetString(), 256); } } catch (JsonException) { } catch (TerraScaleReactiveProtocolException) { }
        var retryAfter = response.Headers.RetryAfter?.Delta;
        if (retryAfter is null && response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            retryAfter = remaining >= TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return new TerraScaleReactiveHttpException(response.StatusCode, $"TerraScale reactive HTTP request returned {(int)response.StatusCode} {response.ReasonPhrase}.", code, detail, retryAfter);
    }
}

internal sealed record ReactiveAttachRequest(
    [property: JsonPropertyName("databaseId")] string DatabaseId,
    [property: JsonPropertyName("statement")] string Statement,
    [property: JsonPropertyName("parameters")] string Parameters,
    [property: JsonPropertyName("checkpoint")] string? Checkpoint,
    [property: JsonPropertyName("subscription")] string? Subscription,
    // OpenAPI publishes this as a signed int64. Keep the internal request
    // shape aligned with that contract even though the current client sends
    // the initial epoch (zero) only.
    [property: JsonPropertyName("entitlement_epoch")] long EntitlementEpoch);
internal sealed record ReactiveAttachResponse(
    [property: JsonPropertyName("view")] string? View,
    [property: JsonPropertyName("generation")] ulong Generation,
    [property: JsonPropertyName("snapshot_chunks")] string[]? SnapshotUrls,
    [property: JsonPropertyName("log")] string? Log,
    [property: JsonPropertyName("checkpoint")] string? Checkpoint,
    [property: JsonPropertyName("capability")] string? Capability,
    [property: JsonPropertyName("capability_header")] string? CapabilityHeader,
    [property: JsonPropertyName("offset")] ulong Offset);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ReactiveAttachRequest))]
[JsonSerializable(typeof(ReactiveAttachResponse))]
internal partial class ReactiveJsonContext : JsonSerializerContext;

internal ref struct ReactiveMsgpackReader
{
    private readonly ReadOnlySpan<byte> bytes;
    private int position;
    internal ReactiveMsgpackReader(byte[] bytes)
    {
        if (bytes.Length > TerraScaleReactiveHttpClient.MaxMessageBytes) throw new TerraScaleReactiveProtocolException("The reactive message exceeds the maximum size.");
        this.bytes = bytes;
        position = 0;
        try { Scan(bytes); } catch (OverflowException) { throw new TerraScaleReactiveProtocolException("The reactive MessagePack frame is malformed or exceeds a declared bound."); }
    }
    internal int Array(int maximum = TerraScaleReactiveHttpClient.MaxArrayItems) { var code = Take(); var count = code is >= 0x90 and <= 0x9f ? code & 15 : code switch { 0xdc => checked((int)U16()), 0xdd => checked((int)U32Raw()), _ => throw Invalid() }; if (count > maximum) throw Invalid(); return count; }
    internal uint U32() { var value = U64(); if (value > uint.MaxValue) throw Invalid(); return (uint)value; }
    internal ulong U64() { var code = Take(); return code switch { <= 0x7f => code, 0xcc => Take(), 0xcd => U16(), 0xce => U32Raw(), 0xcf => U64Raw(), _ => throw Invalid() }; }
    internal string Text(int minimum, int maximum) { var length = Length(Take(), true); if (length < minimum || length > maximum) throw Invalid(); try { return TerraScaleReactiveHttpClient.StrictDecode(Read(length)); } catch (DecoderFallbackException) { throw Invalid(); } }
    internal byte[] Binary(int minimum, int maximum) { var length = Length(Take(), false); if (length < minimum || length > maximum) throw Invalid(); return Read(length).ToArray(); }
    internal byte[]? NilOrBinary(int minimum, int maximum) { if (position < bytes.Length && bytes[position] == 0xc0) { position++; return null; } return Binary(minimum, maximum); }
    internal string? NilOrText(int maximum) { if (position < bytes.Length && bytes[position] == 0xc0) { position++; return null; } return Text(0, maximum); }
    internal void End() { if (position != bytes.Length) throw Invalid(); }
    private byte Take() { if (position >= bytes.Length) throw Invalid(); return bytes[position++]; }
    private ReadOnlySpan<byte> Read(int length) { if (length < 0 || position > bytes.Length - length) throw Invalid(); var value = bytes.Slice(position, length); position += length; return value; }
    private uint U16() => BinaryPrimitives.ReadUInt16BigEndian(Read(2));
    private uint U32Raw() => BinaryPrimitives.ReadUInt32BigEndian(Read(4));
    private ulong U64Raw() => BinaryPrimitives.ReadUInt64BigEndian(Read(8));
    private int Length(byte code, bool text)
    {
        var length = (text, code) switch
        {
            (true, >= 0xa0 and <= 0xbf) => code & 31,
            (true, 0xd9) or (false, 0xc4) => Take(),
            (true, 0xda) or (false, 0xc5) => checked((int)U16()),
            (true, 0xdb) or (false, 0xc6) => checked((int)U32Raw()),
            _ => throw Invalid(),
        };
        var max = text ? TerraScaleReactiveHttpClient.MaxStringBytes : TerraScaleReactiveHttpClient.MaxBinaryBytes;
        if (length > max) throw Invalid();
        return length;
    }
    private static TerraScaleReactiveProtocolException Invalid() => new("The reactive MessagePack frame is malformed or exceeds a declared bound.");
    private static void Scan(ReadOnlySpan<byte> bytes) { var scanner = new ReactiveMsgpackScanner(bytes); scanner.Value(0); scanner.End(); }
}

internal ref struct ReactiveMsgpackScanner
{
    private readonly ReadOnlySpan<byte> bytes;
    private int position;
    internal ReactiveMsgpackScanner(ReadOnlySpan<byte> bytes) { this.bytes = bytes; position = 0; }
    internal void Value(int depth)
    {
        if (depth > 64) throw Invalid();
        var code = Take();
        switch (code)
        {
            case <= 0x7f or >= 0xe0 or 0xc0: return;
            case >= 0xa0 and <= 0xbf: _ = Read(code & 31); return;
            case >= 0x90 and <= 0x9f: Count(code & 15, depth); return;
            case >= 0x80 and <= 0x8f: throw Invalid();
            case 0xca or 0xcb: throw Invalid();
            case 0xcc or 0xd0: _ = Read(1); return;
            case 0xcd or 0xd1: _ = Read(2); return;
            case 0xce or 0xd2: _ = Read(4); return;
            case 0xcf or 0xd3: _ = Read(8); return;
            case 0xd9 or 0xc4: _ = Read(Take()); return;
            case 0xda or 0xc5: _ = Read(BinaryPrimitives.ReadUInt16BigEndian(Read(2))); return;
            case 0xdb or 0xc6: _ = Read(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Read(4)))); return;
            case 0xdc: Count(checked((int)BinaryPrimitives.ReadUInt16BigEndian(Read(2))), depth); return;
            case 0xdd: Count(checked((int)BinaryPrimitives.ReadUInt32BigEndian(Read(4))), depth); return;
            case 0xde or 0xdf or 0xc1 or 0xc7 or 0xc8 or 0xc9: throw Invalid();
            default: throw Invalid();
        }
    }
    internal void End() { if (position != bytes.Length) throw Invalid(); }
    private void Count(int count, int depth) { if (count > TerraScaleReactiveHttpClient.MaxArrayItems) throw Invalid(); for (var i = 0; i < count; i++) Value(depth + 1); }
    private byte Take() { if (position >= bytes.Length) throw Invalid(); return bytes[position++]; }
    private ReadOnlySpan<byte> Read(int count) { if (count < 0 || position > bytes.Length - count) throw Invalid(); var result = bytes.Slice(position, count); position += count; return result; }
    private static TerraScaleReactiveProtocolException Invalid() => new("The reactive MessagePack frame is malformed or exceeds a declared bound.");
}
