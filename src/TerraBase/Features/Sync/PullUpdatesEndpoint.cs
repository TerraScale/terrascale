using System.IO.Pipelines;
using System.Net;
using TerraBase.Features.Replicas;
using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sync;

internal sealed class PullUpdatesEndpoint(ITenantDirectory tenants, IDatabaseRevisions revisions, IReplicaPeers peers) : EndpointWithoutRequest<IResult>, IRevisionPullSink
{
    private const string MediaType = "application/protobuf";
    private const int MaxBodyBytes = 64 * 1024;

    public override void Configure()
    {
        Post("/v1/sync/organizations/{organization}/projects/{project}/databases/{database}/pull-updates");
        Name("pullDatabaseUpdates");
        Summary("Streams the pages of a database that changed since a revision");
        Description("Internal listener only, behind the peer credential; 404 on the public listener. Page pull for region replicas: the body is a "
            + "protobuf pull-updates request in page mode with raw encoding. client_revision is the server_revision of an earlier answer, or empty to bootstrap. "
            + "The answer is a length-delimited protobuf header (server_revision, db_size) followed by one message per page; the TerraBase-Revision header repeats server_revision. A revision from an expired "
            + "history, another incarnation or a rewritten database gets every page. Long polls are answered at once. 400 unsupported_pull for zstd, MVCC "
            + "logical pulls, pinned server revisions and page or query selectors; 404 when the database is not in this node's catalog.");
        Tags("Administration");
        AllowAnonymous();
        Metadata(new RequestBodyMetadata(typeof(byte[]), MediaType), ListenerMetadata.Internal);
        Produces<byte[]>(StatusCodes.Status200OK, MediaType);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status413PayloadTooLarge);
        Produces<ErrorBody>(StatusCodes.Status415UnsupportedMediaType);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(HttpContext.Request.ContentType, MediaType, StringComparison.OrdinalIgnoreCase))
        {
            return ErrorResponse.From(new UnsupportedMediaType($"the body is {MediaType}"));
        }

        if (await ReadBodyAsync(cancellationToken).ConfigureAwait(false) is not { } body)
        {
            return ErrorResponse.From(new PayloadTooLarge("the body is at most 64 KiB"));
        }

        PullUpdatesRequest request;
        try
        {
            request = PullUpdatesRequest.Parser.ParseFrom(body);
        }
        catch (InvalidProtocolBufferException)
        {
            return ErrorResponse.From(new Validation("the body is not a pull-updates request"));
        }

        if (Refusal(request) is { } refusal)
        {
            return ErrorResponse.From(new Validation(refusal, "unsupported_pull"));
        }

        return await tenants.FindDatabaseAsync(RouteValue("organization"), RouteValue("project"), RouteValue("database"), cancellationToken).ConfigureAwait(false) switch
        {
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await PullAsync(database, request.ClientRevision, cancellationToken).ConfigureAwait(false),
        };
    }

    private static string? Refusal(PullUpdatesRequest request) => request switch
    {
        { Encoding: not PageEncoding.Raw } => "only raw page encoding is served",
        { StreamKind: not StreamKind.Pages } => "only page pulls are served",
        { ServerRevision.Length: > 0 } or { ServerQuerySelector.Length: > 0 } or { ServerPagesSelector.IsEmpty: false } or { ClientPages.IsEmpty: false }
            => "partial pulls are not served: send only client_revision",
        _ => null,
    };

    private string RouteValue(string name) => HttpContext.Request.RouteValues[name] as string ?? string.Empty;

    private async ValueTask<byte[]?> ReadBodyAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength > MaxBodyBytes)
        {
            return null;
        }

        using var body = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await HttpContext.Request.Body.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (body.Length + read > MaxBodyBytes)
            {
                return null;
            }

            body.Write(chunk, 0, read);
        }

        return body.ToArray();
    }

    private async ValueTask<IResult> PullAsync(TenantContext database, string clientRevision, CancellationToken cancellationToken)
    {
        var meshPeers = await peers.ListAsync(cancellationToken).ConfigureAwait(false);
        var address = HttpContext.Connection.RemoteIpAddress?.MapToIPv4();
        var peer = meshPeers.FirstOrDefault(candidate =>
            IPAddress.TryParse(candidate.Internal.Host, out var ip) && ip.MapToIPv4().Equals(address));
        if (peer is not null && await tenants.DatabaseRecordAsync(database, cancellationToken).ConfigureAwait(false)
            is TenantDatabaseRecord { Placement: var placement } && (placement.AllRegions || placement.Replicas.Contains(peer.Region)))
        {
            IReadOnlyList<string> regions = placement.AllRegions ? [.. meshPeers.Select(candidate => candidate.Region).Distinct()] : placement.Replicas;
            await revisions.RetainReplicaCdcAsync(database, placement with { Replicas = regions }, peer.Region, clientRevision, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            peer = null;
        }

        RevisionPullResult? answer;
        try
        {
            answer = await revisions.PullAsync(database, clientRevision.Length == 0 ? null : clientRevision, this, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (HttpContext.Response.HasStarted)
        {
            HttpContext.Abort();
            if (error is OperationCanceledException or IOException)
            {
                return Results.Empty;
            }

            throw;
        }

        if (answer is null)
        {
            return ErrorResponse.From(new NotFound("the database is not stored on this node"));
        }

        await FlushAsync(HttpContext.Response.BodyWriter, cancellationToken).ConfigureAwait(false);
        if (peer is not null)
        {
            await revisions.RecordReplicaPullAsync(database, peer.Region, answer, cancellationToken).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    async ValueTask IRevisionPullSink.StartAsync(RevisionPullStart start, CancellationToken cancellationToken)
    {
        var response = HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = MediaType;
        response.Headers.CacheControl = "no-store";
        response.Headers[DatabaseRevision.Header] = start.Revision.ToString();
        Write(response.BodyWriter, new PullUpdatesHeader
        {
            ServerRevision = start.Revision.ToString(),
            DbSize = start.DatabaseSize,
            RawEncoding = new RawPageEncoding(),
            StreamKind = StreamKind.Pages,
            Protocol = PullProtocol.Pages,
        });
        await FlushAsync(response.BodyWriter, cancellationToken).ConfigureAwait(false);
    }

    async ValueTask IRevisionPullSink.WriteAsync(RevisionPageBatch batch, CancellationToken cancellationToken)
    {
        var writer = HttpContext.Response.BodyWriter;
        for (var index = 0; index < batch.Numbers.Length; index++)
        {
            Write(writer, new PageData
            {
                PageId = batch.Numbers.Span[index] - 1UL,
                EncodedPage = UnsafeByteOperations.UnsafeWrap(batch.Pages.Slice(index * batch.PageSize, batch.PageSize)),
            });
        }

        await FlushAsync(writer, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        var flushed = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (flushed.IsCompleted || flushed.IsCanceled)
        {
            throw new OperationCanceledException("the replica stopped reading the pull", cancellationToken);
        }
    }

    private static void Write(PipeWriter writer, IMessage message)
    {
        var length = (uint)message.CalculateSize();
        var prefix = writer.GetSpan(5);
        var used = 0;
        for (; length >= 0x80; length >>= 7)
        {
            prefix[used++] = (byte)(length | 0x80);
        }

        prefix[used++] = (byte)length;
        writer.Advance(used);
        message.WriteTo(writer);
    }
}
