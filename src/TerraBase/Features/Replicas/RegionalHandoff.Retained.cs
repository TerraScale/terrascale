using System.Net;
using TerraBase.Hosting;
using TerraBase.Engine;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionalHandoff
{
    internal async Task<IDisposable?> EnterRetainedCatalogAsync(RegionalOwner expected, CancellationToken token)
    {
        await _changing.WaitAsync(token).ConfigureAwait(false);
        if (owners.Find(options.Region) != expected || owners.Writer)
        {
            _changing.Release();
            return null;
        }
        return new RetainedCatalogLease(_changing);
    }

    private sealed class RetainedCatalogLease(SemaphoreSlim changing) : IDisposable
    {
        public void Dispose() => changing.Release();
    }

    internal async Task<RetainedPage?> RetainedPageAsync(string? after, IPAddress? caller, CancellationToken token)
    {
        if (!owners.Writer)
        {
            return null;
        }
        var nodes = await new PeersFile(options).ListAsync(token).ConfigureAwait(false);
        if (!nodes.Any(node => node.Name != options.MachineId && node.Region == options.Region && node.Address.Equals(caller)))
        {
            return null;
        }
        using var bulk = StorageWork.Bulk();
        var page = await RegionalRetention.RecordsAsync(services.GetRequiredService<IStorageEngine>(), after, token).ConfigureAwait(false);
        var entries = page.Records.Select(RegionalRetention.Read)
            .Where(record => record.Database.Home(options.Region) == options.Region)
            .Select(record => new RetainedEntry(record, record.Revision)).ToArray();
        return new RetainedPage(entries, page.HasMore ? page.Records[^1].Id : null);
    }

    private async Task<bool> CertifyRetainedAsync(RetainedManifest manifest, CancellationToken token)
    {
        var proof = await services.GetRequiredService<RetainedReconciler>().CertifyAsync(token).ConfigureAwait(false);
        return proof.Missing == 0 && proof.Count == manifest.Count && proof.Digest == manifest.Digest;
    }
}
