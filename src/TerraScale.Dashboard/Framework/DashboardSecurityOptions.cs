namespace TerraScale.Dashboard;

/// <summary>Deployment mode guard for the dashboard authentication boundary.</summary>
public sealed record DashboardSecurityOptions(bool DemoMode, LiveIdentityOptions? Identity = null, DashboardStateOptions? State = null)
{
    public static DashboardSecurityOptions Load(IConfiguration configuration, IHostEnvironment environment)
    {
        var productionDemo = configuration["TS_DASHBOARD_PRODUCTION_DEMO"] == "1";
        // Demo is the default; any live setting opts into live validation.
        if (productionDemo || !HasLiveConfiguration(configuration))
        {
            // Production refuses demo authentication unless the operator has
            // explicitly acknowledged there is no live identity provider yet.
            if (environment.IsProduction()
                && configuration["TS2_E2E"] != "1"
                && !productionDemo)
            {
                throw new InvalidOperationException(
                    "The demo backend is development-only; set TS_DASHBOARD_PRODUCTION_DEMO=1 to acknowledge running it in production, or configure the live identity provider.");
            }

            return new(true);
        }

        var idpOrigin = ParseOrigin(configuration["TS_DASHBOARD_IDP_ORIGIN"]?.Trim(), allowLoopbackHttp: !environment.IsProduction(), name: "TS_DASHBOARD_IDP_ORIGIN");
        var publicOrigin = ParseOrigin(configuration["TS_DASHBOARD_PUBLIC_ORIGIN"]?.Trim(), allowLoopbackHttp: !environment.IsProduction(), name: "TS_DASHBOARD_PUBLIC_ORIGIN");
        var stateUrl = ParseOrigin(configuration["TS_DASHBOARD_STATE_URL"]?.Trim(), allowLoopbackHttp: !environment.IsProduction(), name: "TS_DASHBOARD_STATE_URL");
        var instance = configuration["TS_DASHBOARD_IDP_INSTANCE"]?.Trim();
        var clientId = configuration["TS_DASHBOARD_IDP_CLIENT_ID"]?.Trim();
        var clientSecret = configuration["TS_DASHBOARD_IDP_CLIENT_SECRET"];
        var managementKey = configuration["TS_DASHBOARD_IDP_MANAGEMENT_KEY"]?.Trim();
        var stateKey = configuration["TS_DASHBOARD_STATE_API_KEY"]?.Trim();
        var stateDatabase = configuration["TS_DASHBOARD_STATE_DATABASE"]?.Trim();
        if (instance is null || instance.Length < 3
            || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret)
            || string.IsNullOrWhiteSpace(managementKey)
            || string.IsNullOrWhiteSpace(stateKey) || string.IsNullOrWhiteSpace(stateDatabase))
        {
            throw new InvalidOperationException(
                "Live dashboard requires TS_DASHBOARD_IDP_ORIGIN, TS_DASHBOARD_IDP_INSTANCE, TS_DASHBOARD_IDP_CLIENT_ID, TS_DASHBOARD_IDP_CLIENT_SECRET, TS_DASHBOARD_IDP_MANAGEMENT_KEY, TS_DASHBOARD_PUBLIC_ORIGIN, and TS_DASHBOARD_STATE_URL, TS_DASHBOARD_STATE_API_KEY, TS_DASHBOARD_STATE_DATABASE.");
        }

        if (environment.IsProduction()
            && (!idpOrigin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !publicOrigin.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !stateUrl.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Live dashboard requires HTTPS identity-provider, public, and state origins in production.");
        }

        var issuer = new Uri($"{idpOrigin.ToString().TrimEnd('/')}/{instance}");
        var introspection = new Uri($"{idpOrigin.ToString().TrimEnd('/')}/{instance}/oauth/introspect");
        return new(false,
            new LiveIdentityOptions(idpOrigin, publicOrigin, instance, issuer, introspection, clientId, clientSecret, managementKey),
            new DashboardStateOptions(stateUrl, stateKey, stateDatabase));
    }

    private static bool HasLiveConfiguration(IConfiguration configuration) =>
        !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_IDP_ORIGIN"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_IDP_INSTANCE"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_IDP_CLIENT_ID"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_IDP_CLIENT_SECRET"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_IDP_MANAGEMENT_KEY"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_PUBLIC_ORIGIN"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_STATE_URL"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_STATE_API_KEY"])
        || !string.IsNullOrWhiteSpace(configuration["TS_DASHBOARD_STATE_DATABASE"]);

    /// <summary>HTTP origins are development-only and only for loopback hosts,
    /// mirroring the SDK's own loopback rule.</summary>
    private static Uri ParseOrigin(string? raw, bool allowLoopbackHttp, string name)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttps && !(allowLoopbackHttp && parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback)))
        {
            throw new InvalidOperationException($"{name} must be an absolute origin (HTTPS, or HTTP on loopback outside production).");
        }

        return new Uri(parsed.GetLeftPart(UriPartial.Authority));
    }
}

public sealed record LiveIdentityOptions(
    Uri Origin,
    Uri PublicOrigin,
    string InstanceSlug,
    Uri Issuer,
    Uri IntrospectionEndpoint,
    string ClientId,
    string ClientSecret,
    string ManagementApiKey);

public sealed record DashboardStateOptions(Uri ApiUrl, string ApiKey, string Database);
