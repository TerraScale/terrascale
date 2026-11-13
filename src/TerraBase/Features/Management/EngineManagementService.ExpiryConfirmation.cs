using TerraBase.Configuration;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.Management;

internal sealed partial class EngineManagementService
{
    private long _expiryWaiting;
    private long _expiryPassWaiting;

    private async Task<bool> ConfirmExpiryAsync(DeletedDatabaseRecord record, CancellationToken token)
    {
        if (!retention.Writer || record.PurgeAt.AddHours(1) > time.GetUtcNow() || options.PeersFile is null)
        {
            return false;
        }
        var peers = await new PeersFile(options).ListAsync(token).ConfigureAwait(false);
        foreach (var node in peers.Where(node => node.Name != options.MachineId).OrderBy(node => node.Region == options.Region ? 0 : 1))
        {
            var answer = await remote.ConfirmExpiryAsync(node.InternalListener(options.InternalPort), record.DeletedAt, token).ConfigureAwait(false);
            if (answer is not null)
            {
                return answer.Value;
            }
        }
        return false;
    }
}
