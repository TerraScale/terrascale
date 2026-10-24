using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using TerraScale.Authentication.Configuration;
using TerraScale.Authentication.Contracts;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Store;
using TerraScale.DotNet.TerraBase;
using Versioned = TerraScale.Authentication.Features.Platform.Versioned<TerraScale.Authentication.Features.Sso.SsoConnectionRecord>;

namespace TerraScale.Authentication.Features.Sso;

/// <summary>
/// An organization's single sign-on connection (collection <c>organization_sso</c>), keyed by its email domain, so a
/// domain has at most one. Until <paramref name="VerifiedAt"/> is set it changes nothing about sign-in, and another
/// organization may take the domain over.
/// </summary>
/// <param name="InstanceId">The platform tenant.</param>
/// <param name="Domain">The lowercase ASCII email domain.</param>
/// <param name="OrganizationId">The organization.</param>
/// <param name="Protocol"><c>oidc</c> or <c>saml</c>.</param>
/// <param name="DefaultRole">The organization role given to members who join through the identity provider.</param>
/// <param name="VerificationToken">The value the domain's TXT record must carry.</param>
/// <param name="CreatedAt">Creation time.</param>
/// <param name="UpdatedAt">Last change.</param>
/// <param name="VerifiedAt">When the TXT record was found; null while unverified.</param>
/// <param name="Issuer">The OIDC issuer.</param>
/// <param name="ClientId">The OIDC client id.</param>
/// <param name="ClientSecretBox">The OIDC client secret, sealed under the master key.</param>
/// <param name="IdpEntityId">The SAML identity provider's entity id.</param>
/// <param name="SsoUrl">The SAML identity provider's sign-on URL.</param>
/// <param name="Certificate">The SAML identity provider's signing certificate, base64 DER.</param>
public sealed record SsoConnectionRecord(
    string InstanceId, string Domain, string OrganizationId, string Protocol, string DefaultRole, string VerificationToken,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? VerifiedAt = null,
    string? Issuer = null, string? ClientId = null, string? ClientSecretBox = null,
    string? IdpEntityId = null, string? SsoUrl = null, string? Certificate = null);

/// <summary>Organizations' SSO connections: configuration, DNS domain verification, and the identity providers' discovery.</summary>
public sealed partial class SsoConnections(AuthStore store, PlatformDirectory platform, AuthOptions options, IHttpClientFactory clients, TimeProvider clock)
{
    /// <summary>The HTTP client that reaches identity providers and DNS over HTTPS; it connects to public addresses only.</summary>
    public const string HttpClientName = "org-sso";

    /// <summary>OpenID Connect.</summary>
    public const string Oidc = "oidc";

    /// <summary>SAML 2.0.</summary>
    public const string Saml = "saml";

    /// <summary>The TXT record's name under the domain.</summary>
    public const string RecordLabel = "_terrascale-sso";

    /// <summary>The TXT record's value before the token.</summary>
    public const string RecordPrefix = "terrascale-sso-verification=";

    /// <summary>The DNS-over-HTTPS resolver (JSON API) that domain verification asks.</summary>
    public const string Resolver = "https://cloudflare-dns.com/dns-query";

    /// <summary>The redirect URI identity providers return to, under the platform's hosted pages.</summary>
    public const string CallbackPath = $"/ui/{OidcIssuer.Slug}/sso/callback";

    private const string Collection = AuthStore.OrganizationSsoCollection;
    private const string Instance = PlatformDirectory.Instance;
    private const int MaxDiscoveries = 1024;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> discoveries = new(StringComparer.Ordinal);

