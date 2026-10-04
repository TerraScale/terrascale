using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TerraScale.TerraBase.Configuration;

namespace TerraScale.TerraBase.Features.Management;

/// <summary>
/// Authenticates the ts-dashboard backend by its service credential, <c>Authorization: Bearer &lt;secret&gt;</c>.
/// The acting user comes from <see cref="ManagementRoute.UserHeader"/> and is authorized per request by
/// <see cref="IManagementService"/>, not here.
/// </summary>
internal sealed class DashboardAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TerraBaseOptions terraBase) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Dashboard";
    public const string PolicyName = "dashboard";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = terraBase.DashboardCredentialHash;
        var header = Request.Headers.Authorization.ToString();
        if (expected.IsEmpty || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Comparing fixed-length hashes in constant time reveals neither the secret nor its length.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        if (!CryptographicOperations.FixedTimeEquals(presented, expected.Span))
        {
            return Task.FromResult(AuthenticateResult.Fail("invalid dashboard credential"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ts-dashboard")], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }
}
