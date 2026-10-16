using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Shared.UI;

namespace TerraScale.Dashboard.Features.AuthenticationManagement;

// ts-auth creates a project-scoped tenant identity with each customer project.
// Its customer management API and matching project-key delivery are not
// currently available to the dashboard. Never use the dashboard tenant's key.
public static class AuthenticationConsole
{
    public const string Unavailable = "Authentication management is unavailable for this project. ts-auth has a project-scoped tenant, but its customer management API and matching project credential binding are unavailable to the dashboard.";
    public const string ReadOnly = "Changes are disabled in this read-only view.";

    public static IEndpointRouteBuilder MapAuthenticationManagement(this IEndpointRouteBuilder endpoints)
    {
        foreach (var prefix in new[] { "/auth", "/authentication" })
        {
            endpoints.MapGet(prefix, async (HttpContext http, DashboardSessions sessions, AccountService accounts) =>
            {
                var section = http.Request.Query["section"].ToString();
                return AuthenticationSection.Find(section) is not null
                    ? AuthEndpoints.SeeOther(prefix + "/" + Uri.EscapeDataString(section))
                    : await PageAsync(http, sessions, accounts, "overview");
            });
            endpoints.MapGet(prefix + "/{section}", async (HttpContext http, string section, DashboardSessions sessions, AccountService accounts) => await PageAsync(http, sessions, accounts, section));
            endpoints.MapGet(prefix + "/emails/preview", async (HttpContext http, DashboardSessions sessions, AccountService accounts) => await PageAsync(http, sessions, accounts, "emails"));
        }
        endpoints.MapPost("/api/v1/auth-management/{**path}", async (HttpContext http, string path, DashboardSessions sessions, AccountService accounts) =>
        {
            var key = path.Split('/')[0];
            if (key is not ("instance" or "settings" or "emails") && AuthenticationSection.Find(key)?.Fields is null) return Results.NotFound();
            var context = await AccountRequest.RequireAsync(http, sessions, accounts);
            if (context is null) return Results.Unauthorized();
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (!Csrf.Validate(http, form)) return Results.StatusCode(403);
            if (!context.CanManage || context.Project is null) return Results.StatusCode(403);
            return Results.Problem(Unavailable, statusCode: 503);
        });
        return endpoints;
    }

    private static async Task<IResult> PageAsync(HttpContext http, DashboardSessions sessions, AccountService accounts, string key)
    {
        var section = AuthenticationSection.Find(key);
        if (section is null) return Results.NotFound();
        var context = await AccountEndpoints.RequireAsync(http, sessions, accounts);
        if (context is null) return AccountEndpoints.LoginRedirect(http);
        if (context.Project is null) return AuthEndpoints.SeeOther("/");
        // Only the public demo registers this; production has no binding and keeps the 503 unavailable page.
        var data = http.RequestServices.GetService<IAuthenticationDemoData>()?.Snapshot;
        var page = AuthenticationView.Create(new(ShellModel.For(http, context, key == "overview" ? "Authentication" : "Authentication · " + section.Title), section, http.Request.Query["q"].ToString(), data));
        if (data is null) page.StatusCode = 503;
        return page;
    }
}

// Read-only synthetic tenant for the public demo (DEMO=true). Never registered in production.
public interface IAuthenticationDemoData
{
    AuthenticationSnapshot Snapshot { get; }
}
public sealed record AuthenticationRow(string[] Cells, string Status = "", string Tone = "neutral");
public sealed record AuthenticationTable(string[] Columns, AuthenticationRow[] Rows);
public sealed record AuthenticationSnapshot(string Issuer, string ClientId, string Sso, IReadOnlySet<string> Enabled,
    IReadOnlyDictionary<string, AuthenticationTable> Tables, AuthenticationBranding Branding, AuthenticationEmailBranding EmailBranding);

