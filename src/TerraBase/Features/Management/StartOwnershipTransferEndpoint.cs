using TerraBase.Errors;
using TerraBase.Features.Catalog;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

public sealed record TransferRequest(string? UserId);

public sealed class StartOwnershipTransferEndpoint(IAccessService access) : Endpoint<TransferRequest, IResult>
{
    public override void Configure()
    {
        Post(ManagementRoute.OwnershipTransfer);
        Metadata(AnchoredRevocation.Instance);
        Name("startOwnershipTransfer");
        Summary("Offers ownership to a member");
        Description($"userId: a member who is not an owner (404 when not a member, 409 when already an owner). Needs owner. Replaces a pending transfer; expires after 7 days. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.AccessTag);
        Produces<OwnershipTransfer>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<IResult> HandleAsync(TransferRequest request, CancellationToken cancellationToken) =>
        ManagementRoute.CheckUserId(request.UserId, "userId") switch
        {
            Validation error => ErrorResponse.From(error),
            string member => await access.StartTransferAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), member, cancellationToken) switch
            {
                OwnershipTransfer value => TypedResults.Created(HttpContext.Request.Path.Value, value),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
