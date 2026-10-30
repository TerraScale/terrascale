using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public interface IAuthEmailChangeGateway
    {
        Task<TsAuthEmailChange> RequestEmailChangeAsync(SessionRecord session, string email, string proof, CancellationToken cancellationToken);
        Task ConfirmEmailChangeAsync(SessionRecord session, string code, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway : IAuthEmailChangeGateway
    {
        public Task<TsAuthEmailChange> RequestEmailChangeAsync(SessionRecord session, string email, string proof, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.RequestEmailChangeAsync(Bearer(session), new(email), proof, cancellationToken));
        }

        public Task ConfirmEmailChangeAsync(SessionRecord session, string code, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.ConfirmEmailChangeAsync(Bearer(session), new(code), cancellationToken));
        }
    }
}
