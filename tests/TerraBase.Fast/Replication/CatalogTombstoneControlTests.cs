using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Engine;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Errors;
using TerraBase.Features.Management;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using TerraBase.Testing;

namespace TerraBase.Fast.Replication;

/// <summary>
/// The control test of docs/terrabase/system-catalog-invariants.md: two machines that share no peer streams, so every catalog
/// change crosses only when the test ships it (the same source, sink and replica-state calls the streams and a refill make),
/// on fake clocks, in a fixed order, without waiting. A database is removed in gru while sjc, away, holds a stale queued
/// change of it and creates a database of its own; a day later gru runs its retention and garbage-collection pass, and
/// sjc rejoins through a refill plus its queued groups. The removed database must stay removed everywhere (tombstone kept,
/// id still taken, sjc's file deleted) and the live one must survive. With the catalog's garbage collection on (a horizon
/// for _system) it fails: gru collects the tombstone, the refill makes sjc forget its record, sjc's file stays and the
/// removed id can be created again; with the removed database's catalog state purged instead of tombstoned, the repair from
/// sjc brings it back into gru.
/// </summary>
public sealed class CatalogTombstoneControlTests
{
    private static readonly TenantContext System = ChangeGroups.System;
    private static readonly string[] Sjc = ["sjc"];
    private static readonly string[] Gru = ["gru"];

    [Test]
    public async Task A_removed_database_stays_removed_after_garbage_collection_time_a_refill_and_a_stale_change_and_a_live_one_survives()
    {
        var start = DateTimeOffset.UtcNow;
        await using var gru = await Machine.StartAsync("gru", start);
        await using var sjc = await Machine.StartAsync("sjc", start);
        var user = gru.Api.User;
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var (removed, kept) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(user, HttpStatusCode.OK,
            new { op = "create_organization", id = organization, name = "Control" },
            new { op = "create_project", organization, id = project, name = "p" },
            new { op = "create_database", organization, project, name = "removed", id = removed, replica_regions = Sjc },
            new { op = "create_database", organization, project, name = "kept", id = kept, replica_regions = Sjc });
        var state = SystemCatalog.DatabaseState(removed) + "backup_policy";
        await gru.PutAsync(state, "current", """{"days":7}""");
        await gru.ShipToAsync(sjc);
        var removedDatabase = new TenantContext(organization, PlanTier.Hobby, project, removed);
        await Assert.That(await sjc.Tenants.EnsureLocalDatabaseAsync(removedDatabase, CancellationToken.None) is Done).IsTrue();
        await Assert.That(File.Exists(sjc.FileOf(removedDatabase))).IsTrue();

        // sjc is away (nothing ships). It changes the removed database's record (queued in its CDC, older than the removal)
        // and creates a database of its own.
        sjc.Advance(TimeSpan.FromSeconds(1));
        await sjc.PatchRecordAsync(project, removed);
        var madeAway = EntityIds.New().ToString();
        await sjc.CommitAsync(user, HttpStatusCode.OK,
            new { op = "create_database", organization, project, name = "made-away", id = madeAway, replica_regions = Gru });

        // gru removes the database, later than sjc's change, and deletes its own file.
        gru.Advance(TimeSpan.FromSeconds(10));
        await gru.CommitAsync(user, HttpStatusCode.OK, new { op = "set_database_regions", organization, project, database = removed, replica_regions = Array.Empty<string>() });
        await gru.CommitAsync(user, HttpStatusCode.OK, new { op = "delete_database", organization, project, database = removed });
        await Assert.That(File.Exists(gru.FileOf(removedDatabase))).IsFalse();

        // A day later gru runs its retention and garbage-collection pass over the catalog.
        gru.Advance(TimeSpan.FromHours(25));
        sjc.Advance(TimeSpan.FromHours(25));
        await gru.TrimAsync();

        // sjc rejoins: gru's refill (a copy, with gru's horizon), sjc's queued groups to gru (stale now), gru's groups to sjc,
        // and an anti-entropy repair from sjc into gru: sjc still holds the removed database's catalog state as it was.
        await gru.RefillAsync(sjc);
        await sjc.ShipToAsync(gru);
        await gru.ShipToAsync(sjc);
        await sjc.RepairIntoAsync(gru);
        await gru.ConvergeAsync();
        await sjc.ConvergeAsync();

        foreach (var machine in new[] { gru, sjc })
        {
            // Removed: a tombstone, not a live record, no name, not served, and its id stays taken.
            await Assert.That(await machine.VersionAsync($"projects/{project}/databases", removed) is { Deleted: true }).IsTrue().Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/databases", removed)).IsNull().Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/database_names", "removed")).IsNull().Because(machine.Region);
            await Assert.That(await machine.Tenants.RegisteredAsync(removedDatabase, CancellationToken.None) is false).IsTrue().Because(machine.Region);
            await Assert.That(File.Exists(machine.FileOf(removedDatabase))).IsFalse().Because(machine.Region);
            await Assert.That(await machine.ReadAsync(state, "current")).IsNull().Because(machine.Region);

