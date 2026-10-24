using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Documents;

/// <summary>Reads a collection's declarative end-user rules using a project credential.</summary>
public sealed class GetCollectionRulesEndpoint(IDocumentService documents, ITenantDirectory tenants) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(CollectionRulesRoute.Route);
        Name("getCollectionRules");
        Summary("Reads collection access rules");
        Description("Project credentials only. Returns {rules:null} before rules are declared; missing rules deny every end-user operation. Public grants apply to authenticated end-users, never anonymous requests.");
        Tags("Collections");
        Produces<CollectionRulesBody>();
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(ApiKeyScopes.DocumentsRead);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        HttpContext.Features.Get<ProjectAccess>() is { IsEndUser: true }
            ? ValueTask.FromResult(ErrorResponse.From(new Forbidden("project credentials are required")))
            : CollectionSchemaRoute.ExecuteAsync(HttpContext, tenants,
                async (database, name) => CollectionRulesRoute.Answer(await documents.GetCollectionRulesAsync(database, name, cancellationToken)), cancellationToken);
}
