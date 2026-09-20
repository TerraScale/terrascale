using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// Custom-claim JWT templates and token minting. Templates are addressed by
/// name (url-safe, 1-48 characters of a-z0-9-_, normalized to lowercase),
/// not by id.
/// </summary>
public sealed class TerraScaleAuthJwtTemplatesClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Creates a template from a non-empty claims map. The lifetime defaults
    /// to 300 seconds when omitted.
    /// </summary>
    public Task<TerraScaleAuthJwtTemplate> CreateAsync(
        string name,
        IReadOnlyDictionary<string, string> claims,
        int? lifetimeSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(claims);
        return client.SendJsonAsync<TerraScaleAuthCreateJwtTemplate, TerraScaleAuthJwtTemplate>(
            HttpMethod.Post,
            client.ManagementRoute("/jwt-templates"),
            new TerraScaleAuthCreateJwtTemplate(name, claims, lifetimeSeconds),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateJwtTemplate,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthJwtTemplate,
            cancellationToken);
    }

    /// <summary>Lists every JWT template in the instance.</summary>
    public Task<IReadOnlyList<TerraScaleAuthJwtTemplate>> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/jwt-templates"),
            TerraScaleAuthJsonContext.Default.IReadOnlyListTerraScaleAuthJwtTemplate,
            cancellationToken);

    /// <summary>
    /// Updates one template. Absent fields are unchanged; <paramref name="claims"/>
    /// replaces the claim map wholesale.
    /// </summary>
    public Task<TerraScaleAuthJwtTemplate> UpdateAsync(
        string name,
        string? newName = null,
        IReadOnlyDictionary<string, string>? claims = null,
        int? lifetimeSeconds = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return client.SendJsonAsync<TerraScaleAuthUpdateJwtTemplate, TerraScaleAuthJwtTemplate>(
            HttpMethod.Patch,
            TemplateRoute(name),
            new TerraScaleAuthUpdateJwtTemplate(newName, claims, lifetimeSeconds),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthUpdateJwtTemplate,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthJwtTemplate,
            cancellationToken);
    }

    /// <summary>Deletes one template.</summary>
    public Task DeleteAsync(string name, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(HttpMethod.Delete, TemplateRoute(name), cancellationToken);

    /// <summary>
    /// Mints an ES256 JWT for the user with the template's claims (with
    /// <c>{{user.email}}</c> style substitution); exp is iat plus the
    /// template's lifetime.
    /// </summary>
    public Task<TerraScaleAuthMintedToken> MintTokenAsync(string name, string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        return client.SendJsonAsync<TerraScaleAuthMintJwtToken, TerraScaleAuthMintedToken>(
            HttpMethod.Post,
            TemplateRoute(name) + "/tokens",
            new TerraScaleAuthMintJwtToken(userId),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthMintJwtToken,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthMintedToken,
            cancellationToken);
    }

    private string TemplateRoute(string name) =>
        client.ManagementRoute("/jwt-templates/" + TerraScaleAuthPaths.Escape(name));
}

/// <summary>A custom-claim JWT template.</summary>
public sealed record TerraScaleAuthJwtTemplate(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("claims")] IReadOnlyDictionary<string, string> Claims,
    [property: JsonPropertyName("lifetime_seconds")] int LifetimeSeconds,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>A minted ES256 JWT.</summary>
public sealed record TerraScaleAuthMintedToken(
    [property: JsonPropertyName("jwt")] string Jwt)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthMintedToken).Name} {{ Jwt = [REDACTED] }}";
}

internal sealed record TerraScaleAuthCreateJwtTemplate(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("claims")] IReadOnlyDictionary<string, string> Claims,
    [property: JsonPropertyName("lifetime_seconds")] int? LifetimeSeconds);

internal sealed record TerraScaleAuthUpdateJwtTemplate(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("claims")] IReadOnlyDictionary<string, string>? Claims,
    [property: JsonPropertyName("lifetime_seconds")] int? LifetimeSeconds);

internal sealed record TerraScaleAuthMintJwtToken(
    [property: JsonPropertyName("user_id")] string UserId);
