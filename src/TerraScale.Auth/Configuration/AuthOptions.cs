using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using TerraScale.Auth.Features.Oidc;
using TerraScale.DotNet;

namespace TerraScale.Auth.Configuration;

/// <summary>An invalid or incomplete <c>TS_AUTH_*</c> configuration. The process must not start.</summary>
public sealed class AuthConfigurationException(string message) : Exception(message);

/// <summary>
/// Validated process configuration.
/// Every setting is <c>TS_AUTH_&lt;NAME&gt;</c> or the equivalent <c>--name=value</c> argument.
/// </summary>
public sealed partial record AuthOptions
{
    /// <summary>The TerraScale API host every production replica stores through.</summary>
    public const string TerraScaleApiHost = "api.terrascale.tech";

    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        "listen",
        "public-origin",
        "terrascale-api-url",
        "terrascale-api-endpoints",
        "terrascale-api-ca",
        "terrascale-api-failover",
        "terrascale-api-key",
        "database",
        "master-key",
        "audit-signing-key",
        "audit-verify-keys",
        "operator-key",
        "rate-limit",
        "trusted-proxies",
        "smtp-host",
        "smtp-port",
        "smtp-username",
        "smtp-password",
        "smtp-from-address",
        "smtp-check-revocation",
        "sentry-dsn",
        "dashboard-client-secret",
        "dashboard-redirect-uris",
        "dashboard-post-logout-redirect-uris",
        "access-token-audiences",
        "service-clients",
        "invitation-url",
        "signing-key-vault",
        "signing-key-secret",
        "oci-region",
        "oci-tenancy",
        "oci-user",
        "oci-fingerprint",
        "oci-private-key",
        "oci-endpoint",
        "environment");

    /// <summary>Plain-HTTP listen address; TLS terminates in front of the service.</summary>
    public required IPEndPoint Listen { get; init; }

    /// <summary>The public origin browsers and clients use, for example <c>https://auth.terrascale.tech</c>.</summary>
    public required Uri PublicOrigin { get; init; }

    /// <summary>TerraBase API origin.</summary>
    public required Uri TerraScaleApiUrl { get; init; }

    /// <summary>
    /// Private TerraBase member origins that storage uses instead of <see cref="TerraScaleApiUrl"/>, most preferred first:
    /// every replica sends to the first ready one. Empty: storage uses <see cref="TerraScaleApiUrl"/>.
    /// </summary>
    public IReadOnlyList<Uri> TerraScaleApiEndpoints { get; init; } = [];

    /// <summary>The only certificates trusted as roots for <see cref="TerraScaleApiEndpoints"/>, for example the cell's TerraBase member CA.</summary>
    public IReadOnlyList<X509Certificate2> TerraScaleApiCa { get; init; } = [];

    /// <summary>
    /// Whether storage may move to the next of several <see cref="TerraScaleApiEndpoints"/> while the preferred member is
    /// down: a documented exception (<c>TS_AUTH_TERRASCALE_API_FAILOVER=true</c>) to the single storage authority, for
    /// members that replicate. Off, a deployment lists one member and fails closed while it is down.
    /// </summary>
    public bool TerraScaleApiFailover { get; init; }

    /// <summary>TerraBase API key, without any <c>Bearer</c> prefix.</summary>
    public required string TerraScaleApiKey { get; init; }

    /// <summary>TerraBase project/database selector holding all ts-auth state.</summary>
    public required string Database { get; init; }

    /// <summary>Project UUID selected by <c>TS_AUTH_DATABASE</c>.</summary>
    public string StorageProject => this.Database.Split('/')[0];

    /// <summary>Database name selected by <c>TS_AUTH_DATABASE</c>.</summary>
    public string StorageDatabase => this.Database.Split('/')[1];

    /// <summary>32-byte key that encrypts recoverable secrets at rest.</summary>
    public required byte[] MasterKey { get; init; }

    /// <summary>32-byte HMAC key sealing audit events; required outside Development.</summary>
    public byte[]? AuditSigningKey { get; init; }

    /// <summary>Up to five previous audit keys accepted for verification only.</summary>
    public IReadOnlyList<byte[]> AuditVerificationKeys { get; init; } = [];

    /// <summary>Bearer key for operator instance bootstrap.</summary>
    public required string OperatorKey { get; init; }

    /// <summary>Shared per-IP token and management request capacity per minute.</summary>
    public int RateLimitPerWindow { get; init; } = 1000;

    /// <summary>Exact socket peers allowed to supply one rightmost X-Forwarded-For hop. Empty disables forwarding.</summary>
    public IReadOnlyList<IPAddress> TrustedProxies { get; init; } = [];

    /// <summary>SMTP relay host; all four SMTP credentials are set together or not at all.</summary>
    public string? SmtpHost { get; init; }

    /// <summary>SMTP relay port.</summary>
    public int SmtpPort { get; init; } = 587;

    /// <summary>SMTP user name.</summary>
    public string? SmtpUsername { get; init; }

    /// <summary>SMTP password.</summary>
    public string? SmtpPassword { get; init; }

    /// <summary>Sender address for outgoing mail.</summary>
    public string? SmtpFromAddress { get; init; }

    /// <summary>
    /// Whether the relay certificate's revocation is checked online. A deployment without a route to its CA's OCSP and
    /// CRL servers sets <c>TS_AUTH_SMTP_CHECK_REVOCATION=false</c> as a documented exception; chain and host name are
    /// still validated.
    /// </summary>
    public bool SmtpCheckRevocation { get; init; } = true;

    /// <summary>GlitchTip/Sentry DSN. Error reporting is off when unset.</summary>
    public string? SentryDsn { get; init; }

    /// <summary>The ts-dashboard OpenID Connect client, registered at startup when configured.</summary>
    public DashboardClientOptions? DashboardClient { get; init; }

    /// <summary>Exact <c>aud</c> values every access token carries, for example TerraBase's Management API audience.</summary>
    public IReadOnlyList<string> AccessTokenAudiences { get; init; } = [];

    /// <summary>Confidential service clients allowed the client-credentials grant, registered at startup.</summary>
    public IReadOnlyList<ServiceClientOptions> ServiceClients { get; init; } = [];

    /// <summary>
    /// The dashboard page that accepts an invitation (TS-598); invitation emails link to it with <c>?token=</c>. Without
    /// it no invitation email is sent and callers deliver the token they are answered.
    /// </summary>
    public Uri? InvitationUrl { get; init; }

    /// <summary>The OCI Vault secret holding the signing key ring; required outside Development.</summary>
    public SigningKeyVaultOptions? SigningKeyVault { get; init; }

    /// <summary>Whether <c>TS_AUTH_ENVIRONMENT=Development</c> disables the production guards.</summary>
    public bool Development { get; init; }

    /// <summary>
    /// Describes the configuration without any credential: the generated record
    /// <c>ToString</c> would print keys and passwords into any log or exception that formats it.
    /// </summary>
    public override string ToString() =>
        $"AuthOptions {{ Listen = {this.Listen}, PublicOrigin = {this.PublicOrigin}, TerraScaleApiUrl = {this.TerraScaleApiUrl}, "
        + $"TerraScaleApiEndpoints = {this.TerraScaleApiEndpoints.Count}, "
        + $"Database = {this.Database}, Development = {this.Development}, Smtp = {(this.SmtpHost is null ? "off" : "on")}, "
        + $"ErrorReporting = {(this.SentryDsn is null ? "off" : "on")}, DashboardClient = {(this.DashboardClient is null ? "off" : "on")}, "
        + $"AccessTokenAudiences = {this.AccessTokenAudiences.Count}, ServiceClients = {this.ServiceClients.Count}, "
        + $"SigningKeyVault = {(this.SigningKeyVault is null ? "off" : "on")} }}";

    /// <summary>Reads <c>TS_AUTH_*</c> environment variables, then <c>--key=value</c> arguments, which win.</summary>
    public static AuthOptions FromProcess(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("TS_AUTH_", StringComparison.Ordinal) && entry.Value is string value)
            {
                values[key["TS_AUTH_".Length..].ToLowerInvariant().Replace('_', '-')] = value;
            }
        }

        foreach (var arg in args)
        {
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var separator = arg.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? null : arg[2..separator];
            if (key is null || !Keys.Contains(key))
            {
                // Never echo any part of the argument: even a plausible option name can be a
                // misplaced credential. Known names are listed in the documentation.
                throw new AuthConfigurationException(key is not null
                    ? "Unknown option (expected --key=value with a TS_AUTH_* setting name)"
                    : "Malformed option (expected --key=value)");
            }

            values[key] = arg[(separator + 1)..];
        }

        return Load(values);
    }

    /// <summary>Validates settings keyed by their kebab-case names (<c>public-origin</c>, …).</summary>
    public static AuthOptions Load(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        string? Optional(string key) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;
        string Required(string key) => Optional(key) ?? throw Invalid($"{EnvName(key)} is required");

        var development = string.Equals(Optional("environment"), "Development", StringComparison.OrdinalIgnoreCase);
        var publicOrigin = AbsoluteHttpUrl("public-origin", Required("public-origin"));
        // Routes are mapped at the root, so the public origin cannot carry a base path.
        if (publicOrigin.AbsolutePath != "/")
        {
            throw Invalid("TS_AUTH_PUBLIC_ORIGIN must be an origin without a path");
        }

        var apiUrl = AbsoluteHttpUrl("terrascale-api-url", Required("terrascale-api-url"));
        if (apiUrl.Scheme != Uri.UriSchemeHttps || apiUrl.AbsolutePath != "/")
        {
            throw Invalid("TS_AUTH_TERRASCALE_API_URL must be an HTTPS origin without a path");
        }

        var endpoints = ParseEndpoints(Optional("terrascale-api-endpoints"));
        var trusted = ParseCertificates(Optional("terrascale-api-ca"));
        if ((endpoints.Count == 0) != (trusted.Count == 0))
        {
            throw Invalid("TS_AUTH_TERRASCALE_API_ENDPOINTS and TS_AUTH_TERRASCALE_API_CA are required together");
        }

        var failover = Optional("terrascale-api-failover") switch
        {
            null or "false" => false,
            "true" => true,
            _ => throw Invalid("TS_AUTH_TERRASCALE_API_FAILOVER must be true or false"),
        };
        if (endpoints.Count > 1 && !failover)
        {
            throw Invalid("TS_AUTH_TERRASCALE_API_ENDPOINTS may list several members only with TS_AUTH_TERRASCALE_API_FAILOVER=true");
        }

        var database = Required("database");
        var selector = database.Split('/');
        if (selector.Length != 2 || !StorageProjectName().IsMatch(selector[0]) || !StorageName().IsMatch(selector[1]))
        {
            throw Invalid("TS_AUTH_DATABASE must be project-uuid/database with a lowercase database name");
        }

        var normalizedApiKey = NormalizeApiKey(Required("terrascale-api-key"));
        if (normalizedApiKey.Length == 0)
        {
            throw Invalid("TS_AUTH_TERRASCALE_API_KEY is required");
        }
        if (normalizedApiKey.Length > 4096 || normalizedApiKey.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
        {
            throw Invalid("TS_AUTH_TERRASCALE_API_KEY must contain at most 4096 characters without whitespace or controls");
        }

        var verifyValues = (Optional("audit-verify-keys") ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (verifyValues.Length > 5)
        {
            throw Invalid("TS_AUTH_AUDIT_VERIFY_KEYS may contain at most 5 previous keys");
        }

        string?[] smtp = [Optional("smtp-host"), Optional("smtp-username"), Optional("smtp-password"), Optional("smtp-from-address")];
        if (smtp.Any(part => part is not null) && smtp.Any(part => part is null))
        {
            throw Invalid("TS_AUTH_SMTP_* is partially configured");
        }

        var smtpPort = 587;
        if (Optional("smtp-port") is { } rawPort
            && (!int.TryParse(rawPort, NumberStyles.None, CultureInfo.InvariantCulture, out smtpPort) || smtpPort is < 1 or > 65535))
        {
            throw Invalid("TS_AUTH_SMTP_PORT must be between 1 and 65535");
        }

        var smtpCheckRevocation = Optional("smtp-check-revocation") switch
        {
            null or "true" => true,
            "false" => false,
            _ => throw Invalid("TS_AUTH_SMTP_CHECK_REVOCATION must be true or false"),
        };

        var rateLimit = 1000;
        if (Optional("rate-limit") is { } rawLimit
            && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out rateLimit) || rateLimit < 1))
        {
            throw Invalid("TS_AUTH_RATE_LIMIT must be positive");
        }

        var options = new AuthOptions
        {
            Listen = ParseListen(Optional("listen") ?? "http://0.0.0.0:8080"),
            PublicOrigin = publicOrigin,
            TerraScaleApiUrl = apiUrl,
            TerraScaleApiEndpoints = endpoints,
            TerraScaleApiCa = trusted,
            TerraScaleApiFailover = failover,
            TerraScaleApiKey = normalizedApiKey,
            Database = database,
            MasterKey = DecodeKey("master-key", Required("master-key")),
            AuditSigningKey = Optional("audit-signing-key") is { } signing ? DecodeKey("audit-signing-key", signing) : null,
            AuditVerificationKeys = [.. verifyValues.Select(raw => DecodeKey("audit-verify-keys", raw))],
            OperatorKey = Required("operator-key"),
            RateLimitPerWindow = rateLimit,
            TrustedProxies = ParseTrustedProxies(Optional("trusted-proxies")),
            SmtpHost = smtp[0],
            SmtpPort = smtpPort,
            SmtpCheckRevocation = smtpCheckRevocation,
            SmtpUsername = smtp[1],
            SmtpPassword = smtp[2],
            SmtpFromAddress = smtp[3],
            SentryDsn = Optional("sentry-dsn") is { } dsn ? ValidateDsn(dsn) : null,
            DashboardClient = ParseDashboardClient(Optional("dashboard-client-secret"), Optional("dashboard-redirect-uris"), Optional("dashboard-post-logout-redirect-uris")),
            AccessTokenAudiences = ParseAudiences(Optional("access-token-audiences")),
            ServiceClients = ParseServiceClients(Optional("service-clients")),
            InvitationUrl = Optional("invitation-url") is { } invitationUrl ? ParseInvitationUrl(invitationUrl, development) : null,
            SigningKeyVault = ParseSigningKeyVault(Optional, development),
            Development = development,
        };
        options.EnforceProductionGuards();
        return options;
    }

    private static IReadOnlyList<IPAddress> ParseTrustedProxies(string? value)
    {
        if (value is null)
        {
            return [];
        }

        var addresses = new HashSet<IPAddress>();
        foreach (var text in value.Split(';', StringSplitOptions.TrimEntries))
        {
            // IPAddress.TryParse accepts bracketed IPv6 literals; this setting requires bare numeric addresses.
            if (!IPAddress.TryParse(text, out var address) || text.Contains('%')
                || text.StartsWith('[')
                || (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && text != address.ToString())
                || (text.Contains('.') && text[(text.LastIndexOf(':') + 1)..] != address.MapToIPv4().ToString()))
            {
                throw Invalid("TS_AUTH_TRUSTED_PROXIES must contain unadorned numeric IP addresses");
            }

            address = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
            if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
            {
                throw Invalid("TS_AUTH_TRUSTED_PROXIES must not contain unspecified addresses");
            }

            if (!addresses.Add(address))
            {
                throw Invalid("TS_AUTH_TRUSTED_PROXIES must contain distinct normalized addresses");
            }

            if (addresses.Count > 8)
            {
                throw Invalid("TS_AUTH_TRUSTED_PROXIES must contain at most 8 addresses");
            }
        }

        return [.. addresses];
    }

    private void EnforceProductionGuards()
    {
        if (!this.Development && this.AuditSigningKey is null)
        {
            throw Invalid("TS_AUTH_AUDIT_SIGNING_KEY is required outside Development");
        }

        // Clients, issuers and the Secure session cookie all depend on the public origin
        // being HTTPS; plain HTTP is only for local Development.
        if (!this.Development && this.PublicOrigin.Scheme != Uri.UriSchemeHttps)
        {
            throw Invalid("TS_AUTH_PUBLIC_ORIGIN must use HTTPS outside Development");
        }

        var loopback = this.TerraScaleApiUrl.IsLoopback;
        if (!this.Development && loopback)
        {
            throw Invalid("Production auth storage cannot use a loopback TerraScale API URL");
        }

        if (!loopback && this.TerraScaleApiUrl.Host != TerraScaleApiHost
            && !this.TerraScaleApiUrl.Host.EndsWith(".api.terrascale.tech", StringComparison.Ordinal))
        {
            throw Invalid("Global auth storage requires a TerraScale HTTPS API origin");
        }

        // The management API accepts tokens whose audience is the platform issuer (TS-598); organization and project tokens
        // carry only these audiences, so none may be that issuer. Other paths of this origin grant nothing there.
        var issuer = new Uri(this.PublicOrigin, Features.Oidc.OidcIssuer.Slug);
        if (this.AccessTokenAudiences.Any(audience => new Uri(audience) == issuer))
        {
            throw Invalid("TS_AUTH_ACCESS_TOKEN_AUDIENCES must not name this service's platform issuer");
        }

        if (!this.Development && this.SigningKeyVault is null)
        {
            throw Invalid("TS_AUTH_SIGNING_KEY_VAULT and its TS_AUTH_OCI_* credential are required outside Development");
        }
    }

    private static Uri AbsoluteHttpUrl(string key, string raw)
    {
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url)
            || url.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(url.Host)
            || !string.IsNullOrEmpty(url.UserInfo)
            || !string.IsNullOrEmpty(url.Query)
            || !string.IsNullOrEmpty(url.Fragment))
        {
            throw Invalid($"{EnvName(key)} must be an absolute HTTP(S) URL");
        }

        return url;
    }

    // scheme://public-key[:secret]@host[:port]/[path/]project-id with a non-empty public key;
    // the message never repeats the value.
    private static string ValidateDsn(string raw) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var dsn)
            && dsn.Scheme is "http" or "https"
            && dsn.UserInfo.Split(':')[0] is { Length: > 0 }
            && !string.IsNullOrEmpty(dsn.Host)
            && string.IsNullOrEmpty(dsn.Query)
            && string.IsNullOrEmpty(dsn.Fragment)
            && dsn.AbsolutePath.TrimEnd('/').Split('/')[^1] is { Length: > 0 } project
            && project.All(char.IsAsciiDigit)
            ? raw
            : throw Invalid("TS_AUTH_SENTRY_DSN must be a valid DSN (scheme://key@host/project)");

    private static DashboardClientOptions? ParseDashboardClient(string? secret, string? redirects, string? postLogoutRedirects)
    {
        if (secret is null && redirects is null && postLogoutRedirects is null)
        {
            return null;
        }

        if (secret is null || redirects is null)
        {
            throw Invalid("TS_AUTH_DASHBOARD_CLIENT_SECRET and TS_AUTH_DASHBOARD_REDIRECT_URIS are required together");
        }

        if (!IsClientSecret(secret))
        {
            throw Invalid("TS_AUTH_DASHBOARD_CLIENT_SECRET must contain 32 to 512 characters without whitespace or controls");
        }

        var redirectUris = RedirectUris("dashboard-redirect-uris", redirects);
        return redirectUris.Count > 0
            ? new DashboardClientOptions(secret, redirectUris, RedirectUris("dashboard-post-logout-redirect-uris", postLogoutRedirects))
            : throw Invalid("TS_AUTH_DASHBOARD_REDIRECT_URIS must hold at least one URI");
    }

    private static bool IsClientSecret(string secret) =>
        secret.Length is >= 32 and <= 512 && !secret.Any(character => char.IsWhiteSpace(character) || char.IsControl(character) || character == ';');

    // Exact strings, compared byte for byte by resource servers: never normalized (Uri would add a trailing slash).
    // An exact HTTPS page (HTTP only for loopback in Development) without credentials, query or fragment: ?token= follows.
    private static Uri ParseInvitationUrl(string raw, bool development) =>
        Uri.TryCreate(raw, UriKind.Absolute, out var url) && string.IsNullOrEmpty(url.UserInfo) && string.IsNullOrEmpty(url.Query) && string.IsNullOrEmpty(url.Fragment)
            && (url.Scheme == Uri.UriSchemeHttps || development && url.Scheme == Uri.UriSchemeHttp && url.IsLoopback)
            ? url
            : throw Invalid("TS_AUTH_INVITATION_URL must be an absolute HTTPS URL without credentials, query or fragment");

    private static string[] ParseAudiences(string? raw)
    {
        var values = (raw ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return values.Length <= 10 && values.All(value => value.Length <= 256 && Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query)
                && value.All(character => !char.IsWhiteSpace(character) && !char.IsControl(character) && character is not ('#' or '?')))
            ? values
            : throw Invalid("TS_AUTH_ACCESS_TOKEN_AUDIENCES must hold at most 10 absolute HTTPS URIs without credentials, query or fragment");
    }

    // id:secret pairs, semicolon-separated. Ids are canonical ULIDs. A client-credentials token is distinguished by its grant, audience and token bindings.
    private static List<ServiceClientOptions> ParseServiceClients(string? raw)
    {
        var clients = new List<ServiceClientOptions>();
        foreach (var entry in (raw ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = entry.IndexOf(':', StringComparison.Ordinal);
            var id = separator < 0 ? string.Empty : entry[..separator];
            var secret = separator < 0 ? string.Empty : entry[(separator + 1)..];
            if (!TerraScaleIds.IsValid(id) || id == OidcIssuer.DashboardClientId || !IsClientSecret(secret)
                || clients.Any(client => client.ClientId == id))
            {
                throw Invalid("TS_AUTH_SERVICE_CLIENTS must hold distinct id:secret pairs: canonical ULIDs other than the dashboard client id, secrets of 32 to 512 characters");
            }

            clients.Add(new ServiceClientOptions(id, secret));
        }

        return clients.Count <= 20 ? clients : throw Invalid("TS_AUTH_SERVICE_CLIENTS may hold at most 20 clients");
    }

    // Exact URIs, semicolon-separated: HTTPS, or plain HTTP only to localhost or a loopback IP; never credentials or a fragment.
    private static IReadOnlyList<Uri> RedirectUris(string key, string? raw)
    {
        var values = (raw ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length > 10)
        {
            throw Invalid($"{EnvName(key)} may contain at most 10 URIs");
        }

        return [.. values.Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "localhost"))
            && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment) && !value.Contains('#', StringComparison.Ordinal)
                ? uri
                : throw Invalid($"{EnvName(key)} must hold absolute HTTPS URIs (HTTP only for loopback) without credentials or fragments"))];
    }

    // Distinct HTTPS origins, semicolon-separated, in order of preference.
    private static List<Uri> ParseEndpoints(string? raw)
    {
        var endpoints = new List<Uri>();
        foreach (var value in (raw ?? string.Empty).Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.AbsolutePath != "/"
                || !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment)
                || endpoints.Contains(url) || endpoints.Count == 8)
            {
                throw Invalid("TS_AUTH_TERRASCALE_API_ENDPOINTS must hold at most 8 distinct HTTPS origins without a path");
            }

            endpoints.Add(url);
        }

        return endpoints;
    }

    // PEM certificates only: a private key or any other block is refused, never silently dropped. On a refusal the
    // certificates already loaded are disposed; otherwise the options own them for the process lifetime.
    private static List<X509Certificate2> ParseCertificates(string? raw)
    {
        var certificates = new List<X509Certificate2>();
        try
        {
            var rest = (raw ?? string.Empty).AsSpan();
            while (PemEncoding.TryFind(rest, out var fields))
            {
                if (rest[fields.Label] is not "CERTIFICATE" || rest[..fields.Location.Start].Trim().Length != 0 || certificates.Count == 4)
                {
                    throw InvalidCertificates();
                }

                try
                {
                    certificates.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(rest[fields.Base64Data].ToString())));
                }
                catch (CryptographicException)
                {
                    throw InvalidCertificates();
                }

                rest = rest[fields.Location.End..];
            }

            return rest.Trim().Length == 0 ? certificates : throw InvalidCertificates();
        }
        catch (AuthConfigurationException)
        {
            certificates.ForEach(certificate => certificate.Dispose());
            throw;
        }
    }

    private static AuthConfigurationException InvalidCertificates() =>
        Invalid("TS_AUTH_TERRASCALE_API_CA must hold 1 to 4 PEM certificates and nothing else");

    private static byte[] DecodeKey(string key, string raw)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(raw);
        }
        catch (FormatException)
        {
            throw Invalid($"{EnvName(key)} must be base64");
        }

        return bytes.Length == 32 ? bytes : throw Invalid($"{EnvName(key)} must decode to 32 bytes");
    }

    private static IPEndPoint ParseListen(string raw)
    {
        if (!raw.StartsWith("http://", StringComparison.Ordinal))
        {
            throw Invalid("TS_AUTH_LISTEN must be a plain HTTP listen URL");
        }

        var authority = raw["http://".Length..];
        if (authority.IndexOfAny(['/', '?', '#', '@']) >= 0)
        {
            throw Invalid("TS_AUTH_LISTEN must contain only an address and port");
        }

        // An address (IPv4, [IPv6], localhost, + or *) and an explicit port; port 0 asks for an ephemeral one.
        var match = ListenAuthority().Match(authority);
        if (!match.Success
            || !int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port > IPEndPoint.MaxPort)
        {
            throw Invalid("TS_AUTH_LISTEN must use a host and port");
        }

        var host = match.Groups["host"].Value;
        var address = host switch
        {
            "+" or "*" => IPAddress.Any,
            _ when host.Equals("localhost", StringComparison.OrdinalIgnoreCase) => IPAddress.Loopback,
            _ when host.StartsWith('[') => IPAddress.TryParse(host[1..^1], out var v6) && v6.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? v6 : null,
            _ => IPAddress.TryParse(host, out var v4) && v4.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? v4 : null,
        };
        return address is null
            ? throw Invalid("TS_AUTH_LISTEN must use a host and port")
            : new IPEndPoint(address, port);
    }

    [GeneratedRegex(@"\A(?<host>\[[^\]]+\]|[^:\[\]]+):(?<port>[0-9]{1,5})\z")]
    private static partial Regex ListenAuthority();

    [GeneratedRegex(@"\A[a-z][a-z0-9_-]{0,62}\z")]
    private static partial Regex StorageName();

    [GeneratedRegex(@"\A[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\z")]
    private static partial Regex StorageProjectName();

    private static string NormalizeApiKey(string raw)
    {
        raw = raw.Trim();
        if (raw.Equals("bearer", StringComparison.OrdinalIgnoreCase))
        {
            // Only the scheme was supplied: there is no credential.
            return string.Empty;
        }

        return raw.Length > 6 && raw.StartsWith("bearer", StringComparison.OrdinalIgnoreCase) && char.IsWhiteSpace(raw[6])
            ? raw[6..].Trim()
            : raw;
    }

    // All seven settings or none; the endpoint override is for local stand-ins of the OCI APIs.
    private static SigningKeyVaultOptions? ParseSigningKeyVault(Func<string, string?> optional, bool development)
    {
        string?[] parts = [optional("signing-key-vault"), optional("signing-key-secret"), optional("oci-region"), optional("oci-tenancy"),
            optional("oci-user"), optional("oci-fingerprint"), optional("oci-private-key")];
        if (parts.All(part => part is null) && optional("oci-endpoint") is null)
        {
            return null;
        }

        if (parts.Any(part => part is null))
        {
            throw Invalid("TS_AUTH_SIGNING_KEY_VAULT, TS_AUTH_SIGNING_KEY_SECRET and TS_AUTH_OCI_* are partially configured");
        }

        var (vault, secret, region, tenancy, user, fingerprint, pem) = (parts[0]!, parts[1]!, parts[2]!, parts[3]!, parts[4]!, parts[5]!, parts[6]!);
        if (!OciRegion().IsMatch(region))
        {
            throw Invalid("TS_AUTH_OCI_REGION must be an OCI region such as sa-saopaulo-1");
        }

        // The Secrets and Vault endpoints are derived for the commercial realm only (oci.oraclecloud.com).
        if (!VaultOcid().IsMatch(vault) || vault.Split('.')[2] != "oc1")
        {
            throw Invalid("TS_AUTH_SIGNING_KEY_VAULT must be a vault OCID in the commercial OCI realm (oc1)");
        }

        // Region-local: the vault OCID names its region, which must be the configured one.
        if (vault.Split('.')[3] != region)
        {
            throw Invalid("TS_AUTH_SIGNING_KEY_VAULT must be a vault OCID in TS_AUTH_OCI_REGION");
        }

        if (!SecretName().IsMatch(secret))
        {
            throw Invalid("TS_AUTH_SIGNING_KEY_SECRET must be a lowercase hyphenated secret name");
        }

        if (!TenancyOcid().IsMatch(tenancy) || !UserOcid().IsMatch(user) || !Fingerprint().IsMatch(fingerprint))
        {
            throw Invalid("TS_AUTH_OCI_TENANCY, TS_AUTH_OCI_USER and TS_AUTH_OCI_FINGERPRINT must be OCI identifiers and an API key fingerprint");
        }

        try
        {
            using var key = RSA.Create();
            key.ImportFromPem(pem);
            // A public key imports too; only a private key can sign.
            _ = key.ExportParameters(includePrivateParameters: true);
        }
        catch (Exception error) when (error is ArgumentException or CryptographicException)
        {
            throw Invalid("TS_AUTH_OCI_PRIVATE_KEY must be an RSA private key in PEM");
        }

        Uri? endpoint = null;
        if (optional("oci-endpoint") is { } rawEndpoint)
        {
            // Only a local stand-in of the OCI APIs: a remote override would receive signed requests and the ring.
            endpoint = AbsoluteHttpUrl("oci-endpoint", rawEndpoint);
            if (endpoint.AbsolutePath != "/" || !endpoint.IsLoopback || (endpoint.Scheme != Uri.UriSchemeHttps && !development))
            {
                throw Invalid("TS_AUTH_OCI_ENDPOINT must be a loopback origin, HTTPS outside Development");
            }
        }

        return new SigningKeyVaultOptions(vault, secret, region, tenancy, user, fingerprint, pem, endpoint);
    }

    // ocid1.<type>.<realm>.[region][.future use].<unique id>: a vault names its region; tenancies and users name none.
    [GeneratedRegex(@"\Aocid1\.vault\.oc[0-9]+\.[a-z0-9-]+(\.[a-z0-9]*)?\.[a-z0-9]+\z")]
    private static partial Regex VaultOcid();

    [GeneratedRegex(@"\Aocid1\.tenancy\.oc[0-9]+\.(\.[a-z0-9]*)?\.[a-z0-9]+\z")]
    private static partial Regex TenancyOcid();

    [GeneratedRegex(@"\Aocid1\.user\.oc[0-9]+\.(\.[a-z0-9]*)?\.[a-z0-9]+\z")]
    private static partial Regex UserOcid();

    [GeneratedRegex(@"\A[a-z]+(-[a-z]+)+-[0-9]+\z")]
    private static partial Regex OciRegion();

    [GeneratedRegex(@"\A[a-z0-9]+(-[a-z0-9]+)*\z")]
    private static partial Regex SecretName();

    [GeneratedRegex(@"\A[0-9a-f]{2}(:[0-9a-f]{2}){15}\z")]
    private static partial Regex Fingerprint();

    private static string EnvName(string key) => $"TS_AUTH_{key.Replace('-', '_').ToUpperInvariant()}";

    private static AuthConfigurationException Invalid(string message) => new(message);
}

