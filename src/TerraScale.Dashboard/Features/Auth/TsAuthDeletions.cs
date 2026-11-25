using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public interface ITsAuthDeletions
    {
        Task<IReadOnlyList<TsAuthDeletionItem>> ListAsync(CancellationToken cancellationToken);
        Task AcknowledgeAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway : ITsAuthDeletions
    {
        public async Task<IReadOnlyList<TsAuthDeletionItem>> ListAsync(CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            return await PlatformAsync(() => Client().Platform.ListDeletionsAsync(credentials.AuthClientId, credentials.AuthClientSecret, cancellationToken));
        }

        public async Task AcknowledgeAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            await PlatformAsync(() => Client().Platform.AcknowledgeDeletionAsync(credentials.AuthClientId, credentials.AuthClientSecret, proof, cancellationToken));
        }
    }
}
