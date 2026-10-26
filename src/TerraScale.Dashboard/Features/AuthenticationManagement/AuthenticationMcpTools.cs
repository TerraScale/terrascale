using System.Text.Json.Serialization;
using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

// The project's end-user authentication at ts-auth, called with the caller's own sign-in token.
[McpServerToolType]
public sealed class AuthenticationMcpTools(IHttpContextAccessor http, AccountService accounts, ITsAuthProjects projects)
{
    [McpServerTool(Name = "auth_get_overview", Title = "Get project authentication", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("Read a project's ts-auth issuer, discovery and hosted sign-in URLs, sign-in methods, branding, social sign-in providers with their callback URLs, and user and OAuth application counts.")]
    public async Task<TerraScaleAuthProjectSettings> Overview(string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, false);
        return await projects.GetSettingsAsync(context.Session, context.Project!.Id, Cancellation);
    }

    [McpServerTool(Name = "auth_list_users", Title = "List project users", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("List a project's end users, optionally matching an email substring. Pass next back as after for the following page.")]
    public async Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectUser>> Users(string? search = null, string? after = null, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, false);
        return await projects.ListUsersAsync(context.Session, context.Project!.Id, search, after, Cancellation);
    }

    [McpServerTool(Name = "auth_set_user_disabled", Title = "Disable or enable a project user", Idempotent = true, UseStructuredContent = true, Destructive = true, OpenWorld = false),
     Description("Disable an end user, which ends every session and token of the user, or enable them again. Requires an owner or admin.")]
    public async Task<TerraScaleAuthProjectUser> SetDisabled(string userId, bool disabled, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        return await projects.SetUserDisabledAsync(context.Session, context.Project!.Id, userId, disabled, Cancellation);
    }

    [McpServerTool(Name = "auth_delete_user", Title = "Delete a project user", UseStructuredContent = false, Destructive = true, OpenWorld = false),
     Description("Delete an end user of the project. Requires an owner or admin.")]
    public async Task<string> DeleteUser(string userId, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        await projects.DeleteUserAsync(context.Session, context.Project!.Id, userId, Cancellation);
        return "Deleted user " + userId + ".";
    }

    [McpServerTool(Name = "auth_list_sessions", Title = "List live project sessions", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("List live end-user sessions, optionally of one user. Pass next back as after for the following page.")]
    public async Task<TerraScaleAuthPlatformPage<TerraScaleAuthProjectSession>> Sessions(string? userId = null, string? after = null, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, false);
        return await projects.ListSessionsAsync(context.Session, context.Project!.Id, userId, after, Cancellation);
    }

    [McpServerTool(Name = "auth_revoke_session", Title = "Revoke a project session", UseStructuredContent = false, Destructive = true, OpenWorld = false),
     Description("Revoke one live end-user session. Requires an owner or admin.")]
    public async Task<string> RevokeSession(string sessionId, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        await projects.RevokeSessionAsync(context.Session, context.Project!.Id, sessionId, Cancellation);
        return "Revoked session " + sessionId + ".";
    }

