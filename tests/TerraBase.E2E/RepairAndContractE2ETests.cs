using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Bogus;
using Turso.Data.Sqlite;
using TerraBase.Primitives;

namespace TerraBase.E2E;

/// <summary>
/// The design-C replacements for the deleted suite's repair, unique, catalog, observability, flow-control and DDL
/// scenarios (COVERAGE.md rows 9, 14, 20 to 23 and 29). Each is a strict expected failure until the slice <see cref="Gaps"/>
/// names. The ones that edit a node's files or partition regions run on the local nodes only.
/// </summary>
public sealed class RepairAndContractE2ETests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    // The stream lags R4a-2 publishes and the CDC tail lag of design section 18, as Prometheus names them.
    private static readonly string[] ReplicationSeries = ["terrabase_replication_send_lag", "terrabase_replication_ack_lag", "terrabase_replication_tail_lag"];

    // Anti-entropy rounds run every 60 s and repair a bucket that differs in two consecutive rounds (design section 10).
    private static readonly TimeSpan RepairWindow = TimeSpan.FromSeconds(200);

    // A document the streams delivered and acknowledged disappears from sjc's file while sjc is down, as a lost apply would
    // leave it. Streams never send it again (it was acknowledged), so only anti-entropy can bring it back.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop | Faults.FileTampering)]
    public async Task Anti_entropy_repairs_a_document_a_region_lost_after_its_stream_acknowledged_it()
    {
        Skip.When(Cluster.FlyRequested, "edits a local node's database file");
        await Gaps.RunAsync("anti-entropy-repair", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var documents = await WriteAsync(cluster, 50);
            _ = await Converge.ListedAsync(cluster, Digest(documents), "seed");
            var (victim, body) = documents[17];
            await cluster.StopAsync(Cluster.Sjc);
            await DeleteRowsAsync(cluster.DatabaseFile(Cluster.Sjc), victim);
            await cluster.StartAgainAsync(Cluster.Sjc);
            var started = Stopwatch.GetTimestamp();
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[Cluster.Sjc], cluster.Document(victim)),
                read => Docs.Canonical(read) == Docs.Canonical(body), "the lost document on sjc", RepairWindow);
            Converge.Report("anti-entropy repair of one lost document", started);
            _ = await Converge.ListedAsync(cluster, Digest(documents), "every region after the repair");
        });
    }

    // sjc comes back on a copy of its data from before 100 later writes, as after a volume restored from an older snapshot.
    // It must end with every write, by refill or repair, and must not hand its older state back to the other regions.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop | Faults.FileTampering)]
    public async Task A_region_restored_to_an_older_copy_of_its_data_catches_up_without_undoing_later_writes()
    {
        Skip.When(Cluster.FlyRequested, "restores a local node's data directory");
        await Gaps.RunAsync("restore-older-state", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var before = await WriteAsync(cluster, 100);
            _ = await Converge.ListedAsync(cluster, Digest(before), "before the snapshot");
            var snapshot = Directory.CreateTempSubdirectory("terrabase-e2e-snapshot-").FullName;
            try
            {
                await cluster.StopAsync(Cluster.Sjc);
                Copy(cluster.DataDirectory(Cluster.Sjc), snapshot);
                await cluster.StartAgainAsync(Cluster.Sjc);
                var after = await WriteAsync(cluster, 100);
                _ = await Converge.ListedAsync(cluster, Digest([.. before, .. after]), "after the later writes");
                await cluster.StopAsync(Cluster.Sjc);
                Directory.Delete(cluster.DataDirectory(Cluster.Sjc), recursive: true);
                Copy(snapshot, cluster.DataDirectory(Cluster.Sjc));
                await cluster.StartAgainAsync(Cluster.Sjc);
                var restored = Stopwatch.GetTimestamp();
                _ = await Converge.UntilAsync(
                    async () => (await Task.WhenAll(cluster.Regions.Select(region => Docs.ListAsync(cluster[region], cluster)))).All(listing => listing.Digest == Digest([.. before, .. after])),
                    done => done, "every region after sjc's restore", RepairWindow);
                Converge.Report("catch-up of a region restored to an older copy", restored);
            }
            finally
            {
                Directory.Delete(snapshot, recursive: true);
            }
        });
    }

    // Two origins take the same unique email while their direct stream is partitioned; fra confirms both writes. After
    // the heal every region holds the same answer: the document with
    // the smaller id keeps the key; the other loses it, so its GET answers 409 unique_violation and it is not listed (the
    // schema endpoint's contract; design section 9, unique constraints).
    [Test]
    [Scenario(Nodes.InProcess, Faults.SimulatedPartition)]
    public async Task Unique_writes_made_on_both_sides_of_a_partition_converge_to_the_smaller_id()
    {
        Skip.When(Cluster.FlyRequested, "Fly has no partition switch");
        await Gaps.RunAsync("unique-under-partition", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            using var declared = await cluster[Cluster.Gru].PutAsJsonAsync(
                new Uri($"{cluster.DatabasePath}/schema/{cluster.Collection}", UriKind.Relative), new { fields = new[] { new { name = "email", unique = true } } });
            await Docs.EnsureSuccessAsync(declared);
            var (smaller, larger) = (EntityIds.New().ToString(), EntityIds.New().ToString());
            foreach (var region in new[] { Cluster.Gru, Cluster.Sjc })
            {
                _ = await Converge.UntilAsync(() => Docs.GetJsonAsync(cluster[region], new Uri("/health/replication", UriKind.Relative)),
                    health => health?["peers"]?.AsArray().Any(peer => peer?["region"]?.GetValue<string>() == Cluster.Fra
                        && peer["status"]?.GetValue<string>() == "ok") == true, $"{region} streams to fra before its direct partition");
            }

            cluster.Partition(Cluster.Gru, Cluster.Sjc);
            await Docs.PutAsync(cluster[Cluster.Sjc], cluster.Document(smaller), new JsonObject { ["email"] = "same@example.test", ["from"] = "sjc" });
            await Docs.PutAsync(cluster[Cluster.Gru], cluster.Document(larger), new JsonObject { ["email"] = "same@example.test", ["from"] = "gru" });
            cluster.Heal();
            foreach (var region in cluster.Regions)
            {
                _ = await Converge.UntilAsync(
                    async () => (
                        Kept: await Docs.ReadAsync(cluster[region], cluster.Document(smaller)),
                        Lost: await LostAsync(cluster[region], cluster.Document(larger)),
                        Listed: (await Docs.ListAsync(cluster[region], cluster)).Count),
                    state => state.Kept?["from"]?.GetValue<string>() == "sjc" && state.Lost && state.Listed == 1,
                    $"the unique key on {region}: the smaller id keeps it, the larger answers 409 unique_violation and is not listed");
            }
        });
    }

    // Whether the document answers 409 unique_violation: it lost a replicated uniqueness conflict.
    private static async Task<bool> LostAsync(HttpClient client, Uri uri)
    {
        using var response = await client.GetAsync(uri);
        return response.StatusCode == HttpStatusCode.Conflict && (await response.Content.ReadAsStringAsync()).Contains("unique_violation", StringComparison.Ordinal);
    }

    // A database deleted in gru while fra is away stays deleted: fra drops its copy when it returns, and nothing brings the
    // database or its documents back anywhere.
    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    [Category("fly-outage")]
    public async Task A_database_deleted_while_a_region_is_down_does_not_come_back()
    {
        SkipOnFlyUnlessOutageRun();
        await Gaps.RunAsync("deleted-database-stays-deleted", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var name = $"e2e{Guid.NewGuid():N}"[..20];
            var path = new Uri($"{cluster.ProjectPath}/databases/{name}", UriKind.Relative);
            using (var created = await cluster[Cluster.Gru].PostAsJsonAsync(new Uri($"{cluster.ProjectPath}/databases", UriKind.Relative), new { name }))
            {
                await Docs.EnsureSuccessAsync(created);
            }

            foreach (var region in cluster.Regions)
            {
                _ = await Converge.UntilAsync(() => Docs.StatusAsync(cluster[region], path), status => status == HttpStatusCode.OK, $"the new database on {region}");
            }

            await cluster.StopAsync(Cluster.Fra);
            using (var deleted = await cluster[Cluster.Gru].DeleteAsync(path))
            {
                await Docs.EnsureSuccessAsync(deleted);
            }

            await cluster.StartAgainAsync(Cluster.Fra);
            _ = await Converge.UntilAsync(() => Docs.StatusAsync(cluster[Cluster.Fra], path), status => status == HttpStatusCode.NotFound, "the deleted database on fra");
            await Task.Delay(TimeSpan.FromSeconds(5));
            foreach (var region in cluster.Regions)
            {
                await Assert.That(await Docs.StatusAsync(cluster[region], path)).IsEqualTo(HttpStatusCode.NotFound).Because($"{region} five seconds after fra returned");
            }
        });
    }

    // Replication is observable: /health/replication in every region names the two others as peers with a lag of at most
    // 5 s once a write converged, and the Prometheus listener exposes the stream lags R4a-2 publishes and the CDC tail lag
    // (design section 18).
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Every_region_reports_its_peers_and_replication_lag_on_health_and_metrics()
    {
        await Gaps.RunAsync("replication-metrics", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var uri = cluster.Document(EntityIds.New().ToString());
            await Converge.SeedAllAsync(cluster, uri, new JsonObject { ["value"] = "observable" });
            foreach (var region in cluster.Regions)
            {
                var health = await Converge.UntilAsync(
                    () => Docs.GetJsonAsync(cluster[region], new Uri("/health/replication", UriKind.Relative)),
                    body => body?["status"]?.GetValue<string>() == "ok", $"replication health of {region}");
                var peers = health!["peers"]!.AsArray().Select(peer => peer!["region"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray();
                await Assert.That(peers).IsEquivalentTo(cluster.Regions.Where(other => other != region).Order(StringComparer.Ordinal).ToArray());
                await Assert.That(health["peers"]!.AsArray().All(peer => peer!["lagSeconds"]?.GetValue<double>() <= 5)).IsTrue();
                if (!Cluster.FlyRequested)
                {
                    using var metrics = new HttpClient { BaseAddress = cluster.MetricsAddress(region) };
                    var text = await metrics.GetStringAsync(new Uri("/metrics", UriKind.Relative));
                    foreach (var series in ReplicationSeries)
                    {
                        await Assert.That(text).Contains(series);
                    }
                }
            }
        });
    }

    // One hot database does not hold back another: while a 3000-document burst replicates in one database, a single write
    // in another database reaches sjc within 5 s (the deleted suite's "a database written without pause does not hold
    // back the others").
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task A_hot_database_does_not_hold_back_replication_of_another()
    {
        await Gaps.RunAsync("flow-control", async () =>
        {
            await using var cluster = await Cluster.StartAsync(All);
            var hot = $"hot{Guid.NewGuid():N}"[..20];
            using (var created = await cluster[Cluster.Gru].PostAsJsonAsync(new Uri($"{cluster.ProjectPath}/databases", UriKind.Relative), new { name = hot }))
            {
                await Docs.EnsureSuccessAsync(created);
            }

            var hotPath = $"{cluster.ProjectPath}/databases/{hot}";
            _ = await Docs.PostAsync(cluster[Cluster.Gru], new Uri($"{hotPath}/collections", UriKind.Relative), new JsonObject { ["name"] = "burst" });
            var faker = new Faker();
            var burst = Parallel.ForEachAsync(Enumerable.Range(0, 3000), new ParallelOptions { MaxDegreeOfParallelism = 32 }, async (_, token) =>
                await Docs.PutAsync(cluster[Cluster.Gru], new Uri($"{hotPath}/collections/burst/documents/by-key?id={EntityIds.New()}", UriKind.Relative), ThreeRegionE2ETests.Synthetic(faker), token));
            await Task.Delay(TimeSpan.FromSeconds(1));
            var uri = cluster.Document(EntityIds.New().ToString());
            await Docs.PutAsync(cluster[Cluster.Gru], uri, new JsonObject { ["value"] = "quiet" });
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[Cluster.Sjc], uri), read => read is not null, "the quiet database's write on sjc", TimeSpan.FromSeconds(5));
            await burst;
        });
    }

    // Collection DDL replicates: an index created in gru is listed in sjc, a schema attached in gru makes sjc refuse an
    // invalid document, and a collection dropped in gru is gone from sjc.
    [Test]
    [Scenario(Nodes.InProcess)]
    public async Task Indexes_schemas_and_collection_drops_made_in_one_region_take_effect_in_another()
    {
        await Gaps.RunAsync("ddl-replication", async () =>
        {
            await using var cluster = await Cluster.StartAsync(Cluster.Gru, Cluster.Sjc);
            var (gru, sjc) = (cluster[Cluster.Gru], cluster[Cluster.Sjc]);
            var indexes = new Uri($"{cluster.DatabasePath}/indexes", UriKind.Relative);
            _ = await Docs.PostAsync(gru, indexes, new JsonObject { ["collection"] = cluster.Collection, ["fields"] = new JsonArray("email") });
            _ = await Converge.UntilAsync(() => Docs.GetJsonAsync(sjc, indexes),
                body => body?["items"]?.AsArray().Any(index => index?["collection"]?.GetValue<string>() == cluster.Collection) == true, "the index on sjc");

            var schema = new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}/schema", UriKind.Relative);
            using (var attached = await gru.PutAsync(schema, new StringContent("""{"type":"object","required":["email"],"properties":{"email":{"type":"string"}}}""", System.Text.Encoding.UTF8, "application/json")))
            {
                await Docs.EnsureSuccessAsync(attached);
            }

            _ = await Converge.UntilAsync(async () =>
            {
                using var invalid = await sjc.PutAsJsonAsync(cluster.Document(EntityIds.New().ToString()), new { email = 123 });
                return invalid.StatusCode;
            }, status => status == HttpStatusCode.BadRequest, "the schema enforced on sjc");

            using (var dropped = await gru.DeleteAsync(new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}", UriKind.Relative)))
            {
                await Docs.EnsureSuccessAsync(dropped);
            }

            _ = await Converge.UntilAsync(() => Docs.StatusAsync(sjc, new Uri($"{cluster.DatabasePath}/collections/{cluster.Collection}", UriKind.Relative)),
                status => status == HttpStatusCode.NotFound, "the dropped collection on sjc");
        });
    }

    private static async Task<List<(string Id, JsonObject Body)>> WriteAsync(Cluster cluster, int count)
    {
        var faker = new Faker();
        var documents = Enumerable.Range(0, count).Select(_ => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker))).ToList();
        await Parallel.ForEachAsync(Enumerable.Range(0, count), new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (index, token) =>
            await Docs.PutAsync(cluster[All[index % cluster.Regions.Count]], cluster.Document(documents[index].Id), documents[index].Body, token));
        return documents;
    }

    private static string Digest(IEnumerable<(string Id, JsonObject Body)> documents) =>
        Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body))));

    // Deletes the document from every table that keys rows by an id column, whatever the layout of the build.
    private static async Task DeleteRowsAsync(string file, string id)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        await connection.OpenAsync();
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT m.name FROM sqlite_master m JOIN pragma_table_info(m.name) c WHERE m.type = 'table' AND c.name = 'id'";
            await using var reader = await list.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                tables.Add(reader.GetString(0));
            }
        }

        foreach (var table in tables)
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = $"DELETE FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" WHERE id = $id";
            _ = delete.Parameters.AddWithValue("$id", id);
            _ = await delete.ExecuteNonQueryAsync();
        }
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void SkipOnFlyUnlessOutageRun() =>
        Skip.When(Cluster.FlyRequested && !Cluster.FlyMachineFaults,
            "stops a Fly machine: run it alone on a pinned disposable app with the broader fly-outage command (docs/terrabase/replication-e2e.md)");
}
