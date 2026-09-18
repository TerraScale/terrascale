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
