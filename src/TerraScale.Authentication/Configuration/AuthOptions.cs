using System.Collections.Frozen;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.DotNet;

namespace TerraScale.Authentication.Configuration;

/// <summary>An invalid or incomplete <c>TS_AUTH_*</c> configuration. The process must not start.</summary>
public sealed class AuthConfigurationException(string message) : Exception(message);

/// <summary>
/// Validated process configuration.
/// Every setting is <c>TS_AUTH_&lt;NAME&gt;</c> or the equivalent <c>--name=value</c> argument.
/// </summary>
public sealed partial record AuthOptions
{
    private static readonly FrozenSet<string> Keys = FrozenSet.Create(
        StringComparer.Ordinal,
        "listen",
        "metrics-listen",
        "public-origin",
        "master-key",
        "audit-signing-key",
        "audit-verify-keys",
        "rate-limit",
        "trusted-proxies",
        "sentry-dsn",
        "dashboard-client-secret",
        "dashboard-redirect-uris",
        "dashboard-post-logout-redirect-uris",
        "access-token-audiences",
        "service-clients",
        "github-oauth",
        "google-oauth",
        "terrabase-url",
        "environment");

    /// <summary>Plain-HTTP listen address of the public API (8090, behind the ts-orch TLS edge). TerraBase uses port 8080 on the shared host network.</summary>
    public required IPEndPoint Listen { get; init; }

    /// <summary>
    /// Plain-HTTP listen address that serves only Prometheus <c>/metrics</c> (9092; TerraBase has 9091).
    /// </summary>
    public required IPEndPoint MetricsListen { get; init; }

    /// <summary>The global origin of every issuer, <c>https://{origin}/{tenant}</c>, served behind the ts-orch TLS edge.</summary>
    public const string GlobalOrigin = "https://auth.terrascale.tech";

    /// <summary>The public origin browsers and clients use: <see cref="GlobalOrigin"/> unless a preview or test sets another.</summary>
    public required Uri PublicOrigin { get; init; }

    /// <summary>32-byte key that encrypts recoverable secrets at rest.</summary>
    public required byte[] MasterKey { get; init; }

    /// <summary>32-byte HMAC key sealing audit events; required outside Development.</summary>
    public byte[]? AuditSigningKey { get; init; }

    /// <summary>Up to five previous audit keys accepted for verification only.</summary>
    public IReadOnlyList<byte[]> AuditVerificationKeys { get; init; } = [];

    /// <summary>Shared per-IP token and management request capacity per minute.</summary>
    public int RateLimitPerWindow { get; init; } = 1000;

    /// <summary>Exact socket peers allowed to supply one rightmost X-Forwarded-For hop. Empty disables forwarding.</summary>
    public IReadOnlyList<IPAddress> TrustedProxies { get; init; } = [];

    /// <summary>Cloudflare Email Sending account and token; without them email is logged, not delivered.</summary>
    public CloudflareEmailOptions? CloudflareEmail { get; init; }

    /// <summary>Platform OAuth clients, supplied as JSON credentials from OCI Vault; absent credentials hide the button.</summary>
    public IReadOnlyList<Features.ProjectTenants.SocialProviderRecord> SocialClients { get; init; } = [];

    /// <summary>GlitchTip/Sentry DSN. Error reporting is off when unset.</summary>
    public string? SentryDsn { get; init; }

    /// <summary>The ts-dashboard OpenID Connect client, registered at startup when configured.</summary>
    public DashboardClientOptions? DashboardClient { get; init; }

    /// <summary>
    /// TerraBase's audience: every access token carries it, in every region, so TerraBase trusts exactly one
    /// issuer/audience pair (<c>https://auth.terrascale.tech/terrascale</c>, this). Code, not configuration.
    /// </summary>
    public const string TerraBaseAudience = "https://api.terrascale.tech";

    /// <summary>
    /// Exact <c>aud</c> values every access token carries: <see cref="TerraBaseAudience"/> first, then any
    /// <c>TS_AUTH_ACCESS_TOKEN_AUDIENCES</c> (tests and previews name their own TerraBase).
    /// </summary>
    public IReadOnlyList<string> AccessTokenAudiences { get; init; } = [TerraBaseAudience];

    /// <summary>Confidential service clients allowed the client-credentials grant, registered at startup.</summary>
    public IReadOnlyList<ServiceClientOptions> ServiceClients { get; init; } = [];

    /// <summary>
    /// Origin of the TerraBase internal listener that holds ts-auth's storage (<c>TS_AUTH_TERRABASE_URL</c>, for example
    /// <c>http://127.0.0.1:8081</c>). Required outside Development; Development defaults to loopback.
    /// </summary>
    public Uri? TerraBaseUrl { get; init; }

    /// <summary>Whether <c>TS_AUTH_ENVIRONMENT=Development</c> disables the production guards.</summary>
    public bool Development { get; init; }

