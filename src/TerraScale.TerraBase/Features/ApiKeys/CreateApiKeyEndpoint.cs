using TerraScale.Endpoints;

namespace TerraScale.TerraBase.Features.ApiKeys;

/// <summary>A key to issue.</summary>
/// <param name="Name">Operator-chosen label, 1 to 100 characters.</param>
/// <param name="Scopes">Scopes to grant; see <see cref="ApiKeyScopes"/>.</param>
public sealed record CreateApiKeyRequest(string Name, IReadOnlyList<string> Scopes);

/// <summary>An issued key. <paramref name="Secret"/> is shown only in this response.</summary>
/// <param name="Id">Public identifier.</param>
/// <param name="Name">Label.</param>
/// <param name="Scopes">Granted scopes.</param>
/// <param name="Secret">The bearer secret for <c>Authorization: Bearer</c>.</param>
public sealed record CreatedApiKeyBody(string Id, string Name, IReadOnlyList<string> Scopes, string Secret);

/// <summary>
/// <c>POST /admin/api-keys</c>: issues a scoped client API key. Served only on the loopback
/// administrative listener, so it trusts the local operator.
/// </summary>
public sealed class CreateApiKeyEndpoint(IApiKeyStore keys) : Endpoint<CreateApiKeyRequest, IResult>
{
    private const int MaxNameLength = 100;

    /// <inheritdoc />
    public override void Configure()
    {
        Post("/admin/api-keys");
        Name("createApiKey");
        Summary("Issues a scoped client API key");
        Tags("Administration");
        Produces<CreatedApiKeyBody>(StatusCodes.Status201Created);
        AllowAnonymous();
    }

    /// <inheritdoc />
    public override async ValueTask<IResult> HandleAsync(CreateApiKeyRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaxNameLength)
        {
            return TypedResults.BadRequest("name must be 1 to 100 characters");
        }

        if (request.Scopes is not { Count: > 0 } || request.Scopes.Except(ApiKeyScopes.All).Any())
        {
            return TypedResults.BadRequest($"scopes must be a non-empty subset of: {string.Join(", ", ApiKeyScopes.All)}");
        }

        var (key, secret) = await keys.CreateAsync(request.Name, [.. request.Scopes.Distinct()], cancellationToken);
        return TypedResults.Created($"/admin/api-keys/{key.Id}", new CreatedApiKeyBody(key.Id, key.Name, key.Scopes, secret));
    }
}
