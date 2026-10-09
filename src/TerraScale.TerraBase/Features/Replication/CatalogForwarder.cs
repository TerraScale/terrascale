using System.Collections.Frozen;
using System.Diagnostics.Metrics;
using System.Globalization;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Management;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>
/// Endpoint metadata: a catalog follower serves the endpoint's requests itself, and forwards one to its catalog primary when
/// serving it would write the catalog (docs/decisions/0009). Only for authenticated client-listener endpoints that never read
/// their request body, so it is still there to forward. Management API endpoints (<see cref="ListenerMetadata.Management"/>)
/// are always forwarded, without it.
/// </summary>
public sealed class ServedByCatalogPrimary
{
    private ServedByCatalogPrimary()
    {
    }

    /// <summary>The metadata.</summary>
    public static ServedByCatalogPrimary WhenWriting { get; } = new();

    /// <summary>Forwards before the handler reads a body or performs an online authority check.</summary>
    public static ServedByCatalogPrimary BeforeHandling { get; } = new();

    /// <summary>Whether a follower forwards every request to <paramref name="endpoint"/>: a Management API endpoint.</summary>
    internal static bool Always(Endpoint? endpoint) => ReferenceEquals(endpoint?.Metadata.GetMetadata<ListenerMetadata>(), ListenerMetadata.Management)
        || ReferenceEquals(endpoint?.Metadata.GetMetadata<ServedByCatalogPrimary>(), BeforeHandling);

    /// <summary>Whether the catalog primary serves forwarded requests to <paramref name="endpoint"/>.</summary>
    internal static bool Serves(Endpoint? endpoint) => Always(endpoint) || endpoint?.Metadata.GetMetadata<ServedByCatalogPrimary>() is not null;
}

/// <summary>Bounds of <c>CatalogForwarding.Forward</c>.</summary>
internal static class ForwardingProtocol
{
    public const uint Version = 1;

    /// <summary>Largest forwarded request body, the Documents and Management APIs' own bound.</summary>
    public const int MaxRequestBytes = ReplicationProtocol.MaxApplicationBytes;

    /// <summary>Largest forwarded response body: query and schema results hold at most 8 MiB.</summary>
    public const int MaxResponseBytes = 16 * 1024 * 1024;

    /// <summary>Body bytes per response envelope.</summary>
    public const int ChunkBytes = 1024 * 1024;

    /// <summary>Requests a follower forwards, and a primary serves forwarded, at once.</summary>
    public const int MaxInFlight = 64;

