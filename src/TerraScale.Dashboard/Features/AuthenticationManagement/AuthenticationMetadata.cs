namespace TerraScale.Dashboard.Features.AuthenticationManagement;

// Static API field metadata, not the state of a live ts-auth instance.
public static class AuthenticationMetadata
{
    public static AuthenticationCatalog Describe() => new(
        AuthenticationSection.All.Where(section => section.Fields is not null).Select(section => new AuthenticationSectionDefinition(
            section.Key, section.Title, section.Key == "keys" ? "Scoped sk_ keys for the ts-auth Management API. This console records keys without issuing secrets." : section.Description,
            section.Key is "users" or "organizations" or "memberships" or "connections" or "templates",
            section.Key is "invitations" or "keys", section.Key switch
            { "users" => ["block"], "connections" => ["toggle"], "directories" => ["rotate"], _ => null },
            section.Key == "connections" ? null : section.Fields!.Select(field => Field(section.Key, field)).ToArray())).ToArray(),
        Providers(), Strategies());

    private static AuthenticationFieldDefinition Field(string section, AuthenticationField field) => new(field.Key, field.Label, field.Type,
        field.Required, section + ":" + field.Key is "organizations:slug" or "memberships:organization" or "memberships:user",
        section + ":" + field.Key is "users:email" or "users:username" or "organizations:slug" or "domains:domain" or "templates:name",
        field.Hint.Length == 0 ? null : field.Hint, field.Placeholder.Length == 0 ? null : field.Placeholder,
        field.Type == "role" ? ["admin", "member"] : field.Options);

    private static AuthenticationFieldDefinition ProviderField(string key, string label, string type = "text", bool required = false,
        bool immutable = false, string? hint = null, string? example = null) => new(key, label, type, required, immutable, false, hint, example);

    private static IReadOnlyList<AuthenticationProviderDefinition> Providers()
    {
        var name = ProviderField("name", "Display name", required: true, hint: "Shown on the hosted sign-in button.");
        var client = ProviderField("client_id", "Client ID", required: true, immutable: true);
        var secret = ProviderField("client_secret", "Client secret", "password", true, hint: "Encrypted at rest and never shown again.");
        var organization = ProviderField("organization", "Organization", "organization", immutable: true, hint: "Optional. Scope the connection to one organization's members.");
        return
        [
            new("Google", "Social", "Google accounts through Google's OpenID Connect issuer.", [name, client, secret]),
            new("Microsoft", "Social", "Work and school accounts in one Microsoft Entra ID tenant.",
                [name, ProviderField("tenant", "Directory (tenant) ID", required: true, immutable: true, example: "00000000-0000-0000-0000-000000000000"), client, secret]),
            new("GitHub", "Social", "GitHub OAuth apps; only verified email addresses are accepted.", [name, client, secret]),
            new("OIDC", "Enterprise", "Any OpenID Connect issuer, discovered from its well-known document.",
                [name, ProviderField("issuer", "Issuer URL", "url", true, true, example: "https://login.example.com"), client, secret,
                    ProviderField("scopes", "Scopes", immutable: true, hint: "Space separated; must include openid and email.", example: "openid email profile"), organization]),
            new("SAML", "Enterprise", "SAML 2.0 identity providers with signed assertions.",
                [name, ProviderField("idp_entity_id", "IdP entity ID", required: true, immutable: true, example: "https://idp.example.com/metadata"),
                    ProviderField("sso_redirect_url", "SSO URL", "url", true, true, example: "https://idp.example.com/sso"),
                    ProviderField("x509_certificate_pem", "Signing certificate", "textarea", true, true, example: "-----BEGIN CERTIFICATE-----"), organization])
        ];
    }

    private static IReadOnlyList<AuthenticationStrategy> Strategies() =>
    [
        new("email_verification", "Verify at sign-up", "sign-in", "New users confirm their address from an emailed link before their identity is established.", true, false),
        new("email_passcode", "Email verification code", "sign-in", "Sign in with a one-time code sent to the user's email address.", true, true),
        new("magic_link", "Email magic link", "sign-in", "Sign in by opening a single-use link sent to the user's email address.", true, true),
        new("password", "Sign in with a password", "sign-in", "Users set a password at sign-up and can reset it from an emailed link.", true, true),
        new("username", "Allow usernames", "sign-in", "Users pick a username at sign-up and can use it as their identifier.", false, false),
        new("passkeys", "Sign in with a passkey", "sign-in", "Discoverable credentials with sign-count clone detection; users manage them from their account page.", true, true),
        new("totp", "Authenticator app (TOTP)", "mfa", "Users enroll from their account page; enrolled users complete a step-up challenge at every sign-in.", true, false),
        new("recovery_codes", "Recovery codes", "mfa", "Issued when a user enrolls an authenticator app; each code works once.", true, false, "totp")
    ];
}
