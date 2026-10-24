using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.ApiKeys;

[McpServerToolType]
public sealed class ApiKeyMcpTools(IHttpContextAccessor http, ApiKeyService keys, AccountService accounts)
{
    [McpServerTool(Name = "list_api_keys", Title = "List API keys", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a project's API keys and machine-to-machine (M2M) clients without their secrets.")]
    public async Task<McpCredentialList> ListApiKeys(string kind = "", string status = "", string databaseId = "", string search = "", string? organizationId = null, string? projectId = null)
    {
        if (kind is not ("" or "api-key" or "m2m")) throw new AccountAccessException("Kind must be api-key or m2m.", 422);
        if (status is not ("" or "active" or "inactive")) throw new AccountAccessException("Status must be active or inactive.", 422);
        var context = Scope(organizationId, projectId);
        if (databaseId.Length > 0) DatabaseService.Database(context, databaseId);
        search = search.Trim();
        var list = await keys.ListAsync(context, Cancellation);
        return new(list.Where(key => kind != "m2m" && (status.Length == 0 || (status == "active") == key.IsActive) && (databaseId.Length == 0 || databaseId == key.DatabaseId)
            && (search.Length == 0 || (key.Name + " " + key.Id + "_").Contains(search, StringComparison.OrdinalIgnoreCase))).Select(ApiKeyService.CredentialMetadata).ToArray());
    }

    [McpServerTool(Name = "create_api_key", Title = "Create API key", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Issue a project API key for the TerraScale SDKs. Returns the secret once; store it securely. expiresInDays is 0 (default, works until revoked), 30, 90 or 365. Requires an owner or admin role.")]
    public Task<ProjectApiKeySecret> CreateApiKey(string name, string[] scopes, string databaseId = "", int expiresInDays = 0, string? organizationId = null, string? projectId = null) =>
        CreateCredentialAsync(Scope(organizationId, projectId), "api-key", name, scopes, databaseId, expiresInDays);

    [McpServerTool(Name = "create_m2m_client", Title = "Create M2M client", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Issue a machine-to-machine client credential for a project. Requires an owner or admin role and a producer M2M contract.")]
    public Task<ProjectApiKeySecret> CreateM2mClient(string name, string[] scopes, string databaseId = "", string? organizationId = null, string? projectId = null) =>
        CreateCredentialAsync(Scope(organizationId, projectId), "m2m", name, scopes, databaseId, 0);

    [McpServerTool(Name = "rotate_api_key", Title = "Rotate API key", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Replace an API key's secret and return the new secret once; store it securely. The old secret keeps working for previousSecretTtlSeconds (0 to 86400, default 0 = stops immediately); another rotation is refused while that grace period lasts. Requires an owner or admin role.")]
    public async Task<ProjectApiKeySecret> RotateApiKey(string keyId, int previousSecretTtlSeconds = 0, string? organizationId = null, string? projectId = null)
    {
        var result = await keys.RotateAsync(Scope(organizationId, projectId), keyId, previousSecretTtlSeconds, Cancellation);
        return new(ApiKeyService.Metadata(result.Key), result.Secret);
    }

    [McpServerTool(Name = "revoke_api_key", Title = "Revoke API key", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Revoke a project API key to prevent new token issuance. Previously issued tokens remain valid until they expire, within 60 seconds.")]
    public async Task<McpCredentialRevoked> RevokeApiKey(string keyId, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        await keys.RevokeAsync(context, keyId, Cancellation);
        await keys.RecordAsync(accounts, context, keyId, false, Cancellation);
        return new(keyId, true);
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

    private async Task<ProjectApiKeySecret> CreateCredentialAsync(AccountContext context, string kind, string name, string[] scopes, string databaseId, int expiresInDays)
    {
        var result = await keys.CreateAsync(context, kind, name, databaseId, scopes, expiresInDays, Cancellation);
        await keys.RecordAsync(accounts, context, result.Key.Id, true, Cancellation);
        return new(ApiKeyService.Metadata(result.Key), result.Secret);
    }
}

public sealed record McpCredentialList(IReadOnlyList<ProjectCredentialMetadata> Credentials);
public sealed record McpCredentialRevoked(string KeyId, bool Revoked);
