using TerraScale.Dashboard;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard.Features.ApiKeys.Pages.ApiKeys;

public sealed class ApiKeysPageEndpoint(DashboardStore store)
    : SlicePageEndpoint<ApiKeysSlice, ApiKeysPageModel>(store, "/api-keys")
{
    protected override ApiKeysPageModel Load()
    {
        var query = HttpContext.Request.Query;
        var projectId = Store.ActiveProjectId;
        var databases = Store.ProjectDatabases(projectId)
            .Select(d => (d.Id, d.Name))
            .ToList();
        var databaseNames = databases.ToDictionary(d => d.Id, d => d.Name);

        var keys = Store.ApiKeys.Where(k => k.ProjectId == projectId).Select(k => new CredentialView(
            "key", k.KeyId, k.Name, k.Prefix, k.LastFour, [.. k.Scopes],
            k.DatabaseId ?? "", k.CreatedByEmail, k.LastUsedAt, k.ExpiresAt,
            DashboardStore.CredentialStatus(k.RevokedAt, k.ExpiresAt)));
        var clients = Store.M2mCredentials.Where(c => c.ProjectId == projectId).Select(c => new CredentialView(
            "m2m", c.ClientId, c.Name, c.Prefix, c.LastFour, c.Scopes,
            c.DatabaseId, c.CreatedByEmail, c.LastUsedAt, c.ExpiresAt, c.Status));
        List<CredentialView> all = [.. keys, .. clients];

        var filters = (Query: query["q"].ToString().Trim(), Kind: query["kind"].ToString(), Status: query["status"].ToString(), DatabaseId: query["database"].ToString());
        var filtered = FilterCredentials(all, filters, databaseNames);

        var secret = Store.ConsumePendingSecret();
        var secretId = query["created"].ToString();
        (string CredentialId, string Name, string Secret)? secretView =
            secret is { } s && (secretId.Length == 0 || secretId == s.CredentialId || query["rotated"].Count > 0)
                ? s
                : null;

        return new ApiKeysPageModel(
            ActiveKeys: all.Count(c => c.Kind == "key" && c.Status != "Revoked"),
            Expiring: all.Count(c => c.Kind == "key" && c.Status == "Expiring"),
            M2mClients: all.Count(c => c.Kind == "m2m" && c.Status != "Revoked"),
            Databases: databases,
            Credentials: filtered,
            HasAnyCredentials: all.Count > 0,
            Query: filters.Query,
            Kind: filters.Kind,
            Status: filters.Status,
            DatabaseId: filters.DatabaseId,
            CreateKind: query["create"].ToString() is ("key" or "m2m") ? query["create"].ToString() : null,
            Error: query["error"].ToString() is { Length: > 0 } e ? e : null,
            LoadError: Store.DatabaseListError,
            Toast: query["toast"].ToString() is { Length: > 0 } t ? t : null,
            SecretId: secretView?.CredentialId,
            SecretName: secretView?.Name,
            SecretValue: secretView?.Secret)
        {
            Shell = ShellFactory.Create(Store, PageRoute),
        };
    }

    /// <summary>Port of filterCredentials + credentialSearchText.</summary>
    private static List<CredentialView> FilterCredentials(
        List<CredentialView> credentials,
        (string Query, string Kind, string Status, string DatabaseId) filters,
        Dictionary<string, string> databaseNames)
    {
        var term = filters.Query.ToLowerInvariant();
        return [.. credentials.Where(c =>
        {
            if (filters.Kind is ("key" or "m2m") && c.Kind != filters.Kind) return false;
            if (filters.Kind is not ("key" or "m2m") && filters.Kind.Length > 0) return false;
            if (filters.Status is ("Active" or "Expiring" or "Revoked") && c.Status != filters.Status) return false;
            if (filters.DatabaseId.Length > 0 && c.DatabaseId != filters.DatabaseId) return false;
            if (term.Length == 0) return true;
            var search = string.Join(' ',
                c.Name, c.Prefix, c.LastFour, c.CreatedByEmail, c.Status,
                c.Kind == "key" ? "api key" : "m2m client",
                string.Join(' ', c.Scopes),
                databaseNames.GetValueOrDefault(c.DatabaseId, c.DatabaseId)).ToLowerInvariant();
            return search.Contains(term);
        })];
    }
}
