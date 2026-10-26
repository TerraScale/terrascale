using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Platform;

/// <summary><c>GET /v1/platform/databases/{database}/collections/{collection}/documents</c>: redacted documents, for platform admins.</summary>
public sealed class ListPlatformDocumentsEndpoint(IDocumentService documents, ITenantDirectory tenants, ILogger<ListPlatformDocumentsEndpoint> logger)
    : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(PlatformExplore.Documents);
        Name("listPlatformDocuments");
        Summary("Lists a platform collection's documents, redacted");
        Description("Platform admins only (platform_role admin). Query: limit (1 to 1000, default 50) and cursor (next_cursor of the previous page). Secret-looking fields (passwords, hashes, sealed values, tokens, key material) read \"[redacted]\".");
        Tags("Platform");
        Produces<DocumentPageBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(PlatformExplore.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var name = HttpContext.Request.RouteValues["database"] as string;
        var found = await PlatformExplore.FindAsync(tenants, name, cancellationToken);
        if (found is not TenantContext database)
        {
            return PlatformExplore.Refused(found);
        }

        if ((DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckParameters(HttpContext, "limit", "cursor")) is not (string collection, IQueryCollection query))
        {
            return ErrorResponse.From(new Validation("collection must be a collection name; only limit and cursor are accepted"));
        }

        var digest = QueryParser.Digest(database, collection, "platform");
        QueryPosition? after = null;
        if (query.TryGetValue("cursor", out var cursor))
        {
            if (QueryParser.CheckPosition(cursor!, digest, 0) is not QueryPosition position)
            {
                return ErrorResponse.From(new Validation("cursor is not a cursor of this listing"));
            }

            after = position;
        }

        if (DocumentRoute.CheckLimit(query, QueryParser.DefaultLimit, QueryParser.MaxLimit) is not int limit)
        {
            return ErrorResponse.From(new Validation($"limit must be 1 to {QueryParser.MaxLimit}"));
        }

        PlatformExplore.Audit(logger, HttpContext, name!, collection, null);
        return await documents.QueryAsync(database, new DocumentQuery(collection, null, [], after, limit, DocumentRoute.MaxPageBytes), countLimit: null, cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            QueryOutcome outcome => Page(database, collection, digest, outcome.Page, cancellationToken),
        };
    }

    private IResult Page(TenantContext database, string collection, string digest, QueryPage page, CancellationToken cancellationToken)
    {
        List<DocumentBody> items = [.. page.Rows.Select(row => GetPlatformDocumentEndpoint.Redacted(collection, row.Document, cancellationToken))];
        string? next = null;
        if (page.HasMore)
        {
            var cursor = QueryParser.CheckCursor(digest, page.Rows[^1]);
            if (cursor is Validation error)
            {
                return ErrorResponse.From(error);
            }

            next = cursor switch { string value => value, Validation => throw new InvalidOperationException("cursor validation was already handled") };
        }

        documents.AcceptRead(database, collection, page);
        return TypedResults.Ok(new DocumentPageBody(items, next));
    }
}
