using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TerraBase.Features.ApiKeys;

/// <summary>
/// Authenticates <c>Authorization: Bearer &lt;api key&gt;</c>, emits one <c>scope</c> claim per granted scope and keeps the
/// <see cref="ApiKey"/> on the request (<see cref="ApiKey.Of"/>), whose organization is the request's tenant.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IApiKeyStore keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string ScopeClaim = "scope";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // The scheme name is case-insensitive (RFC 9110); the secret is not.
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var secret = header["Bearer ".Length..].Trim();
        if (!secret.StartsWith(EngineApiKeyStore.SecretPrefix, StringComparison.Ordinal))
        {
            // Another scheme's token: leave it to that scheme instead of logging a failed API key.
            return AuthenticateResult.NoResult();
        }

        if (await keys.FindAsync(secret, Context.RequestAborted).ConfigureAwait(false) is not ApiKey key)
        {
            // Never echo the presented secret.
            return AuthenticateResult.Fail("unknown API key");
        }

        Context.Features.Set(key);
        Claim[] claims = [new(ClaimTypes.NameIdentifier, key.Id), .. key.Scopes.Select(scope => new Claim(ScopeClaim, scope))];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