    /// <summary>
    /// Describes the configuration without any credential: the generated record
    /// <c>ToString</c> would print keys and passwords into any log or exception that formats it.
    /// </summary>
    public override string ToString() =>
        $"AuthOptions {{ Listen = {this.Listen}, MetricsListen = {this.MetricsListen}, PublicOrigin = {this.PublicOrigin}, "
        + $"Development = {this.Development}, Email = {(this.CloudflareEmail is null ? "off" : "on")}, "
        + $"ErrorReporting = {(this.SentryDsn is null ? "off" : "on")}, DashboardClient = {(this.DashboardClient is null ? "off" : "on")}, "
        + $"AccessTokenAudiences = {this.AccessTokenAudiences.Count}, ServiceClients = {this.ServiceClients.Count} }}";

    /// <summary>
    /// Replaces every secret variable (<c>TS_AUTH_*</c>, <c>CLOUDFLARE_*</c>, <c>TERRABASE_PEER_SECRET</c>) that has a
    /// <c>&lt;NAME&gt;_FILE</c> companion with that file's contents, trailing newline trimmed: a host that mounts secrets as
    /// files (OCI Vault on gru) delivers them this way. The file wins over the variable. Call before <see cref="FromProcess"/>.
    /// </summary>
    public static void ReadSecretFiles()
    {
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.EndsWith("_FILE", StringComparison.Ordinal) && entry.Value is string { Length: > 0 } path
                && key[..^"_FILE".Length] is var name
                && (name.StartsWith("TS_AUTH_", StringComparison.Ordinal) || name.StartsWith("CLOUDFLARE_", StringComparison.Ordinal) || name == "TERRABASE_PEER_SECRET"))
            {
                try
                {
                    Environment.SetEnvironmentVariable(name, File.ReadAllText(path).TrimEnd('\r', '\n'));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    // Names the variable, never the path's contents.
                    throw Invalid($"{key} must name a readable file");
                }
            }
        }
    }

    /// <summary>Reads <c>TS_AUTH_*</c> environment variables, then <c>--key=value</c> arguments, which win.</summary>
    public static AuthOptions FromProcess(IEnumerable<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && key.StartsWith("TS_AUTH_", StringComparison.Ordinal) && !key.EndsWith("_FILE", StringComparison.Ordinal) && entry.Value is string value)
            {
                values[key["TS_AUTH_".Length..].ToLowerInvariant().Replace('_', '-')] = value;
            }
        }

        // Cloudflare's credential names are separate from TS_AUTH_* settings.
        if (Environment.GetEnvironmentVariable("CLOUDFLARE_ACCOUNT_ID") is { } accountId)
        {
            values["cloudflare-account-id"] = accountId;
        }

        if (Environment.GetEnvironmentVariable("CLOUDFLARE_EMAIL_TOKEN") is { } emailToken)
        {
            values["cloudflare-email-token"] = emailToken;
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
        var publicOrigin = AbsoluteHttpUrl("public-origin", Optional("public-origin") ?? GlobalOrigin);
        // Routes are mapped at the root, so the public origin cannot carry a base path.
        if (publicOrigin.AbsolutePath != "/")
        {
            throw Invalid("TS_AUTH_PUBLIC_ORIGIN must be an origin without a path");
        }

        var verifyValues = (Optional("audit-verify-keys") ?? string.Empty)
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (verifyValues.Length > 5)
        {
            throw Invalid("TS_AUTH_AUDIT_VERIFY_KEYS may contain at most 5 previous keys");
        }

        var cloudflareAccountId = Optional("cloudflare-account-id");
        var cloudflareEmailToken = Optional("cloudflare-email-token");
        if ((cloudflareAccountId is null) != (cloudflareEmailToken is null))
        {
            throw Invalid("CLOUDFLARE_ACCOUNT_ID and CLOUDFLARE_EMAIL_TOKEN must be set together");
        }

        var rateLimit = 1000;
        if (Optional("rate-limit") is { } rawLimit
            && (!int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out rateLimit) || rateLimit < 1))
        {
            throw Invalid("TS_AUTH_RATE_LIMIT must be positive");
        }

        var options = new AuthOptions
        {
            Listen = ParseListen("listen", Optional("listen") ?? "http://0.0.0.0:8090"),
            MetricsListen = ParseListen("metrics-listen", Optional("metrics-listen") ?? "http://0.0.0.0:9092"),
            PublicOrigin = publicOrigin,
            MasterKey = DecodeKey("master-key", Required("master-key")),
            AuditSigningKey = Optional("audit-signing-key") is { } signing ? DecodeKey("audit-signing-key", signing) : null,
            AuditVerificationKeys = [.. verifyValues.Select(raw => DecodeKey("audit-verify-keys", raw))],
            RateLimitPerWindow = rateLimit,
            TrustedProxies = ParseTrustedProxies(Optional("trusted-proxies")),
            CloudflareEmail = cloudflareAccountId is null || cloudflareEmailToken is null ? null : new CloudflareEmailOptions(cloudflareAccountId, cloudflareEmailToken),
            SentryDsn = Optional("sentry-dsn") is { } dsn ? ValidateDsn(dsn) : null,
            DashboardClient = ParseDashboardClient(Optional("dashboard-client-secret"), Optional("dashboard-redirect-uris"), Optional("dashboard-post-logout-redirect-uris")),
            AccessTokenAudiences = [.. ParseAudiences(Optional("access-token-audiences")).Prepend(TerraBaseAudience).Distinct(StringComparer.Ordinal)],
            ServiceClients = ParseServiceClients(Optional("service-clients")),
            TerraBaseUrl = Optional("terrabase-url") is { } terraBaseUrl ? ParseOrigin("terrabase-url", terraBaseUrl) : null,
            Development = development,
        };
        options = options with { SocialClients = ParseSocialClients(options, Optional("github-oauth"), Optional("google-oauth")) };
        options.EnforceProductionGuards();
        return options;
    }

    private static List<Features.ProjectTenants.SocialProviderRecord> ParseSocialClients(AuthOptions options, string? github, string? google)
    {
        var clients = new List<Features.ProjectTenants.SocialProviderRecord>();
        foreach (var (provider, raw) in new[] { ("github", github), ("google", google) })
        {
            if (raw is null)
            {
                continue;
            }

            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(raw);
                clients.Add(Features.ProjectTenants.SocialProviders.Seal(options, OidcIssuer.InstanceId, provider, null,
                    document.RootElement.GetProperty("client_id").GetString(), document.RootElement.GetProperty("client_secret").GetString(), null, null, null));
            }
            catch (Exception error) when (error is System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException or Contracts.ApiException)
            {
                throw Invalid($"TS_AUTH_{provider.ToUpperInvariant()}_OAUTH must contain valid client_id and client_secret JSON credentials");
            }
        }

        return clients;
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

        // The management API accepts tokens whose audience is the platform issuer (TS-598); organization and project tokens
        // carry only these audiences, so none may be that issuer. Other paths of this origin grant nothing there.
        var issuer = new Uri(this.PublicOrigin, Features.Oidc.OidcIssuer.Slug);
        if (this.AccessTokenAudiences.Any(audience => new Uri(audience) == issuer))
        {
            throw Invalid("TS_AUTH_ACCESS_TOKEN_AUDIENCES must not name this service's platform issuer");
        }
    }

    private static Uri ParseOrigin(string key, string raw)
    {
        var url = AbsoluteHttpUrl(key, raw);
        return url.AbsolutePath == "/" ? url : throw Invalid($"{EnvName(key)} must be an origin without a path");
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
            if (!TerraScaleIds.IsValid(id) || id is OidcIssuer.DashboardClientId or OidcIssuer.StatusClientId || !IsClientSecret(secret)
                || clients.Any(client => client.ClientId == id))
            {
                throw Invalid("TS_AUTH_SERVICE_CLIENTS must hold distinct id:secret pairs: canonical ULIDs other than the dashboard and status client ids, secrets of 32 to 512 characters");
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

    private static IPEndPoint ParseListen(string key, string raw)
    {
        if (!raw.StartsWith("http://", StringComparison.Ordinal))
        {
            throw Invalid($"{EnvName(key)} must be a plain HTTP listen URL");
        }

        var authority = raw["http://".Length..];
        if (authority.IndexOfAny(['/', '?', '#', '@']) >= 0)
        {
            throw Invalid($"{EnvName(key)} must contain only an address and port");
        }

        // An address (IPv4, [IPv6], localhost, + or *) and an explicit port; port 0 asks for an ephemeral one.
        var match = ListenAuthority().Match(authority);
        if (!match.Success
            || !int.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            || port > IPEndPoint.MaxPort)
        {
            throw Invalid($"{EnvName(key)} must use a host and port");
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
            ? throw Invalid($"{EnvName(key)} must use a host and port")
            : new IPEndPoint(address, port);
    }

    [GeneratedRegex(@"\A(?<host>\[[^\]]+\]|[^:\[\]]+):(?<port>[0-9]{1,5})\z")]
    private static partial Regex ListenAuthority();

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

/// <summary>The Cloudflare account and token that send email through Cloudflare Email Sending.</summary>
/// <param name="AccountId">The Cloudflare account that owns the Email Sending domain.</param>
/// <param name="ApiToken">A token with the account's Email Sending permission.</param>
public sealed record CloudflareEmailOptions(string AccountId, string ApiToken)
{
    /// <summary>Omits the token, so formatting or logging the record never discloses it.</summary>
    public override string ToString() => nameof(CloudflareEmailOptions);
}

