using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using TerraScale.DotNet.TerraBase;

namespace ApiBench;

/// <summary>Counts successful TCP connects of handlers built from the SDK's transport configuration.</summary>
internal sealed class ParityConnections
{
    private long connects;

    /// <summary>False until <see cref="CreateHandler"/> installs the counter; a fake transport leaves the count unknown.</summary>
    internal bool Counted { get; private set; }

    internal long Count => Interlocked.Read(ref connects);

    // Keeps TerraBaseHttp.CreateHandler's redirect, cookie, connect-timeout, pool, TLS and proxy settings; only the
    // connect step is replaced, with the documented default socket connect.
    internal SocketsHttpHandler CreateHandler()
    {
        var handler = TerraBaseHttp.CreateHandler();
        handler.ConnectCallback = ConnectAsync;
        Counted = true;
        return handler;
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        Interlocked.Increment(ref connects);
        return new NetworkStream(socket, ownsSocket: true);
    }
}

/// <summary>Observes token exchanges on the separate auth client, which must never carry a region pin.</summary>
internal sealed class ParityAuth(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private long attempts, pinned;

    internal (long Attempts, long Pinned) Snapshot => (Interlocked.Read(ref attempts), Interlocked.Read(ref pinned));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref attempts);
        if (request.Headers.Contains(TerraBaseRegion.PinHeader)) Interlocked.Increment(ref pinned);
        return base.SendAsync(request, cancellationToken);
    }
}

internal sealed record ParityTarget(string Lane, string Region, string Project, string Database, string Id, int PayloadBytes, ParityDocument Document)
{
    internal const string Collection = "bench";
}

/// <summary>One lane of one cell: a fresh pool, observed sends and a full-document read.</summary>
internal sealed class ParityLane : IDisposable
{
    internal static readonly TimeSpan OperationDeadline = TimeSpan.FromSeconds(30);
    private const int MaxBody = 1024 * 1024;
    private static readonly MediaTypeWithQualityHeaderValue Json = new("application/json");

    private readonly ParityTarget target;
    private readonly Tokens tokens;
    private readonly Uri uri;
    private readonly Version? exact;
    private readonly HttpClient http;
    private readonly TerraBaseDatabase? sdk;

    internal ParityLane(ParityTarget target, Tokens tokens, Func<ParityConnections, HttpMessageHandler> transport)
    {
        this.target = target;
        this.tokens = tokens;
        exact = target.Lane switch
        {
            "raw-h1" => HttpVersion.Version11,
            "raw-h2" => HttpVersion.Version20,
            "sdk" => null,
            _ => throw new ArgumentException("unknown parity lane", nameof(target)),
        };
        uri = Http.ReadUri(TerraBaseRegion.PublicOrigin, target.Project, target.Database, ParityTarget.Collection, target.Id);
        Attempts = new ParityAttempts(transport(Connections), uri, target.Region, exact);
        http = new HttpClient(Attempts) { MaxResponseContentBufferSize = MaxBody };
        // The SDK keeps its shipped H2-or-lower negotiation, retries and provider-driven token refresh.
        if (exact is null)
            sdk = new TerraBaseDocumentsClient(http, new TerraBaseDocumentsClientOptions
            {
                BaseAddress = TerraBaseRegion.PublicOrigin,
                Region = target.Region,
                AccessTokenProvider = tokens.GetAsync,
            }).Database(target.Project, target.Database);
    }

    internal ParityConnections Connections { get; } = new();

    internal ParityAttempts Attempts { get; }

    /// <summary>One logical read: null when the exact seed came back, otherwise a reason code without response content.</summary>
    internal async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            if (sdk is not null)
            {
                var document = await sdk.GetDocumentAsync(ParityTarget.Collection, target.Id, cancellationToken);
                return document is null ? "404" : target.Document.Matches(document) ? null : "document";
            }
            deadline.CancelAfter(OperationDeadline);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = exact!, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetAsync(deadline.Token));
            request.Headers.Accept.Add(Json);
            request.Headers.Add(TerraBaseRegion.PinHeader, target.Region);
            using var response = await http.SendAsync(request, deadline.Token);
            var body = await response.Content.ReadAsByteArrayAsync(deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) return ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture);
            return target.Document.Matches(body) ? null : "document";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return "timeout";
        }
        catch (Exception error) when (!cancellationToken.IsCancellationRequested)
        {
            return error is TerraBaseException { StatusCode: { } code } ? ((int)code).ToString(CultureInfo.InvariantCulture) : Http.StatusOf(error);
        }
    }

    public void Dispose() => http.Dispose();
}

