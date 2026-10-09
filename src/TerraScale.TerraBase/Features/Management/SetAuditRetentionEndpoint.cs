using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

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
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(AuditRetention request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Days is < 1 or > 90)
        {
            return ErrorResponse.From(new Validation("days must be an integer between 1 and 90"));
        }

        return await access.SetAuditRetentionAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), request.Days, cancellationToken) switch
        {
            AuditRetention value => TypedResults.Ok(value),
            NotFound error => ErrorResponse.From(error),
            Forbidden error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }
}
