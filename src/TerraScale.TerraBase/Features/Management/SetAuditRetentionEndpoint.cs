using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>PUT …/organizations/{organization}/audit-retention</c>: sets how long audit events are kept.</summary>
public sealed class SetAuditRetentionEndpoint(IAccessService access) : Endpoint<AuditRetention, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.AuditRetention);
        Name("setAuditRetention");
        Summary("Sets the audit retention");
        Description($"days: 1 to 90. Needs admin. Older events stop being listed at once and are deleted as new events are recorded. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<AuditRetention>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(AuditRetention request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Days is < 1 or > 90)
        {
            return TypedResults.BadRequest("days must be an integer between 1 and 90");
        }

        return ManagementRoute.Ok(await access.SetAuditRetentionAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), request.Days, cancellationToken));
    }
}