internal sealed record ParityCellPlan(ParityWindowPlan Window, TimeSpan ReadinessLimit, TimeSpan ReadinessPause)
{
    internal static ParityCellPlan Live(int concurrency) => new(ParityWindowPlan.Live(concurrency), TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(100));

    // Readiness, warmup, window and drain must all fit before a cached token is due to refresh.
    internal TimeSpan TokenHorizon => ReadinessLimit + Window.Warmup + Window.Measure + Window.Drain;
}

internal sealed record ParityReadiness(long Attempts, long? Connects, double Ms, string? Last);

/// <summary>
/// One emitted cell. Attempts, connects and auth cover warmup, window and drain; readiness is separate. Percentiles are
/// approximate (16,384-sample reservoir). Socket-level replays inside SocketsHttpHandler are invisible, so they are null.
/// </summary>
internal sealed record ParityCellRow(string Type, string Region, string Lane, int Repeat, int PayloadBytes, int Concurrency,
    ParityReadiness Readiness, ParityWindowResult? Window, ParityAttemptSnapshot? Attempts, long? Connects,
    long AuthExchanges, long AuthPinned, long? WireReplays, string Percentiles, string? Rejection);

internal static class ParityCell
{
    internal static async Task<ParityCellRow> RunAsync(ParityLane lane, ParityTarget target, int repeat, ParityCellPlan plan,
        ParityAuth auth, Tokens tokens, TimeProvider clock, CancellationToken interrupt)
    {
        await tokens.PrewarmAsync(plan.TokenHorizon, interrupt);
        var authBefore = auth.Snapshot.Attempts;
        long? Connects(long since) => lane.Connections.Counted ? lane.Connections.Count - since : null;
        ParityCellRow Row(ParityReadiness readiness, ParityWindowResult? window, ParityAttemptSnapshot? attempts, long? connects, string? rejection)
        {
            // Any exchange after the prewarm is inside the cell; a pinned auth request ever sent rejects every later cell.
            var (exchanges, pinned) = auth.Snapshot;
            exchanges -= authBefore;
            rejection ??= exchanges != 0 ? "auth" : pinned != 0 ? "auth_pin" : null;
            return new("cell", target.Region, target.Lane, repeat, target.PayloadBytes, plan.Window.Concurrency, readiness, window,
                attempts, connects, exchanges, pinned, null, "approximate_reservoir_16384", rejection);
        }

        // Untimed and bounded: the seed must be readable in the pinned region before warmup starts.
        var started = clock.GetTimestamp();
        var readyFrom = lane.Attempts.Snapshot;
        var readyConnects = lane.Connections.Count;
        using var limit = new CancellationTokenSource(plan.ReadinessLimit, clock);
        using var ready = CancellationTokenSource.CreateLinkedTokenSource(interrupt, limit.Token);
        string? last = null, stopped = null;
        while (true)
        {
            try
            {
                last = await lane.ReadAsync(ready.Token);
                if (last is null) break;
                await Task.Delay(plan.ReadinessPause, clock, ready.Token);
            }
            catch (OperationCanceledException) when (ready.IsCancellationRequested)
            {
                stopped = interrupt.IsCancellationRequested ? "interrupted" : "readiness";
                break;
            }
        }
        var readiness = new ParityReadiness(lane.Attempts.Snapshot.Attempts - readyFrom.Attempts, Connects(readyConnects),
            clock.GetElapsedTime(started).TotalMilliseconds, last);
        if (stopped is not null) return Row(readiness, null, null, null, stopped);

        var before = lane.Attempts.Snapshot;
        var connects = lane.Connections.Count;
        string? failure = null;
        var window = await ParityWindow.RunAsync(async ct =>
        {
            if (await lane.ReadAsync(ct) is { } reason)
            {
                Interlocked.CompareExchange(ref failure, reason, null);
                throw new InvalidDataException("parity read rejected");
            }
        }, plan.Window, clock, interrupt);
        var after = lane.Attempts.Snapshot;
        var rejection = window.Rejection == "operation" ? failure : window.Rejection;
        rejection ??= after.RejectionSince(before, window.Starts);
        return Row(readiness, window, after.Since(before), Connects(connects), rejection);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ParityCellRow))]
[JsonSerializable(typeof(ParityHeader))]
[JsonSerializable(typeof(ParityOwned))]
[JsonSerializable(typeof(ParityCleanup))]
[JsonSerializable(typeof(ParityOutcome))]
internal partial class ParityJson : JsonSerializerContext
{
}
