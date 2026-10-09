using System.Buffers;
using System.Globalization;
using System.Runtime.ExceptionServices;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Http.Features;
using TerraBase.Configuration;
using TerraBase.Engine;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication.V1;
using TerraBase.Hosting;

namespace TerraBase.Features.Replication;

/// <summary>
/// The catalog primary's side of <c>CatalogForwarding.Forward</c>: serves a request a follower forwarded through this member's
/// whole HTTP pipeline, as if its client listener had accepted it and answering as the follower's region, so it is
/// authenticated, authorized and validated like any other; the pipeline serves a forwarded request only on an endpoint the
/// primary serves (<see cref="ServedByCatalogPrimary"/>; 404 otherwise). The response is buffered, at most
/// <see cref="ForwardingProtocol.MaxResponseBytes"/>, and sent after this catalog's head.
/// </summary>
internal sealed class ForwardedRequests(TerraBaseOptions options, IHttpContextFactory contexts, ISystemReplication system, IMemoryBudget budget)
    : CatalogForwarding.CatalogForwardingBase
{
    private static readonly SearchValues<char> Token = SearchValues.Create("!#$%&'*+-.^_`|~0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");
    private static readonly SearchValues<char> Controls = SearchValues.Create(
        Enumerable.Range(0, 0x20).Where(character => character != '\t').Select(character => (char)character).Append('\x7f').ToArray());

    private RequestDelegate? _pipeline;
    private int _inFlight;

    /// <summary>Takes the application pipeline from its first middleware.</summary>
    public RequestDelegate Capture(RequestDelegate next) => _pipeline = next;

    public override async Task Forward(ForwardRequest request, IServerStreamWriter<ForwardResponse> responseStream, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        ArgumentNullException.ThrowIfNull(context);
        if (options.CatalogPrimary is not null || _pipeline is not { } pipeline)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "this member is not a catalog primary"));
        }

        var length = Validate(request);
        try
        {
            if (Interlocked.Increment(ref _inFlight) > ForwardingProtocol.MaxInFlight)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "the catalog primary serves too many forwarded requests"));
            }

            // The body, and one response chunk's envelope.
            using var reservation = budget.TryReserve(MemoryLane.Foreground, length + ForwardingProtocol.ChunkBytes + (2 * ApplicationEnvelope.HeaderBytes))
                ?? throw new RpcException(new Status(StatusCode.ResourceExhausted, "the catalog primary has no memory left for a forwarded request"));
            await ServeAsync(pipeline, request, Body(request, length), responseStream, context.CancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    private async Task ServeAsync(
        RequestDelegate pipeline, ForwardRequest request, byte[] body, IServerStreamWriter<ForwardResponse> responses, CancellationToken cancellationToken)
    {
        var headers = new HeaderDictionary();
        foreach (var header in request.Headers)
        {
            headers.Append(header.Name, header.Value);
        }

        // A response of up to 64 KiB is reserved before the request runs, so a write it commits is never answered as refused.
        using var buffer = new ResponseBuffer(budget);
        if (!buffer.Reserve(ResponseBuffer.InitialBytes))
        {
            throw new RpcException(new Status(StatusCode.ResourceExhausted, "the catalog primary has no memory left for a forwarded response"));
        }

        var response = new BufferedResponse();
        var responseBody = new StreamResponseBodyFeature(buffer);
        var features = new FeatureCollection();
        features.Set<IHttpRequestFeature>(new HttpRequestFeature
        {
            Protocol = HttpProtocol.Http2,
            Scheme = "https",
            Method = request.Method,
            Path = request.Path,
            QueryString = request.Query,
            RawTarget = request.Path + request.Query,
            Headers = headers,
            Body = new MemoryStream(body, writable: false),
        });
        features.Set<IHttpResponseFeature>(response);
        features.Set<IHttpResponseBodyFeature>(responseBody);
        features.Set<IHttpRequestLifetimeFeature>(new HttpRequestLifetimeFeature { RequestAborted = cancellationToken });
        features.Set(Listeners.AcceptedBy(TerraBaseListener.Client));
        features.Set(new AnsweringRegion(request.Region));
        features.Set(ForwardedRequest.Instance);
        var http = contexts.Create(features);
        try
        {
            await pipeline(http).ConfigureAwait(false);
            await response.StartAsync().ConfigureAwait(false);
            await responseBody.CompleteAsync().ConfigureAwait(false);
            if (buffer.Refused)
            {
                throw new RpcException(new Status(StatusCode.ResourceExhausted, "the response exceeds 16 MiB or the catalog primary's memory"));
            }

            // Every write of the request committed before its pipeline returned: the follower waits for this head.
            var catalog = await system.GetHeadAsync(cancellationToken).ConfigureAwait(false);
            var head = new ForwardHead
            {
                Status = (uint)response.StatusCode,
                BodyLength = (ulong)buffer.Length,
                Origin = UnsafeByteOperations.UnsafeWrap(catalog.Origin),
                Sequence = (ulong)catalog.Sequence,
            };
            foreach (var (name, values) in response.Headers)
            {
                foreach (var value in values)
                {
                    head.Headers.Add(new ForwardedHeader { Name = name, Value = value ?? "" });
                }
            }

            await responses.WriteAsync(new ForwardResponse { Head = head }, cancellationToken).ConfigureAwait(false);
            var bytes = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
            for (var offset = 0; offset < bytes.Length; offset += ForwardingProtocol.ChunkBytes)
            {
                var chunk = bytes.Slice(offset, Math.Min(ForwardingProtocol.ChunkBytes, bytes.Length - offset));
                await responses.WriteAsync(new ForwardResponse { Body = UnsafeByteOperations.UnsafeWrap(ApplicationEnvelope.Encode(chunk.Span)) }, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                await response.CompleteAsync().ConfigureAwait(false);
            }
            finally
            {
                contexts.Dispose(http);
            }
        }
    }

    // The request must be one an HTTP/2 server could have accepted from a follower of a valid region; its body is as long as
    // its only Content-Length, read before anything is decoded.
    private static int Validate(ForwardRequest request)
    {
        var lengths = request.Headers.Where(header => string.Equals(header.Name, "Content-Length", StringComparison.OrdinalIgnoreCase)).ToList();
        var length = 0;
        if (request.Protocol != ForwardingProtocol.Version || !TerraBaseOptions.IsRegion(request.Region)
            || lengths.Count > 1 || (lengths.Count == 0 && !request.Body.IsEmpty)
            || (lengths.Count == 1 && (!int.TryParse(lengths[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out length)
                || length > ForwardingProtocol.MaxRequestBytes || length == 0 != request.Body.IsEmpty))
            || request.Method.Length is 0 or > 16 || request.Method.AsSpan().ContainsAnyExcept(Token)
            || !request.Path.StartsWith('/') || request.Path.Length + request.Query.Length > 16 * 1024
            || (request.Query.Length > 0 && request.Query[0] != '?')
            || request.Path.AsSpan().ContainsAny(Controls) || request.Query.AsSpan().ContainsAny(Controls) || request.Headers.Count > 256
            || request.Headers.Any(header => header.Name.Length == 0 || header.Name.AsSpan().ContainsAnyExcept(Token) || header.Value.AsSpan().ContainsAny(Controls)))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "the forwarded request is not a valid HTTP request of protocol 2"));
        }

        return length;
    }

    private static byte[] Body(ForwardRequest request, int length)
    {
        try
        {
            return length == 0 ? []
                : ApplicationEnvelope.Decode(request.Body.Span, length) is { } body && body.Length == length ? body
                : throw new InvalidDataException("the forwarded body is shorter than its Content-Length");
        }
        catch (InvalidDataException error)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, error.Message));
        }
    }

    // Marks a request a follower forwarded, for the pipeline's endpoint check.
    internal sealed class ForwardedRequest
    {
        public static ForwardedRequest Instance { get; } = new();
    }

    // A buffered response: it never starts while the pipeline runs, so error handling can still replace it; OnStarting callbacks
    // run once it completes, and OnCompleted ones (disposing request services) after it was sent.
    private sealed class BufferedResponse : HttpResponseFeature
    {
        private readonly Stack<(Func<object, Task> Callback, object State)> _starting = new();
        private readonly Stack<(Func<object, Task> Callback, object State)> _completed = new();

        public override void OnStarting(Func<object, Task> callback, object state) => _starting.Push((callback, state));

        public override void OnCompleted(Func<object, Task> callback, object state) => _completed.Push((callback, state));

        public async Task StartAsync()
        {
            while (_starting.TryPop(out var starting))
            {
                await starting.Callback(starting.State).ConfigureAwait(false);
            }
        }

        // Every callback runs, as in Kestrel: an early one disposes the request services. The first failure is rethrown.
        public async Task CompleteAsync()
        {
            ExceptionDispatchInfo? failed = null;
            while (_completed.TryPop(out var completed))
            {
                try
                {
                    await completed.Callback(completed.State).ConfigureAwait(false);
                }
                catch (Exception error)
                {
                    failed ??= ExceptionDispatchInfo.Capture(error);
                }
            }

            failed?.Throw();
        }
    }

    // The response body, at most MaxResponseBytes, reserved from the memory budget as it grows; past either it drops the
    // rest and is refused. Its capacity is set to exactly the reservation, so MemoryStream never grows it on its own, and
    // the old reservation is held while the buffer is copied into the new one. Every write reaches Write(byte[], int, int)
    // or WriteByte: MemoryStream routes a derived type's span and async writes there.
    private sealed class ResponseBuffer(IMemoryBudget budget) : MemoryStream
    {
        public const int InitialBytes = 64 * 1024;

        private IMemoryReservation? _reservation;

        public bool Refused { get; private set; }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Reserve(Position + count))
            {
                base.Write(buffer, offset, count);
            }
        }

        public override void WriteByte(byte value)
        {
            if (Reserve(Position + 1))
            {
                base.WriteByte(value);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _reservation?.Dispose();
            }

            base.Dispose(disposing);
        }

        public bool Reserve(long length)
        {
            var reserved = _reservation?.Bytes ?? 0;
            if (Refused || length <= reserved)
            {
                return !Refused;
            }

            var next = length <= ForwardingProtocol.MaxResponseBytes
                ? budget.TryReserve(MemoryLane.Foreground, Math.Clamp(Math.Max(2 * reserved, InitialBytes), length, ForwardingProtocol.MaxResponseBytes))
                : null;
            if (next is null)
            {
                Refused = true;
                return false;
            }

            Capacity = checked((int)next.Bytes);
            _reservation?.Dispose();
            _reservation = next;
            return true;
        }
    }
}
