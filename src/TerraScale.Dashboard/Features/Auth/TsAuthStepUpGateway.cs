using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public sealed partial class TsAuthGateway : IAuthStepUpGateway
    {
        public async Task<StepUpChallenge> BeginAsync(SessionRecord session, string purpose, string audience, string? organizationId, string returnUri, CancellationToken cancellationToken)
        {
            var challenge = await PlatformAsync(() => Client().Platform.BeginStepUpAsync(Bearer(session),
                new TsAuthStepUpRequest(purpose, audience, returnUri, organizationId), cancellationToken));
            return new(challenge.Proof, challenge.StepUpUrl, challenge.ExpiresAt);
        }

        public Task<string?> ConsumeAsync(SessionRecord session, string proof, string purpose, string audience, string? operationReceipt, string? organizationId, CancellationToken cancellationToken)
        {
            return PlatformAsync(() => Client().Platform.ConsumeStepUpAsync(Bearer(session),
                new TsAuthStepUpConsume(proof, purpose, audience, operationReceipt, organizationId), cancellationToken));
        }
    }
}
