using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Credentials;

/// <summary><c>DELETE …/api-keys/{key}</c>: revokes a data API key of a project.</summary>
public sealed class RevokeProjectApiKeyEndpoint(IApiKeyStore keys) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Delete(ManagementRoute.ApiKey);
        Name("revokeProjectApiKey");
        Summary("Revokes a data API key of a project");
        Description("The key stops authenticating immediately." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await keys.RevokeProjectKeyAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), ManagementRoute.Value(HttpContext, "key"), cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
        };
}
