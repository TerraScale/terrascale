using System.Text.Json;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Contracts;
using TerraScale.Contracts.Continuity;
using TerraScale.Contracts.Http;
using TerraScale.Endpoints;

namespace TerraBase.Features.InternalApi;

public sealed record InternalChangefeedInput(string? DatabaseId, string? After);

internal sealed class InternalChangefeedEndpoint(DatabaseAccess access, IChangefeedHub hub)
    : Endpoint<InternalChangefeedInput, Result<ChangefeedLines, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
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

    public override async ValueTask<Result<ChangefeedLines, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        InternalChangefeedInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (InternalRoute.DatabaseId(request.DatabaseId) is not { } databaseId)
        {
            return new Validation("databaseId must be a canonical uppercase ULID", "invalid_database");
        }

        if (!CausalBookmark.TryDecode(request.After, out var after))
        {
            return new Validation("after must be a bookmark returned by TerraScale", "invalid_bookmark");
        }

        return await access.DataByIdAsync(HttpContext.Caller, databaseId, cancellationToken) switch
        {
            ResolvedDatabase target => new ChangefeedLines(hub, new ChangefeedSubscription(target.Database.Id, after)),
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
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
