using System.Text.Json.Serialization;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.ApiKeys;

[McpServerToolType]
public sealed class ApiKeyMcpTools(IHttpContextAccessor http, ApiKeyService keys, AccountService accounts)
{
    [McpServerTool(Name = "list_api_keys", Title = "List API keys", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a project's API key metadata without secrets. M2M clients are currently unavailable.")]
    public async Task<McpCredentialList> ListApiKeys(
        [Description("api-key or empty lists API keys; m2m currently returns no clients.")] string kind = "",
        [Description("active, inactive, or empty for both.")] string status = "",
        string databaseId = "", string search = "", string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        if (kind is not ("" or "api-key" or "m2m")) throw new AccountAccessException("Kind must be api-key or m2m.", 422);
        if (status is not ("" or "active" or "inactive")) throw new AccountAccessException("Status must be active or inactive.", 422);
        var context = Scope(organizationId, projectId);
        if (databaseId.Length > 0) DatabaseService.Database(context, databaseId);
        search = search.Trim();
        var list = await keys.ListAsync(context, Cancellation);
        var page = CursorPaging.Page(list.Where(key => kind != "m2m" && (status.Length == 0 || (status == "active") == key.IsActive) && (databaseId.Length == 0 || databaseId == key.DatabaseId)
            && (search.Length == 0 || (key.Name + " " + key.Id + "_").Contains(search, StringComparison.OrdinalIgnoreCase))).Select(ApiKeyService.CredentialMetadata),
            key => key.KeyId, CursorPaging.Scope(context, "credentials", kind, status, databaseId, search), limit, after);
        return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

    [McpServerTool(Name = "create_api_key", Title = "Create API key", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Issue a project API key for the TerraScale SDKs. Returns the secret once; store it securely. expiresInDays is 0 (default, works until revoked), 30, 90 or 365. allowedCidrs (up to 32 IPs or CIDR ranges, empty = any IP) limits where requests with the key may come from. Requires an owner or admin role.")]
    public Task<ProjectApiKeySecret> CreateApiKey(string name,
        [Description(ApiKeyService.ScopeDescription)] string[] scopes,
        string databaseId = "", int expiresInDays = 0, string[]? allowedCidrs = null, string? organizationId = null, string? projectId = null) =>
        CreateCredentialAsync(Scope(organizationId, projectId), "api-key", name, scopes, databaseId, expiresInDays, allowedCidrs ?? []);

    [McpServerTool(Name = "set_api_key_allowed_cidrs", Title = "Set API key allowed IPs", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Replace the IPs or CIDR ranges (up to 32; an empty list allows any IP) that requests with an API key may come from. Requests must also match the project's list when it has one. Changes apply within 60 seconds. Requires an owner or admin role.")]
    public async Task<ProjectCredentialMetadata> SetApiKeyAllowedCidrs(string keyId, string[] allowedCidrs, string? organizationId = null, string? projectId = null) =>
        ApiKeyService.CredentialMetadata(await keys.SetAllowedCidrsAsync(Scope(organizationId, projectId), keyId, allowedCidrs, Cancellation));

    [McpServerTool(Name = "create_m2m_client", Title = "Create M2M client", Destructive = false, OpenWorld = false, UseStructuredContent = true), Description("Issue a machine-to-machine client credential for a project. Requires an owner or admin role and a producer M2M contract.")]
    public Task<ProjectApiKeySecret> CreateM2mClient(string name, string[] scopes, string databaseId = "", string? organizationId = null, string? projectId = null) =>
        CreateCredentialAsync(Scope(organizationId, projectId), "m2m", name, scopes, databaseId, 0, []);

    [McpServerTool(Name = "rotate_api_key", Title = "Rotate API key", Destructive = true, OpenWorld = false, UseStructuredContent = true), Description("Replace an API key's secret and return the new secret once; store it securely. With previousSecretTtlSeconds = 0 (default), the old secret and its tokens stop everywhere within 2 seconds; open connections (including Postgres) close within 3 seconds. With a grace period (up to 86400 seconds), the old secret works until it ends; these bounds start then. Another rotation is refused during the grace. Requires an owner or admin role.")]
    public async Task<ProjectApiKeySecret> RotateApiKey(string keyId, int previousSecretTtlSeconds = 0, string? organizationId = null, string? projectId = null)
    {
        var result = await keys.RotateAsync(Scope(organizationId, projectId), keyId, previousSecretTtlSeconds, Cancellation);
        return new(ApiKeyService.Metadata(result.Key), result.Secret);
    }

    [McpServerTool(Name = "revoke_api_key", Title = "Revoke API key", Destructive = true, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Revoking stops this key everywhere within 2 seconds: new requests are refused and open connections (including Postgres) are closed within 3 seconds.")]
    public async Task<McpCredentialRevoked> RevokeApiKey(string keyId, string? organizationId = null, string? projectId = null)
    {
        var context = Scope(organizationId, projectId);
        await keys.RevokeAsync(context, keyId, Cancellation);
        await keys.RecordAsync(accounts, context, keyId, false, Cancellation);
        return new(keyId, true);
    }

    private AccountContext Scope(string? organizationId, string? projectId) => DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;

    private async Task<ProjectApiKeySecret> CreateCredentialAsync(AccountContext context, string kind, string name, string[] scopes, string databaseId, int expiresInDays, string[] allowedCidrs)
    {
        var result = await keys.CreateAsync(context, kind, name, databaseId, scopes, expiresInDays, allowedCidrs, Cancellation);
        await keys.RecordAsync(accounts, context, result.Key.Id, true, Cancellation);
        return new(ApiKeyService.Metadata(result.Key), result.Secret);
    }
}

public sealed record McpCredentialList(IReadOnlyList<ProjectCredentialMetadata> Credentials, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
public sealed record McpCredentialRevoked(string KeyId, bool Revoked);
