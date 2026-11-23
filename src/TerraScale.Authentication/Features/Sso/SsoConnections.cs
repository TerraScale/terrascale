using Dapper;
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
using TerraScale.Authentication.Errors;
using TerraScale.Authentication.Features.Oidc;
using TerraScale.Authentication.Features.Platform;
using TerraScale.Authentication.Security;
using TerraScale.Authentication.Storage;
using VersionedConnection = TerraScale.Authentication.Storage.Versioned<TerraScale.Authentication.Features.Sso.SsoConnectionRecord>;

namespace TerraScale.Authentication.Features.Sso;

public sealed record SsoConnectionRecord(
    string InstanceId, string Domain, string OrganizationId, string Protocol, string DefaultRole, string VerificationToken,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? VerifiedAt = null,
    string? Issuer = null, string? ClientId = null, string? ClientSecretBox = null,
    string? IdpEntityId = null, string? SsoUrl = null, string? Certificate = null)
{
    public string? Version { get; init; }
}

public sealed partial class SsoConnections(AuthData data, PlatformDirectory platform, AuthOptions options, IHttpClientFactory clients, TimeProvider clock)
{
    public const string HttpClientName = "org-sso";

    public const string Oidc = "oidc";

    public const string Saml = "saml";

    public const string RecordLabel = "_terrascale-sso";

    public const string RecordPrefix = "terrascale-sso-verification=";

    public const string Resolver = "https://cloudflare-dns.com/dns-query";

    public const string CallbackPath = $"/ui/{OidcIssuer.Slug}/sso/callback";

    private const string Instance = PlatformDirectory.Instance;
    private const int MaxDiscoveries = 1024;
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> discoveries = new(StringComparer.Ordinal);

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

    public static string? EmailDomain(string? email) => email?.LastIndexOf('@') is > 0 and var at ? Domain(email[(at + 1)..]) : null;

    public async Task<VersionedConnection?> ForDomainAsync(string domain, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<SsoConnectionRecord>(
            "SELECT * FROM sso_connections WHERE domain = @domain AND instance_id = @Instance", new { domain, Instance }), cancellationToken));

    public async Task<SsoConnectionRecord?> EnforcedAsync(string? email, CancellationToken cancellationToken) =>
        EmailDomain(email) is { } domain && await this.ForDomainAsync(domain, cancellationToken) is { Value.VerifiedAt: not null } found
        && await platform.OrganizationAsync(found.Value.OrganizationId, cancellationToken) is not null
            ? found.Value
            : null;

    public async Task<VersionedConnection?> ForOrganizationAsync(string organizationId, CancellationToken cancellationToken) =>
        Versioned.Of(await data.ReadAsync(connection => connection.QuerySingleOrDefaultAsync<SsoConnectionRecord>(
            "SELECT * FROM sso_connections WHERE organization_id = @organizationId AND instance_id = @Instance", new { organizationId, Instance }), cancellationToken));

    public async Task<PutConnectionResult> PutAsync(string organizationId, string actor, string domain, Func<SsoConnectionRecord, SsoConnectionRecord> change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (await this.ForOrganizationAsync(organizationId, cancellationToken) is { } own && own.Value.Domain != domain)
        {
            return new Conflict("This organization has a connection for another domain; delete it first.");
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = clock.GetUtcNow();
            var existing = await this.ForDomainAsync(domain, cancellationToken);
            var mine = existing?.Value.OrganizationId == organizationId;
            if (existing is { Value.VerifiedAt: not null } && !mine && await platform.OrganizationAsync(existing.Value.OrganizationId, cancellationToken) is not null)
            {
                return new Conflict("Another organization verified this domain.");
            }

            var start = mine ? existing!.Value : new SsoConnectionRecord(Instance, domain, organizationId, Oidc, Roles.Member, Tokens.RandomToken(32), now, now);
            var updated = change(start) with { InstanceId = Instance, Domain = domain, OrganizationId = organizationId, UpdatedAt = now };
            if (await data.SaveAsync(
                [
                    existing is null ? Change.Insert(updated) : Change.Update(existing, updated),
                    platform.Audit(organizationId, actor, "sso.updated", null, domain),
                ], cancellationToken))
            {
                return updated;
            }
        }

        throw ApiException.Unavailable();
    }

    public async Task<VerifyConnectionResult> VerifyAsync(string organizationId, string actor, CancellationToken cancellationToken)
    {
        var found = await this.ForOrganizationAsync(organizationId, cancellationToken);
        if (found is null)
        {
            return new NotFound("No such resource.");
        }

        if (found.Value.VerifiedAt is not null)
        {
            return found.Value;
        }

        if (!await this.PublishedAsync(found.Value, cancellationToken))
        {
            return new Conflict($"No TXT record {RecordLabel}.{found.Value.Domain} with the verification value was found; DNS changes can take a few minutes.", "domain_unverified");
        }

        var verified = found.Value with { VerifiedAt = clock.GetUtcNow(), UpdatedAt = clock.GetUtcNow() };
        return await data.SaveAsync(
            [
                Change.Update(found, verified),
                platform.Audit(organizationId, actor, "sso.verified", null, verified.Domain),
            ], cancellationToken)
            ? verified
            : new Conflict("The connection changed; retry.");
    }

    public async Task<DeleteConnectionResult> DeleteAsync(string organizationId, string actor, CancellationToken cancellationToken)
    {
        var found = await this.ForOrganizationAsync(organizationId, cancellationToken);
        if (found is null)
        {
            return new NotFound("No such resource.");
        }

        if (!await data.SaveAsync(
            [
                Change.Delete(found),
                platform.Audit(organizationId, actor, "sso.deleted", null, found.Value.Domain),
            ], cancellationToken))
        {
            return new Conflict("The connection changed; retry.");
        }

        return new Done();
    }

    public string Seal(string domain, string secret) => SecretBox.Seal(options.MasterKey, Encoding.UTF8.GetBytes(secret), SecretContext(domain));

    public string ClientSecret(SsoConnectionRecord connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Encoding.UTF8.GetString(SecretBox.Open(options.MasterKey, connection.ClientSecretBox ?? throw ApiException.Internal(), SecretContext(connection.Domain)));
    }

    public ConfigurationManager<OpenIdConnectConfiguration> Discovery(string issuer)
    {
        if (this.discoveries.Count >= MaxDiscoveries)
        {
            this.discoveries.Clear();
        }

        return this.discoveries.GetOrAdd(issuer, key => new(key.TrimEnd('/') + "/.well-known/openid-configuration", new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(clients.CreateClient(HttpClientName)) { RequireHttps = true }));
    }

    public async Task<OpenIdConnectConfiguration?> DiscoverAsync(string issuer, CancellationToken cancellationToken)
    {
        try
        {
            var configuration = await this.Discovery(issuer).GetConfigurationAsync(cancellationToken);
            return configuration.Issuer == issuer && Https(configuration.AuthorizationEndpoint) && Https(configuration.TokenEndpoint) ? configuration : null;
        }
        catch (InvalidOperationException)
        {
            this.discoveries.TryRemove(issuer, out _);
            return null;
        }
    }

    private static bool Https(string? endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private async Task<bool> PublishedAsync(SsoConnectionRecord connection, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{Resolver}?name={RecordLabel}.{connection.Domain}&type=TXT");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await clients.CreateClient(HttpClientName).SendAsync(request, cancellationToken);
            using var answer = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
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

public readonly union PutConnectionResult(SsoConnectionRecord, Conflict);
public readonly union VerifyConnectionResult(SsoConnectionRecord, NotFound, Conflict);
public readonly union DeleteConnectionResult(Done, NotFound, Conflict);
