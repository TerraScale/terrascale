using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Primitives;

namespace TerraBase.Features.UserAuth;

/// <summary>
/// Authenticates the management service credential, <c>Authorization: Bearer &lt;secret&gt;</c>, acting as the user named
/// in <see cref="UserHeader"/>. It exists for bootstrap before ts-auth runs (TS-529), so it is accepted only on the
/// loopback administrative listener, only when <c>management-credential-file</c> is set, and every use is logged.
/// </summary>
internal sealed partial class ManagementCredentialHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    TerraBaseOptions terraBase) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = UserAuthExtensions.LegacyServiceScheme;

    /// <summary>The acting user, trusted only together with the credential.</summary>
    public const string UserHeader = "X-TerraBase-User-Id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var expected = terraBase.ManagementCredentialHash;
        var header = Request.Headers.Authorization.ToString();
        // Never on the public client listener, whatever is presented there.
        if (expected.IsEmpty || Listeners.ArrivedOn(Context) != TerraBaseListener.Admin
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        // Comparing fixed-length hashes in constant time reveals neither the secret nor its length.
        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header["Bearer ".Length..].Trim()));
        if (!CryptographicOperations.FixedTimeEquals(presented, expected.Span))
        {
            LogRejected(Logger, Request.Method, Request.Path.Value);
            return Task.FromResult(AuthenticateResult.Fail("invalid management credential"));
        }

        var values = Request.Headers[UserHeader];
        var user = values.Count == 1 ? values[0] : null;
        if (!EntityIds.IsValid(user))
        {
            return Task.FromResult(AuthenticateResult.Fail(UserHeader + " must be one canonical uppercase ULID"));
        }

        // The audit trail of the credential: who it acted as and what it did, never the secret.
        LogUsed(Logger, user, Request.Method, Request.Path.Value);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user)], SchemeName));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Management service credential acted as user {UserId}: {Method} {Path}")]
    private static partial void LogUsed(ILogger logger, string userId, string method, string? path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Management service credential rejected: {Method} {Path}")]
    private static partial void LogRejected(ILogger logger, string method, string? path);
}
