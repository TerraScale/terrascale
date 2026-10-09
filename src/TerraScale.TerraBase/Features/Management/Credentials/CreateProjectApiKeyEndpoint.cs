using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.UserAuth;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Management.Credentials;

/// <summary>A project key to issue.</summary>
/// <param name="Name">Label, 1 to 128 characters.</param>
/// <param name="Scopes">A non-empty subset of <c>documents:read</c> and <c>documents:write</c>.</param>
/// <param name="DatabaseId">Restricts the key to this database of the project; omit for every database of the project.</param>
public sealed record CreateProjectApiKeyRequest(string? Name, IReadOnlyList<string>? Scopes, string? DatabaseId);

/// <summary><c>POST …/projects/{project}/api-keys</c>: issues a data API key that reaches only the project's databases.</summary>
public sealed class CreateProjectApiKeyEndpoint(IApiKeyStore keys) : Endpoint<CreateProjectApiKeyRequest, IResult>
{
    private static readonly string[] DataScopes = [ApiKeyScopes.DocumentsRead, ApiKeyScopes.DocumentsWrite];

    /// <inheritdoc />
    public override void Configure()
    {
        Post(ManagementRoute.ApiKeys);
        Name("createProjectApiKey");
        Summary("Issues a data API key of a project");
        Description("name: 1 to 128 characters. scopes: a non-empty subset of documents:read, documents:write. databaseId (optional): a database of this project, else 404. The secret is returned only in this response; only its hash is stored." + " " + ManagementRoute.IdentityDescription);
        Tags(ManagementRoute.Tag);
        Produces<CreatedProjectApiKey>(StatusCodes.Status201Created);
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateProjectApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var organization = ManagementRoute.OrganizationId(HttpContext);
        return (ManagementRoute.CheckDisplayName(new NameRequest(request.Name)), Scopes(request.Scopes)) switch
        {
            (Validation error, _) => ErrorResponse.From(error),
            (_, Validation error) => ErrorResponse.From(error),
            (string name, string[] scopes) => await keys.CreateProjectKeyAsync(
                ManagementRoute.UserId(HttpContext), organization, ManagementRoute.Value(HttpContext, "project"), name, scopes, request.DatabaseId, cancellationToken) switch
            {
                CreatedProjectApiKey created => TypedResults.Created(
                    $"{ManagementRoute.Organizations}/{organization}/projects/{created.Key.ProjectId}/api-keys/{created.Key.Id}", created),
                NotFound error => ErrorResponse.From(error),
                Forbidden error => ErrorResponse.From(error),
                Unavailable error => ErrorResponse.From(error),
            },
        };
    }

    private static Valid<string[]> Scopes(IReadOnlyList<string>? scopes) =>
        scopes is { Count: > 0 } && !scopes.Except(DataScopes).Any()
            ? scopes.Distinct().ToArray()
            : new Validation("scopes must be a non-empty subset of: documents:read, documents:write");
}
