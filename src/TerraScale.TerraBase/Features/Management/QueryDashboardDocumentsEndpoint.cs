using System.Globalization;
using TerraScale.Endpoints;
using TerraScale.TerraBase.Engine;
using TerraScale.TerraBase.Errors;
using TerraScale.TerraBase.Features.Documents;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;
using TerraScale.TerraBase.Tenancy;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>POST /v1/management/documents/{collection}/query</c>: queries the dashboard document space (query v1).</summary>
public sealed class QueryDashboardDocumentsEndpoint(IManagementCommits commits) : EndpointWithoutRequest<IResult>
{
    // count examines at most this many documents; beyond, it is a lower bound.
    private const long MaxCount = 100_000;

    // Cursors bind to their scope: the dashboard document space is one scope of its own.
    private static readonly TenantContext Space = new("dashboard", PlanTier.Hobby, DatabaseId: "dashboard");

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Documents + "/query");
        Name("queryDashboardDocuments");
        Summary("Queries dashboard documents");
        Description($"The query v1 body of POST …/collections/{{collection}}/query (filter, sort, project, limit, cursor, count) over a collection of the dashboard document space. Versions in items are the if_match values of commits. Management service credential only (loopback administrative listener): a ts-auth user token gets 403.");
        Tags(ManagementRoute.Tag);
        Metadata(new RequestBodyMetadata(typeof(QueryRequest), DocumentRoute.JsonMediaType));
        Produces<DocumentPageBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        if (!HttpContext.User.IsManagementService())
        {
            return ErrorResponse.From(new Forbidden("the dashboard document space needs the management service credential"));
        }

        var collection = DocumentRoute.CollectionName(HttpContext);
        var body = await DocumentRoute.ReadBodyAsync(HttpContext, DocumentRoute.MaxRequestBytes, "invalid_query", cancellationToken, DocumentRoute.JsonMediaType);
        var request = RequestJson.Deserialize<QueryRequest>(body, "invalid_query");
        var query = QueryParser.Parse(Space, collection, request);
        var after = request.Cursor is { } cursor ? QueryParser.Position(cursor, query.Digest, query.Sort.Count) : null;
        var (page, count) = await commits.QueryDocumentsAsync(
            new DocumentQuery(collection, query.Filter, query.Sort, after, query.Limit, DocumentRoute.MaxPageBytes),
            request.Count == true && after is null ? MaxCount : null,
            cancellationToken);
        return TypedResults.Ok(new DocumentPageBody(
            [.. page.Rows.Select(row => GetDocumentEndpoint.Body(collection, row.Document, query.Project, cancellationToken) with
            {
                Version = EngineManagementService.DocumentVersion(row.Document, collection),
            })],
            page.HasMore ? QueryParser.Cursor(query.Digest, page.Rows[^1], body) : null,
            count is var (n, complete) ? new CountBody(n.ToString(CultureInfo.InvariantCulture), complete) : null));
    }
}
