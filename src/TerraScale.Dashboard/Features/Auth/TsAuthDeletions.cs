using TerraScale.Dashboard.Platform.TsAuth;

namespace TerraScale.Dashboard.Features.Auth
{
    public interface ITsAuthDeletions
    {
        Task<IReadOnlyList<TsAuthDeletionItem>> ListAsync(bool peek, CancellationToken cancellationToken);
        Task AcknowledgeAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken);
    }

    public sealed partial class TsAuthGateway : ITsAuthDeletions
    {
        public async Task<IReadOnlyList<TsAuthDeletionItem>> ListAsync(bool peek, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            return await PlatformAsync(() => Client().Platform.ListDeletionsAsync(credentials.AuthClientId, credentials.AuthClientSecret, peek, cancellationToken));
        }

        public async Task AcknowledgeAsync(TsAuthDeletionProof proof, CancellationToken cancellationToken)
        {
            var credentials = await identity.ReadAsync(cancellationToken);
            await PlatformAsync(() => Client().Platform.AcknowledgeDeletionAsync(credentials.AuthClientId, credentials.AuthClientSecret, proof, cancellationToken));
        }
    }
}