/// <summary>The ts-dashboard confidential client (<c>TS_AUTH_DASHBOARD_*</c>).</summary>
/// <param name="Secret">Client secret; only its PBKDF2 hash is stored.</param>
/// <param name="RedirectUris">Exact authorization callback URIs.</param>
/// <param name="PostLogoutRedirectUris">Exact post-logout return URIs; may be empty.</param>
public sealed record DashboardClientOptions(string Secret, IReadOnlyList<Uri> RedirectUris, IReadOnlyList<Uri> PostLogoutRedirectUris)
{
    /// <summary>Omits the secret, so formatting or logging the record never discloses it.</summary>
    public override string ToString() => nameof(DashboardClientOptions);
}

/// <summary>A confidential service client allowed only the client-credentials grant (<c>TS_AUTH_SERVICE_CLIENTS</c>).</summary>
/// <param name="ClientId">The client id; also the <c>sub</c> of its tokens.</param>
/// <param name="Secret">Client secret; only its PBKDF2 hash is stored.</param>
public sealed record ServiceClientOptions(string ClientId, string Secret)
{
    /// <summary>Omits the secret, so formatting or logging the record never discloses it.</summary>
    public override string ToString() => $"{nameof(ServiceClientOptions)} {{ ClientId = {this.ClientId} }}";
}

/// <summary>The OCI Vault secret holding the signing key ring, and the API key that reads and versions it.</summary>
/// <param name="VaultId">The region-local vault OCID.</param>
/// <param name="SecretName">The ring secret's name, for example <c>gru-ts-auth-signing-key-ring</c>.</param>
/// <param name="Region">The OCI region, for example <c>sa-saopaulo-1</c>.</param>
/// <param name="TenancyId">The tenancy OCID.</param>
/// <param name="UserId">The OCI user OCID owning the API key.</param>
/// <param name="Fingerprint">The API key fingerprint.</param>
/// <param name="PrivateKeyPem">The API private key in PEM.</param>
/// <param name="Endpoint">Overrides both OCI API origins; for local test doubles only.</param>
public sealed record SigningKeyVaultOptions(string VaultId, string SecretName, string Region, string TenancyId, string UserId, string Fingerprint, string PrivateKeyPem, Uri? Endpoint)
{
    /// <summary>Omits the private key, so formatting or logging the record never discloses it.</summary>
    public override string ToString() => $"{nameof(SigningKeyVaultOptions)} {{ VaultId = {this.VaultId}, SecretName = {this.SecretName}, Region = {this.Region} }}";
}
