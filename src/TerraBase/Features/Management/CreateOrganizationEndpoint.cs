using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>POST /v1/management/organizations</c>: creates an organization.</summary>
public sealed class CreateOrganizationEndpoint(IManagementService management) : Endpoint<NameRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Organizations);
        Name("createOrganization");
        Summary("Creates an organization");
        Description("name: 1 to 128 characters with a letter or digit, unique among the organizations the acting user created, ignoring case. The acting user becomes its first member." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<Organization>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(NameRequest request, CancellationToken cancellationToken) =>
        ManagementRoute.CheckDisplayName(request) switch
        {
            Validation error => ErrorResponse.From(error),
            string name => await management.CreateOrganizationAsync(ManagementRoute.UserId(HttpContext), name, cancellationToken) switch
            {
                Organization value => TypedResults.Created($"{ManagementRoute.Organizations}/{value.Id}", value),
                Conflict error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
}