    /// <summary>The lowercase ASCII form of an email domain with at least two labels, or null.</summary>
    public static string? Domain(string? value)
    {
        try
        {
            var ascii = value is { Length: > 0 and <= 253 } ? new IdnMapping().GetAscii(value.Trim().TrimEnd('.')).ToLowerInvariant() : null;
            return ascii is not null && DomainPattern().IsMatch(ascii) ? ascii : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The domain part of an email address, normalized like <see cref="Domain"/>.</summary>
    public static string? EmailDomain(string? email) => email?.LastIndexOf('@') is > 0 and var at ? Domain(email[(at + 1)..]) : null;

    /// <summary>The connection for <paramref name="domain"/>, whatever its organization.</summary>
    public async Task<Versioned?> ForDomainAsync(string domain, CancellationToken cancellationToken) =>
        await store.GetAsync(Collection, Instance, domain, cancellationToken).ConfigureAwait(false) is { } document
        && document.Document.Deserialize(SsoJson.Default.SsoConnectionRecord) is { } connection && connection.InstanceId == Instance && connection.Domain == domain
            ? new(connection, document.Version)
            : null;

    /// <summary>The verified connection that governs sign-in for <paramref name="email"/>, when its organization still exists.</summary>
    public async Task<SsoConnectionRecord?> EnforcedAsync(string? email, CancellationToken cancellationToken) =>
        EmailDomain(email) is { } domain && await this.ForDomainAsync(domain, cancellationToken).ConfigureAwait(false) is { Value.VerifiedAt: not null } found
        && await platform.OrganizationAsync(found.Value.OrganizationId, cancellationToken).ConfigureAwait(false) is not null
            ? found.Value
            : null;

    /// <summary>The organization's connection, or null.</summary>
    public async Task<Versioned?> ForOrganizationAsync(string organizationId, CancellationToken cancellationToken)
    {
        await foreach (var document in store.QueryAsync(Collection, Instance, "/organizationId", organizationId, cancellationToken).ConfigureAwait(false))
        {
            if (document.Document.Deserialize(SsoJson.Default.SsoConnectionRecord) is { } connection && connection.OrganizationId == organizationId
                && await this.ForDomainAsync(connection.Domain, cancellationToken).ConfigureAwait(false) is { Value.OrganizationId: var owner } current && owner == organizationId)
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Creates or changes the organization's connection with <paramref name="change"/>. 409 when the organization has a
    /// connection for another domain, or another live organization verified this one; an unverified domain moves.
    /// </summary>
    public async Task<SsoConnectionRecord> PutAsync(string organizationId, string actor, string domain, Func<SsoConnectionRecord, SsoConnectionRecord> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await this.ForOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) is { } own && own.Value.Domain != domain)
        {
            throw PlatformDirectory.Conflict("conflict", "This organization has a connection for another domain; delete it first.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = clock.GetUtcNow();
            var existing = await this.ForDomainAsync(domain, cancellationToken).ConfigureAwait(false);
            var mine = existing?.Value.OrganizationId == organizationId;
            if (existing is { Value.VerifiedAt: not null } && !mine && await platform.OrganizationAsync(existing.Value.OrganizationId, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw PlatformDirectory.Conflict("conflict", "Another organization verified this domain.");
            }

            var start = mine ? existing!.Value : new SsoConnectionRecord(Instance, domain, organizationId, Oidc, Roles.Member, Tokens.RandomToken(32), now, now);
            var updated = change(start) with { InstanceId = Instance, Domain = domain, OrganizationId = organizationId, UpdatedAt = now };
            var record = JsonSerializer.SerializeToElement(updated, SsoJson.Default.SsoConnectionRecord);
            if (await store.TransactAsync(
                [
                    existing is null ? AuthStore.Create(Collection, Instance, domain, record) : AuthStore.Replace(Collection, Instance, domain, record, existing.Version),
                    platform.Audit(organizationId, actor, "sso.updated", null, domain),
                ], cancellationToken).ConfigureAwait(false) is null)
            {
                return updated;
            }
        }

        throw ApiException.Unavailable();
    }

    /// <summary>Marks the domain verified when its TXT record carries the token; 409 <c>domain_unverified</c> otherwise.</summary>
    public async Task<SsoConnectionRecord> VerifyAsync(string organizationId, string actor, CancellationToken cancellationToken)
    {
        var found = await this.ForOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (found.Value.VerifiedAt is not null)
        {
            return found.Value;
        }

        if (!await this.PublishedAsync(found.Value, cancellationToken).ConfigureAwait(false))
        {
            throw PlatformDirectory.Conflict("domain_unverified", $"No TXT record {RecordLabel}.{found.Value.Domain} with the verification value was found; DNS changes can take a few minutes.");
        }

        var verified = found.Value with { VerifiedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        return await store.TransactAsync(
            [
                AuthStore.Replace(Collection, Instance, verified.Domain, JsonSerializer.SerializeToElement(verified, SsoJson.Default.SsoConnectionRecord), found.Version),
                platform.Audit(organizationId, actor, "sso.verified", null, verified.Domain),
            ], cancellationToken).ConfigureAwait(false) is null
            ? verified
            : throw PlatformDirectory.Conflict("conflict", "The connection changed; retry.");
    }

    /// <summary>Deletes the organization's connection; 404 when it has none.</summary>
    public async Task DeleteAsync(string organizationId, string actor, CancellationToken cancellationToken)
    {
        var found = await this.ForOrganizationAsync(organizationId, cancellationToken).ConfigureAwait(false) ?? throw PlatformDirectory.NotFound();
        if (await store.TransactAsync(
            [
                AuthStore.Delete(Collection, Instance, found.Value.Domain, found.Version),
                platform.Audit(organizationId, actor, "sso.deleted", null, found.Value.Domain),
            ], cancellationToken).ConfigureAwait(false) is not null)
        {
            throw PlatformDirectory.Conflict("conflict", "The connection changed; retry.");
        }
    }

    /// <summary>Seals an OIDC client secret for the connection of <paramref name="domain"/>.</summary>
    public string Seal(string domain, string secret) => SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), SecretContext(domain));

    /// <summary>The connection's OIDC client secret.</summary>
    public string ClientSecret(SsoConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Encoding.UTF8.GetString(SecretBox.Open(options.MasterKey, connection.ClientSecretBox ?? throw ApiException.Internal(), SecretContext(connection.Domain)));
    }

    /// <summary>The issuer's discovery document and keys, cached and refreshed by the IdentityModel configuration manager.</summary>
    public ConfigurationManager<OpenIdConnectConfiguration> Discovery(string issuer)
    {
        if (this.discoveries.Count >= MaxDiscoveries)
        {
            this.discoveries.Clear();
        }

        return this.discoveries.GetOrAdd(issuer, key => new(key.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(clients.CreateClient(HttpClientName)) { RequireHttps = true }));
    }

    /// <summary>The issuer's discovery document, which must name the same issuer; null when it cannot be read.</summary>
    public async Task<OpenIdConnectConfiguration?> DiscoverAsync(string issuer, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await this.Discovery(issuer).GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return configuration.Issuer == issuer && Https(configuration.AuthorizationEndpoint) && Https(configuration.TokenEndpoint) ? configuration : null;
        }
        catch (InvalidOperationException)
        {
            this.discoveries.TryRemove(issuer, out _);
            return null;
        }
    }

    private static bool Https(string? endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    // Asks the resolver for the TXT record; any failure reads as not published.
    private async Task<bool> PublishedAsync(SsoConnectionRecord connection, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Resolver}?name={RecordLabel}.{connection.Domain}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken).ConfigureAwait(false);
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
            var expected = RecordPrefix + connection.VerificationToken;
            return response.IsSuccessStatusCode && answer.RootElement.TryGetProperty("Answer", out var records) && records.ValueKind == JsonValueKind.Array
                && records.EnumerateArray().Any(record => record.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.Number && type.GetInt32() == 16
                    && record.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String && data.GetString()!.Replace("\"", string.Empty, StringComparison.Ordinal) == expected);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or TaskCanceledException)
        {
            return false;
        }
    }

    private static string SecretContext(string domain) => string.Create(CultureInfo.InvariantCulture, $"ts-auth org sso client secret {domain}");

    [GeneratedRegex("^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\\.)+[a-z][a-z0-9-]{0,61}[a-z0-9]$")]
    private static partial Regex DomainPattern();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SsoConnectionRecord))]
internal sealed partial class SsoJson : JsonSerializerContext;
