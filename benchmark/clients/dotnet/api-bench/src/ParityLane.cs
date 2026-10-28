using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
