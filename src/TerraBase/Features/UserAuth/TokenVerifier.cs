using System.Buffers;
using System.Buffers.Text;
using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Features.Callers;
using TerraBase.Features.Rules;

namespace TerraBase.Features.UserAuth;

internal sealed class TokenVerifier
{
    public const string HttpClientName = "ts-auth";

    private static readonly JsonWebTokenHandler Tokens = new() { MaximumTokenSizeInBytes = 16 * 1024 };

    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    private static readonly string[] Types = ["at+jwt", "application/at+jwt", "JWT"];

    private readonly TerraBaseOptions _options;
    private readonly TimeProvider _time;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _configuration;

    public TokenVerifier(TerraBaseOptions options, IHttpClientFactory http, TimeProvider time)
    {
        _options = options;
        _time = time;
        if (options.AuthIssuer is { } issuer)
        {
            var metadata = string.Concat(issuer.TrimEnd('/'), "/.well-known/openid-configuration");
            _configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadata, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(http.CreateClient(HttpClientName)) { RequireHttps = true })
            {
                AutomaticRefreshInterval = TimeSpan.FromHours(1),
                RefreshInterval = TimeSpan.FromSeconds(30),
            };
        }
    }

    public async ValueTask<Result<Caller, Unauthorized, Unavailable>> VerifyAsync(string token, CancellationToken cancellationToken)
    {
        if (_configuration is null || _options.AuthIssuer is not { } issuer)
        {
            return new Unauthorized("bearer tokens need TERRABASE_AUTH_ISSUER", "invalid_token");
        }

        if (!Tokens.CanReadToken(token))
        {
            return new Unauthorized("the bearer token is malformed", "invalid_token");
        }

        if (await ConfigurationAsync(_configuration, cancellationToken) is not { } configuration)
        {
            return new Unavailable("ts-auth signing keys are unavailable; retry", "token_keys_pending");
        }

        var validated = await Tokens.ValidateTokenAsync(token, Parameters(issuer, configuration.SigningKeys));
        if (!validated.IsValid && validated.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            _configuration.RequestRefresh();
            if (await ConfigurationAsync(_configuration, cancellationToken) is { } refreshed)
            {
                validated = await Tokens.ValidateTokenAsync(token, Parameters(issuer, refreshed.SigningKeys));
            }
        }

        if (!validated.IsValid || validated.SecurityToken is not JsonWebToken jwt)
        {
            return new Unauthorized("the bearer token is invalid or expired", "invalid_token");
        }

        return Classify(jwt);
    }

    private static Result<Caller, Unauthorized, Unavailable> Classify(JsonWebToken jwt)
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

        if (jwt.TryGetPayloadValue<string>("token_kind", out var kind) && kind == "end_user")
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

    private TokenValidationParameters Parameters(string issuer, IEnumerable<SecurityKey> keys) => new()
    {
        ValidIssuer = issuer,
        ValidAudiences = _options.AuthAudiences,
        ValidateAudience = _options.AuthAudiences.Count > 0,
        IssuerSigningKeys = keys,
        ValidAlgorithms = Algorithms,
        ValidTypes = Types,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(30),
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = _time.GetUtcNow().UtcDateTime;
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
