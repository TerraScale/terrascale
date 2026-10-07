using TerraBase.Errors;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary><c>GET /v1/management/versions</c>: versions of the acting user's account and of organizations, for fences.</summary>
public sealed class GetVersionsEndpoint(IManagementCommits commits) : EndpointWithoutRequest<IResult>
{
    private const int MaxOrganizations = 16;

    /// <inheritdoc />
    public override void Configure()
    {
        Get(ManagementRoute.Versions);
        Name("getVersions");
        Summary("Reads versions for fencing commits");
        Description($"Query: organization (0 to 16, repeated). Answers the account's and each organization's version in one snapshot; read them before the aggregate a commit fences (docs/management-commits.md). Versions are opaque. 404 if the user is not a member of one of them. {ManagementRoute.IdentityDescription}");
        Tags(ManagementRoute.Tag);
        Produces<VersionsBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken)
    {
        var userId = ManagementRoute.UserId(HttpContext);
        var query = HttpContext.Request.Query;
        if (query.Keys.Any(key => key != "organization"))
        {
            return ErrorResponse.From(new Validation("the only query parameter is organization"));
        }

        var values = query["organization"];
        if (values.Count > MaxOrganizations || values.Any(string.IsNullOrEmpty))
        {
            return ErrorResponse.From(new Validation("organization is repeated at most 16 times and never empty"));
        }

        var organizations = values.Select(organization => organization!).Distinct(StringComparer.Ordinal).ToList();

        return await commits.GetVersionsAsync(userId, organizations, cancellationToken) switch
        {
            VersionsBody versions => TypedResults.Ok(versions),
            NotFound error => ErrorResponse.From(error),
        };
    }
}
