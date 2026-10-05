using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Credentials;

/// <summary><c>GET …/projects/{project}/api-keys</c>: lists the data API keys of a project, without secrets.</summary>
public sealed class ListProjectApiKeysEndpoint(IApiKeyStore keys) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.ApiKeys);
        Name("listProjectApiKeys");
        Summary("Lists the data API keys of a project");
        Description("Query: after (exclusive sequence cursor from the previous page's next), limit (1 to 100, default 20). Items are in committed creation order. " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ManagementPage<ProjectApiKey>>();
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return await keys.ListProjectKeysAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), after, limit, cancellationToken) is { } page
            ? TypedResults.Ok(page)
            : TypedResults.NotFound();
    }
}
