using System.Collections.Immutable;
using System.Globalization;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.ApiKeys;

/// <summary>One row of the unified credentials table (API keys + M2M clients).</summary>
public sealed record CredentialView(
    string Kind,              // key | m2m
    string Id,
    string Name,
    string Prefix,
    string LastFour,
    string[] Scopes,
    string DatabaseId,
    string CreatedByEmail,
    string? LastUsedAt,
    string? ExpiresAt,
    string Status);

public sealed record ApiKeysPageModel(
    int ActiveKeys,
    int Expiring,
    int M2mClients,
    IReadOnlyList<(string Id, string Name)> Databases,
    IReadOnlyList<CredentialView> Credentials,
    bool HasAnyCredentials,
    string Query,
    string Kind,
    string Status,
    string DatabaseId,
    string? CreateKind,
    string? Error,
    string? Toast,
    string? SecretId,
    string? SecretName,
    string? SecretValue) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;
}

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

/// <summary>Port of formatUtcDateTime (en, UTC, medium date + short time).</summary>
public static class ApiKeyFormatting
{
    public static string UtcDateTime(string? value, string fallback = "Never")
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date.UtcDateTime.ToString("MMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture)
            : value;
    }
}
