using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary><c>GET …/organizations/{organization}/audit-events</c>: lists the organization's audit events.</summary>
public sealed class ListAuditEventsEndpoint(IAccessService access) : EndpointWithoutRequest<IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.AuditEvents);
        Name("listAuditEvents");
        Summary("Lists the audit events of an organization");
        Description($"Needs admin. Newest first, within the audit retention. Query: after (id cursor from the previous page's next), limit (1 to 100, default 20). {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<ManagementPage<AuditEvent>>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var (after, limit) = ManagementRoute.Page(HttpContext);
        return ManagementRoute.Ok(await access.ListAuditEventsAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), after, limit, cancellationToken));
    }
}