public sealed record AuthenticationPage(ShellModel Shell, AuthenticationSection Section, string Search = "", AuthenticationSnapshot? Data = null)
{
    public AccountContext Context => Shell.Context;
    public string Heading => Section.Key == "overview" ? "Authentication" : "Authentication: " + Section.Title;
}
public sealed record AuthenticationField(string Key, string Label, string Type = "text", bool Required = false, string Placeholder = "", string Hint = "", string[]? Options = null);
public sealed record AuthenticationSection(string Key, string Title, string Description, string Icon, string Group, string Noun = "", AuthenticationField[]? Fields = null)
{
    public static AuthenticationSection? Find(string key) => All.FirstOrDefault(section => section.Key == key);
    public static readonly AuthenticationSection[] All =
    [
        new("overview", "Overview", "Setup progress and identity totals for this instance.", "LayoutDashboard", ""),
        new("quickstart", "Quickstart", "Connect an application to this instance with OpenID Connect.", "Zap", ""),
        new("users", "Users", "People who can sign in to your application.", "Users", "Users", "user", [new("name", "Full name", Placeholder: "Ada Lovelace"), new("email", "Email address", "email", true, "ada@example.com"), new("username", "Username", Placeholder: "ada", Hint: "Optional. Used when username sign-in is enabled.")]),
        new("organizations", "Organizations", "Customer workspaces with their own members, roles, and SSO.", "Building2", "Users", "organization", [new("name", "Name", Required: true, Placeholder: "Acme Inc."), new("slug", "Slug", Required: true, Placeholder: "acme", Hint: "Lowercase letters, numbers, and dashes. Cannot change later.")]),
        new("memberships", "Memberships", "Which users belong to which organization, and with what role.", "Network", "Users", "membership", [new("organization", "Organization", "organization", true), new("user", "User", "user", true), new("role", "Role", "role", true, Hint: "admin, member, or any custom role up to 64 characters.")]),
        new("invitations", "Invitations", "Pending email invitations to join an organization.", "MailPlus", "Users", "invitation", [new("email", "Email address", "email", true, "teammate@example.com"), new("organization", "Organization", "organization", true), new("role", "Role", "role", true)]),
        new("domains", "Enrollment domains", "Users with a verified email on these domains can join the organization.", "Globe", "Users", "domain", [new("domain", "Domain", Required: true, Placeholder: "acme.com"), new("organization", "Organization", "organization", true)]),
        new("sign-in", "Sign-in methods", "How users identify themselves and prove who they are.", "LockKeyhole", "Sign-in"),
        new("connections", "SSO connections", "Social and enterprise identity providers users can sign in with.", "GlobeLock", "Sign-in", "connection", [new("name", "Name", Required: true)]),
        new("mfa", "Multi-factor", "Second factors users can enroll and must pass at sign-in.", "ShieldCheck", "Sign-in"),
        new("sessions", "Sessions", "How long sign-ins last and how they end.", "Monitor", "Sign-in"),
        new("directories", "Directory sync", "SCIM 2.0 directories that provision and deprovision users from an identity provider.", "Layers", "Sign-in", "directory", [new("organization", "Scope", "organization", Hint: "Leave empty for an instance-wide directory that controls authentication for every linked user.")]),
        new("clients", "OAuth applications", "Applications that sign users in through the OAuth 2.1 authorization code flow with PKCE.", "Boxes", "Developers", "application", [new("name", "Application name", Required: true, Placeholder: "Web app"), new("type", "Client authentication", "select", true, Hint: "Public clients run in the browser or on devices and cannot keep a secret.", Options: ["client_secret_basic", "client_secret_post", "none"]), new("redirect_uris", "Redirect URIs", "textarea", true, "https://app.example.com/callback", "One per line. HTTPS, or HTTP on localhost and loopback addresses.")]),
        new("templates", "JWT templates", "Custom-claim tokens minted on demand for third-party services.", "FileJson", "Developers", "template", [new("name", "Name", Required: true, Placeholder: "supabase", Hint: "Up to 48 letters, numbers, dashes, and underscores."), new("claims", "Claims", "textarea", true, "{\"sub\": \"{{user.id}}\", \"email\": \"{{user.email}}\"}"), new("lifetime_seconds", "Lifetime in seconds", "number", true, "300")]),
        new("keys", "Management keys", "Scoped sk_ keys for the ts-auth Management API.", "KeySquare", "Developers", "key", [new("name", "Key name", Required: true, Placeholder: "Backend"), new("scopes", "Scopes", "choices", true, Hint: "A key can call /v1/{slug}/{family} routes for each selected family.", Options: ["*", "users", "sessions", "organizations", "domains", "connections", "directories", "oauth-clients", "jwt-templates", "webhooks", "audit-events", "api-keys", "instance", "billing"])]),
        new("webhooks", "Webhooks", "HMAC-signed event deliveries with retries and a dead-letter queue.", "Webhook", "Developers", "endpoint", [new("url", "Endpoint URL", "url", true, "https://api.example.com/webhooks/auth"), new("events", "Events", "choices", true, Options: ["*", "user.created", "user.updated", "user.deleted", "user.signed_up", "user.signed_in", "user.signed_out", "user.email_verified", "user.password_changed", "user.password_reset", "user.impersonated", "organization.created", "organization.deleted", "organization.enrolled", "membership.created", "membership.updated", "membership.deleted", "invitation.created", "invitation.accepted"])]),
        new("branding", "Branding", "Name, logo, and color of the hosted sign-in pages.", "Wand2", "Customize"),
        new("emails", "Emails", "Branding, sender, and wording of the emails ts-auth sends.", "Mail", "Customize"),
        new("audit", "Audit log", "Authentication changes made in this project.", "ScrollText", "Customize"),
        new("danger", "Danger zone", "Actions here affect every user of this project and cannot be undone.", "TriangleAlert", "")
    ];
}
