using System.Buffers;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Rules;
using TerraBase.Hosting;

namespace TerraBase.Features.UserAuth;

internal sealed class TokenVerifier(TerraBaseOptions options, IHttpClientFactory http, TimeProvider time) : IDisposable
{
    public const string HttpClientName = "ts-auth";

    private const string TenantUserUse = "user";

    private const string TenantCustomUse = "custom";

    private const string ProjectDataPath = "/v1/projects/";

    private const int MaxProjectIssuers = 1024;

    private static readonly TimeSpan ProjectIssuerIdle = TimeSpan.FromHours(1);

    private static readonly JsonWebTokenHandler Tokens = new() { MaximumTokenSizeInBytes = 16 * 1024 };

    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    private static readonly string[] Types = ["at+jwt", "application/at+jwt", "JWT"];

    private static readonly string[] TenantRefusedClaims = ["project_id", "instance_id", "token_kind", "platform_role", "key_id", "key_gen"];

    private readonly IssuerEntry? _platform = options.AuthIssuer is { } issuer ? CreateIssuer(http, issuer) : null;

    private readonly MemoryCache _projects = new(new MemoryCacheOptions { SizeLimit = MaxProjectIssuers });

    public async ValueTask<CallerAuthenticationOutcome> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (_platform is null || options.AuthIssuer is not { } rawPlatformIssuer)
        {
            return new CredentialRejected("bearer tokens need TERRABASE_AUTH_ISSUER", "invalid_token");
        }

        var platformIssuer = rawPlatformIssuer.TrimEnd('/');

        if (!Tokens.CanReadToken(token) || Read(token) is not { } unverified)
        {
            return new CredentialRejected("the bearer token is malformed", "invalid_token");
        }

        if (Trust(unverified, platformIssuer) is not { } trusted)
        {
            return new CredentialRejected("the bearer token's issuer is not trusted", "invalid_token");
        }

        if (await ConfigurationAsync(trusted.Entry.Manager, cancellationToken) is not { } configuration)
        {
            return new BackendUnavailable("ts-auth signing keys are unavailable; retry", "token_keys_pending");
        }

        var validated = await Tokens.ValidateTokenAsync(token, Parameters(trusted, configuration.SigningKeys));
        if (!validated.IsValid && validated.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            trusted.Entry.Manager.RequestRefresh();
            if (await FetchFreshAsync(trusted.Entry, cancellationToken) is not { } refreshed)
            {
                return new BackendUnavailable("ts-auth signing keys are unavailable; retry", "token_keys_pending");
            }

            validated = await Tokens.ValidateTokenAsync(token, Parameters(trusted, refreshed.SigningKeys));
        }

        if (!validated.IsValid || validated.SecurityToken is not JsonWebToken jwt)
        {
            return new CredentialRejected("the bearer token is invalid or expired", "invalid_token");
        }

