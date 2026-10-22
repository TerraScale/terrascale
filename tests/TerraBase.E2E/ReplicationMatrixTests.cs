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
    public async Task Each_region_rejoins_with_writes_and_tombstones_from_both_survivors(string offline, CancellationToken cancellationToken)
    {
        Skip.When(Cluster.FlyRequested,
            "each parallel outage owns local nodes; remote machine lifecycle control is unavailable");
        await RejoinOwnedAsync(offline, cancellationToken);
    }

    // Preserve the outage and recovery errors if releasing the cluster also fails.
    internal static async Task RejoinOwnedAsync(string offline, CancellationToken token)
    {
        var cluster = await Cluster.StartAsync(All);
        try
        {
            await RejoinAsync(cluster, offline, token);
        }
        catch (Exception error)
        {
            try
            {
                await cluster.DisposeAsync();
            }
            catch (Exception cleanup)
            {
                throw new AggregateException(error, cleanup);
            }

            throw;
        }

        await cluster.DisposeAsync();
    }

    /// <summary>
    /// Stops <paramref name="offline"/>, writes, deletes and conflicts through both survivors, starts it again on its data and
    /// waits until it serves both backlogs, the tombstones and the settled conflict. The cluster's collection holds only
    /// this outage's documents, so the digest names all of them. If anything fails while <paramref name="offline"/> may be
    /// down (the stop included), recovery attempts to start it on a clock independent of <paramref name="token"/>;
    /// the original failure is rethrown, or reported with the recovery's.
    /// </summary>
    internal static async Task RejoinAsync(Cluster cluster, string offline, CancellationToken token = default)
    {
        var survivors = All.Where(region => region != offline).ToArray();
        var deleted = survivors.Select(_ => EntityIds.New().ToString()).ToArray();
        foreach (var id in deleted)
        {
            await Converge.SeedAllAsync(cluster, cluster.Document(id), new JsonObject { ["value"] = "before-outage" }, token);
        }

        var conflictId = EntityIds.New().ToString();
        var conflict = cluster.Document(conflictId);
        await Converge.SeedAllAsync(cluster, conflict, new JsonObject { ["value"] = "seed" }, token);
        // A stop that fails or is canceled may still have taken the region down; a start may return before it serves.
        var started = false;
        try
        {
            await cluster.StopAsync(offline, token);
            // Seed convergence through the stopped region does not prove the survivors can confirm each other's writes.
            (string Id, string Canonical)[] probes = [];
            if (Cluster.FlyRequested)
            {
                probes = await ProbeSurvivorsAsync(cluster, survivors, token);
            }
            else
            {
                await Task.WhenAll(survivors.Select(from => Converge.UntilAsync(() => cluster.ReplicationAsync(from, token),
                    status => status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() == survivors.Single(to => to != from)),
                    $"{from} streams to the other survivor while {offline} is down", token)));
            }

            const int count = 40;
            var faker = new Faker();
            var documents = Enumerable.Range(0, count)
                .Select(index => (Id: EntityIds.New().ToString(), Body: ThreeRegionE2ETests.Synthetic(faker), From: survivors[index % survivors.Length])).ToArray();
            await Task.WhenAll(survivors.Select(async (region, index) =>
            {
                foreach (var (id, body, _) in documents.Where(document => document.From == region))
                {
                    await Docs.PutAsync(cluster[region], cluster.Document(id), body, token);
                }

                await Docs.DeleteAsync(cluster[region], cluster.Document(deleted[index]), token);
                await Docs.PatchAsync(cluster[region], conflict, new JsonObject { ["value"] = region, [region] = "during-outage" }, token);
            }));

            // No writes after restart: catch-up must replay the durable backlog without a client kick.
            await cluster.StartAgainAsync(offline, token);
            started = true;
            var rejoined = Stopwatch.GetTimestamp();
            await Task.WhenAll(survivors.Select(async from =>
            {
                var expected = documents.Where(document => document.From == from).ToArray();
                _ = await Converge.UntilAsync(() => BootingAsync(() => Task.WhenAll(expected.Select(document => Docs.ReadAsync(cluster[offline], cluster.Document(document.Id), token)))),
                    copies => copies is not null && copies.Select(Docs.Canonical).SequenceEqual(expected.Select(document => Docs.Canonical(document.Body))), $"{from} backlog to {offline}", token);
                Converge.Report($"rejoin {cluster.Identity(from)} to {cluster.Identity(offline)} ({expected.Length} documents)", rejoined);
            }));

            foreach (var id in deleted)
            {
                _ = await Converge.UntilAsync(() => Converge.AllAsync(cluster, cluster.Document(id), token),
                    copies => copies.Copies.Values.All(copy => copy is null), $"stale pre-outage document {id} stays deleted", token);
                await CheckTombstonesAsync(cluster, id, token);
            }

            var settled = await Converge.UntilAsync(() => Converge.AllAsync(cluster, conflict, token),
                copies => copies.Identical && survivors.All(region => copies.Value?[region]?.GetValue<string>() == "during-outage")
                    && survivors.Contains(copies.Value?["value"]?.GetValue<string>()), "conflicting outage writes converge after rejoin", token);
            var digest = Docs.Digest(documents.Select(document => (document.Id, Docs.Canonical(document.Body)))
                .Append((conflictId, Docs.Canonical(settled.Value))).Concat(probes));
            _ = await Converge.ListedAsync(cluster, digest, $"every acknowledged document after {offline} rejoined", token);
        }
        catch (Exception error)
        {
            try
            {
                await RecoverAsync(cluster, offline, started);
            }
            catch (Exception recovery)
            {
                throw new AggregateException(error, recovery);
            }

            throw;
        }
    }

    // Starts offline again and waits until it serves: the cluster's cleanup checks every machine's identity and deletes the
    // collection through gru. Bounded by its own clock, never the test's token. A start that fails (the region was never
    // stopped, or already started) is forgiven only when the region then serves.
    private static async Task RecoverAsync(Cluster cluster, string offline, bool started)
    {
        // Fly's start waits up to two minutes for the machine to reach stopped, then the region has a convergence deadline.
        using var recovery = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Exception? start = null;
        if (!started)
        {
            try
            {
                await cluster.StartAgainAsync(offline, recovery.Token);
            }
            catch (Exception error)
            {
                start = error;
            }
        }

        try
        {
            _ = await Converge.UntilAsync(() => BootingAsync(() => Docs.StatusAsync(cluster[offline], new Uri("/healthz", UriKind.Relative), recovery.Token)),
                status => status == HttpStatusCode.OK, $"{offline} serves again", recovery.Token);
        }
        catch (Exception serving) when (start is not null)
        {
            throw new AggregateException(start, serving);
        }
    }

    // Fly's internal listener is out of reach. With the third region down, a survivor's write is acknowledged remote_durable
    // only once the other survivor holds a durable copy, which must then serve it. Visibility alone does not prove that
    // durability, so both are checked. Answers the probes for the collection digest.
    private static async Task<(string Id, string Canonical)[]> ProbeSurvivorsAsync(Cluster cluster, string[] survivors, CancellationToken token) =>
        await Task.WhenAll(survivors.Select(async from =>
        {
            var (id, to) = (EntityIds.New().ToString(), survivors.Single(region => region != from));
            var body = new JsonObject { ["probe"] = from };
            var durability = await Docs.PutRefusedBeforeRunAsync(cluster[from], cluster.Document(id), body, token);
            Expected.Check(durability == "remote_durable", $"{from} acknowledges its probe remote_durable (answered {durability ?? "no durability"})");
            _ = await Converge.UntilAsync(() => Docs.ReadAsync(cluster[to], cluster.Document(id), token),
                copy => Docs.Canonical(copy) == Docs.Canonical(body), $"{from} write confirmed by {to}", token);
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
    private static async Task CheckTombstonesAsync(Cluster cluster, string id, CancellationToken token = default)
    {
        _ = await Converge.UntilAsync(
            async () => new Tombstones(await Task.WhenAll(cluster.Regions.Select(async region => (region, await Docs.TombstoneAsync(cluster[region], cluster.Document(id), token))))),
            tombstones => tombstones.Tags.All(tag => tag.Tag is { Length: > 0 }) && tombstones.Tags.Select(tag => tag.Tag).Distinct(StringComparer.Ordinal).Count() == 1,
            $"every region's tombstone ETag for {id}", token);
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
            await connection.OpenAsync(token);
            await using var query = connection.CreateCommand();
            query.CommandText = "SELECT count(*) FROM _tb_state WHERE collection = $collection AND id = $id AND live = 0";
            _ = query.Parameters.AddWithValue("$collection", cluster.Collection);
            _ = query.Parameters.AddWithValue("$id", id);
            Expected.Check((long)(await query.ExecuteScalarAsync(token))! == 1, $"{region} retains the replicated tombstone for {id}");
        }
    }

    // Each region's tombstone ETag, named in a failure: a 404 without one is a document never written there (or collected).
    internal sealed record Tombstones((string Region, string? Tag)[] Tags)
    {
        public override string ToString() => string.Join(' ', Tags.Select(tag => $"{tag.Region}={tag.Tag ?? "no-tombstone(never-written-or-collected)"}"));
    }
}
