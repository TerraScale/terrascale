using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace TerraScale.TerraBase.Features.ApiKeys;

/// <summary>Authenticates <c>Authorization: Bearer &lt;api key&gt;</c> and emits one <c>scope</c> claim per granted scope.</summary>
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

        var key = await keys.FindAsync(header["Bearer ".Length..].Trim(), Context.RequestAborted).ConfigureAwait(false);
        if (key is null)
        {
            // Never echo the presented secret.
            return AuthenticateResult.Fail("unknown API key");
        }

        Claim[] claims = [new(ClaimTypes.NameIdentifier, key.Id), .. key.Scopes.Select(scope => new Claim(ScopeClaim, scope))];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }
}
