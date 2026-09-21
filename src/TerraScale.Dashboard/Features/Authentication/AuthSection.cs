namespace TerraScale.Dashboard.Features.Authentication;

/// <summary>Navigation, form fields and table columns share one feature-owned contract.</summary>
public sealed record AuthSection(string Key, string Title, string Description, string Icon, string Singular, AuthField[] Fields, string[] Columns)
{
    public string Href => Key == "overview" ? "/auth" : $"/auth/{Key}";
    public bool CanCreate => Fields.Length > 0;
    public bool CanEdit => Key is "users" or "organizations" or "memberships" or "connections" or "clients" or "templates";
    public bool CanDelete => CanCreate || Key == "sessions";
    public string DeleteLabel => Key is "sessions" or "keys" or "invitations" ? "Revoke" : "Delete";
    public string ApiFamily => Key switch
    {
        "keys" => "api-keys", "clients" => "oauth-clients", "templates" => "jwt-templates",
        "audit" => "audit-events", "deliveries" => "webhooks/dead-letter", _ => Key,
    };

    public static readonly AuthSection[] All =
    [
        new("overview", "Overview", "Your application's identity workspace.", "ShieldCheck", "", [], []),
        new("users", "Users", "Manage your application's users, profiles and access.", "Users", "user",
        [
            new("name", "Full name", "The name shown in the user's profile."),
            new("email", "Email address", "Unique within this project.", "email"),
            new("username", "Username", "An optional unique handle.", Required: false),
            new("public_metadata", "Public metadata", "JSON object visible to your application.", "json", Default: "{}"),
            new("private_metadata", "Private metadata", "JSON object for server-side use only.", "json", Default: "{}"),
        ], ["email", "username"]),
        new("sessions", "Sessions", "Inspect and revoke application sessions. Dashboard sessions are managed in your profile.", "Monitor", "session", [], ["user", "device", "ip"]),
        new("organizations", "Organizations", "Group application users into customer organizations.", "Building2", "organization",
        [new("name", "Organization name", "The name your customer sees."), new("slug", "Slug", "Lowercase letters, numbers and hyphens.", "slug")], ["slug"]),
        new("memberships", "Memberships", "Assign users to organizations with an explicit role.", "Users", "membership",
        [new("name", "Membership label", "A recognizable name for this membership."), new("organization", "Organization", "Choose an application organization.", "organization"), new("user", "User", "Choose an existing application user.", "user"), new("role", "Role", "Access within this organization.", "select", Default: "member", Options: ["member", "admin"])], ["organization", "user", "role"]),
        new("invitations", "Invitations", "Prepare invitations to your application organizations.", "MailPlus", "invitation",
        [new("email", "Email address", "The person you want to invite.", "email"), new("organization", "Organization", "The organization they will join.", "organization"), new("role", "Role", "Role granted when the invitation is accepted.", "select", Default: "member", Options: ["member", "admin"])], ["organization", "role"]),
        new("domains", "Enrollment domains", "Configure organization enrollment by email domain.", "Globe", "domain",
        [new("name", "Domain", "A domain such as example.com. Use a domain owned by your organization.", "domain"), new("organization", "Organization", "The organization that owns this domain.", "organization")], ["organization"]),
        new("connections", "Connections", "Configure social and enterprise sign-in with OpenID Connect or SAML.", "GlobeLock", "connection",
        [new("name", "Connection name", "For example, Acme workforce."), new("protocol", "Protocol", "Select the identity provider protocol.", "select", Default: "OIDC", Options: ["OIDC", "SAML"]), new("issuer", "Issuer / entity ID", "The upstream provider's HTTPS issuer URL.", "url"), new("client_id", "OIDC client ID", "Required for OIDC connections.", Required: false), new("client_secret", "OIDC client secret", "Required for confidential OIDC providers. Credentials are never displayed after saving.", "password", false), new("sso_url", "SAML sign-in URL", "Required for SAML connections.", "url", false), new("certificate", "SAML certificate", "PEM-encoded signing certificate. Required for SAML.", "textarea", false)], ["protocol", "issuer"]),
        new("directories", "Directories", "Prepare SCIM 2.0 endpoints for workforce provisioning.", "FolderOpen", "directory",
        [new("name", "Directory name", "For example, Acme workforce."), new("organization", "Organization", "The organization receiving provisioned users.", "organization")], ["organization"]),
        new("clients", "OAuth clients", "Register web apps, public clients and service integrations.", "KeySquare", "client",
        [new("name", "Client name", "A recognizable application name."), new("type", "Application type", "Public clients use PKCE without a client secret.", "select", Default: "Web application", Options: ["Web application", "Public client", "Machine-to-machine"]), new("redirect_uris", "Redirect URIs", "One exact callback URL per line. HTTPS or loopback HTTP; no wildcards.", "uris", false), new("allowed_origins", "Allowed origins", "One HTTPS origin per line, without a path. Loopback HTTP is allowed.", "origins", false)], ["type", "redirect_uris"]),
        new("templates", "JWT templates", "Define custom claims for tokens minted by ts-auth.", "FileJson", "template",
        [new("name", "Template name", "Lowercase letters, numbers, hyphens and underscores; up to 48 characters.", "template"), new("claims", "Claims", "JSON object of string claims. Supports {{user.email}} and {{org.id}}.", "claims", Default: "{\n  \"email\": \"{{user.email}}\"\n}"), new("lifetime_seconds", "Token lifetime (seconds)", "Between 1 and 86,400 seconds.", "number", Default: "300")], ["lifetime_seconds", "claims"]),
        new("keys", "Management keys", "Issue scoped credentials for the ts-auth management API.", "KeyRound", "key",
        [new("name", "Key name", "Describe the workload using this key."), new("scopes", "Scopes", "Comma-separated API families: users, sessions, organizations, oauth-clients, connections, directories, jwt-templates, webhooks, audit-events, instance or *.", Default: "users,sessions")], ["scopes", "prefix"]),
        new("webhooks", "Webhooks", "Configure signed identity event delivery to your application.", "Webhook", "endpoint",
        [new("url", "Endpoint URL", "An HTTPS destination for identity events.", "url"), new("events", "Events", "Comma-separated event names or * for all events.", Default: "user.created,user.updated,session.created")], ["events"]),
        new("deliveries", "Webhook deliveries", "Inspect delivery outcomes and queue failed events for replay.", "Send", "delivery", [], ["endpoint", "event", "attempts"]),
        new("audit", "Audit log", "Review changes made to application identities and configuration.", "ScrollText", "event", [], ["actor", "target"]),
        new("billing", "Billing", "Review identity usage and your instance plan.", "CreditCard", "", [], []),
        new("settings", "Settings", "Instance branding and hosted sign-in defaults.", "Settings", "", [], []),
        new("quickstart", "Quickstart", "Connect your application using the ts-auth management API.", "Terminal", "", [], []),
    ];

    public static AuthSection? Find(string key) => All.FirstOrDefault(section => section.Key == key);
}
