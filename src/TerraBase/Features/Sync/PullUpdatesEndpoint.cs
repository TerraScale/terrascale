using System.Globalization;
using System.IO.Pipelines;
using System.Net;
using TerraBase.Features.Replicas;
using TerraBase.Features.Management;
using TerraBase.Configuration;
using Google.Protobuf;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Errors;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Sync;

internal sealed class PullUpdatesEndpoint(ITenantDirectory tenants, IDatabaseRevisions revisions, IReplicaPeers peers,
    IStorageEngine engine, TerraBaseOptions options, InternalCredential credential) : EndpointWithoutRequest<IResult>, IRevisionPullSink
{
    private const string MediaType = PullAnswer.MediaType;
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

    private int? _unreadableFormat;

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

        if (PullAnswer.Refusal(request) is { } refusal)
        {
            return ErrorResponse.From(new Validation(refusal, "unsupported_pull"));
        }

        return await tenants.FindDatabaseAsync(HttpContext.RouteValue("organization"), HttpContext.RouteValue("project"), HttpContext.RouteValue("database"), cancellationToken).ConfigureAwait(false) switch
        {
            NotFound error => await PullRetainedAsync(request.ClientRevision, error, cancellationToken).ConfigureAwait(false),
            Unavailable error => ErrorResponse.From(error),
            TenantContext database => await PullAsync(database, request.ClientRevision, cancellationToken).ConfigureAwait(false),
        };
    }


    private async ValueTask<IResult> PullRetainedAsync(string revision, NotFound missing, CancellationToken cancellationToken)
    {
        if (options.PeersFile is null || !credential.Matches(HttpContext) || ReplicaRoutes.Database(HttpContext) is not { } database)
        {
            return ErrorResponse.From(missing);
        }
        using var bulk = StorageWork.Bulk();
        var nodes = await new PeersFile(options).ListAsync(cancellationToken).ConfigureAwait(false);
        var address = HttpContext.Connection.RemoteIpAddress?.MapToIPv4();
        if (!nodes.Any(node => node.Region == options.Region && node.Name != options.MachineId && node.Address.Equals(address)))
        {
            return ErrorResponse.From(missing);
        }
        var store = await engine.OpenSystemDatabaseAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await store.BeginTransactionAsync(write: false, cancellationToken).ConfigureAwait(false);
        var retained = await SystemCatalog.GetAsync(transaction, DeletedDatabases.Kind, database.DatabaseId!,
            ManagementJsonContext.Default.DeletedDatabaseRecord, cancellationToken).ConfigureAwait(false);
        if (retained is null || retained.Database.Home(options.Region) != options.Region
            || !DatabasePurge.SameDatabase(retained.Storage, database))
        {
            return ErrorResponse.From(missing);
        }
        return await PullAsync(retained.Storage, revision, cancellationToken).ConfigureAwait(false);
    }


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

        if (_unreadableFormat is { } format)
        {
            return ErrorResponse.From(new Unavailable(
                $"this node holds the database in storage format {StorageFormat.Name(format)}, which the requesting node does not support yet", "storage_format_unsupported"));
        }

        await FlushAsync(HttpContext.Response.BodyWriter, cancellationToken).ConfigureAwait(false);
        if (peer is not null)
        {
            await revisions.RecordReplicaPullAsync(database, peer.Region, answer, cancellationToken).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    async ValueTask<bool> IRevisionPullSink.StartAsync(RevisionPullStart start, CancellationToken cancellationToken)
    {
        if (!StorageFormat.Accepts(HttpContext.Request.Headers.TryGetValue(StorageFormat.AcceptHeader, out var accepted) ? accepted.ToString() : null, start.Format))
        {
            _unreadableFormat = start.Format;
            return false;
        }

        var response = HttpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = MediaType;
        response.Headers.CacheControl = "no-store";
        if (StorageFormat.Marker(start.Format) is { } marker)
        {
            response.Headers[StorageFormat.Header] = marker;
        }
        response.Headers[DatabaseRevision.Header] = start.Revision.ToString();
        response.Headers[RevisionPullStart.CdcHeader] = start.CdcPosition.ToString(CultureInfo.InvariantCulture);
        PullAnswer.WriteHeader(response.BodyWriter, start);
        await FlushAsync(response.BodyWriter, cancellationToken).ConfigureAwait(false);
        return true;
    }

    async ValueTask IRevisionPullSink.WriteAsync(RevisionPageBatch batch, CancellationToken cancellationToken)
    {
        PullAnswer.WritePages(HttpContext.Response.BodyWriter, batch);
        await FlushAsync(HttpContext.Response.BodyWriter, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
    {
        var flushed = await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (flushed.IsCompleted || flushed.IsCanceled)
        {
            throw new OperationCanceledException("the replica stopped reading the pull", cancellationToken);
        }
    }
}
