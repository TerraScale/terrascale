namespace TerraScale.Dashboard.Platform.Identity
{
    public interface IAuthGateway
    {
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
}
