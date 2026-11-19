using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public partial interface ITsAuthPlatform
    {
        Task<IReadOnlyList<TsAuthAgent>> ListAccountAgentsAsync(SessionRecord session, CancellationToken cancellationToken);
        Task RevokeAccountAgentAsync(SessionRecord session, string agentId, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway
    {
        public Task<IReadOnlyList<TsAuthAgent>> ListAccountAgentsAsync(SessionRecord session, CancellationToken cancellationToken)
        {
            return PagesAsync(after => Client().Platform.ListAccountAgentsAsync(Bearer(session), after, cancellationToken));
        }

        public Task RevokeAccountAgentAsync(SessionRecord session, string agentId, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(agentId))
            {
                throw new AccountAccessException("Agent is unavailable.", StatusCodes.Status404NotFound);
            }

            return PlatformAsync(() => Client().Platform.RevokeAccountAgentAsync(Bearer(session), agentId, cancellationToken));
        }
    }
}
