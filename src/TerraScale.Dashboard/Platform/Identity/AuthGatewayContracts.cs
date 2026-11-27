using System.Net;
using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Platform.Identity
{
    public sealed record SessionRefreshRejected;

    public sealed record SessionRefreshNotStarted(string ErrorType, HttpStatusCode? StatusCode, string ErrorCode, string Message);

    public sealed record SessionRefreshFailed(string ErrorType, HttpStatusCode? StatusCode, string ErrorCode, string Message);

    public readonly union SessionRefreshOutcome(
        SessionRecord,
        SessionRefreshRejected,
        SessionRefreshNotStarted,
        SessionRefreshFailed);

    public sealed record SessionStillActive;

    public sealed record SessionEnded;

    public sealed record SessionActivityUnavailable(string ErrorType, HttpStatusCode? StatusCode, string ErrorCode, string Message);

    public readonly union SessionActivityOutcome(
        SessionStillActive,
        SessionEnded,
        SessionActivityUnavailable);

    public interface IAuthGateway
    {
        Task<SessionActivityOutcome> IsActiveAsync(SessionRecord session, CancellationToken cancellationToken);
        Task RevokeAsync(SessionRecord session, CancellationToken cancellationToken);
        Task<TokenRevocationOutcome> TryRevokeAsync(SessionRecord session, CancellationToken cancellationToken);
    }

    public interface IAuthRefreshGateway
    {
        Task<SessionRefreshOutcome> TryRefreshAsync(SessionRecord session, TimeSpan timeout, CancellationToken cancellationToken);
    }

    public sealed class SignInRejectedException : Exception;
}
