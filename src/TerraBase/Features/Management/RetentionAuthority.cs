using TerraBase.Engine;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.Management;

internal sealed class RetentionAuthority(HomeLeaseGate gate, RegionalOwners owners, RegionalIngress ingress)
{
    internal bool Writer => owners.Writer;

    internal async Task<Lease?> EnterAsync(string database, CancellationToken token)
    {
        var ingressLease = await ingress.EnterAsync(parkWhileHeld: false, token).ConfigureAwait(false);
        if (ingressLease is null)
        {
            return null;
        }
        var epoch = gate.Epoch;
        if (!owners.Writer || !gate.Holds(database))
        {
            ingressLease.Dispose();
            return null;
        }
        return new Lease(gate, database, epoch, ingressLease);
    }

    internal sealed class Lease(HomeLeaseGate gate, string database, long epoch, RegionalIngress.Lease ingressLease) : IDisposable
    {
        internal void Fence(IStorageTransaction transaction)
        {
            gate.ThrowIfUnleased(database);
            transaction.FenceRegionalWriter(epoch);
        }

        public void Dispose() => ingressLease.Dispose();
    }
}
