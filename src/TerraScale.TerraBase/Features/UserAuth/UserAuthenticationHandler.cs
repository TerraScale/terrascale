using System.Security.Claims;
using System.Text.Encodings.Web;
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
/// The principal carries the <c>sub</c> as <see cref="ClaimTypes.NameIdentifier"/> and, when <c>email_verified</c> is
/// true, the <c>email</c> as <see cref="ClaimTypes.Email"/>. Request headers never name the user.
/// </summary>
internal sealed class UserAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TerraBaseOptions terraBase,
    IIssuerKeys issuerKeys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = UserAuthExtensions.TokenScheme;

    /// <summary>Tolerance for clock differences with ts-auth on <c>exp</c> and <c>nbf</c>.</summary>
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
        // Public calls only: the administrative listener takes the management service credential instead.
        if (terraBase.AuthIssuer is null || Listeners.ArrivedOn(Context) != TerraBaseListener.Client
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        if (issuerKeys.Keys is not { } keys)
        {
            // Fail closed until the first fetch succeeds; requests never fetch the keys themselves.
            return AuthenticateResult.Fail("ts-auth signing keys are not loaded");
        }

        var token = header["Bearer ".Length..].Trim();
        var result = await Tokens.ValidateTokenAsync(token, Parameters(keys)).ConfigureAwait(false);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // A key ts-auth rotated in after the last fetch: one rate-limited refresh, then one more attempt.
            var refreshed = await issuerKeys.RefreshAsync(Context.RequestAborted).ConfigureAwait(false);
            if (refreshed is not null && !ReferenceEquals(refreshed, keys))
            {
                result = await Tokens.ValidateTokenAsync(token, Parameters(refreshed)).ConfigureAwait(false);
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
        foreach (var name in new[] { "project_id", "database", "scope" })
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
        }

        if (jwt.TryGetPayloadValue<string>("database", out var database))
        {
            if (!Engine.DatabaseName.TryParse(database, out _))
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

        if (jwt.TryGetPayloadValue<bool>("email_verified", out var verified) && verified
            && jwt.TryGetPayloadValue<string>(JwtRegisteredClaimNames.Email, out var email) && !string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        Context.Features.Set(new ProjectToken(
            project, database, scope,
            new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero)));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private TokenValidationParameters Parameters(IReadOnlyList<SecurityKey> keys) => new()
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
    };
}