    /// <summary>Longest a follower waits to apply what a forwarded request wrote before it answers anyway.</summary>
    public static TimeSpan ApplyTimeout { get; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Longest a forwarded request may take, response included; past it the follower answers 503 or resets the connection,
    /// and the primary's request is cancelled. Its own bounds (5-second queries) are far below it.
    /// </summary>
    public static TimeSpan Deadline { get; } = TimeSpan.FromSeconds(60);

    // Connection-specific headers, never forwarded either way; Content-Length is set from the forwarded body. The follower
    // checked the region pin against its own region, which the primary answers as.
    private static readonly FrozenSet<string> Connection = FrozenSet.Create(
        StringComparer.OrdinalIgnoreCase, "Connection", "Content-Length", "Keep-Alive", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        "X-TerraScale-Region-Pin");

    /// <summary>
    /// The headers of <paramref name="headers"/> to forward: none that is connection-specific, including those its
    /// <c>Connection</c> header names (HTTP/1.1).
    /// </summary>
    public static IEnumerable<(string Name, string Value)> Forwarded(IEnumerable<(string Name, string Value)> headers)
    {
        var all = headers.ToList();
        var nominated = all.Where(header => string.Equals(header.Name, "Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(header => header.Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return all.Where(header => !header.Name.StartsWith(':') && !Connection.Contains(header.Name) && !nominated.Contains(header.Name));
    }
}

/// <summary>
/// A catalog follower's side of <c>CatalogForwarding.Forward</c>: sends an authenticated client-listener request the catalog
/// primary serves to the primary as it arrived, and answers with the primary's response once this follower applied the
/// primary's catalog head after it (within <see cref="ForwardingProtocol.ApplyTimeout"/>), so that a key or database the
/// request created already serves here. A primary that cannot serve it answers 503 <c>catalog_primary_unavailable</c>.
/// </summary>
internal sealed partial class CatalogForwarder
{
    /// <summary>Marks a request this follower relayed to the catalog primary.</summary>
    internal sealed class Relayed
    {
        public static Relayed Instance { get; } = new();
    }

    private readonly TerraBaseOptions _options;
    private readonly PeerCredentials _credentials;
    private readonly CatalogFollower _follower;
    private readonly IMemoryBudget _budget;
    private readonly TimeProvider _time;
    private readonly ILogger<CatalogForwarder> _logger;
    private readonly Counter<long> _forwarded;
    private int _inFlight;

    public CatalogForwarder(
        TerraBaseOptions options, PeerCredentials credentials, CatalogFollower follower, IMemoryBudget budget, TimeProvider time,
        ILogger<CatalogForwarder> logger, IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        (_options, _credentials, _follower, _budget, _time, _logger) = (options, credentials, follower, budget, time, logger);
        _forwarded = meters.Create(Telemetry.MeterName).CreateCounter<long>("terrabase.catalog.forwarded", "{request}",
            "Requests a catalog follower forwarded to its primary, by result: answered, lagging (answered before this follower applied what it wrote) or unavailable (503, or a reset once the response started).");
    }

    /// <summary>
    /// Serves a request to a <see cref="ServedByCatalogPrimary.WhenWriting"/> endpoint here, and forwards it when that is refused
    /// because it would write the catalog; it wrote nothing then.
    /// </summary>
    public async Task ForwardWhenWritingAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (CatalogFollowerException) when (!context.Response.HasStarted)
        {
            context.Response.Clear();
            await ForwardAsync(context).ConfigureAwait(false);
        }
    }

    public async Task ForwardAsync(HttpContext context)
    {
        // The primary serves, resolves and meters the request; this member only relays it.
        context.Features.Set(Relayed.Instance);
        var aborted = context.RequestAborted;
        try
        {
            if (Interlocked.Increment(ref _inFlight) > ForwardingProtocol.MaxInFlight)
            {
                await UnavailableAsync(context, "capacity", "this member forwards too many requests to the catalog primary at once").ConfigureAwait(false);
                return;
            }

            var declared = context.Request.ContentLength;
            var bound = context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == false ? 0 : declared ?? ForwardingProtocol.MaxRequestBytes;
            if (bound > ForwardingProtocol.MaxRequestBytes)
            {
                await TooLargeAsync(context).ConfigureAwait(false);
                return;
            }

            // The body as read (twice its size while a body of unknown length grows), its envelope and the serialized message,
            // then a held-back response chunk and the next one's envelope and decoded bytes.
            using var reservation = _budget.TryReserve(MemoryLane.Foreground,
                (4 * bound) + (3 * ForwardingProtocol.ChunkBytes) + (2 * ApplicationEnvelope.HeaderBytes));
            if (reservation is null)
            {
                await UnavailableAsync(context, "capacity", "this member has no memory left to forward the request").ConfigureAwait(false);
                return;
            }

            // The deadline covers reading the body too: a slow upload holds a slot and a reservation no longer than any request.
            var deadline = _time.GetUtcNow().UtcDateTime + ForwardingProtocol.Deadline;
            using var expiry = new CancellationTokenSource(ForwardingProtocol.Deadline, _time);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(aborted, expiry.Token);
            ForwardRequest? request;
            try
            {
                request = await RequestAsync(context, declared, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                context.Response.Headers.CacheControl = "no-store";
                await BadRequests.WriteAsync(context, StatusCodes.Status408RequestTimeout, "request_timeout", "the request body did not arrive in time", op: null)
                    .ConfigureAwait(false);
                return;
            }

            if (request is null)
            {
                await TooLargeAsync(context).ConfigureAwait(false);
                return;
            }

            using var lease = _credentials.Capture();
            // ponytail: a channel (TLS handshake) per request; pool one per credential generation if forwarded traffic grows.
            using var channel = PeerChannel.Create(_options.CatalogPrimary!, lease);
            using var call = new CatalogForwarding.CatalogForwardingClient(channel).Forward(request, deadline: deadline, cancellationToken: bounded.Token);
            ForwardHead head;
            try
            {
                head = await call.ResponseStream.MoveNext(bounded.Token).ConfigureAwait(false) && call.ResponseStream.Current.Head is { } first
                    && first.Status is >= 200 and <= 599 && first.BodyLength <= ForwardingProtocol.MaxResponseBytes && first.Origin.Length == 16
                        ? first
                        : throw new InvalidDataException("the catalog primary did not start its response with a valid head");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.InvalidArgument && !aborted.IsCancellationRequested)
            {
                // The primary refused it before serving it; it never will, so a retry would not help.
                context.Response.Headers.CacheControl = "no-store";
                await BadRequests.WriteAsync(context, StatusCodes.Status400BadRequest, "invalid_request", "the catalog primary cannot serve this request", op: null)
                    .ConfigureAwait(false);
                return;
            }
            catch (Exception error) when (error is RpcException or InvalidDataException && !aborted.IsCancellationRequested)
            {
                LogUnavailable(_logger, error);
                await UnavailableAsync(context, "catalog_primary_unavailable", "the catalog primary could not serve the request").ConfigureAwait(false);
                return;
            }

            var applied = await WaitAppliedAsync(head, bounded.Token, aborted).ConfigureAwait(false);
            if (await RelayAsync(context, call.ResponseStream, head, bounded.Token, aborted).ConfigureAwait(false))
            {
                _forwarded.Add(1, new KeyValuePair<string, object?>("result", applied ? "answered" : "lagging"));
            }
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    // Null when the body is larger than a forwarded request holds.
    private async Task<ForwardRequest?> RequestAsync(HttpContext context, long? declared, CancellationToken cancellationToken)
    {
        if (await ReadBodyAsync(context.Request.Body, declared, cancellationToken).ConfigureAwait(false) is not { } body)
        {
            return null;
        }

        var request = new ForwardRequest
        {
            Protocol = ForwardingProtocol.Version,
            Method = context.Request.Method,
            Path = context.Features.Get<LegacyManagementRoute>()?.OriginalPath ?? context.Request.Path.Value ?? "/",
            Query = context.Request.QueryString.Value ?? "",
            Body = body.Length > 0 ? UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(body)) : ByteString.Empty,
            Region = _options.Region,
        };
        foreach (var (name, value) in ForwardingProtocol.Forwarded(
            context.Request.Headers.SelectMany(header => header.Value.Select(value => (header.Key, value ?? "")))))
        {
            request.Headers.Add(new ForwardedHeader { Name = name, Value = value });
        }

        if (body.Length > 0 || declared is not null)
        {
            request.Headers.Add(new ForwardedHeader { Name = "Content-Length", Value = body.Length.ToString(CultureInfo.InvariantCulture) });
        }

        return request;
    }

    // The server enforces a declared Content-Length; a body of unknown length is read up to the bound.
    private static async Task<byte[]?> ReadBodyAsync(Stream body, long? declared, CancellationToken cancellationToken)
    {
        if (declared is { } length)
        {
            var exact = new byte[length];
            await body.ReadExactlyAsync(exact, cancellationToken).ConfigureAwait(false);
            return exact;
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ForwardingProtocol.MaxRequestBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    // Lets this follower serve what the request wrote before it answers: false when it answers before it applied it, past the
    // timeout or because it does not follow the primary's journal now. The forwarding deadline ends the wait too; the relay
    // then answers 503.
    private async Task<bool> WaitAppliedAsync(ForwardHead head, CancellationToken bounded, CancellationToken aborted)
    {
        var sequence = (long)Math.Min(head.Sequence, long.MaxValue);
        if (_follower.Follows(head.Origin.Memory))
        {
            using var timeout = new CancellationTokenSource(ForwardingProtocol.ApplyTimeout, _time);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(bounded, timeout.Token);
            try
            {
                await _follower.WaitAppliedAsync(head.Origin.Memory, sequence, wait.Token).ConfigureAwait(false);
                return true;
            }
            catch (OperationCanceledException) when (!aborted.IsCancellationRequested)
            {
                // Lagging, below.
            }
        }

        LogLagging(_logger, sequence);
        return false;
    }

    // Writes the primary's response within the forwarding deadline, holding back each body chunk until the next frame or the
    // call's successful end arrived: a call that fails, or sends more or less than its head declared, is answered 503 while
    // nothing of it went out, and resets the connection after. True when the whole response went out.
    private async Task<bool> RelayAsync(
        HttpContext context, IAsyncStreamReader<ForwardResponse> frames, ForwardHead head, CancellationToken bounded, CancellationToken aborted)
    {
        var response = context.Response;
        response.StatusCode = (int)head.Status;
        foreach (var (name, value) in ForwardingProtocol.Forwarded(head.Headers.Select(header => (header.Name, header.Value))))
        {
            response.Headers.Append(name, value);
        }

        if (head.BodyLength > 0)
        {
            response.ContentLength = (long)head.BodyLength;
        }

        try
        {
            byte[]? held = null;
            var received = 0UL;
            while (await frames.MoveNext(bounded).ConfigureAwait(false))
            {
                var chunk = frames.Current.FrameCase == ForwardResponse.FrameOneofCase.Body
                    ? ApplicationEnvelope.Decode(frames.Current.Body.Span, ForwardingProtocol.ChunkBytes)
                    : throw new InvalidDataException("the catalog primary sent a second head");
                received += (ulong)chunk.Length;
                if (received > head.BodyLength)
                {
                    throw new InvalidDataException("the catalog primary sent more body than its head declared");
                }

                if (held is not null)
                {
                    await response.Body.WriteAsync(held, bounded).ConfigureAwait(false);
                }

                held = chunk;
            }

            if (received != head.BodyLength)
            {
                throw new InvalidDataException("the catalog primary ended its response early");
            }

            if (held is not null)
            {
                await response.Body.WriteAsync(held, bounded).ConfigureAwait(false);
            }

            return true;
        }
        catch (Exception error) when (error is RpcException or InvalidDataException or OperationCanceledException && !aborted.IsCancellationRequested)
        {
            // OperationCanceledException: the deadline passed while a client that stopped reading held a write.
            if (!response.HasStarted)
            {
                response.Clear();
                LogUnavailable(_logger, error);
                await UnavailableAsync(context, "catalog_primary_unavailable", "the catalog primary could not serve the request").ConfigureAwait(false);
                return false;
            }

            // The status went out already: only a reset tells the client the response is incomplete.
            _forwarded.Add(1, new KeyValuePair<string, object?>("result", "unavailable"));
            LogBroken(_logger, error);
            context.Abort();
            return false;
        }
    }

    private async Task UnavailableAsync(HttpContext context, string code, string message)
    {
        _forwarded.Add(1, new KeyValuePair<string, object?>("result", "unavailable"));
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "1";
        await BadRequests.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, code, message, op: null).ConfigureAwait(false);
    }

    private static Task TooLargeAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        return BadRequests.WriteAsync(
            context, StatusCodes.Status413PayloadTooLarge, "payload_too_large", "a request the catalog primary serves has a body of at most 4 MiB", op: null);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The catalog primary could not serve a forwarded request; answered 503")]
    private static partial void LogUnavailable(ILogger logger, Exception error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A forwarded request was answered before this follower applied catalog group {Sequence}")]
    private static partial void LogLagging(ILogger logger, long sequence);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The catalog primary's response to a forwarded request broke off; the client connection was reset")]
    private static partial void LogBroken(ILogger logger, Exception error);
}