    [McpServerTool(Name = "auth_list_clients", Title = "List OAuth applications", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("List the project's OAuth applications. Client secrets are never listed.")]
    public async Task<AuthenticationClients> Clients(string? organizationId = null, string? projectId = null,
        [Description(CursorPaging.Description)] int limit = 100, string after = "")
    {
        var context = await Fresh(organizationId, projectId, false);
        var page = CursorPaging.Page(await projects.ListClientsAsync(context.Session, context.Project!.Id, Cancellation),
            client => client.ClientId, CursorPaging.Scope(context, "auth-clients"), limit, after);
        return new(page.Items, page.NextCursor, page.HasMore, page.Limit);
    }

    [McpServerTool(Name = "auth_create_client", Title = "Create an OAuth application", UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Create an OAuth application. type is public or confidential; 1 to 10 https redirect URIs (http only on localhost and loopback). A confidential client's client_secret is returned once. Requires an owner or admin.")]
    public async Task<TerraScaleAuthProjectOAuthClient> CreateClient(string name, string type, IReadOnlyList<string> redirectUris, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        return await projects.CreateClientAsync(context.Session, context.Project!.Id, AuthenticationConsole.NewClient(name, type, redirectUris), Cancellation);
    }

    [McpServerTool(Name = "auth_delete_client", Title = "Delete an OAuth application", UseStructuredContent = false, Destructive = true, OpenWorld = false),
     Description("Delete an OAuth application. Requires an owner or admin.")]
    public async Task<string> DeleteClient(string clientId, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        await projects.DeleteClientAsync(context.Session, context.Project!.Id, clientId, Cancellation);
        return "Deleted OAuth application " + clientId + ".";
    }

    [McpServerTool(Name = "auth_update_settings", Title = "Update sign-in methods and branding", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Set the hosted pages' sign-in methods (passcode, password, signUp; passcode or password stays on) and, when any branding argument is given, their branding (name up to 64 characters, https logoUrl, #rrggbb themeColor; an empty string clears a value). Omitted arguments keep their current value. Requires an owner or admin.")]
    public async Task<TerraScaleAuthProjectSettings> UpdateSettings(bool? passcode = null, bool? password = null, bool? signUp = null,
        string? name = null, string? logoUrl = null, string? themeColor = null, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        // Omitted arguments keep their current value, so read the settings first.
        var current = await projects.GetSettingsAsync(context.Session, context.Project!.Id, Cancellation);
        var methods = passcode is null && password is null && signUp is null ? null
            : AuthenticationConsole.Methods(passcode ?? current.Methods.Passcode, password ?? current.Methods.Password, signUp ?? current.Methods.SignUp);
        var validated = AuthenticationConsole.Branding(name ?? current.Branding.Name, logoUrl ?? current.Branding.LogoUrl, themeColor ?? current.Branding.ThemeColor);
        var branding = name is null && logoUrl is null && themeColor is null ? null
            : current.Branding with { Name = validated.Name, LogoUrl = validated.LogoUrl, ThemeColor = validated.ThemeColor };
        return await projects.UpdateSettingsAsync(context.Session, context.Project!.Id, new(methods, branding), Cancellation);
    }

    [McpServerTool(Name = "auth_set_provider", Title = "Configure a social sign-in provider", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Configure or update social sign-in: provider is google, github, apple or oidc. All take clientId (Apple: the Services ID) and clientSecret (Apple: the .p8 private key text), which is required the first time and kept when omitted. Apple also takes 10-character teamId and keyId; oidc takes an https issuer. Register the provider's callback_url from auth_get_overview at the provider. The secret is never returned. Requires an owner or admin.")]
    public async Task<TerraScaleAuthProjectSettings> SetProvider(string provider, string clientId, string? clientSecret = null, string? issuer = null, string? teamId = null,
        string? keyId = null, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        var update = AuthenticationConsole.Provider(provider, clientId, clientSecret, issuer, teamId, keyId);
        return await projects.SetProviderAsync(context.Session, context.Project!.Id, provider, update, Cancellation);
    }

    [McpServerTool(Name = "auth_remove_provider", Title = "Remove a social sign-in provider", UseStructuredContent = true, Destructive = true, OpenWorld = false, Idempotent = true),
     Description("Remove a social sign-in provider (google, github, apple or oidc) and its stored secret. Requires an owner or admin.")]
    public async Task<TerraScaleAuthProjectSettings> RemoveProvider(string provider, string? organizationId = null, string? projectId = null)
    {
        var context = await Fresh(organizationId, projectId, true);
        if (AuthenticationProvider.Find(provider) is null) throw new AccountAccessException("Choose google, github, apple or oidc.", 422);
        return await projects.DeleteProviderAsync(context.Session, context.Project!.Id, provider, Cancellation);
    }

    private async Task<AccountContext> Fresh(string? organizationId, string? projectId, bool manage)
    {
        var context = await AccountScope.RefreshAsync(accounts, DashboardMcp.Scope(http.HttpContext, organizationId, projectId), Cancellation, requireProject: true);
        if (context.Role.Length == 0) throw new AccountAccessException("Organization membership is required.");
        if (manage && !context.CanManage) throw new AccountAccessException("Organization management is required.");
        return context;
    }

    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}

public sealed record AuthenticationClients(IReadOnlyList<TerraScaleAuthProjectOAuthClient> Clients, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? NextCursor, bool HasMore, int Limit);
