using TerraScale.Dashboard;

namespace TerraScale.Dashboard.Features.ApiKeys.Pages.ApiKeys;

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
    string? LoadError,
    string? Toast,
    string? SecretId,
    string? SecretName,
    string? SecretValue) : IPageModel
{
    public const int PageSize = 10;

    public ShellModel Shell { get; init; } = null!;
    public int Page { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int Total { get; init; }

    public int First => Total is 0 ? 0 : ((Page - 1) * PageSize) + 1;
    public int Last => Math.Min(Page * PageSize, Total);

    public string Href(int page)
    {
        var query = new List<string>();
        if (Query.Length > 0) query.Add($"q={Uri.EscapeDataString(Query)}");
        if (Kind.Length > 0) query.Add($"kind={Uri.EscapeDataString(Kind)}");
        if (Status.Length > 0) query.Add($"status={Uri.EscapeDataString(Status)}");
        if (DatabaseId.Length > 0) query.Add($"database={Uri.EscapeDataString(DatabaseId)}");
        if (page > 1) query.Add($"page={page}");
        return query.Count is 0 ? "/api-keys" : "/api-keys?" + string.Join('&', query);
    }
}
