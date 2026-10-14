using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using Bogus;
using TerraBase.Primitives;
using Turso.Data.Sqlite;

namespace TerraBase.E2E;

/// <summary>Deletes in all six directions, and each region catching up on writes and tombstones after an outage.</summary>
[Category("replication-matrix")]
public sealed class ReplicationMatrixTests
{
    private static readonly string[] All = [Cluster.Gru, Cluster.Sjc, Cluster.Fra];

    [Test]
    [Category("replication-matrix-live")]
    [Scenario(Nodes.InProcess)]
    [Arguments(Cluster.Gru, Cluster.Sjc)]
    [Arguments(Cluster.Sjc, Cluster.Gru)]
    [Arguments(Cluster.Sjc, Cluster.Fra)]
    [Arguments(Cluster.Fra, Cluster.Sjc)]
    [Arguments(Cluster.Gru, Cluster.Fra)]
    [Arguments(Cluster.Fra, Cluster.Gru)]
    public async Task Delete_and_tombstone_replicate_to_both_other_regions(string from, string to)
    {
        await using var cluster = await Cluster.StartAsync(All);
        var id = EntityIds.New().ToString();
        var uri = cluster.Document(id);
        await Converge.SeedAllAsync(cluster, uri, ThreeRegionE2ETests.Synthetic(new Faker()));
        await Docs.DeleteAsync(cluster[from], uri);
        var acknowledged = Stopwatch.GetTimestamp();
        _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[to], uri), document => document is null, $"delete {from} to {to}");
        Converge.Report($"delete {cluster.Identity(from)} to {cluster.Identity(to)}", acknowledged);
        _ = await Converge.UntilAsync(() => Converge.AllAsync(cluster, uri),
            copies => copies.Copies.Values.All(copy => copy is null), $"delete {from} everywhere");
        await CheckTombstonesAsync(cluster, id);
    }

    [Test]
    [Scenario(Nodes.InProcess, Faults.NodeStop)]
    [Arguments(Cluster.Gru)]
    [Arguments(Cluster.Sjc)]
    [Arguments(Cluster.Fra)]
    public async Task Each_region_rejoins_with_writes_and_tombstones_from_both_survivors(string offline)
    {
        Skip.When(Cluster.FlyRequested,
            "each parallel outage owns local nodes; live outage proof runs separately on a pinned disposable app with make terrabase-e2e-fly-outage");
        await using var cluster = await Cluster.StartAsync(All);
        await RejoinAsync(cluster, offline);
    }

    /// <summary>
    /// Stops <paramref name="offline"/>, writes, deletes and conflicts through both survivors, starts it again on its data and
    /// waits until it serves both backlogs, the tombstones and the settled conflict. The cluster's collection holds only
    /// this outage's documents, so the digest names all of them.
    /// </summary>
    internal static async Task RejoinAsync(Cluster cluster, string offline)
    {
        var survivors = All.Where(region => region != offline).ToArray();
        var deleted = survivors.Select(_ => EntityIds.New().ToString()).ToArray();
        foreach (var id in deleted)
        {
            await Converge.SeedAllAsync(cluster, cluster.Document(id), new JsonObject { ["value"] = "before-outage" });
        }

        var conflictId = EntityIds.New().ToString();
        var conflict = cluster.Document(conflictId);
        await Converge.SeedAllAsync(cluster, conflict, new JsonObject { ["value"] = "seed" });
        await cluster.StopAsync(offline);
        var stopped = true;
        try
        {
            // Seed convergence through the stopped region does not prove the survivors can confirm each other's writes.
            (string Id, string Canonical)[] probes = [];
            if (Cluster.FlyRequested)
            {
                probes = await ProbeSurvivorsAsync(cluster, survivors);
            }
            else
            {
                await Task.WhenAll(survivors.Select(from => Converge.UntilAsync(() => cluster.ReplicationAsync(from),
                    status => status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() == survivors.Single(to => to != from)),
                    $"{from} streams to the other survivor while {offline} is down")));
            }

            const int count = 40;
            var faker = new Faker();
            var documents = Enumerable.Range(0, count)
                .Select(index => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker), From: survivors[index % survivors.Length])).ToArray();
            await Task.WhenAll(survivors.Select(async (region, index) =>
            {
                foreach (var (id, body, _) in documents.Where(document => document.From == region))
                {
                    await Docs.PutAsync(cluster[region], cluster.Document(id), body);
                }

                await Docs.DeleteAsync(cluster[region], cluster.Document(deleted[index]));
                await Docs.PatchAsync(cluster[region], conflict, new JsonObject { ["value"] = region, [region] = "during-outage" });
            }));

            // No writes after restart: catch-up must replay the durable backlog without a client kick.
            await cluster.StartAgainAsync(offline);
            stopped = false;
            var rejoined = Stopwatch.GetTimestamp();
            await Task.WhenAll(survivors.Select(async from =>
            {
                var expected = documents.Where(document => document.From == from).ToArray();
                _ = await Converge.UntilAsync(() => BootingAsync(() => Task.WhenAll(expected.Select(document => Docs.ReadAsync(cluster[offline], cluster.Document(document.Id))))),
                    copies => copies is not null && copies.Select(Docs.Canonical).SequenceEqual(expected.Select(document => Docs.Canonical(document.Body))), $"{from} backlog to {offline}");
                Converge.Report($"rejoin {cluster.Identity(from)} to {cluster.Identity(offline)} ({expected.Length} documents)", rejoined);
            }));

            foreach (var id in deleted)
            {
                _ = await Converge.UntilAsync(() => Converge.AllAsync(cluster, cluster.Document(id)),
                    copies => copies.Copies.Values.All(copy => copy is null), $"stale pre-outage document {id} stays deleted");
                await CheckTombstonesAsync(cluster, id);
            }

            var settled = await Converge.UntilAsync(() => Converge.AllAsync(cluster, conflict),
                copies => copies.Identical && survivors.All(region => copies.Value?[region]?.GetValue<string>() == "during-outage")
                    && survivors.Contains(copies.Value?["value"]?.GetValue<string>()), "conflicting outage writes converge after rejoin");
            var digest = Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))
                .Append((conflictId, Docs.Canonical(settled.Value))).Concat(probes));
            _ = await Converge.ListedAsync(cluster, digest, $"every acknowledged document after {offline} rejoined");
        }
        finally
        {
            if (stopped)
            {
                await cluster.StartAgainAsync(offline);
                // The cluster's cleanup checks every machine's identity and deletes the collection through gru.
                _ = await Converge.UntilAsync(() => BootingAsync(() => Docs.StatusAsync(cluster[offline], new Uri("/healthz", UriKind.Relative))),
                    status => status == HttpStatusCode.OK, $"{offline} serves again");
            }
        }
    }

    // Fly's internal listener is out of reach. With the third region down, a survivor's write is acknowledged only by a
    // durable copy on the other survivor, which must then serve it. Answers the probes for the collection digest.
    private static async Task<(string Id, string Canonical)[]> ProbeSurvivorsAsync(Cluster cluster, string[] survivors) =>
        await Task.WhenAll(survivors.Select(async from =>
        {
            var (id, to) = (EntityIds.New().ToString(), survivors.Single(region => region != from));
            var body = new JsonObject { ["probe"] = from };
            await Docs.PutRefusedBeforeRunAsync(cluster[from], cluster.Document(id), body);
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[to], cluster.Document(id)),
                copy => Docs.Canonical(copy) == Docs.Canonical(body), $"{from} write confirmed by {to}");
            return (id, Docs.Canonical(body));
        }));

    // While a restarted Fly machine boots, the proxy answers for it without TerraBase's identity headers (the pin refuses
    // that) or the node answers 503: the read is not there yet. Reads only; a write is never sent again this way.
    private static async Task<T?> BootingAsync<T>(Func<Task<T>> read)
    {
        try
        {
            return await read();
        }
        catch (Exception error) when (error is RegionMismatchException or HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable })
        {
            return default;
        }
    }

    // Every region answers the deleted document with 404 and the same retained tombstone's ETag; locally its file holds it too.
    private static async Task CheckTombstonesAsync(Cluster cluster, string id)
    {
        _ = await Converge.UntilAsync(() => Task.WhenAll(cluster.Regions.Select(region => Docs.TombstoneAsync(cluster[region], cluster.Document(id)))),
            tags => tags.All(tag => tag is { Length: > 0 }) && tags.Distinct(StringComparer.Ordinal).Count() == 1,
            $"every region's tombstone ETag for {id}");
        if (Cluster.FlyRequested)
        {
            return;
        }

        foreach (var region in All)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = cluster.DatabaseFile(region),
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            await connection.OpenAsync();
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM _tb_state WHERE collection = $collection AND id = $id AND live = 0";
            _ = query.Parameters.AddWithValue("$collection", cluster.Collection);
            _ = query.Parameters.AddWithValue("$id", id);
            Expected.Check((long)(await query.ExecuteScalarAsync())! == 1, $"{region} retains the replicated tombstone for {id}");
        }
    }
}
