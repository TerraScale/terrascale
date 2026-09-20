namespace TerraScale.Dashboard2;

/// <summary>Deployment mode guard for the dashboard authentication boundary.</summary>
public sealed record DashboardSecurityOptions(bool DemoMode, LiveIdentityOptions? Identity = null, DashboardStateOptions? State = null)
{
    public static DashboardSecurityOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var mode = configuration["TS_DASHBOARD_MODE"]?.Trim().ToLowerInvariant();
        if (mode is null or "")
        {
            if (environment.IsProduction())
                throw new InvalidOperationException("TS_DASHBOARD_MODE must be set to live after configuring a real identity provider; demo authentication is disabled in production.");
            mode = "demo";
        }

        if (mode == "demo")
        {
            if (environment.IsProduction() && configuration["TS2_E2E"] != "1")
                throw new InvalidOperationException("TS_DASHBOARD_MODE=demo is development-only and cannot start in production.");
            return new(true);
        }

        if (mode != "live")
            throw new InvalidOperationException("TS_DASHBOARD_MODE must be either live or demo.");

        var issuer = configuration["TS_DASHBOARD_IDP_ISSUER"]?.Trim();
        var introspection = configuration["TS_DASHBOARD_IDP_INTROSPECTION_URL"]?.Trim();
        var clientId = configuration["TS_DASHBOARD_IDP_CLIENT_ID"]?.Trim();
        var clientSecret = configuration["TS_DASHBOARD_IDP_CLIENT_SECRET"];
        var stateUrl = configuration["TS_DASHBOARD_STATE_URL"]?.Trim();
        var stateKey = configuration["TS_DASHBOARD_STATE_API_KEY"]?.Trim();
        var stateDatabase = configuration["TS_DASHBOARD_STATE_DATABASE"]?.Trim();
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) || issuerUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(introspection, UriKind.Absolute, out var introspectionUri) || introspectionUri.Scheme != Uri.UriSchemeHttps
            || !Uri.TryCreate(stateUrl, UriKind.Absolute, out var stateUri) || stateUri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(stateKey) || string.IsNullOrWhiteSpace(stateDatabase))
        {
            throw new InvalidOperationException("Live dashboard requires HTTPS identity-provider issuer/introspection URLs, identity-provider credentials, and TS_DASHBOARD_STATE_URL, TS_DASHBOARD_STATE_API_KEY, and TS_DASHBOARD_STATE_DATABASE for durable account state.");
        }

        return new(false, new LiveIdentityOptions(issuerUri, introspectionUri, clientId, clientSecret), new DashboardStateOptions(stateUri, stateKey, stateDatabase));
    }
}

public sealed record LiveIdentityOptions(Uri Issuer, Uri IntrospectionEndpoint, string ClientId, string ClientSecret);

public sealed record DashboardStateOptions(Uri ApiUrl, string ApiKey, string Database);
