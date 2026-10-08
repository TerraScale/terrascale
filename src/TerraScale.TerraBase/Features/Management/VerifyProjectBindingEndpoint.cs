using TerraBase.Errors;
using TerraBase.Features.Replication;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management;

/// <summary>The ts-auth organization that owns the project being migrated.</summary>
/// <param name="Organization">The ts-auth organization id, independent of the legacy storage namespace.</param>
public sealed record ProjectBindingRequest(string? Organization);

/// <summary>Explicitly binds legacy storage after verifying its owner's live ts-auth project authority.</summary>
public sealed class VerifyProjectBindingEndpoint(IProjectCatalog catalog) : Endpoint<ProjectBindingRequest, IResult>
{
    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.Project + "/binding");
        Name("verifyProjectBinding");
        Summary("Verifies a legacy project storage binding");
        Description("A sign-in token's user must own the legacy organization and currently administer the exact project in ts-auth. "
            + "organization is its ts-auth organization id. This explicit migration preserves database IDs and files.");
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Client, ServedByCatalogPrimary.BeforeHandling, new ProducesResponseTypeMetadata(StatusCodes.Status204NoContent, typeof(void)));
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status409Conflict);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(ProjectBindingRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = ManagementRoute.Value(HttpContext, "project");
        if (!IsId(request.Organization) || !IsId(project))
        {
            return ErrorResponse.From(new Validation("organization and project must be lowercase hyphenated UUIDs"));
        }

        var header = HttpContext.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header["Bearer ".Length..].Trim() is not { Length: > 0 } token)
        {
            return ErrorResponse.From(new Unauthorized());
        }

        return await catalog.VerifyBindingAsync(
            HttpContext.User.UserId(), ManagementRoute.OrganizationId(HttpContext), project, request.Organization!,
            token, cancellationToken) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private static bool IsId(string? value) => Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value;
}
