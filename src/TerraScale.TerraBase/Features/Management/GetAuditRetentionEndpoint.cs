using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/audit-retention</c>: reads how long audit events are kept.</summary>
public sealed class GetAuditRetentionEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.AuditRetention);
        Name("getAuditRetention");
        Summary("Reads the audit retention");
        Description($"Needs admin. 90 days unless set. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<AuditRetention>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
        await access.GetAuditRetentionAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), cancellationToken) switch
        {
            AuditRetention value => TypedResults.Ok(value),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
}
