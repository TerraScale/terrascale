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
    public ShellModel Shell { get; init; } = null!;
}
