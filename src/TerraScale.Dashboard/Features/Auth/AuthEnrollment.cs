namespace TerraScale.Dashboard.Features.Auth;

public interface IAuthEnrollmentGateway
{
    Task<string> SignUpAsync(string email, string password, string ip, CancellationToken cancellationToken);
    Task<SessionRecord> VerifySignUpAsync(string email, string code, string pendingToken, string ip, CancellationToken cancellationToken);
}

public interface IAuthOidcGateway
{
    Task<string> AuthorizationUrlAsync(OidcFlow flow, bool promptLogin, CancellationToken cancellationToken);
    Task<SessionRecord> ExchangeAsync(string code, OidcFlow flow, CancellationToken cancellationToken);
}

public interface IAuthRefreshGateway
{
    Task<SessionRecord> RefreshAsync(SessionRecord session, CancellationToken cancellationToken);
}

public interface IAuthLogoutGateway
{
    Task<string> EndSessionUrlAsync(string dashboardOrigin, CancellationToken cancellationToken);
}

public sealed class AuthRequestRejectedException(string message, string field = "", int statusCode = StatusCodes.Status422UnprocessableEntity, int retryAfter = 0) : Exception(message)
{
    public string Field { get; } = field;
    public int StatusCode { get; } = statusCode;
    public int RetryAfter { get; } = retryAfter;
}

public sealed record SignUpFlow(string Name, string Email, string Password, string PendingToken, string ReturnTo, DateTimeOffset ExpiresAt, DateTimeOffset ResendAt)
{
    public override string ToString() => "Dashboard signup [REDACTED]";
}

public sealed record OidcFlow(string State, string Nonce, string Verifier, string ReturnTo, string RedirectUri, DateTimeOffset ExpiresAt)
{
    public override string ToString() => "Dashboard OIDC flow [REDACTED]";
}
