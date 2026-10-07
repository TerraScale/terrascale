using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>The email of the user to add.</summary>
/// <param name="Email">The user's address as the operator supplies it; recorded in the audit log only.</param>
public sealed record AddMemberRequest(string? Email);

/// <summary>
/// <c>PUT …/organizations/{organization}/members/{user}</c>: adds a user as a member without an invitation. Served only on the
/// loopback administrative listener, where only the management service credential authenticates: the TS-529 bootstrap
/// needs it because the credential carries no verified email and so cannot accept an invitation (TS-535).
/// </summary>
public sealed class AddMemberEndpoint(IAccessService access) : Endpoint<AddMemberRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Put(ManagementRoute.Member);
        Name("addMember");
        Summary("Adds a user as a member (administrative listener only)");
        Description("Loopback administrative listener only (404 on the client listener): the management service credential, with X-TerraBase-User-Id naming the acting user. user: the ts-auth user id to add; email: its address, recorded in the audit log only. Adds the user as a member. Needs admin. A user who is already a member is returned unchanged. Organizations the acting user is not a member of answer 404.");
        Tags(ManagementRoute.AccessTag);
        Produces<OrganizationMember>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Admin);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(AddMemberRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (ManagementRoute.CheckUserId(ManagementRoute.Value(HttpContext, "user"), "user"), ManagementRoute.CheckEmail(request.Email)) switch
        {
            (Validation error, _) => ErrorResponse.From(error),
            (_, Validation error) => ErrorResponse.From(error),
            (string member, string email) => await access.AddMemberAsync(ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), member, email, cancellationToken) switch
            {
                OrganizationMember added => TypedResults.Ok(added),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
            },
        };
    }
}
