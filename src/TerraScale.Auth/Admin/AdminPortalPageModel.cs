namespace TerraScale.Auth.Admin;

using TerraScale.Auth.Store;

/// <summary>Data shaped for the admin Razor slice; the slice owns the page markup.</summary>
public sealed record AdminPortalPageModel(
    Instance Instance,
    User User,
    IReadOnlyList<Organization> AdminOrgs,
    IReadOnlyList<Connection> Connections,
    IReadOnlyList<ScimDirectory> Directories,
    string PublicOrigin,
    string? Error)
{
    public string Slug => Instance.Slug;
    public int ActiveConnections => Connections.Count(connection => connection.Enabled);

    public string ProviderAction(string provider) => $"@get('/ui/{Slug}/admin/providers/{provider}')";

    public string? Callback(Connection connection) => connection.Type switch
    {
        "oidc" => $"{PublicOrigin}/ui/{Slug}/connections/{connection.Id}/oidc/callback",
        "github" => $"{PublicOrigin}/ui/{Slug}/connections/{connection.Id}/github/callback",
        _ => null,
    };
}
