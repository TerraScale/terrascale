using System.ComponentModel;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

[McpServerToolType]
public sealed class AuthenticationMcpTools(IHttpContextAccessor http, AccountService accounts)
{
    [McpServerTool(Name = "auth_describe_sections", Title = "Describe authentication sections", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("List the ts-auth record sections with their fields, validation rules, and actions, the SSO connection providers, and the sign-in strategies. Strategy values describe the source API defaults, not a live project's configuration. Use it to build auth_save_resource values.")]
    public async Task<AuthenticationCatalog> Describe()
    {
        var context = http.HttpContext?.Items[DashboardMcp.ContextKey] as AccountContext ?? throw new AccountAccessException("Authentication is required.", 401);
        _ = await accounts.GetContextAsync(context.Session, Cancellation);
        return AuthenticationMetadata.Describe();
    }

    [McpServerTool(Name = "auth_get_overview", Title = "Get authentication overview", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("Summarize a project's ts-auth management settings. Returns unavailable because the current producer does not expose its customer management API or matching project credential binding.")]
    public Task<AuthenticationOverview> Overview(string? organizationId = null, string? projectId = null) => Read<AuthenticationOverview>(organizationId, projectId);

    [McpServerTool(Name = "auth_list_resources", Title = "List authentication records", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("List a ts-auth record section. Call auth_describe_sections for each section's fields. Requires the project's customer management API and matching project credential binding.")]
    public Task<AuthenticationResources> List(string section, string search = "", string? organizationId = null, string? projectId = null) => Read<AuthenticationResources>(organizationId, projectId);

    [McpServerTool(Name = "auth_save_resource", Title = "Save authentication record", UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Create a ts-auth record or update an editable record's mutable fields. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationResource> Save(string section,
        [Description("Field values keyed by field key from auth_describe_sections. Choices fields take comma-separated values. Connections also need protocol: Google, Microsoft, GitHub, OIDC, or SAML.")] IReadOnlyDictionary<string, string> values,
        string id = "", string? organizationId = null, string? projectId = null) => Manage<AuthenticationResource>(organizationId, projectId);

    [McpServerTool(Name = "auth_delete_resource", Title = "Delete authentication record", UseStructuredContent = true, Destructive = true, OpenWorld = false),
     Description("Revoke an invitation or management key, or delete another ts-auth record. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationDeleted> Delete(string section, string id, string? organizationId = null, string? projectId = null) => Manage<AuthenticationDeleted>(organizationId, projectId);

    [McpServerTool(Name = "auth_run_resource_action", Title = "Run authentication record action", UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Block or unblock a user, enable or disable an SSO connection, or rotate a SCIM bearer token. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationResource> Action(string section, string id, string action, string? organizationId = null, string? projectId = null) => Manage<AuthenticationResource>(organizationId, projectId);

    [McpServerTool(Name = "auth_set_strategy", Title = "Set sign-in strategy", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Change a customer's sign-in or multi-factor strategy. This is an authentication security setting, not a dashboard deployment switch. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationStrategies> Strategy(string strategy, bool enabled, string? organizationId = null, string? projectId = null) => Manage<AuthenticationStrategies>(organizationId, projectId);

    [McpServerTool(Name = "auth_update_branding", Title = "Update hosted page branding", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Set the name, HTTPS logo and six-digit hex theme color of hosted sign-in pages. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationBranding> Branding(string name, string logoUrl = "", string themeColor = "", string? organizationId = null, string? projectId = null) => Manage<AuthenticationBranding>(organizationId, projectId);

    [McpServerTool(Name = "auth_list_audit_events", Title = "List authentication audit events", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("List the authentication console changes made for a project, newest first. Requires its customer management API and matching project credential binding.")]
    public Task<AuthenticationEvents> Audit(string? organizationId = null, string? projectId = null) => Read<AuthenticationEvents>(organizationId, projectId);

    [McpServerTool(Name = "auth_get_emails", Title = "Get authentication emails", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("Read ts-auth email branding, customized templates and one email's fields and defaults. Requires the project's customer management API and matching project credential binding.")]
    public Task<AuthenticationEmails> Emails(string template = "sign_in_code", string locale = "en", string? organizationId = null, string? projectId = null) => Read<AuthenticationEmails>(organizationId, projectId);

    [McpServerTool(Name = "auth_update_email_branding", Title = "Update email branding", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Change the logo, colors, theme, sender and footer of every ts-auth email. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationEmailSaved> EmailBranding(AuthenticationEmailBranding? branding = null, IReadOnlyList<string>? clear = null,
        string? organizationId = null, string? projectId = null) => Manage<AuthenticationEmailSaved>(organizationId, projectId);

    [McpServerTool(Name = "auth_update_email_template", Title = "Update authentication email", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Change one email's wording; omitted fields keep their wording and clear restores named defaults. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationEmails> EmailTemplate(string template = "sign_in_code", string locale = "en", AuthenticationEmailFields? fields = null,
        IReadOnlyList<string>? clear = null, string? organizationId = null, string? projectId = null) => Manage<AuthenticationEmails>(organizationId, projectId);

    [McpServerTool(Name = "auth_reset_email_template", Title = "Reset authentication email", Idempotent = true, UseStructuredContent = true, Destructive = false, OpenWorld = false),
     Description("Return one authentication email to its default wording. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationEmails> ResetEmail(string template = "sign_in_code", string locale = "en", string? organizationId = null, string? projectId = null) => Manage<AuthenticationEmails>(organizationId, projectId);

    [McpServerTool(Name = "auth_preview_email", Title = "Preview authentication email", ReadOnly = true, UseStructuredContent = true, Destructive = false, OpenWorld = false, Idempotent = true),
     Description("Render one ts-auth email in light, dark or auto color scheme. Requires the project's customer management API and matching project credential binding.")]
    public Task<AuthenticationRenderedEmail> PreviewEmail(string template = "sign_in_code", string locale = "en", string colorScheme = "auto",
        string? organizationId = null, string? projectId = null) => Read<AuthenticationRenderedEmail>(organizationId, projectId);

    [McpServerTool(Name = "auth_send_test_email", Title = "Send test authentication email", OpenWorld = true, UseStructuredContent = true, Destructive = false),
     Description("Send a test authentication email to the verified caller. Requires an owner or admin and the project's customer management API with its matching project credential binding.")]
    public Task<AuthenticationEmailSent> SendEmail(string template = "sign_in_code", string locale = "en", string? organizationId = null, string? projectId = null) => Manage<AuthenticationEmailSent>(organizationId, projectId);

    private async Task<AccountContext> Fresh(string? organizationId, string? projectId) =>
        await AccountScope.RefreshAsync(accounts, DashboardMcp.Scope(http.HttpContext, organizationId, projectId), Cancellation, requireProject: true);

    // Project tenants exist, but customer management routes and matching
    // project-scoped credentials are not available from the producer yet.
    // The dashboard tenant's key or confidential email client is never used.
    private async Task<T> Read<T>(string? organizationId, string? projectId)
    {
        _ = await Fresh(organizationId, projectId);
        throw new AccountAccessException(AuthenticationConsole.Unavailable, 503);
    }
    private async Task<T> Manage<T>(string? organizationId, string? projectId)
    {
        var context = await Fresh(organizationId, projectId);
        if (!context.CanManage) throw new AccountAccessException("Organization management is required.");
        throw new AccountAccessException(AuthenticationConsole.Unavailable, 503);
    }
    private CancellationToken Cancellation => http.HttpContext?.RequestAborted ?? default;
}
