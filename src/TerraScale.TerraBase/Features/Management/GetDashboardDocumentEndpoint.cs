using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET /v1/management/documents/{collection}/{id}</c>: reads a document of the dashboard document space.</summary>
public sealed class GetDashboardDocumentEndpoint(IManagementCommits commits) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Documents + "/{id}");
        Name("getDashboardDocument");
        Summary("Reads a dashboard document");
        Description($"A document of the dashboard document space, written only by commits (docs/management-commits.md); the ETag is its version for if_match. 404 not_found when absent or deleted. Management service credential only (loopback administrative listener): a ts-auth user token gets 403.");
        Tags(ManagementRoute.Tag);
        Produces<DocumentBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
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

        return (DocumentRoute.CheckCollectionName(HttpContext), DocumentRoute.CheckDocumentId(ManagementRoute.Value(HttpContext, "id"))) switch
        {
            (Validation error, _) => ErrorResponse.From(error),
            (_, Validation error) => ErrorResponse.From(error),
            (string collection, string id) => await ReadAsync(collection, id, cancellationToken),
        };
    }

    private async ValueTask<IResult> ReadAsync(string collection, string id, CancellationToken cancellationToken)
    {
        if (await commits.GetDocumentAsync(collection, id, cancellationToken) is not StoredDocument document)
        {
            return ErrorResponse.From(new NotFound("the document does not exist"));
        }

        var body = GetDocumentEndpoint.Body(collection, document, cancellationToken: cancellationToken) with
        {
            Version = EngineManagementService.DocumentVersion(document, collection),
        };
        ETags.Set(HttpContext, body.Version);
        return TypedResults.Ok(body);
    }
}
