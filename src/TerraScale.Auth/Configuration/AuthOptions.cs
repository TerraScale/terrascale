namespace TerraScale.Auth.Configuration;

/// <summary>
/// Runtime configuration for ts-auth, loaded from <c>TS_AUTH_*</c> environment
/// variables and overridden by <c>--key=value</c> command-line arguments.
/// </summary>
public sealed record AuthOptions
{
    private const int MaximumAuditVerificationKeys = 5;
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

    /// <summary>Dedicated HMAC key (base64, 32 bytes) sealing audit events. Env <c>TS_AUTH_AUDIT_SIGNING_KEY</c>.</summary>
    public byte[]? AuditSigningKey { get; init; }

    /// <summary>Previous HMAC keys retained during rotation. Env <c>TS_AUTH_AUDIT_VERIFY_KEYS</c>, semicolon-separated base64 keys.</summary>
    public IReadOnlyList<byte[]> AuditVerificationKeys { get; init; } = [];

    /// <summary>Operator key that can create instances. Env <c>TS_AUTH_OPERATOR_KEY</c>.</summary>
    public required string OperatorKey { get; init; }

    /// <summary>Dedicated private metrics scrape key. Env <c>TS_AUTH_METRICS_KEY</c>. Metrics are disabled when it is absent.</summary>
    public string? MetricsKey { get; init; }

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

    /// <summary>SMTP host (OCI Email Delivery). When any SMTP setting is present, all four are required and email is sent over SMTP instead of logged. Env <c>TS_AUTH_SMTP_HOST</c>.</summary>
    public string? SmtpHost { get; init; }

    /// <summary>SMTP port. Env <c>TS_AUTH_SMTP_PORT</c>, default 587 (STARTTLS).</summary>
    public int SmtpPort { get; init; } = 587;

    /// <summary>SMTP username — the OCI SMTP credential OCID, full string including the <c>@oci</c> suffix. Env <c>TS_AUTH_SMTP_USERNAME</c>.</summary>
    public string? SmtpUsername { get; init; }

    /// <summary>SMTP credential password. Env <c>TS_AUTH_SMTP_PASSWORD</c>.</summary>
    public string? SmtpPassword { get; init; }

    /// <summary>Envelope-from address; must be an approved sender (OCI: <c>noreply@terrascale.tech</c>). Env <c>TS_AUTH_SMTP_FROM_ADDRESS</c>.</summary>
    public string? SmtpFromAddress { get; init; }

    /// <summary>Load options from environment variables, then <c>--key=value</c> args.</summary>
    public static AuthOptions Load(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in EnvironmentVariables()) values[key] = value;

