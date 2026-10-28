using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

// The SDK sends confirmation requests once. An uncertain consume answer never
// authorizes local effects or an automatic retry with the consumed proof.
public sealed partial class TsAuthGateway : IAuthStepUpGateway
{
    public async Task<StepUpChallenge> BeginAsync(SessionRecord session, string purpose, string audience, string returnUri, CancellationToken cancellationToken)
    {
        var challenge = await PlatformAsync(() => Client().Platform.BeginStepUpAsync(Bearer(session),
            new TerraScaleAuthStepUpRequest(purpose, audience, returnUri), cancellationToken));
        return new(challenge.Proof, challenge.StepUpUrl, challenge.ExpiresAt);
    }

    public Task<string?> ConsumeAsync(SessionRecord session, string proof, string purpose, string audience, string? operationReceipt, CancellationToken cancellationToken) =>
        PlatformAsync(() => Client().Platform.ConsumeStepUpAsync(Bearer(session),
            new TerraScaleAuthStepUpConsume(proof, purpose, audience, operationReceipt), cancellationToken));
}
