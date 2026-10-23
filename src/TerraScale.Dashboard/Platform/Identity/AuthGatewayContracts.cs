namespace TerraScale.Dashboard.Platform.Identity;

public interface IAuthGateway
{
    Task<SessionRecord> PasswordAsync(string email, string password, string ip, CancellationToken cancellationToken);
    Task<SessionRecord> EmailCodeAsync(string email, string code, string ip, CancellationToken cancellationToken);
    Task StartEmailCodeAsync(string email, string ip, CancellationToken cancellationToken);
    Task StartPasswordResetAsync(string email, string ip, CancellationToken cancellationToken);
    Task CompletePasswordResetAsync(string email, string code, string password, string ip, CancellationToken cancellationToken);
    Task<bool> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken);
    Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken);
}

public interface IAuthRefreshGateway
{
    Task<SessionRecord> RefreshAsync(SessionRecord session, CancellationToken cancellationToken);
}

public sealed class SignInRejectedException : Exception;

// ts-auth answered the refresh without reading its secret (unavailable or rate
// limited), or the request never left: the same secret may be sent again.
public sealed class RefreshNotStartedException(Exception inner) : IOException("ts-auth did not start the refresh.", inner);
