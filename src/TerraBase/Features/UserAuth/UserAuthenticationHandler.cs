using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

/// <summary>
/// Authenticates <c>Authorization: Bearer &lt;ts-auth access token&gt;</c>: an asymmetrically signed JWT of type
/// <c>at+jwt</c> from <c>auth-issuer</c>, for one of <c>auth-audience</c>, within its lifetime, with a <c>sub</c>, and not
/// a ts-auth guest's (<c>is_anonymous</c>).
/// The principal carries the <c>sub</c> as <see cref="ClaimTypes.NameIdentifier"/>. Request headers never name the user.
/// </summary>
internal sealed class UserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TerraBaseOptions terraBase,
    IIssuerKeys issuerKeys,
    ValidatedTokens validated,
    [FromKeyedServices(UserAuthenticationHandler.SchemeName)] TimeProvider? time = null) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = UserAuthExtensions.TokenScheme;

    /// <summary>Tolerance for clock differences with ts-auth on <c>nbf</c>; <c>exp</c> is strict.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);

    private static readonly JsonWebTokenHandler Tokens = new();

    // Asymmetric signatures only: "none" and HMAC (HS*) are never accepted.
    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    // RFC 9068 §4: an access token, so an ID token or another JWT from the same issuer is refused.
    private static readonly string[] Types = ["at+jwt", "application/at+jwt"];

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The scheme name is case-insensitive (RFC 9110); the token is not.
        var header = Request.Headers.Authorization.ToString();
        // Public calls only: the internal listener takes the platform operator instead.
        if (terraBase.AuthIssuer is null || Listeners.ArrivedOn(Context) != TerraBaseListener.Client
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        // TerraBase starts without waiting for ts-auth: a token that arrives before the keys load joins the running fetch,
        // and fails closed while ts-auth does not answer. Requests never start a fetch here.
        if ((issuerKeys.Keys ?? await issuerKeys.JoinAsync(Context.RequestAborted).ConfigureAwait(false)) is not { } keys)
        {
            return AuthenticateResult.Fail("ts-auth signing keys are not loaded");
        }

        var token = header["Bearer ".Length..].Trim();
        var cacheKey = ValidatedTokens.KeyOf(token);
        if (validated.TryGet(cacheKey, keys, out var accepted))
        {
            return Success(accepted, token);
        }

        var checkedWith = keys;
        var result = await Tokens.ValidateTokenAsync(token, Parameters(keys)).ConfigureAwait(false);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // A key ts-auth rotated in after the last fetch: one rate-limited refresh, then one more attempt.
            var refreshed = await issuerKeys.RefreshAsync(Context.RequestAborted).ConfigureAwait(false);
            if (refreshed is IReadOnlyList<SecurityKey> refreshedKeys && !ReferenceEquals(refreshedKeys, keys))
            {
                checkedWith = refreshedKeys;
                result = await Tokens.ValidateTokenAsync(token, Parameters(refreshedKeys)).ConfigureAwait(false);
            }
        }

        if (!result.IsValid || result.SecurityToken is not JsonWebToken jwt)
        {
            // The failure kind only, never the token.
            return AuthenticateResult.Fail(result.Exception?.GetType().Name ?? "invalid token");
        }

        var user = jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Sub, out var sub) ? sub : null;
        if (!EntityIds.IsValid(user))
        {
            return AuthenticateResult.Fail("the token's sub must be a canonical uppercase ULID");
        }

        if (jwt.TryGetPayloadValue<bool>("is_anonymous", out var anonymous) && anonymous)
        {
            // ts-auth guests sign in without an account; they must not own organizations.
            return AuthenticateResult.Fail("guest tokens are not accepted");
        }

        List<Claim> claims = [new(ClaimTypes.NameIdentifier, user)];
        var endUser = jwt.TryGetPayloadValue<string>("token_kind", out var kind) && kind == "end_user";
        if (jwt.TryGetPayloadValue<object>("token_kind", out _) && !endUser
            || jwt.TryGetPayloadValue<object>("instance_id", out _) && !endUser)
        {
            return AuthenticateResult.Fail("invalid token kind");
        }
        foreach (var name in new[] { "project_id", "org_id", "database", "scope", "key_cidrs", "project_cidrs", "key_id", "key_gen" })
        {
            if (jwt.TryGetPayloadValue<object>(name, out var claim) && claim is not string)
            {
                return AuthenticateResult.Fail("project access claims must be strings");
            }
        }

        if (jwt.TryGetPayloadValue<string>("project_id", out var project))
        {
            if (!EntityIds.IsValid(project)
                || jwt.Alg != SecurityAlgorithms.EcdsaSha256 || string.IsNullOrEmpty(jwt.Kid))
            {
                return AuthenticateResult.Fail("invalid project access token");
            }

            claims.Add(new Claim("project_id", project));
            if (jwt.TryGetPayloadValue<string>("org_id", out var organization))
            {
                if (!EntityIds.IsValid(organization))
                {
                    return AuthenticateResult.Fail("invalid project access token");
                }

                claims.Add(new Claim("org_id", organization));
            }
        }

        if (endUser)
        {
            // Inspect JSON types before IdentityModel can coerce a role or linkage value to a string.
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(jwt.EncodedPayload));
            if (project is null || !payload.RootElement.TryGetProperty("instance_id", out var instance)
                || instance.ValueKind != JsonValueKind.String || instance.GetString() != project
                || payload.RootElement.TryGetProperty("scope", out _) || payload.RootElement.TryGetProperty("database", out _)
                || payload.RootElement.TryGetProperty(Platform.PlatformExplore.RoleClaim, out _)
                || payload.RootElement.TryGetProperty("is_anonymous", out var guest) && guest.ValueKind != JsonValueKind.False
                || !payload.RootElement.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array
                || roles.GetArrayLength() > 64 || roles.EnumerateArray().Any(role => role.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(role.GetString()) || role.GetString()!.Length > 128 || role.GetString()!.Any(char.IsControl)))
            {
                return AuthenticateResult.Fail("invalid end-user project linkage or roles");
            }

            claims.Add(new Claim("token_kind", "end_user"));
            claims.AddRange(roles.EnumerateArray().Select(role => role.GetString()!).Distinct(StringComparer.Ordinal).Select(role => new Claim("roles", role)));
            // These admit only the document pipeline; collection rules decide every data operation.
            claims.Add(new Claim("scope", "documents:read"));
            claims.Add(new Claim("scope", "documents:write"));
        }

        if (jwt.TryGetPayloadValue<string>("database", out var database))
        {
            // Only the immutable ULID narrows a token: a name could later belong to another database.
            if (!EntityIds.IsValid(database))
            {
                return AuthenticateResult.Fail("invalid database claim");
            }

            claims.Add(new Claim("database", database));
        }

        if (jwt.TryGetPayloadValue<string>("scope", out var scope))
        {
            if (scope.Length > 4096)
            {
                return AuthenticateResult.Fail("scope exceeds its size limit");
            }

            claims.AddRange(scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)
                .Select(value => new Claim("scope", value)));
        }

        List<IPNetwork[]> allowlists = [];
        foreach (var name in IpAllowlists.Claims)
        {
            if (jwt.TryGetPayloadValue<string>(name, out var cidrs))
            {
                if (project is null || endUser || IpAllowlists.Parse(cidrs) is not { } networks)
                {
                    return AuthenticateResult.Fail("invalid IP allowlist claim");
                }

                allowlists.Add(networks);
            }
        }

        // An API key's token names its key and key string; the project check asks ts-auth whether both still act.
        (string Id, string Generation)? key = null;
        var hasKey = jwt.TryGetPayloadValue<string>("key_id", out var keyId);
        if (hasKey | jwt.TryGetPayloadValue<string>("key_gen", out var generation))
        {
            if (!hasKey || keyId != user || endUser || !KeyToken.IsGeneration(generation))
            {
                return AuthenticateResult.Fail("invalid API key claims");
            }

            key = (keyId, generation);
        }

        // A platform role rides only on a user's own sign-in token, never on a project token.
        if (project is null && jwt.TryGetPayloadValue<string>(Platform.PlatformExplore.RoleClaim, out var role) && role == Platform.PlatformExplore.AdminRole)
        {
            claims.Add(new Claim(Platform.PlatformExplore.RoleClaim, role));
        }

        // Exactly the list the signature was checked against: a later refresh replaces it and so retires the entry.
        scope = endUser ? "documents:read documents:write" : scope;
        accepted = new ValidatedTokens.Accepted(
            checkedWith, jwt.ValidTo, [.. claims.Select(claim => (claim.Type, claim.Value))], project, database, scope, allowlists, key);
        validated.Add(cacheKey, accepted);
        return Success(accepted, token);
    }

    private AuthenticateResult Success(ValidatedTokens.Accepted accepted, string token)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(accepted.Claims.Select(claim => new Claim(claim.Type, claim.Value)), SchemeName));
        Context.Features.Set(new ProjectToken(accepted.Project, accepted.Database, accepted.Scope, new DateTimeOffset(accepted.ValidTo, TimeSpan.Zero), accepted.Allowlists,
            accepted.Key is var (id, generation) ? new KeyToken(id, generation, accepted.Project, token) : null));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private TokenValidationParameters Parameters(IReadOnlyList<SecurityKey> keys) => Parameters(terraBase, keys, time);

    /// <summary>How every ts-auth access token is validated, here and for <see cref="TsAuthKeyExchange"/>.</summary>
    internal static TokenValidationParameters Parameters(TerraBaseOptions terraBase, IReadOnlyList<SecurityKey> keys, TimeProvider? time) => new()
    {
        ValidIssuer = terraBase.AuthIssuer,
        ValidAudiences = terraBase.AuthAudiences,
        // aud values are compared as exact strings, like iss: "https://terrabase.test/" is another audience.
        IgnoreTrailingSlashWhenValidatingAudience = false,
        IssuerSigningKeys = keys,
        // The kid must name a cached key; an unknown one triggers the refresh above instead of trying every key.
        TryAllIssuerSigningKeys = false,
        ValidAlgorithms = Algorithms,
        ValidTypes = Types,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = ClockSkew,
        LifetimeValidator = (notBefore, expires, _, _) =>
        {
            var now = (time ?? TimeProvider.System).GetUtcNow().UtcDateTime;
            return expires is { } end && now < end
                && (notBefore is not { } start || start <= end && start <= now + ClockSkew);
        },
    };
}
