namespace TerraScale.Auth.Configuration;

/// <summary>
/// Runtime configuration for ts-auth, loaded from <c>TS_AUTH_*</c> environment
/// variables and overridden by <c>--key=value</c> command-line arguments.
/// </summary>
public sealed record AuthOptions
{
    /// <summary>HTTP listen URL (ASP.NET format, e.g. <c>http://+:8080</c>). Env <c>TS_AUTH_LISTEN</c>.</summary>
    public required string Listen { get; init; }

    /// <summary>Public origin of this ts-auth deployment (used in emails, redirects, OIDC issuer). Env <c>TS_AUTH_PUBLIC_ORIGIN</c>.</summary>
    public required Uri PublicOrigin { get; init; }

    /// <summary>TerraScale API origin the SDK talks to. Env <c>TS_AUTH_TERRASCALE_API_URL</c>.</summary>
    public required Uri TerrascaleApiUrl { get; init; }

    /// <summary>Project API key for TerraScale. Env <c>TS_AUTH_TERRASCALE_API_KEY</c>.</summary>
    public required string TerrascaleApiKey { get; init; }

    /// <summary>TerraScale project id selector. Env <c>TS_AUTH_TERRASCALE_PROJECT_ID</c>.</summary>
    public string? TerrascaleProjectId { get; init; }

    /// <summary>TerraScale tenant id selector. Env <c>TS_AUTH_TERRASCALE_TENANT_ID</c>.</summary>
    public string? TerrascaleTenantId { get; init; }

    /// <summary>TerraBase database that backs ts-auth. Env <c>TS_AUTH_DATABASE</c>.</summary>
    public required string Database { get; init; }

    /// <summary>Master encryption key (base64, 32 bytes) protecting recoverable secrets (TOTP seeds) at rest. Env <c>TS_AUTH_MASTER_KEY</c>.</summary>
    public required byte[] MasterKey { get; init; }

    /// <summary>Operator key that can create instances. Env <c>TS_AUTH_OPERATOR_KEY</c>.</summary>
    public required string OperatorKey { get; init; }

    /// <summary>Management API requests allowed per API key per 5-minute window. Env <c>TS_AUTH_RATE_LIMIT</c>, default 1000.</summary>
    public int RateLimitPerWindow { get; init; } = 1000;

    /// <summary>Stripe secret key; billing routes 503 when unset. Env <c>TS_AUTH_STRIPE_SECRET_KEY</c>.</summary>
    public string? StripeSecretKey { get; init; }

    /// <summary>Stripe webhook signing secret. Env <c>TS_AUTH_STRIPE_WEBHOOK_SECRET</c>.</summary>
    public string? StripeWebhookSecret { get; init; }

    /// <summary>Stripe API origin (overridable for tests). Env <c>TS_AUTH_STRIPE_API_URL</c>.</summary>
    public Uri StripeApiUrl { get; init; } = new("https://api.stripe.com", UriKind.Absolute);

    /// <summary>Stripe price id for the pro plan. Env <c>TS_AUTH_PRICE_PRO</c>.</summary>
    public string? PricePro { get; init; }

    /// <summary>Stripe price id for the enterprise plan. Env <c>TS_AUTH_PRICE_ENTERPRISE</c>.</summary>
    public string? PriceEnterprise { get; init; }

    /// <summary>Load options from environment variables, then <c>--key=value</c> args.</summary>
    public static AuthOptions Load(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in EnvironmentVariables()) values[key] = value;
        foreach (var arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.AsSpan().IndexOf('=') is int eq && eq > 2)
                values[arg[2..eq]] = arg[(eq + 1)..];
        }

        string Required(string name)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"TS_AUTH_{name.ToUpperInvariant().Replace('-', '_')} is required");
            return value;
        }

        var listen = values.GetValueOrDefault("listen", "http://+:8080");
        if (!listen.StartsWith("http://", StringComparison.Ordinal) && !listen.StartsWith("https://", StringComparison.Ordinal))
            throw new InvalidOperationException("TS_AUTH_LISTEN must be an http(s) URL");
        var publicOrigin = Required("public-origin");
        var apiUrl = values.GetValueOrDefault("terrascale-api-url", "https://api.terrascale.tech");
        var masterKey = Required("master-key");

        byte[] masterKeyBytes;
        try
        {
            masterKeyBytes = Convert.FromBase64String(masterKey);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("TS_AUTH_MASTER_KEY must be base64", exception);
        }
        if (masterKeyBytes.Length is not (32 or 48 or 64))
            throw new InvalidOperationException("TS_AUTH_MASTER_KEY must decode to 32, 48, or 64 bytes");

        return new AuthOptions
        {
            Listen = listen,
            PublicOrigin = new Uri(publicOrigin, UriKind.Absolute),
            TerrascaleApiUrl = new Uri(apiUrl, UriKind.Absolute),
            TerrascaleApiKey = Required("terrascale-api-key"),
            TerrascaleProjectId = values.GetValueOrDefault("terrascale-project-id"),
            TerrascaleTenantId = values.GetValueOrDefault("terrascale-tenant-id"),
            Database = Required("database"),
            MasterKey = masterKeyBytes,
            OperatorKey = Required("operator-key"),
            RateLimitPerWindow = int.TryParse(values.GetValueOrDefault("rate-limit"), out var rateLimit) && rateLimit > 0 ? rateLimit : 1000,
            StripeSecretKey = values.GetValueOrDefault("stripe-secret-key"),
            StripeWebhookSecret = values.GetValueOrDefault("stripe-webhook-secret"),
            StripeApiUrl = string.IsNullOrWhiteSpace(values.GetValueOrDefault("stripe-api-url"))
                ? new Uri("https://api.stripe.com", UriKind.Absolute)
                : new Uri(values.GetValueOrDefault("stripe-api-url")!, UriKind.Absolute),
            PricePro = values.GetValueOrDefault("price-pro"),
            PriceEnterprise = values.GetValueOrDefault("price-enterprise"),
        };
    }

    private static IEnumerable<(string Key, string Value)> EnvironmentVariables()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("TS_AUTH_", StringComparison.Ordinal) && entry.Value is string value)
                yield return (key["TS_AUTH_".Length..].Replace('_', '-'), value);
        }
    }
}
