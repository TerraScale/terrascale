using TerraScale.DotNet;

namespace TerraScale.Dashboard.Features.Auth;

public interface ITsAuthDeletions
{
    Task<IReadOnlyList<TerraScaleAuthDeletionProof>> ListAsync(CancellationToken cancellationToken);
    Task AcknowledgeAsync(TerraScaleAuthDeletionProof proof, CancellationToken cancellationToken);
}

public sealed partial class TsAuthGateway : ITsAuthDeletions
{
    public async Task<IReadOnlyList<TerraScaleAuthDeletionProof>> ListAsync(CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        var page = await PlatformAsync(() => Client().Platform.ListDeletionsAsync(credentials.AuthClientId, credentials.AuthClientSecret, cancellationToken));
        return page.Items ?? throw new IOException("ts-auth returned an invalid deletion page.");
    }

    public async Task AcknowledgeAsync(TerraScaleAuthDeletionProof proof, CancellationToken cancellationToken)
    {
        var credentials = await identity.ReadAsync(cancellationToken);
        await PlatformAsync(() => Client().Platform.AcknowledgeDeletionAsync(credentials.AuthClientId, credentials.AuthClientSecret, proof, cancellationToken));
    }
}
