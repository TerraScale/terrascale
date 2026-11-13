using TerraBase.Engine;
using TerraBase.Features.Replicas;

namespace TerraBase.Features.Management;

internal sealed class RetentionAuthority(HomeLeaseGate gate, RegionalOwners owners, RegionalIngress ingress)
{
    private readonly SemaphoreSlim[] _copies = [.. Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1))];

    internal bool Writer => owners.Writer;

    internal async Task<IDisposable> LockCopyAsync(string database, CancellationToken token)
    {
        var held = _copies[(uint)StringComparer.Ordinal.GetHashCode(database) % (uint)_copies.Length];
        await held.WaitAsync(token).ConfigureAwait(false);
        return new CopyLease(held);
    }

    private sealed class CopyLease(SemaphoreSlim held) : IDisposable
    {
        public void Dispose() => held.Release();
    }

    internal async Task<Lease?> EnterAsync(string database, CancellationToken token)
    {
        var ingressLease = await ingress.EnterAsync(parkWhileHeld: false, token).ConfigureAwait(false);
        if (ingressLease is null)
        {
            return null;
        }
        var semaphore = _copies[(uint)StringComparer.Ordinal.GetHashCode(database) % (uint)_copies.Length];
        if (!await semaphore.WaitAsync(0, token).ConfigureAwait(false))
        {
            ingressLease.Dispose();
            return null;
        }
        IDisposable copyLease = new CopyLease(semaphore);
        var epoch = gate.Epoch;
        if (!owners.Writer || !gate.Holds(database))
        {
            copyLease.Dispose();
            ingressLease.Dispose();
            return null;
        }
        return new Lease(gate, database, epoch, ingressLease, copyLease);
    }

    internal sealed class Lease(HomeLeaseGate gate, string database, long epoch, RegionalIngress.Lease ingressLease, IDisposable copyLease) : IDisposable
    {
        private int _disposed;

        internal void Fence(IStorageTransaction transaction)
        {
            gate.ThrowIfUnleased(database);
            transaction.FenceRegionalWriter(epoch);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                copyLease.Dispose();
                ingressLease.Dispose();
            }
        }
    }
}
