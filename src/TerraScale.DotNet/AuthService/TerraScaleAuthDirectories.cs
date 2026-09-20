using System.Text.Json.Serialization;

namespace TerraScale.DotNet;

/// <summary>
/// SCIM provisioning directories: creation, listing, bearer-token rotation,
/// and deletion. The cleartext <c>scim_...</c> token is returned exactly once
/// at creation and once per rotation; it is never returned again.
/// </summary>
public sealed class TerraScaleAuthDirectoriesClient(TerraScaleAuthClient client)
{
    /// <summary>
    /// Creates a SCIM directory, optionally bound to an organization that must
    /// already exist in this instance. The cleartext token is returned exactly once.
    /// </summary>
    public Task<TerraScaleAuthDirectorySecret> CreateAsync(
        string? organizationId = null,
        CancellationToken cancellationToken = default) =>
        client.SendJsonAsync<TerraScaleAuthCreateDirectory, TerraScaleAuthDirectorySecret>(
            HttpMethod.Post,
            client.ManagementRoute("/directories"),
            new TerraScaleAuthCreateDirectory(organizationId),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthCreateDirectory,
            TerraScaleAuthJsonContext.Default.TerraScaleAuthDirectorySecret,
            cancellationToken);

    /// <summary>Lists every SCIM directory in the instance; the token is never included.</summary>
    public Task<TerraScaleAuthDirectoryPage> ListAsync(CancellationToken cancellationToken = default) =>
        client.GetJsonAsync(
            client.ManagementRoute("/directories"),
            TerraScaleAuthJsonContext.Default.TerraScaleAuthDirectoryPage,
            cancellationToken);

    /// <summary>
    /// Rotates the directory's SCIM bearer token. The old token stops
    /// authenticating immediately; the new cleartext token is returned exactly once.
    /// </summary>
    public Task<TerraScaleAuthDirectorySecret> RotateTokenAsync(string directoryId, CancellationToken cancellationToken = default) =>
        client.PostJsonAsync(
            DirectoryRoute(directoryId) + "/rotate",
            TerraScaleAuthJsonContext.Default.TerraScaleAuthDirectorySecret,
            cancellationToken);

    /// <summary>Deletes one SCIM directory; its SCIM token is revoked with it.</summary>
    public Task DeleteAsync(string directoryId, CancellationToken cancellationToken = default) =>
        client.SendForNoContentAsync(HttpMethod.Delete, DirectoryRoute(directoryId), cancellationToken);

    private string DirectoryRoute(string directoryId) =>
        client.ManagementRoute("/directories/" + TerraScaleAuthPaths.Escape(directoryId));
}

/// <summary>A SCIM directory record; the bearer token is never included.</summary>
public sealed record TerraScaleAuthDirectory(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("org_id")] string? OrganizationId,
    [property: JsonPropertyName("created_at")] long CreatedAt);

/// <summary>One page of the SCIM directory list.</summary>
public sealed record TerraScaleAuthDirectoryPage(
    [property: JsonPropertyName("data")] IReadOnlyList<TerraScaleAuthDirectory> Data);

/// <summary>
/// A directory with its cleartext SCIM bearer token, returned exactly once by
/// creation and by each rotation; never retrievable afterwards.
/// </summary>
public sealed record TerraScaleAuthDirectorySecret(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("org_id")] string? OrganizationId,
    [property: JsonPropertyName("token")] string Token)
{
    /// <inheritdoc cref="object.ToString" />
    public override string ToString() =>
        $"{typeof(TerraScaleAuthDirectorySecret).Name} {{ Id = {Id}, OrganizationId = {OrganizationId}, Token = [REDACTED] }}";
}

internal sealed record TerraScaleAuthCreateDirectory(
    [property: JsonPropertyName("org_id")] string? OrganizationId);
