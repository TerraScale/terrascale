using System.Text.Json;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Features.InternalApi;

internal readonly union InternalChangefeedResponse(ChangefeedLines, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

public sealed record InternalChangefeedInput(string? DatabaseId, string? After);

internal sealed class InternalChangefeedEndpoint(DatabaseAccess access, IChangefeedHub hub)
    : Endpoint<InternalChangefeedInput, InternalChangefeedResponse>
{
    public override void Configure()
    {
        Get(InternalRoute.Database + "/changefeed");
        Name("followInternalChangefeed");
        Summary("Streams a database's committed changes as NDJSON");
        Description("Replays every commit after the ?after= bookmark, then follows live commits; each line is a ChangefeedHttpItem carrying the bookmark to resume from.");
        Tags(InternalRoute.Tag);
        AllowAnonymous();
        Metadata(ListenerMetadata.Internal);
    }

    public override async ValueTask<InternalChangefeedResponse> HandleAsync(
        InternalChangefeedInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (InternalRoute.DatabaseId(request.DatabaseId) is not { } databaseId)
        {
            return new InvalidInput("databaseId must be a canonical uppercase ULID", "invalid_database");
        }

        if (!CausalBookmark.TryDecode(request.After, out var after))
        {
            return new InvalidInput("after must be a bookmark returned by TerraScale", "invalid_bookmark");
        }

        var outcome = await access.DataByIdAsync(HttpContext.Caller, databaseId, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase target => new ChangefeedLines(hub, new ChangefeedSubscription(target.Database.Id, after)),
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}

public sealed class ChangefeedLines(IChangefeedHub hub, ChangefeedSubscription subscription) : IResult
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = PlatformHeaders.Changefeed;
        response.Headers.CacheControl = "no-store";
        await response.Body.FlushAsync(httpContext.RequestAborted);
        var position = subscription.After;
        await foreach (var entry in hub.SubscribeAsync(subscription, httpContext.RequestAborted))
        {
            position = position.Merge(CausalBookmark.ForRegion(entry.OriginRegion, entry.Sequence));
            var item = new ChangefeedHttpItem(entry.DatabaseId, entry.TxId, entry.OriginRegion, entry.Sequence, entry.SchemaEpoch, entry.Mode, entry.CommittedAtUtc, position.Encode());
            await JsonSerializer.SerializeAsync(response.Body, item, TerraScaleContractsJsonContext.Default.ChangefeedHttpItem, httpContext.RequestAborted);
            await response.Body.WriteAsync(NewLine, httpContext.RequestAborted);
            await response.Body.FlushAsync(httpContext.RequestAborted);
        }
    }
}
