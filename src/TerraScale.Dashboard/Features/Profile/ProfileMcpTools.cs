using System.ComponentModel;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.Profile
{
    [McpServerToolType]
    public sealed class ProfileMcpTools(IHttpContextAccessor http, AccountService accounts, AccessTokens tokens, ITsAuthMfa mfa)
    {
        [McpServerTool(Name = "get_profile", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("Read your profile and stored language preference.")]
        public async Task<DashboardMcpProfile> Profile(string? organizationId = null, string? projectId = null)
        {
            var current = Account();
            var context = current.Organization is null && string.IsNullOrEmpty(organizationId) && string.IsNullOrEmpty(projectId)
                ? await accounts.GetContextAsync(current.Session, Cancellation)
                : Scope(organizationId, projectId);
            if (context.Organization is not null)
            {
                context = await AccountScope.RefreshAsync(accounts, context, Cancellation, requireProject: context.Project is not null);
            }

            var permissions = new List<string>();
            if (context.Project is not null)
            {
                permissions.Add("Access selected project");
                if (context.CanWriteProjectData)
                {
                    permissions.Add("Write selected project data");
                }
            }
            if (context.CanManage)
            {
                permissions.Add("Manage organization resources");
            }

            if (context.Organization?.CanProvision(context.UserId) == true)
            {
                permissions.Add("Provision organization resources");
            }

            return new(context.UserId, context.Account.Id, context.Account.Name, context.Account.Email, context.Account.Phone,
                context.Account.Locale, context.Account.ActiveOrganizationId, context.Account.ActiveProjectId,
                context.Organization?.Id ?? "", context.Project?.Id ?? "", context.Organization?.Name ?? "", context.Role.Length == 0 ? null : context.Role,
                permissions, (await mfa.GetAccountMfaAsync(context.Session, Cancellation))?.Enabled, null, await tokens.ListAsync(context, Cancellation),
                PlatformDatabases.IsPlatform(context.Organization?.Id) ? "viewer" : context.Project?.Role, context.CanWriteProjectData);
        }

        [McpServerTool(Name = "update_profile", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Update your display name and optional phone number. Omit a field to keep it.")]
        public async Task<DashboardMcpSaved> UpdateProfile(
            [Description("Display name, 1–128 characters.")] string? name = null,
            [Description("Phone number with 7–15 digits, optionally starting with +; spaces, dots, dashes and parentheses are allowed. An empty string clears it.")] string? phone = null)
        {
            await ProfileChanges.SaveAsync(accounts, Account().Session, name, phone, Cancellation);
            return new(true);
        }

        [McpServerTool(Name = "set_language", UseStructuredContent = true, Idempotent = true, Destructive = false, OpenWorld = false), Description("Save your account language preference (en or pt-BR). The dashboard currently displays in English.")]
        public async Task<DashboardMcpSaved> Language(string locale)
        {
            if (locale is not ("en" or "pt-BR"))
            {
                throw new AccountAccessException("Choose an available language.", 422);
            }

            await accounts.UpdateAccountAsync(Account().Session, account => account.Locale = locale, Cancellation);
            return new(true);
        }

        [McpServerTool(Name = "list_access_tokens", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true), Description("List live personal access token metadata without secrets.")]
        public async Task<DashboardMcpAccessTokens> ListAccessTokens()
        {
            return new(await tokens.ListAsync(Account(), Cancellation));
        }

        [McpServerTool(Name = "revoke_access_token", UseStructuredContent = true, Destructive = true, OpenWorld = false), Description("Revoke one of your personal access tokens immediately.")]
        public async Task<DashboardMcpRevoked> RevokeAccessToken(string tokenId) { await tokens.RevokeAsync(Account(), tokenId, Cancellation); return new(true); }

        private AccountContext Account()
        {
            return McpScope.Account(http.HttpContext);
        }

        private AccountContext Scope(string? organizationId, string? projectId)
        {
            return DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
        }

        private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
    }

    public sealed record DashboardMcpProfile(string UserId, string AccountId, string Name, string Email, string Phone, string Locale,
        string ActiveOrganizationId, string ActiveProjectId, string OrganizationId, string ProjectId, string OrganizationName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Role, IReadOnlyList<string> Permissions,
        [property: Description("Whether two-factor authentication is on for your account, as ts-auth reports it; null when ts-auth did not report it, which does not mean disabled.")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        bool? MfaEnabled,
        [property: Description("Real passkey records are unavailable from the current owning provider contract; null does not mean no passkeys.")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)]
        IReadOnlyList<DashboardMcpPasskey>? Passkeys,
        IReadOnlyList<ProfileToken> AccessTokens,
        [property: Description("Effective role for exactly projectId, independent of organization membership.")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ProjectRole, bool CanWriteProjectData);

    public sealed record DashboardMcpPasskey(string Id, string Name, DateTimeOffset CreatedAt);

    public sealed record DashboardMcpSaved(bool Saved);
    public sealed record DashboardMcpRevoked(bool Revoked);
    public sealed record DashboardMcpAccessTokens(IReadOnlyList<ProfileToken> AccessTokens);
}
