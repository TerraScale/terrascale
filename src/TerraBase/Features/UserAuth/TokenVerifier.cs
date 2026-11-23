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

namespace TerraBase.Features.UserAuth;

internal sealed class TokenVerifier(TerraBaseOptions options, IHttpClientFactory http, TimeProvider time) : IDisposable
{
    public const string HttpClientName = "ts-auth";

    private const string EndUserKind = "end_user";

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

    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _platform = options.AuthIssuer is { } issuer ? Manager(http, issuer) : null;

    private readonly MemoryCache _projects = new(new MemoryCacheOptions { SizeLimit = MaxProjectIssuers });

    public async ValueTask<Result<Caller, Unauthorized, Unavailable>> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (_platform is null || options.AuthIssuer is not { } platformIssuer)
        {
            return new Unauthorized("bearer tokens need TERRABASE_AUTH_ISSUER", "invalid_token");
        }

        if (!Tokens.CanReadToken(token) || Read(token) is not { } unverified)
        {
            return new Unauthorized("the bearer token is malformed", "invalid_token");
        }

        if (Trust(unverified, platformIssuer) is not { } trusted)
        {
            return new Unauthorized("the bearer token's issuer is not trusted", "invalid_token");
        }

        if (await ConfigurationAsync(trusted.Manager, cancellationToken) is not { } configuration)
        {
            return new Unavailable("ts-auth signing keys are unavailable; retry", "token_keys_pending");
        }

        var validated = await Tokens.ValidateTokenAsync(token, Parameters(trusted, configuration.SigningKeys));
        if (!validated.IsValid && validated.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            trusted.Manager.RequestRefresh();
            if (await ConfigurationAsync(trusted.Manager, cancellationToken) is { } refreshed)
            {
                validated = await Tokens.ValidateTokenAsync(token, Parameters(trusted, refreshed.SigningKeys));
            }
        }

        if (!validated.IsValid || validated.SecurityToken is not JsonWebToken jwt)
        {
            return new Unauthorized("the bearer token is invalid or expired", "invalid_token");
        }

        return Classify(jwt, trusted.EndUser);
    }

    public void Dispose() => _projects.Dispose();

    private TrustedIssuer? Trust(JsonWebToken token, string platformIssuer)
    {
        if (string.Equals(token.Issuer, platformIssuer, StringComparison.Ordinal) && _platform is { } platform)
        {
            return new TrustedIssuer(platformIssuer, platform, options.AuthAudiences, EndUser: false);
        }

        if (!token.TryGetPayloadValue<string>("token_kind", out var kind) || kind != EndUserKind
            || !token.TryGetPayloadValue<string>("project_id", out var project) || !IsCanonicalUlid(project))
        {
            return null;
        }

        var projectIssuer = new Uri(new Uri(platformIssuer), project).AbsoluteUri;
        if (!string.Equals(token.Issuer, projectIssuer, StringComparison.Ordinal))
        {
            return null;
        }

        var audiences = options.AuthAudiences.Select(audience => string.Concat(audience.TrimEnd('/'), ProjectDataPath, project)).ToArray();
        return new TrustedIssuer(projectIssuer, ProjectManager(projectIssuer), audiences, EndUser: true);
    }

    private ConfigurationManager<OpenIdConnectConfiguration> ProjectManager(string issuer)
    {
        if (_projects.TryGetValue(issuer, out ConfigurationManager<OpenIdConnectConfiguration>? cached) && cached is { } existing)
        {
            return existing;
        }

        var created = Manager(http, issuer);
        _projects.Set(issuer, created, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = ProjectIssuerIdle });
        return created;
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> Manager(IHttpClientFactory http, string issuer) => new(
        string.Concat(issuer.TrimEnd('/'), "/.well-known/openid-configuration"),
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever(http.CreateClient(HttpClientName)) { RequireHttps = new Uri(issuer).Scheme == Uri.UriSchemeHttps })
    {
        AutomaticRefreshInterval = TimeSpan.FromHours(1),
        RefreshInterval = TimeSpan.FromSeconds(30),
    };

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

    private static Result<Caller, Unauthorized, Unavailable> Classify(JsonWebToken jwt, bool endUser)
    {
        var invalid = new Unauthorized("the bearer token's claims are invalid", "invalid_token");
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

        var endUserKind = jwt.TryGetPayloadValue<string>("token_kind", out var kind) && kind == EndUserKind;
        if (endUserKind != endUser)
        {
            return invalid;
        }

        if (endUser)
        {
            if (projectUlid is not { } endUserProject || Payload(jwt) is not { } payload)
            {
                return invalid;
            }

            using (payload)
            {
                return RulesAuth.Read(subject, payload.RootElement, custom: false) is { } auth
                    ? new EndUserCaller(endUserProject, database, auth)
                    : invalid;
            }
        }

        if (projectUlid is { } scopedProject)
        {
            var scopes = jwt.TryGetPayloadValue<string>("scope", out var scope) && scope.Length <= 4096
                ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToFrozenSet(StringComparer.Ordinal)
                : FrozenSet<string>.Empty;
            return new ProjectCaller(subject, scopedProject, database, scopes);
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
}

internal sealed record TrustedIssuer(string Issuer, ConfigurationManager<OpenIdConnectConfiguration> Manager, IReadOnlyList<string> Audiences, bool EndUser);