        // Unknown or malformed options must fail fast: silently ignoring them lets a
        // typo'd arg (or the unsupported `--key value` form) lose to the environment
        // without the operator ever noticing.
        string[] knownKeys =
        [
            "listen", "public-origin", "terrascale-api-url", "terrascale-api-key",
            "terrascale-project-id", "terrascale-tenant-id", "database", "master-key",
            "audit-signing-key", "audit-verify-keys", "operator-key", "metrics-key", "rate-limit", "stripe-secret-key", "stripe-webhook-secret",
            "stripe-api-url", "price-pro", "price-enterprise",
            "smtp-host", "smtp-port", "smtp-username", "smtp-password", "smtp-from-address",
        ];
        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--")
                continue;
            if (arg.AsSpan().IndexOf('=') is int eq && eq > 2 && knownKeys.Contains(arg[2..eq], StringComparer.OrdinalIgnoreCase))
                values[arg[2..eq]] = arg[(eq + 1)..];
            else
                throw new InvalidOperationException($"Unknown or malformed option '{arg}' (expected --key=value)");
        }

        string Required(string name)
        {
            if (!values.TryGetValue(name, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException($"TS_AUTH_{name.ToUpperInvariant().Replace('-', '_')} is required");
            return value;
        }

        string? Optional(string name) =>
            values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        Uri HttpUrl(string name, string value)
        {
            try
            {
                var uri = new Uri(value, UriKind.Absolute);
                if (uri.Scheme is not ("http" or "https"))
                    throw new InvalidOperationException($"TS_AUTH_{name} must be an absolute http(s) URL");
                return uri;
            }
            catch (UriFormatException exception)
            {
                throw new InvalidOperationException($"TS_AUTH_{name} must be an absolute http(s) URL", exception);
            }
        }

        var listen = values.GetValueOrDefault("listen", "http://+:8080");
        if (!listen.StartsWith("http://", StringComparison.Ordinal) && !listen.StartsWith("https://", StringComparison.Ordinal))
            throw new InvalidOperationException("TS_AUTH_LISTEN must be an http(s) URL");
        var publicOrigin = Required("public-origin");
        var apiUrl = Optional("terrascale-api-url") ?? "https://api.terrascale.tech";
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
        // SecretBox feeds this straight to AES-GCM, which only accepts 128/192/256-bit
        // keys — so anything but 32 bytes must fail here, not at first use.
        if (masterKeyBytes.Length != 32)
            throw new InvalidOperationException("TS_AUTH_MASTER_KEY must decode to 32 bytes");

        byte[]? auditSigningKey = null;
        var auditSigningKeyRaw = Optional("audit-signing-key");
        if (auditSigningKeyRaw is not null)
        {
            try
            {
                auditSigningKey = Convert.FromBase64String(auditSigningKeyRaw);
            }
            catch (FormatException exception)
            {
                throw new InvalidOperationException("TS_AUTH_AUDIT_SIGNING_KEY must be base64", exception);
            }
            if (auditSigningKey.Length != 32)
                throw new InvalidOperationException("TS_AUTH_AUDIT_SIGNING_KEY must decode to 32 bytes");
        }

        var verificationKeys = new List<byte[]>();
        if (Optional("audit-verify-keys") is { } verificationRaw)
        {
            var encodedKeys = verificationRaw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (encodedKeys.Length > MaximumAuditVerificationKeys)
                throw new InvalidOperationException($"TS_AUTH_AUDIT_VERIFY_KEYS may contain at most {MaximumAuditVerificationKeys} previous keys");
            foreach (var encoded in encodedKeys)
            {
                byte[] decoded;
                try
                {
                    decoded = Convert.FromBase64String(encoded);
                }
                catch (FormatException exception)
                {
                    throw new InvalidOperationException("TS_AUTH_AUDIT_VERIFY_KEYS must contain semicolon-separated base64 keys", exception);
                }
                if (decoded.Length != 32)
                    throw new InvalidOperationException("Each TS_AUTH_AUDIT_VERIFY_KEYS entry must decode to 32 bytes");
                verificationKeys.Add(decoded);
            }
        }

        int rateLimit = 1000;
        if (values.TryGetValue("rate-limit", out var rateLimitRaw) && !string.IsNullOrWhiteSpace(rateLimitRaw)
            && (!int.TryParse(rateLimitRaw, out rateLimit) || rateLimit <= 0))
            throw new InvalidOperationException("TS_AUTH_RATE_LIMIT must be a positive integer");

        // SMTP is all-or-nothing: a half-configured sender would fall back to logging
        // (or fail at the first email) without the operator noticing — name what's missing.
        var smtpParts = new (string Name, string? Value)[]
        {
            ("smtp-host", Optional("smtp-host")),
            ("smtp-username", Optional("smtp-username")),
            ("smtp-password", Optional("smtp-password")),
            ("smtp-from-address", Optional("smtp-from-address")),
        };
        string? smtpHost = null, smtpUsername = null, smtpPassword = null, smtpFromAddress = null;
        var smtpPort = 587;
        if (smtpParts.Any(p => p.Value is not null))
        {
            var missing = smtpParts.Where(p => p.Value is null)
                .Select(p => "TS_AUTH_" + p.Name.Replace('-', '_').ToUpperInvariant()).ToArray();
            if (missing.Length > 0)
                throw new InvalidOperationException($"TS_AUTH_SMTP_* is partially configured; missing {string.Join(", ", missing)}");
            smtpHost = smtpParts[0].Value!;
            smtpUsername = smtpParts[1].Value!;
            smtpPassword = smtpParts[2].Value!;
            smtpFromAddress = smtpParts[3].Value!;
            if (values.TryGetValue("smtp-port", out var smtpPortRaw) && !string.IsNullOrWhiteSpace(smtpPortRaw)
                && (!int.TryParse(smtpPortRaw, out smtpPort) || smtpPort is < 1 or > 65535))
                throw new InvalidOperationException("TS_AUTH_SMTP_PORT must be an integer between 1 and 65535");
        }

        return new AuthOptions
        {
            Listen = listen,
            PublicOrigin = HttpUrl("PUBLIC_ORIGIN", publicOrigin),
            TerrascaleApiUrl = HttpUrl("TERRASCALE_API_URL", apiUrl),
            TerrascaleApiKey = Required("terrascale-api-key"),
            TerrascaleProjectId = Optional("terrascale-project-id"),
            TerrascaleTenantId = Optional("terrascale-tenant-id"),
            Database = Required("database"),
            MasterKey = masterKeyBytes,
            AuditSigningKey = auditSigningKey,
            AuditVerificationKeys = verificationKeys,
            OperatorKey = Required("operator-key"),
            MetricsKey = Optional("metrics-key"),
            RateLimitPerWindow = rateLimit,
            StripeSecretKey = Optional("stripe-secret-key"),
            StripeWebhookSecret = Optional("stripe-webhook-secret"),
            StripeApiUrl = HttpUrl("STRIPE_API_URL", Optional("stripe-api-url") ?? "https://api.stripe.com"),
            PricePro = Optional("price-pro"),
            PriceEnterprise = Optional("price-enterprise"),
            SmtpHost = smtpHost,
            SmtpPort = smtpPort,
            SmtpUsername = smtpUsername,
            SmtpPassword = smtpPassword,
            SmtpFromAddress = smtpFromAddress,
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