        return trusted.Tenant is { } tenant ? ClassifyTenant(jwt, tenant) : Classify(jwt);
    }

    public void Dispose() => _projects.Dispose();

    private TrustedIssuer? Trust(JsonWebToken token, string platformIssuer)
    {
        if (string.Equals(token.Issuer, platformIssuer, StringComparison.Ordinal) && _platform is { } platform)
        {
            return new TrustedIssuer(platformIssuer, platform, options.AuthAudiences, Tenant: null);
        }

        if (!Uri.TryCreate(token.Issuer, UriKind.Absolute, out var issuer) || issuer.Segments is not [.., var last] || !IsCanonicalUlid(last)
            || !Ulid.TryParse(last, out var project))
        {
            return null;
        }

        var projectIssuer = new Uri(new Uri(platformIssuer), last).AbsoluteUri;
        if (!string.Equals(token.Issuer, projectIssuer, StringComparison.Ordinal))
        {
            return null;
        }

        var audiences = options.AuthAudiences.Select(audience => string.Concat(audience.TrimEnd('/'), ProjectDataPath, last)).ToArray();
        return new TrustedIssuer(projectIssuer, ProjectIssuer(projectIssuer), audiences, project);
    }

    private IssuerEntry ProjectIssuer(string issuer)
    {
        if (_projects.TryGetValue(issuer, out IssuerEntry? cached) && cached is { } existing)
        {
            return existing;
        }

        var created = CreateIssuer(http, issuer);
        _projects.Set(issuer, created, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = ProjectIssuerIdle });
        return created;
    }

    private static IssuerEntry CreateIssuer(IHttpClientFactory http, string issuer)
    {
        var metadataAddress = string.Concat(issuer.TrimEnd('/'), "/.well-known/openid-configuration");
        var retriever = new HttpDocumentRetriever(http.CreateClient(HttpClientName)) { RequireHttps = new Uri(issuer).Scheme == Uri.UriSchemeHttps };
        var manager = new ConfigurationManager<OpenIdConnectConfiguration>(metadataAddress, new OpenIdConnectConfigurationRetriever(), retriever)
        {
            AutomaticRefreshInterval = TimeSpan.FromHours(1),
            RefreshInterval = TimeSpan.FromSeconds(30),
        };
        return new IssuerEntry(metadataAddress, retriever, manager);
    }

    private static bool IsCanonicalUlid(string value) =>
        value.Length == 26 && Ulid.TryParse(value, out var parsed) && string.Equals(parsed.ToString(), value, StringComparison.Ordinal);

    private static JsonWebToken? Read(string token)
    {
        try
        {
            return Tokens.ReadJsonWebToken(token);
        }
        catch (Exception error) when (error is SecurityTokenMalformedException or ArgumentException)
        {
            return null;
        }
    }

    private static CallerAuthenticationOutcome ClassifyTenant(JsonWebToken jwt, Ulid project)
    {
        var invalid = new CredentialRejected("the project tenant token's claims are invalid", "invalid_token");
        if (!jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject) || !IsCanonicalUlid(subject)
            || !jwt.TryGetPayloadValue<string>("token_use", out var use) || use is not (TenantUserUse or TenantCustomUse)
            || TenantRefusedClaims.Any(name => jwt.TryGetPayloadValue<object>(name, out _)))
        {
            return invalid;
        }

        var custom = use == TenantCustomUse;
        string? database = null;
        if (jwt.TryGetPayloadValue<object>("database", out _)
            && (!custom || !jwt.TryGetPayloadValue<string>("database", out database) || !IsCanonicalUlid(database)))
        {
            return invalid;
        }

        if (Payload(jwt) is not { } payload)
        {
            return invalid;
        }

        using (payload)
        {
            return RulesAuth.Read(subject, payload.RootElement, custom) is { } auth
                ? new EndUserCaller(project, database, auth)
                : invalid;
        }
    }

    private static CallerAuthenticationOutcome Classify(JsonWebToken jwt)
    {
        var invalid = new CredentialRejected("the bearer token's claims are invalid", "invalid_token");
        if (!jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var subject) || string.IsNullOrWhiteSpace(subject) || subject.Length > 128)
        {
            return invalid;
        }

        var projectId = jwt.TryGetPayloadValue<string>("project_id", out var project) ? project : null;
        var database = jwt.TryGetPayloadValue<string>("database", out var narrowed) ? narrowed : null;
        Ulid? projectUlid = projectId is not null && projectId.Length == 26 && Ulid.TryParse(projectId, out var parsed) ? parsed : null;
        if ((projectId is not null && projectUlid is null) || (database is not null && projectUlid is null))
        {
            return invalid;
        }

        if (projectUlid is { } scopedProject)
        {
            var organizationId = jwt.TryGetPayloadValue<string>("org_id", out var org) ? org : null;
            Ulid? organizationUlid = organizationId is not null && IsCanonicalUlid(organizationId) && Ulid.TryParse(organizationId, out var parsedOrg) ? parsedOrg : null;
            if (organizationId is not null && organizationUlid is null)
            {
                return invalid;
            }

            var scopes = jwt.TryGetPayloadValue<string>("scope", out var scope) && scope.Length <= 4096
                ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(StringComparer.Ordinal)
                : FrozenSet<string>.Empty;
            return new ProjectCaller(subject, scopedProject, organizationUlid, database, scopes);
        }

        return subject.Length == 26 && Ulid.TryParse(subject, out _) ? new UserCaller(subject, ViaOperator: false) : invalid;
    }

    private static JsonDocument? Payload(JsonWebToken jwt)
    {
        var encoded = jwt.EncodedPayload;
        var bytes = new byte[Base64Url.GetMaxDecodedLength(encoded.Length)];
        if (Base64Url.DecodeFromChars(encoded, bytes, out _, out var written) != OperationStatus.Done)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(bytes.AsMemory(0, written));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TokenValidationParameters Parameters(TrustedIssuer trusted, IEnumerable<SecurityKey> keys) => new()
    {
        ValidIssuer = trusted.Issuer,
        ValidAudiences = trusted.Audiences,
        ValidateAudience = trusted.Audiences.Count > 0,
        IssuerSigningKeys = keys,
        ValidAlgorithms = Algorithms,
        ValidTypes = Types,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = time.GetUtcNow().UtcDateTime;
            return expires is { } until && until >= now.AddSeconds(-30) && (notBefore is null || notBefore.Value <= now.AddSeconds(30));
        },
    };

    private static async Task<OpenIdConnectConfiguration?> ConfigurationAsync(ConfigurationManager<OpenIdConnectConfiguration> manager, CancellationToken cancellationToken)
    {
        try
        {
            return await manager.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }

    private static async Task<OpenIdConnectConfiguration?> FetchFreshAsync(IssuerEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            return await OpenIdConnectConfigurationRetriever.GetAsync(entry.MetadataAddress, entry.Retriever, cancellationToken);
        }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or IOException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            return null;
        }
    }
}

internal sealed record IssuerEntry(string MetadataAddress, HttpDocumentRetriever Retriever, ConfigurationManager<OpenIdConnectConfiguration> Manager);

internal sealed record TrustedIssuer(string Issuer, IssuerEntry Entry, IReadOnlyList<string> Audiences, Ulid? Tenant);
