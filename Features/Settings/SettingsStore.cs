using System.Collections.Immutable;
namespace TerraScale.Dashboard2;

public sealed record SettingsAuthProvider(
    string Provider,
    string DisplayName,
    string Protocol,
    string Status,          // Enabled | Disabled
    bool BuiltIn,
    string ClientId,
    string SecretStatus,
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string UserInfoEndpoint,
    string RedirectUri,
    string SamlServiceProviderEntityId,
    string SamlSingleSignOnUrl,
    string SamlCertificateStatus,
    string SamlAssertionConsumerServiceUrl,
    string UpdatedAt);

public sealed record SettingsCredentialView(
    string Id,
    string Type,            // API key | M2M application
    string Name,
    string Prefix,
    string LastFour,
    IReadOnlyList<string> Scopes,
    string Status,          // Active | Revoked
    string CreatedByEmail,
    string CreatedAt,
    string ExpiresAt,
    string LastUsedAt);

public sealed partial class DashboardStore
{
    public bool AuthenticationEnabled { get; private set; } = true;

    public string SecuritySettingsUpdatedAt { get; private set; } = "2026-08-30T14:22:00Z";

    public ImmutableArray<SettingsAuthProvider> AuthProviders { get; private set; } =
    [
        new("entra-id", "Microsoft Entra ID", "OIDC", "Enabled", false,
            "00000000-0000-0000-0000-000000000000", "Secret configured",
            "https://login.microsoftonline.com/9c1f4d2e-4f6b-4a7e-9d3a-1b2c3d4e5f6a/oauth2/v2.0/authorize",
            "https://login.microsoftonline.com/9c1f4d2e-4f6b-4a7e-9d3a-1b2c3d4e5f6a/oauth2/v2.0/token",
            "https://graph.microsoft.com/oidc/userinfo",
            "https://dashboard.terrascale.tech/api/v1/auth/callback",
            "", "", "", "", "2026-08-30T14:22:00Z"),
        new("okta-saml", "Okta (SAML)", "SAML", "Enabled", false,
            "", "",
            "", "", "", "",
            "https://terrascale.okta.com/saml/sp/01JC9Q7T4N8Y2M3P", "https://terrascale.okta.com/app/terrascale/sso/saml",
            "Certificate valid until 2027-04-12", "https://dashboard.terrascale.tech/api/v1/auth/saml/acs",
            "2026-07-18T09:40:00Z"),
    ];

    public bool CanManageByok => false;     // demo: not on an Enterprise plan
    public bool CanDeleteProject => true;   // demo session is the org owner

    public ImmutableArray<SettingsCredentialView> SettingsCredentials =>
    [
        .. ApiKeys.Select(k => new SettingsCredentialView(
            k.KeyId, "API key", k.Name, k.Prefix, k.LastFour, k.Scopes,
            k.Status, k.CreatedByEmail, k.CreatedAt, k.ExpiresAt ?? "", k.LastUsedAt ?? "")),
        .. M2mApplications.Select(m => new SettingsCredentialView(
            m.ClientId, "M2M application", m.Name, m.ClientId[..8], m.ClientId[^4..],
            ["documents:read", "documents:write"], m.Status, CurrentUser.Email, m.CreatedAt, "", m.LastUsedAt ?? "")),
    ];

    public void SetAuthenticationEnabled(bool enabled) =>
        AuthenticationEnabled = enabled;

    public void DeleteProject(string projectId) =>
        Projects = [.. Projects.Where(p => p.Id != projectId)];
}