            // Live: the database kept from before and the one sjc created while away.
            await Assert.That(await machine.ReadAsync($"projects/{project}/database_names", "kept")).IsEqualTo($"\"{kept}\"").Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/database_names", "made-away")).IsEqualTo($"\"{madeAway}\"").Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/databases", madeAway)).IsNotNull().Because(machine.Region);
        }

        await gru.CommitAsync(user, HttpStatusCode.Conflict, new { op = "create_database", organization, project, name = "again", id = removed });
    }

    /// <summary>
    /// A restore of a machine's catalog file (section 2 of the invariants doc): gru's _system goes back to a copy taken before
    /// it deleted one database and created another. Until a repair from a peer, gru reads the deleted database live again and
    /// lacks the new one: the window the doc states. A stale group from sjc that carries gru's own lost entry (sjc's later
    /// change of the new database's record) is joined, not dropped: the catalog never collects, so a document gru lacks was
    /// never a collected deletion. The repair then brings back the deletion, and nothing removed comes back. With the
    /// drop-stale-change rule applied to _system as well, sjc's change is lost until that repair.
    /// </summary>
    [Test]
    public async Task A_restored_catalog_reads_its_lost_deletion_live_until_a_repair_and_joins_a_stale_change_of_its_lost_entry()
    {
        var start = DateTimeOffset.UtcNow;
        await using var gru = await Machine.StartAsync("gru", start);
        await using var sjc = await Machine.StartAsync("sjc", start);
        var user = gru.Api.User;
        var (organization, project) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        var (removed, created) = (EntityIds.New().ToString(), EntityIds.New().ToString());
        await gru.CommitAsync(user, HttpStatusCode.OK,
            new { op = "create_organization", id = organization, name = "Restore" },
            new { op = "create_project", organization, id = project, name = "p" },
            new { op = "create_database", organization, project, name = "removed", id = removed });
        await gru.ShipToAsync(sjc);
        var copy = await gru.CopyCatalogAsync();

        // After the copy, gru deletes one database and creates another; sjc receives both and changes the new record (its own
        // change, queued: sjc is away from here on).
        gru.Advance(TimeSpan.FromSeconds(10));
        await gru.CommitAsync(user, HttpStatusCode.OK, new { op = "delete_database", organization, project, database = removed });
        await gru.CommitAsync(user, HttpStatusCode.OK, new { op = "create_database", organization, project, name = "created", id = created });
        await gru.ShipToAsync(sjc);
        sjc.Advance(TimeSpan.FromSeconds(20));
        await sjc.PatchRecordAsync(project, created);

        // A day later gru's catalog is restored from the copy; then sjc's queued group (stale now) reaches it.
        gru.Advance(TimeSpan.FromHours(25));
        sjc.Advance(TimeSpan.FromHours(25));
        await gru.RestoreCatalogAsync(copy);
        await Assert.That(await gru.ReadAsync($"projects/{project}/databases", removed)).IsNotNull().Because("the window: the restored copy holds the record live");
        await sjc.ShipToAsync(gru);
        await Assert.That(await gru.ReadAsync($"projects/{project}/databases", created)).Contains("\"placementVersion\":7").Because("sjc's stale change of gru's lost entry joined");
        await Assert.That(await gru.ReadAsync($"projects/{project}/databases", removed)).IsNotNull().Because("still the window: nothing carried the deletion yet");

        // The repair ends the window.
        await sjc.RepairIntoAsync(gru);
        await gru.ConvergeAsync();
        foreach (var machine in new[] { gru, sjc })
        {
            await Assert.That(await machine.VersionAsync($"projects/{project}/databases", removed) is { Deleted: true }).IsTrue().Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/database_names", "removed")).IsNull().Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/databases", created)).Contains("\"placementVersion\":7").Because(machine.Region);
            await Assert.That(await machine.ReadAsync($"projects/{project}/database_names", "created")).IsEqualTo($"\"{created}\"").Because(machine.Region);
        }

        await gru.CommitAsync(user, HttpStatusCode.Conflict, new { op = "create_database", organization, project, name = "again", id = removed });
    }

    private sealed class Machine : IAsyncDisposable
    {
        private readonly FakeTimeProvider _time;
        private readonly string _directory = Directory.CreateTempSubdirectory("terrabase-catalog-control-").FullName;
        private readonly Dictionary<ReplicaId, long> _shipped = [];

        private readonly string _nodeName = Convert.ToHexStringLower(Guid.NewGuid().ToByteArray())[..14];

        private Machine(string region, FakeTimeProvider time)
        {
            (Region, _time) = (region, time);
        }

        public string Region { get; }

        public ManagementApi Api { get; private set; } = null!;

        public ITenantDirectory Tenants => Api.Server.Services.GetRequiredService<ITenantDirectory>();

        private ReplicaId Node => Api.Server.Services.GetRequiredService<LocalReplica>().Id;

        private TursoChangeGroupSource Source => Api.Server.Services.GetRequiredService<TursoChangeGroupSource>();

        private IReplicaStates States => Api.Server.Services.GetRequiredService<IReplicaStates>();

        private IChangeGroupSink Sink => Api.Server.Services.GetRequiredService<IChangeGroupSink>();

        // A machine alone: no member directory of others, so no peer stream or anti-entropy round runs.
        public static async Task<Machine> StartAsync(string region, DateTimeOffset start)
        {
            var machine = new Machine(region, new FakeTimeProvider(start));
            await machine.StartAsync();
            return machine;
        }

        private async Task StartAsync() =>
            Api = await ManagementApi.StartAsync(settings =>
            {
                settings["region"] = Region;
                settings["node-name"] = _nodeName;
                settings["data-directory"] = _directory;
            }, time: _time);

        // A copy of this machine's catalog files (_system.db and its sidecars), taken while it is stopped.
        public async Task<string> CopyCatalogAsync()
        {
            var copy = Directory.CreateTempSubdirectory("terrabase-catalog-copy-").FullName;
            await Api.DisposeAsync();
            foreach (var file in Directory.EnumerateFiles(_directory, "_system*"))
            {
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            }

            await StartAsync();
            return copy;
        }

        // Stops this machine, puts back the catalog files of `copy` in place of its own, and starts it again.
        public async Task RestoreCatalogAsync(string copy)
        {
            await Api.DisposeAsync();
            foreach (var file in Directory.EnumerateFiles(_directory, "_system*"))
            {
                File.Delete(file);
            }

            foreach (var file in Directory.EnumerateFiles(copy))
            {
                File.Copy(file, Path.Combine(_directory, Path.GetFileName(file)));
            }

            Directory.Delete(copy, recursive: true);
            await StartAsync();
        }

        public void Advance(TimeSpan by) => _time.Advance(by);

        public string FileOf(TenantContext database) => Path.Combine(_directory, database.OrganizationId, database.ProjectId!, database.DatabaseId + ".db");

        public async Task CommitAsync(string user, HttpStatusCode expected, params object[] ops)
        {
            using var service = Api.Service(user);
            using var response = await service.PostAsJsonAsync(ManagementApi.Uri("/v1/management/commits"), new { id = EntityIds.New().ToString(), ops });
            await Assert.That(response.StatusCode).IsEqualTo(expected).Because(await response.Content.ReadAsStringAsync());
        }

        // Every catalog group of this machine's own writes not yet shipped to `to`, joined there as its streams would.
        public async Task ShipToAsync(Machine to)
        {
            var after = _shipped.GetValueOrDefault(to.Node);
            while (await Source.ReadAsync(System, after, ChangeGroups.MaxGroups, ChangeGroups.MaxBatchBytes, CancellationToken.None) is { Count: > 0 } groups)
            {
                await Assert.That(await to.Sink.ApplyAsync(System, Node, groups, CancellationToken.None)).IsNotNull();
                after = groups[^1].ChangeId;
            }

            _shipped[to.Node] = after;
        }

        // The copying refill a returning peer gets (AntiEntropy.RefillAsync): every state of every bucket joined there, then
        // what `to` holds and this machine lacks, below this machine's horizon, forgotten there.
        // An anti-entropy repair: every state of every bucket of this machine joined into `to`.
        public async Task RepairIntoAsync(Machine to)
        {
            BucketCursor? after = null;
            do
            {
                (var states, after) = await States.ReadBucketsAsync(System, [.. Enumerable.Range(0, 4096)], after, 1024, ChangeGroups.MaxBatchBytes, CancellationToken.None);
                _ = await to.Sink.RepairAsync(System, states, CancellationToken.None);
            }
            while (after is not null);
        }

        public async Task PutAsync(string collection, string id, string json)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            _ = await transaction.PutAsync(collection, id, Encoding.UTF8.GetBytes(json), CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        public async Task RefillAsync(Machine to)
        {
            var buckets = Enumerable.Range(0, 4096).ToList();
            var ours = new HashSet<(string, string)>();
            BucketCursor? after = null;
            do
            {
                (var states, after) = await States.ReadBucketsAsync(System, buckets, after, 1024, ChangeGroups.MaxBatchBytes, CancellationToken.None);
                ours.UnionWith(states.Select(state => (state.Collection, state.Id)));
                _ = await to.Sink.RepairAsync(System, states, CancellationToken.None);
            }
            while (after is not null);

            var horizon = await States.HorizonAsync(System, CancellationToken.None);
            List<(string Collection, string Id)> lacking = [];
            after = null;
            do
            {
                (var theirs, after) = await to.States.ReadBucketsAsync(System, buckets, after, 1024, ChangeGroups.MaxBatchBytes, CancellationToken.None);
                lacking.AddRange(theirs.Where(state => !ours.Contains((state.Collection, state.Id)) && state.State.MaxStamp.Value < horizon.Value)
                    .Select(state => (state.Collection, state.Id)));
            }
            while (after is not null);

            _ = await to.States.ForgetAsync(System, lacking, horizon, CancellationToken.None);
        }

        // The retention and garbage-collection pass the background loop runs every few seconds.
        public async Task TrimAsync() => await Source.TrimAsync(System, CancellationToken.None);

        public async Task ConvergeAsync() =>
            await Api.Server.Services.GetRequiredService<EngineManagementService>().ConvergeAsync(CancellationToken.None);

        // A change of a database record's placement version, as a local write (queued in this machine's CDC).
        public async Task PatchRecordAsync(string project, string database)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: true, CancellationToken.None);
            var record = JsonNode.Parse((await transaction.GetAsync($"projects/{project}/databases", database, CancellationToken.None))!.Json.Span)!.AsObject();
            var field = record.Select(entry => entry.Key).First(key => key.Contains("lacement", StringComparison.Ordinal));
            record[field] = 7;
            _ = await transaction.PatchAsync($"projects/{project}/databases", database, Encoding.UTF8.GetBytes(record.ToJsonString()),
                [new FieldOperation(FieldOperationKind.Set, field, JsonElement.Parse("7"))], CancellationToken.None);
            _ = await transaction.CommitAsync(CancellationToken.None);
        }

        public async Task<string?> ReadAsync(string collection, string id)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetAsync(collection, id, CancellationToken.None) is { } document ? Encoding.UTF8.GetString(document.Json.Span) : null;
        }

        public async Task<DocumentVersion?> VersionAsync(string collection, string id)
        {
            var store = await Api.Server.Services.GetRequiredService<IStorageEngine>().OpenSystemDatabaseAsync(CancellationToken.None);
            await using var transaction = await store.BeginTransactionAsync(write: false, CancellationToken.None);
            return await transaction.GetVersionAsync(collection, id, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync();
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A file the stopped server still held: the temporary directory is left for the OS to clean.
            }
        }
    }
}
