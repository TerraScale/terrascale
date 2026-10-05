using TerraScale.Endpoints;
using TerraScale.TerraBase.Features.ApiKeys;
using TerraScale.TerraBase.Features.UserAuth;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Management.Credentials;

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
        Produces<string>(StatusCodes.Status400BadRequest);
        Produces<string>(StatusCodes.Status404NotFound);
        RequireAuthorization(UserAuthExtensions.PolicyName);
        Metadata(ListenerMetadata.Management);
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateProjectApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = ManagementRoute.DisplayName(new NameRequest(request.Name));
        if (request.Scopes is not { Count: > 0 } scopes || scopes.Except(DataScopes).Any())
        {
            throw new BadHttpRequestException("scopes must be a non-empty subset of: documents:read, documents:write");
        }

        var result = await keys.CreateProjectKeyAsync(
            ManagementRoute.UserId(HttpContext), ManagementRoute.OrganizationId(HttpContext), ManagementRoute.Value(HttpContext, "project"), name, [.. scopes.Distinct()], request.DatabaseId, cancellationToken);
        return result.Value is { } value
            ? TypedResults.Created($"{ManagementRoute.Organizations}/{ManagementRoute.OrganizationId(HttpContext)}/projects/{value.Key.ProjectId}/api-keys/{value.Key.Id}", value)
            : ManagementRoute.Error(result.Error);
    }
}
