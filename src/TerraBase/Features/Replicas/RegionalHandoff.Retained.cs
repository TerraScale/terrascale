using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using TerraBase.Configuration;
using TerraBase.Hosting;
using TerraBase.Features.HomePush;
using TerraBase.Engine;
using TerraBase.Engine.Revisions;
using TerraBase.Features.Management;
using TerraBase.Primitives;
using TerraBase.Features.Memory;

namespace TerraBase.Features.Replicas;

internal sealed partial class RegionalHandoff
{
    internal async Task<RetainedPage?> RetainedPageAsync(string operation, long epoch, string? after, IPAddress? caller, CancellationToken token)
    {
        if (_kv is not { } kv || await ReadAsync(kv, options.Region, token).ConfigureAwait(false) is not { Value: { } value })
        {
            return null;
        }
        var owner = Decode(value);
        if (owner is not { Transfer: { Phase: Preparing } transfer } || owner.Node != options.MachineId
            || owner.Epoch != epoch || transfer.Id != operation)
        {
            return null;
        }
        var nodes = await new PeersFile(options).ListAsync(token).ConfigureAwait(false);
        if (!nodes.Any(node => node.Name == transfer.Target && node.Region == options.Region && node.Address.Equals(caller)))
        {
            return null;
        }
        var engine = services.GetRequiredService<IStorageEngine>();
        var revisions = services.GetRequiredService<IDatabaseRevisions>();
        var page = await RegionalRetention.RecordsAsync(engine, after, token).ConfigureAwait(false);
        List<RetainedEntry> entries = [];
        foreach (var stored in page.Records)
        {
            var record = RegionalRetention.Read(stored);
            if (record.Database.Home(options.Region) != options.Region || !engine.HasDatabaseFiles(record.Storage, token))
            {
                return null;
            }
            _ = await revisions.DrainWritersAsync(record.Storage, token).ConfigureAwait(false);
            if (await revisions.CurrentRevisionAsync(record.Storage, token).ConfigureAwait(false) is not { } position)
            {
                return null;
            }
            entries.Add(new RetainedEntry(record, position.ToString()));
        }
        return new RetainedPage(entries, page.HasMore ? page.Records[^1].Id : null);
    }

    private async Task<bool> ReceiveRetainedAsync(RegionalPrepare request, CancellationToken token)
    {
        var manifest = request.Retained!;
        if (manifest.Count is < 0 or > RegionalRetention.MaxDatabases || manifest.Digest.Length != 64)
        {
            return false;
        }
        var nodes = await new PeersFile(options).ListAsync(token).ConfigureAwait(false);
        var source = nodes.Single(node => node.Name == request.Source && node.Region == options.Region).InternalListener(options.InternalPort);
        var engine = services.GetRequiredService<IStorageEngine>();
        var store = await engine.OpenSystemDatabaseAsync(token).ConfigureAwait(false);
        await using (var clear = await store.BeginDurableTransactionAsync(token).ConfigureAwait(false))
        {
            _ = await clear.DropKindAsync(DeletedDatabases.Kind, token).ConfigureAwait(false);
            _ = await clear.DeleteKindsAsync(DeletedDatabases.Kind + "/", token).ConfigureAwait(false);
            await clear.CommitAsync(token).ConfigureAwait(false);
        }
        var replicas = services.GetRequiredService<IDatabaseReplicas>();
        var replication = services.GetRequiredService<RegionReplication>();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var count = 0;
        string? after = null;
        do
        {
            if (!await PreparingAsync(request, token).ConfigureAwait(false))
            {
                return false;
            }
            var address = new Uri(source, "/v1/rollout/retained?operation=" + Uri.EscapeDataString(request.Operation!)
                + "&epoch=" + request.Epoch.ToString(CultureInfo.InvariantCulture)
                + (after is null ? "" : "&after=" + Uri.EscapeDataString(after)));
            using var response = await _http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > RegionalRetention.PageBytes * 4)
            {
                return false;
            }
            using var reservation = services.GetRequiredService<IMemoryBudget>().TryReserve(RegionalRetention.PageBytes * 4, tenant: null);
            if (reservation is null)
            {
                return false;
            }
            await response.Content.LoadIntoBufferAsync(RegionalRetention.PageBytes * 4, token).ConfigureAwait(false);
            var page = await response.Content.ReadFromJsonAsync(ReplicasJsonContext.Default.RetainedPage, token).ConfigureAwait(false);
            if (page is null || page.Entries.Count > RegionalRetention.PageSize)
            {
                return false;
            }
            foreach (var entry in page.Entries)
            {
                var record = entry.Record;
                var database = record.Storage;
                if (++count > manifest.Count || string.CompareOrdinal(after, record.Database.Id) >= 0
                    || database.DatabaseId != record.Database.Id || database.ProjectId != record.Database.ProjectId
                    || record.Database.Home(options.Region) != options.Region
                    || !DatabaseRevision.TryParse(entry.Revision, out var expected))
                {
                    return false;
                }
                RegionalRetention.Append(hash, record.Database.Id);
                after = record.Database.Id;
                await services.GetRequiredService<ITransactionCrypto>().PrepareAsync(database, token).ConfigureAwait(false);
                if (_prepared.GetValueOrDefault(database.DatabaseId!) != entry.Revision)
                {
                    await replicas.OpenAsync(database, new Uri(source, ReplicaRoutes.DatabasePath(database)), _http, token).ConfigureAwait(false);
                    _ = await replicas.PullAsync(database, fromHome: true, token).ConfigureAwait(false);
                    if (replicas.HeldRevision(database) is not { } actual || actual.Incarnation != expected.Incarnation
                        || DatabaseRevision.Compare(actual, expected) < 0)
                    {
                        return false;
                    }
                    await replication.SuspendAsync(database).ConfigureAwait(false);
                    _promoted[database.DatabaseId!] = database;
                    var detached = await replicas.DetachAsync(database, token).ConfigureAwait(false);
                    if (detached is null)
                    {
                        return false;
                    }
                    _ = await engine.DeleteDatabaseAsync(database, token).ConfigureAwait(false);
                    await services.GetRequiredService<IWalBackup>().ImportDatabaseAsync(database, detached, token).ConfigureAwait(false);
                    _prepared[database.DatabaseId!] = entry.Revision;
                }
                await using var transaction = await store.BeginDurableTransactionAsync(token).ConfigureAwait(false);
                var json = EntityJson(record);
                await transaction.PutAsync(DeletedDatabases.Kind, record.Database.Id, json, token).ConfigureAwait(false);
                await transaction.PutAsync(DeletedDatabases.Project(record.Database.ProjectId), record.Database.Id, json, token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
            }
            if (page.Next is null)
            {
                break;
            }
            if (page.Entries.Count == 0 || page.Next != after)
            {
                return false;
            }
        }
        while (true);
        return count == manifest.Count && Convert.ToHexStringLower(hash.GetHashAndReset()) == manifest.Digest
            && await PreparingAsync(request, token).ConfigureAwait(false);
    }

    private static ReadOnlyMemory<byte> EntityJson(DeletedDatabaseRecord record) =>
        EntityPayload.WithoutId(JsonSerializer.SerializeToUtf8Bytes(record, ManagementJsonContext.Default.DeletedDatabaseRecord), record.Database.Id, nested: true);
}
