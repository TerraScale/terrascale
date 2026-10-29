using System.Buffers;
using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sync;

internal sealed class PullUpdatesEndpoint(ITenantDirectory tenants, IDatabaseRevisions revisions) : EndpointWithoutRequest<IResult>
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
            + "The answer is a length-delimited protobuf header (server_revision, db_size) followed by one message per page. A revision from an expired "
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
        var pages = new ArrayBufferWriter<byte>();
        var answer = await revisions.PullAsync(database, clientRevision.Length == 0 ? null : clientRevision,
            (number, page) => Write(pages, new PageData { PageId = number - 1UL, EncodedPage = ByteString.CopyFrom(page) }), cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            return ErrorResponse.From(new NotFound("the database is not stored on this node"));
        }

        var response = HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = MediaType;
        response.Headers.CacheControl = "no-store";
        Write(response.BodyWriter, new PullUpdatesHeader
        {
            ServerRevision = answer.Revision.ToString(),
            DbSize = answer.DatabaseSize,
            RawEncoding = new RawPageEncoding(),
            StreamKind = StreamKind.Pages,
            Protocol = PullProtocol.Pages,
        });
        response.BodyWriter.Write(pages.WrittenSpan);
        await response.BodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Results.Empty;
    }

    private static void Write(IBufferWriter<byte> writer, IMessage message)
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
