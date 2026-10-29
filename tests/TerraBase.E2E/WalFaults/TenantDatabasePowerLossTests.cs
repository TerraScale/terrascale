using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;
using TerraBase.Testing;
using Turso.Data.Sqlite;

namespace TerraBase.E2E.WalFaults;

public sealed class TenantDatabasePowerLossTests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_FULL_tenant_commit_syncs_earlier_NORMAL_frames_as_well_as_its_own_document()
    {
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync(nameof(TenantPowerLossHost));
        var (path, wal) = await CreateTenantAsync(node);
        var faults = new WalSyncFaults(node.PathOf(Cluster.Gru) + ".walfaults");
        await File.WriteAllTextAsync(node.PathOf("prime-normal"), wal[..^4]);
        var ready = node.PathOf("normal-ready");
        var clock = Stopwatch.StartNew();
        while (!File.Exists(ready) && clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            await Task.Delay(20);
        }

        await Assert.That(File.Exists(ready)).IsTrue().Because("the child must write the earlier frames before the FULL request");
        File.Delete(node.PathOf("prime-normal"));
        var normalLength = long.Parse(await File.ReadAllTextAsync(ready), CultureInfo.InvariantCulture);
        var before = faults.SyncedLength(wal);
        await Assert.That(normalLength).IsGreaterThan(before).Because("NORMAL commits wrote frames without syncing them");
        var database = wal[..^4];
        var main = await File.ReadAllBytesAsync(database);
        var snapshot = node.PathOf("before-full.db");
        await File.WriteAllBytesAsync(snapshot, main);
        File.Copy(database + ".key", snapshot + ".key");
        await Assert.That(await NormalRowsAsync(snapshot)).IsEqualTo(0).Because("the earlier NORMAL rows must exist only in the WAL, not in checkpointed main-database pages");
        await Assert.That(new FileInfo(wal).Length).IsEqualTo(normalLength).Because("no intervening writer or checkpoint changed the WAL");
        await Assert.That(faults.SyncedLength(wal)).IsEqualTo(before).Because("no intervening sync made the NORMAL commits durable");
        var id = EntityIds.New().ToString();
        await Docs.PutAsync(node.Client, Document(path, id), Body(0, "FULL barrier"));
        var synced = faults.SyncedLength(wal);
        await Assert.That(synced).IsGreaterThan(normalLength).Because("the FULL tenant commit synced its own and all earlier frames");
        await node.PowerLossAsync();
        await Assert.That(new FileInfo(wal).Length).IsEqualTo(synced);
        await Assert.That((await File.ReadAllBytesAsync(database)).SequenceEqual(main)).IsTrue().Because("no checkpoint supplied the NORMAL rows instead of the FULL WAL sync");
        var recoveredCopy = node.PathOf("after-loss.db");
        File.Copy(database, recoveredCopy);
        File.Copy(database + ".key", recoveredCopy + ".key");
        File.Copy(wal, recoveredCopy + "-wal");
        await Assert.That(await NormalRowsAsync(recoveredCopy)).IsEqualTo(3).Because("the FULL barrier persisted the three earlier NORMAL commits");

        await node.StartAgainAsync();
        await Assert.That(Docs.Canonical(await Docs.ReadAsync(node.Client, Document(path, id)))).IsEqualTo(Docs.Canonical(Body(0, "FULL barrier")));
        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"B3 barrier normal_commits=3 normal_recovered=3 full_acknowledged=1 full_recovered=1 before_sync_bytes={before} normal_bytes={normalLength} full_sync_bytes={synced}");
        Outcomes.Mark("pass", evidence);
        Console.WriteLine(evidence);
    }

    [Test]
    [Arguments(181, 1)]
    [Arguments(13013, 12)]
    [Arguments(802, 2048)]
    [Scenario(Nodes.Processes, Faults.ProcessKill | Faults.FileTampering)]
    public async Task A_random_cut_keeps_every_acknowledged_tenant_document_and_discards_the_torn_transaction(int seed, int tornBytes)
    {
        Skip.When(WalSyncFaults.Library is null, "needs Linux and cc to build the write-ahead log fault shim");
        await using var node = await PowerLossNode.StartAsync(nameof(TenantPowerLossHost));
        var (path, wal) = await CreateTenantAsync(node);
        var faults = new WalSyncFaults(node.PathOf(Cluster.Gru) + ".walfaults");
        var random = new Random(seed);
        var count = random.Next(12, 25);
        var payload = new Faker { Random = new Randomizer(seed) }.Random.String2(random.Next(4096, 8193), "abcdefghijklmnopqrstuvwxyz");
        var ids = Enumerable.Range(0, count).Select(_ => EntityIds.New().ToString()).ToArray();
        var main = await File.ReadAllBytesAsync(wal[..^4]);
        var previousSync = faults.SyncedLength(wal);
        foreach (var (index, id) in ids.Index())
        {
            await Docs.PutAsync(node.Client, Document(path, id), Body(index, payload));
            var committedSync = faults.SyncedLength(wal);
            await Assert.That(committedSync).IsGreaterThan(previousSync).Because($"acknowledgement {index + 1} has its own successful tenant WAL sync");
            await Assert.That(new FileInfo(wal).Length).IsEqualTo(committedSync);
            previousSync = committedSync;
        }

        var synced = faults.SyncedLength(wal);
        await Assert.That(synced).IsGreaterThan(0).Because("the shim observed a successful sync of this tenant WAL");
        await Assert.That(new FileInfo(wal).Length).IsEqualTo(synced).Because("the last acknowledged commit synced every preceding frame");

        var interrupted = EntityIds.New().ToString();
        faults.KillAtSync(1, wal);
        try
        {
            await Docs.PutAsync(node.Client, Document(path, interrupted), Body(count, payload));
            throw new InvalidOperationException("the interrupted commit was acknowledged despite SIGKILL before its sync");
        }
        catch (HttpRequestException exception) when (exception.StatusCode is null)
        {
        }

        var written = await File.ReadAllBytesAsync(wal);
        await Assert.That(faults.SyncedLength(wal)).IsEqualTo(synced).Because("the interrupted sync persisted no new frames");
        await Assert.That(written.LongLength).IsGreaterThan(synced).Because("this cut must exercise unsynced tenant frames");
        var pageSize = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(8, 4));
        var frameSize = checked((int)pageSize + 24);
        var unsynced = checked(written.Length - (int)synced);
        await Assert.That(unsynced % frameSize).IsEqualTo(0).Because("SIGKILL intercepted sync after complete frames were written");
        await Assert.That(unsynced / frameSize).IsGreaterThanOrEqualTo(2).Because("the torn tail retains complete uncommitted frames as well as part of the final frame");
        await Assert.That(tornBytes).IsLessThan(frameSize);
        for (var offset = checked((int)synced); offset < written.Length; offset += frameSize)
        {
            var databaseSize = BinaryPrimitives.ReadUInt32BigEndian(written.AsSpan(offset + 4, 4));
            await Assert.That(databaseSize > 0).IsEqualTo(offset + frameSize == written.Length)
                .Because("only the final unsynced frame commits the interrupted transaction");
        }
        var retained = unsynced - frameSize + tornBytes;
        await node.PowerLossAsync();
        await Assert.That((await File.ReadAllBytesAsync(wal[..^4])).SequenceEqual(main)).IsTrue().Because("the acknowledged prefix must be recovered from synced WAL frames, not checkpointed main-database pages");
        await Assert.That(new FileInfo(wal).Length).IsEqualTo(synced).Because("power loss drops every unsynced byte first");
        await using (var file = new FileStream(wal, FileMode.Append, FileAccess.Write))
        {
            await file.WriteAsync(written.AsMemory(checked((int)synced), retained));
        }

        faults.Clear();
        await node.StartAgainAsync();
        var recovered = 0;
        foreach (var (index, id) in ids.Index())
        {
            await Assert.That(Docs.Canonical(await Docs.ReadAsync(node.Client, Document(path, id))))
                .IsEqualTo(Docs.Canonical(Body(index, payload))).Because($"acknowledged write {index + 1} survives seed {seed}");
            recovered++;
        }

        await Assert.That(await Docs.ReadAsync(node.Client, Document(path, interrupted))).IsNull()
            .Because("complete frames before the torn commit frame cannot publish the interrupted transaction");
        var next = EntityIds.New().ToString();
        var recoveredSync = faults.SyncedLength(wal);
        await Docs.PutAsync(node.Client, Document(path, next), Body(count + 1, payload));
        var nextSync = faults.SyncedLength(wal);
        await Assert.That(nextSync).IsGreaterThan(recoveredSync).Because("the post-recovery acknowledgement has a new successful WAL sync");
        await Assert.That(new FileInfo(wal).Length).IsEqualTo(nextSync);
        await node.PowerLossAsync();
        await node.StartAgainAsync();
        foreach (var (index, id) in ids.Index())
        {
            await Assert.That(Docs.Canonical(await Docs.ReadAsync(node.Client, Document(path, id))))
                .IsEqualTo(Docs.Canonical(Body(index, payload))).Because($"acknowledged write {index + 1} also survives the second power loss for seed {seed}");
        }

        await Assert.That(Docs.Canonical(await Docs.ReadAsync(node.Client, Document(path, next)))).IsEqualTo(Docs.Canonical(Body(count + 1, payload)))
            .Because("a FULL commit after torn-tail recovery also survives power loss");
        await Assert.That(await Docs.ReadAsync(node.Client, Document(path, interrupted))).IsNull();
        var evidence = string.Create(CultureInfo.InvariantCulture,
            $"B3 seed={seed} acknowledged={count} recovered={recovered} interrupted=1 discarded=1 synced_bytes={synced} unsynced_bytes={unsynced} retained_bytes={retained} torn_bytes={tornBytes} post_recovery_acknowledged=1 post_recovery_recovered=1");
        Outcomes.Mark("pass", evidence);
        Console.WriteLine(evidence);
    }

    private static async Task<(string Path, string Wal)> CreateTenantAsync(PowerLossNode node)
    {
        var (organization, project, database) = (EntityIds.New().ToString(), EntityIds.New().ToString(), EntityIds.New().ToString());
        await node.CommitAsync(EntityIds.New().ToString(), new JsonObject
        {
            ["id"] = EntityIds.New().ToString(),
            ["ops"] = new JsonArray(
                new JsonObject { ["op"] = "create_organization", ["id"] = organization, ["name"] = $"power-{organization}" },
                new JsonObject { ["op"] = "create_project", ["organization"] = organization, ["id"] = project, ["name"] = "power" },
                new JsonObject { ["op"] = "create_database", ["organization"] = organization, ["project"] = project, ["name"] = "canary", ["id"] = database, ["replica_regions"] = new JsonArray() }),
        });
        var path = $"/v1/projects/{project}/databases/canary";
        await Cluster.CreateCollectionAsync(new(StringComparer.Ordinal) { [Cluster.Gru] = node.Client }, path, "items");
        return (path, node.PathOf(Path.Combine(Cluster.Gru, organization, project, database + ".db-wal")));
    }

    private static JsonObject Body(int index, string payload) => new() { ["n"] = index, ["payload"] = payload };

    private static async Task<long> NormalRowsAsync(string database)
    {
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }));
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = '_b3_normal_frames'";
        if ((long)(await command.ExecuteScalarAsync())! == 0)
        {
            return 0;
        }

        command.CommandText = "SELECT count(*) FROM _b3_normal_frames WHERE n IN (1, 2, 3)";
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Uri Document(string path, string id) => new($"{path}/collections/items/documents/by-key?id={id}", UriKind.Relative);
}
[Category("tenant-power-loss-host")]
public sealed class TenantPowerLossHost
{
    [Test]
    [Timeout(6 * 3600 * 1000)]
    public async Task Hosts_a_tenant_and_primes_NORMAL_frames_when_requested(CancellationToken cancellationToken)
    {
        var info = Environment.GetEnvironmentVariable(RegionProcessHost.InfoVariable);
        Skip.When(string.IsNullOrEmpty(info), "the child process of TenantDatabasePowerLossTests");
        var root = Path.GetDirectoryName(info)!;
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["region"] = Environment.GetEnvironmentVariable(RegionProcessHost.RegionVariable)!;
            settings["node-name"] = Environment.GetEnvironmentVariable(RegionProcessHost.MachineVariable)!;
            settings["data-directory"] = Environment.GetEnvironmentVariable(RegionProcessHost.DataVariable)!;
        });
        await File.WriteAllTextAsync(info + ".partial", new JsonObject { ["internal"] = api.Server.BaseAddress.ToString() }.ToJsonString(), cancellationToken);
        File.Move(info + ".partial", info!, overwrite: true);
        var arm = Path.Combine(root, "prime-normal");
        while (!File.Exists(arm))
        {
            await Task.Delay(20, cancellationToken);
        }

        var database = await File.ReadAllTextAsync(arm, cancellationToken);
        await using var connection = new SqliteConnection(TestMasterKey.ConnectionString(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }));
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA synchronous = FULL; CREATE TABLE _b3_normal_frames (n INTEGER PRIMARY KEY); PRAGMA synchronous = NORMAL;";
        _ = await command.ExecuteNonQueryAsync(cancellationToken);
        for (var row = 1; row <= 3; row++)
        {
            command.CommandText = string.Create(CultureInfo.InvariantCulture, $"INSERT INTO _b3_normal_frames VALUES ({row})");
            _ = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var ready = Path.Combine(root, "normal-ready");
        await File.WriteAllTextAsync(ready + ".partial", new FileInfo(database + "-wal").Length.ToString(CultureInfo.InvariantCulture), cancellationToken);
        File.Move(ready + ".partial", ready, overwrite: true);
        await Task.Delay(Timeout.Infinite, cancellationToken);
    }
}
