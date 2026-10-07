using System.Globalization;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

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

        return DocumentRoute.CheckCollectionName(HttpContext) switch
        {
            Validation error => ErrorResponse.From(error),
            string collection => await DocumentRoute.CheckBodyAsync(
                HttpContext, DocumentRoute.MaxRequestBytes, "invalid_query", cancellationToken, DocumentRoute.JsonMediaType) switch
            {
                Validation error => ErrorResponse.From(error),
                PayloadTooLarge error => ErrorResponse.From(error),
                UnsupportedMediaType error => ErrorResponse.From(error),
                byte[] body => RequestJson.Read<QueryRequest>(body, "invalid_query") switch
                {
                    Validation error => ErrorResponse.From(error),
                    QueryRequest request => QueryParser.CheckParse(Space, collection, request) switch
                    {
                        Validation error => ErrorResponse.From(error),
                        ParsedQuery query => request.Cursor is not { } cursor
                            ? await QueryAsync(collection, body, request, query, after: null, cancellationToken)
                            : QueryParser.CheckPosition(cursor, query.Digest, query.Sort.Count) switch
                            {
                                Validation error => ErrorResponse.From(error),
                                QueryPosition after => await QueryAsync(collection, body, request, query, after, cancellationToken),
                            },
                    },
                },
            },
        };
    }

    private async ValueTask<IResult> QueryAsync(
        string collection, byte[] body, QueryRequest request, ParsedQuery query, QueryPosition? after, CancellationToken cancellationToken) =>
        await commits.QueryDocumentsAsync(
            new DocumentQuery(collection, query.Filter, query.Sort, after, query.Limit, DocumentRoute.MaxPageBytes),
            request.Count == true && after is null ? MaxCount : null,
            cancellationToken) switch
        {
            NotFound error => ErrorResponse.From(error),
            Validation error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
            QueryOutcome outcome => Page(collection, body, query, outcome, cancellationToken),
        };

    private static IResult Page(string collection, byte[] body, ParsedQuery query, QueryOutcome outcome, CancellationToken cancellationToken)
    {
        var page = outcome.Page;
        if (!page.HasMore)
        {
            return TypedResults.Ok(Body(collection, query, outcome, next: null, cancellationToken));
        }

        return QueryParser.CheckCursor(query.Digest, page.Rows[^1], body) switch
        {
            Validation error => ErrorResponse.From(error),
            string next => TypedResults.Ok(Body(collection, query, outcome, next, cancellationToken)),
        };
    }

    private static DocumentPageBody Body(string collection, ParsedQuery query, QueryOutcome outcome, string? next, CancellationToken cancellationToken) =>
        new(
            [.. outcome.Page.Rows.Select(row => GetDocumentEndpoint.Body(collection, row.Document, query.Project, cancellationToken) with
            {
                Version = EngineManagementService.DocumentVersion(row.Document, collection),
            })],
            next,
            outcome.Count is var (n, complete) ? new CountBody(n.ToString(CultureInfo.InvariantCulture), complete) : null);
}
