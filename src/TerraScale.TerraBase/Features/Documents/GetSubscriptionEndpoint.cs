using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Memory;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Streams a project-authorized collection or document through the retained shape feed.</summary>
public sealed class GetSubscriptionEndpoint(ITenantDirectory tenants, IShapeSubscriptionService subscriptions, IMemoryBudget memory, TimeProvider time)
    : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get("/projects/{project}/databases/{database}/collections/{collection}/subscribe");
        Name("subscribeCollection");
        Summary("Subscribes to a collection or document over SSE");
        Description("Requires a ts-auth JWT with project_id matching the project, optional database restriction and shapes:read scope. Sends shape events containing ShapeBody snapshots and filtered change pages. Optional id selects one document; where uses the shape filter grammar. offset defaults to -1; other offsets require handle. Resume using Last-Event-ID or explicit handle and offset, with the same id and where. Each event id is handle:next_offset. Deleted documents and documents leaving the filter emit delete operations. Terminal failures emit an error event and close; stale history requires a new snapshot. The stream ends at the signed token expiration without skew. Use fetch with an Authorization header; query-string tokens and cookie authentication are unsupported. Node admission and memory limits return 429 subscription_busy. This slice does not enforce plan entitlements.");
        Tags("Shapes");
        Produces<ShapeBody>(StatusCodes.Status200OK, "text/event-stream");
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status401Unauthorized);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status429TooManyRequests);
        RequireAuthorization(UserAuthExtensions.SubscriptionPolicy);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var project = DocumentRoute.Project(HttpContext);
        var name = HttpContext.Request.RouteValues["database"] as string;
        if (HttpContext.Features.Get<ProjectToken>() is not { } token || !token.Reaches(project, name ?? ""))
        {
            return ErrorResponse.From(DocumentRoute.UnknownDatabase);
        }

        if (!token.Grants(ApiKeyScopes.ShapesRead))
        {
            return ErrorResponse.From(new Forbidden("the token does not grant shapes:read", "forbidden_scope"));
        }

        if (token.ExpiresAt <= time.GetUtcNow())
        {
            return ErrorResponse.From(new Unauthorized("the access token expired"));
        }

        using var reservation = memory.TryReserve(MemoryLane.Foreground, 64 * 1024);
        if (reservation is null)
        {
            return ErrorResponse.From(ShapeSubscriptionService.Busy);
        }

        return (DocumentRoute.CheckDatabase(HttpContext), DocumentRoute.CheckCollectionName(HttpContext),
            DocumentRoute.CheckParameters(HttpContext, "where", "id", "handle", "offset")) switch
        {
            (Validation error, _, _) => ErrorResponse.From(error),
            (_, Validation error, _) => ErrorResponse.From(error),
            (_, _, Validation error) => ErrorResponse.From(error),
            (DatabaseName databaseName, string collection, IQueryCollection query) =>
                await tenants.FindProjectDatabaseAsync(project, databaseName.Value, cancellationToken) is not { } database
                    ? ErrorResponse.From(DocumentRoute.UnknownDatabase)
                    : await SubscribeAsync(database, collection, query, token, cancellationToken),
        };
    }

    private async ValueTask<IResult> SubscribeAsync(TenantContext database, string collection, IQueryCollection query, ProjectToken token, CancellationToken cancellationToken)
    {
        var offsetText = query.TryGetValue("offset", out var rawOffset) ? rawOffset.ToString() : "-1";
        var handle = query.TryGetValue("handle", out var rawHandle) ? rawHandle.ToString() : null;
        if (HttpContext.Request.Headers.TryGetValue("Last-Event-ID", out var resume))
        {
            if (resume.Count != 1 || resume.ToString().Length > 2048 || query.ContainsKey("offset") || query.ContainsKey("handle")
                || resume.ToString().Split(':') is not [var resumeHandle, var resumeOffset] || resumeHandle.Length == 0)
            {
                return ErrorResponse.From(new Validation("Last-Event-ID must be one handle:offset, without handle or offset parameters"));
            }

            handle = resumeHandle;
            offsetText = resumeOffset;
        }

        if (ShapeOffset.Parse(offsetText) is not { } offset || (offset.Kind != ShapeOffsetKind.Initial && handle is null)
            || (offset.Kind == ShapeOffsetKind.Initial && handle is not null))
        {
            return ErrorResponse.From(new Validation("offset must be -1 without handle, or a shape offset with handle"));
        }

        DocumentFilter? filter = null;
        var canonical = "";
        if (query.TryGetValue("where", out var where))
        {
            if (!RequestJson.IsText(Encoding.UTF8.GetBytes(where.ToString())))
            {
                return ErrorResponse.From(new Validation(RequestJson.TextRule, "invalid_query"));
            }

            try
            {
                using var json = JsonDocument.Parse(where.ToString());
                if (QueryParser.CheckFilter(json.RootElement) is not DocumentFilter parsed)
                {
                    return ErrorResponse.From(new Validation("where must be a valid shape filter", "invalid_query"));
                }

                filter = parsed;
                canonical = JsonSerializer.Serialize(json.RootElement, TerraBaseJsonContext.Default.JsonElement);
            }
            catch (JsonException)
            {
                return ErrorResponse.From(new Validation("where must be a JSON filter", "invalid_query"));
            }
        }

        if (query.TryGetValue("id", out var rawId))
        {
            if (DocumentRoute.CheckDocumentId(rawId.ToString()) is not string id)
            {
                return ErrorResponse.From(new Validation("id must be 1 to 256 characters without control characters"));
            }

            var byId = new CompareFilter(DocumentPath.Id, Comparison.Equal, new ScalarValue(ScalarKind.Text, id));
            filter = filter is null ? byId : new AndFilter([filter, byId]);
            // Bind the document restriction into the same identity as an equivalent explicit filter.
            var encoded = JsonSerializer.Serialize(id, TerraBaseJsonContext.Default.String);
            canonical = canonical.Length == 0 ? $"{{\"eq\":[\"$id\",{encoded}]}}" : $"{{\"and\":[{canonical},{{\"eq\":[\"$id\",{encoded}]}}]}}";
        }

        return await subscriptions.StreamAsync(HttpContext,
            new ShapeRequest(database, collection, filter, canonical, offset, handle, Live: false), token.ExpiresAt, cancellationToken);
    }
}
