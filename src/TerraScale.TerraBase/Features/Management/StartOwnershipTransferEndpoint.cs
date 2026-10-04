using TerraScale.Endpoints;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>The member to offer ownership to.</summary>
/// <param name="UserId">User id of a member.</param>
public sealed record TransferRequest(string? UserId);

/// <summary><c>POST …/organizations/{organization}/ownership-transfer</c>: offers ownership to a member.</summary>
public sealed class StartOwnershipTransferEndpoint(IAccessService access) : Endpoint<TransferRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.OwnershipTransfer);
        Name("startOwnershipTransfer");
        Summary("Offers ownership to a member");
        Description($"userId: a member who is not an owner (404 when not a member, 409 when already an owner). Needs owner. Replaces a pending transfer; expires after 7 days. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OwnershipTransfer>(StatusCodes.Status201Created);
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status403Forbidden);
        Produces<string>(StatusCodes.Status404NotFound);
        Produces<string>(StatusCodes.Status409Conflict);
        RequireAuthorization(DashboardAuthenticationHandler.PolicyName);
        Metadata(ListenerMetadata.Client);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(TransferRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var result = await access.StartTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.ValidUserId(request.UserId, "userId"), cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created(HttpContext.Request.Path.Value, value)
            : ManagementRoute.Error(result.Error);
    }
}
