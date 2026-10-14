using System.Net;
using System.Text.Json;
using TerraBase.Engine.Lattices;
using TerraBase.Engine.Sqlite;
using TerraBase.Features.Replication;
using TerraBase.Features.Replication.V1;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.E2E;

/// <summary>Only the surviving region's disk can supply a write after its origin is killed (decision 0013a).</summary>
public sealed class RemoteAckE2ETests
{
    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    [Arguments(Cluster.Gru, Cluster.Sjc)]
    [Arguments(Cluster.Sjc, Cluster.Gru)]
    public async Task A_201_survives_the_origins_immediate_SIGKILL_and_the_remote_regions_restart(string origin, string remote)
    {
        Skip.When(Cluster.FlyRequested, "kills disposable local region processes");
        await using var regions = await RegionProcesses.StartAsync(origin, remote);
        await ConnectedAsync(regions, origin, remote);
        var database = await DatabaseAsync(regions, origin);
        var id = EntityIds.New().ToString();
        var uri = regions.Document(id);
        var value = EntityIds.New().ToString();
        using var response = await regions[origin].PutAsJsonAsync(uri, new { value });

        // Kill immediately after the response arrives, before parsing it or reading either region.
        await regions.KillAsync(origin);
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
        await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("durability").GetString()).IsEqualTo("remote_durable");

        // Never restart the origin: neither a later send nor a refill from its surviving file can rescue this write.
        await CheckAsync();
        var (incarnation, before) = await PeerDocumentsAsync(regions[remote], database, [id]);
        await Assert.That(before).ContainsKey(id);
        await Assert.That(before[id].GetProperty("value").GetString()).IsEqualTo(value);
        await regions.KillAsync(remote);
        await regions.StartAgainAsync(remote);

        // Client reads fail closed until the dead origin can vouch for the catalog. The repair RPC deliberately remains
        // available: read the same pinned disk through it, without reviving any machine that could rescue this write.
        using var gated = await regions[remote].GetAsync(uri);
        await Assert.That(gated.StatusCode).IsEqualTo(HttpStatusCode.ServiceUnavailable);
        await Assert.That((await gated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString()).IsEqualTo("catalog_unverified");
        var (_, after) = await PeerDocumentsAsync(regions[remote], database, [id], incarnation);
        await Assert.That(after).ContainsKey(id);
        await Assert.That(after[id].GetProperty("value").GetString()).IsEqualTo(value);

        async Task CheckAsync()
        {
            using var read = await regions[remote].GetAsync(uri);
            await Assert.That(read.StatusCode).IsEqualTo(HttpStatusCode.OK).Because(await read.Content.ReadAsStringAsync());
            var body = await read.Content.ReadFromJsonAsync<JsonElement>();
            await Assert.That(body.GetProperty("document").GetProperty("value").GetString()).IsEqualTo(value);
        }
    }

    [Test]
    [Scenario(Nodes.Processes, Faults.ProcessKill)]
    public async Task A_killed_origins_durable_change_log_ships_to_a_region_that_missed_its_confirmed_writes()
    {
        Skip.When(Cluster.FlyRequested, "kills disposable local region processes");
        await using var regions = await RegionProcesses.StartAsync(Cluster.Gru, Cluster.Sjc, Cluster.Fra);
        await regions.KillAsync(Cluster.Fra);
        await ConnectedAsync(regions, Cluster.Gru, Cluster.Sjc);
        var database = await DatabaseAsync(regions, Cluster.Gru);
        var acknowledged = new List<(string Id, int Step)>();
        for (var step = 0; step < 200; step++)
        {
            var id = EntityIds.New().ToString();
            var uri = regions.Document(id);
            using var response = await regions[Cluster.Gru].PutAsJsonAsync(uri, new { step });
            await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.Created).Because(await response.Content.ReadAsStringAsync());
            await Assert.That((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("durability").GetString()).IsEqualTo("remote_durable");
            acknowledged.Add((id, step));
        }

        var shipped = await regions[Cluster.Gru].GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative));
        var source = shipped.GetProperty("peers").EnumerateArray().First(peer => peer.GetProperty("region").GetString() == Cluster.Sjc)
            .GetProperty("databases").EnumerateArray().Single(copy => copy.GetProperty("databaseId").GetString() == database.DatabaseId);
        var incarnation = source.GetProperty("incarnation").GetString();
        var head = source.GetProperty("head").GetInt64();
        await Assert.That(head).IsGreaterThanOrEqualTo(acknowledged.Count);

        await regions.KillAsync(Cluster.Gru);
        await regions.KillAsync(Cluster.Sjc);
        await regions.StartAgainAsync(Cluster.Fra);
        await regions.StartAgainAsync(Cluster.Gru);
        await regions.StartAgainAsync(Cluster.Sjc);
        // Placement reads require a verified catalog. Restore its witnesses, then prove the original CDC position survived
        // gru's SIGKILL and was acknowledged by fra; document convergence alone could be supplied by sjc's repair state.
        _ = await Converge.UntilAsync(() => regions[Cluster.Gru].GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative)),
            status => status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() == Cluster.Fra
                && peer.GetProperty("databases").EnumerateArray().Any(copy => copy.GetProperty("databaseId").GetString() == database.DatabaseId
                    && copy.GetProperty("incarnation").GetString() == incarnation && copy.GetProperty("head").GetInt64() >= head
                    && copy.GetProperty("acknowledged").GetInt64() >= head)), "gru's original CDC head ships to fra after its SIGKILL");
        _ = await Converge.UntilAsync(async () =>
        {
            var (_, documents) = await PeerDocumentsAsync(regions[Cluster.Fra], database, [.. acknowledged.Select(write => write.Id)]);
            var missing = acknowledged.Where(write => !documents.TryGetValue(write.Id, out var document)
                || document.GetProperty("step").GetInt32() != write.Step).ToArray();
            return (Missing: missing.Length, FirstMissing: missing.FirstOrDefault().Id);
        }, snapshot => snapshot.Missing == 0, "gru's confirmed writes on fra after gru's SIGKILL");
    }

    private static async Task ConnectedAsync(RegionProcesses regions, string origin, string remote)
    {
        _ = await Converge.UntilAsync(() => regions[origin].GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative)),
            status => status.GetProperty("peers").EnumerateArray().Any(peer => peer.GetProperty("region").GetString() == remote),
            $"{origin} streams to {remote}");
    }

    private static async Task<Database> DatabaseAsync(RegionProcesses regions, string region)
    {
        var project = regions.DatabasePath.Split('/')[3];
        var status = await Converge.UntilAsync(() => regions[region].GetFromJsonAsync<JsonElement>(new Uri("/replication", UriKind.Relative)),
            snapshot => snapshot.GetProperty("peers").EnumerateArray().SelectMany(peer => peer.GetProperty("databases").EnumerateArray())
                .Any(database => database.GetProperty("projectId").GetString() == project), "the process database's replication identity");
        var database = status.GetProperty("peers").EnumerateArray().SelectMany(peer => peer.GetProperty("databases").EnumerateArray())
            .First(database => database.GetProperty("projectId").GetString() == project);
        return new Database
        {
            OrganizationId = database.GetProperty("organizationId").GetString(),
            ProjectId = project,
            DatabaseId = database.GetProperty("databaseId").GetString(),
        };
    }

    private static async Task<(Ulid Incarnation, Dictionary<string, JsonElement> Documents)> PeerDocumentsAsync(
        HttpClient region, Database database, string[] ids, Ulid? expected = null)
    {
        using var deadline = new CancellationTokenSource(Converge.Deadline);
        using var channel = PeerChannel.Create(region.BaseAddress!, PeerTls.Credential);
        var peer = new Features.Replication.V1.Replication.ReplicationClient(channel);
        var request = database.Clone();
        if (expected is { } pin)
        {
            request.Incarnation = Google.Protobuf.ByteString.CopyFrom(pin.ToByteArray());
        }

        var digest = await peer.DigestAsync(new DigestRequest { Database = request }, cancellationToken: deadline.Token);
        await Assert.That(digest.Held).IsTrue();
        var incarnation = new Ulid(digest.Incarnation.Span);
        if (expected is { } previous)
        {
            await Assert.That(incarnation).IsEqualTo(previous);
        }

        request.Incarnation = digest.Incarnation;
        var buckets = new BucketRequest { Database = request };
        // RegionProcesses provisions the e2e collection.
        buckets.Buckets.AddRange(ids.Select(id => (uint)SqliteStorageTransaction.Bucket("e2e", id)).Distinct());
        using var call = peer.ReadBucket(buckets, cancellationToken: deadline.Token);
        var documents = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        while (await call.ResponseStream.MoveNext(deadline.Token))
        {
            var page = call.ResponseStream.Current;
            await Assert.That(new Ulid(page.Incarnation.Span)).IsEqualTo(incarnation);
            foreach (var state in StateList.Parser.ParseFrom(ApplicationEnvelope.Decode(page.States.Span, ReplicationProtocol.MaxApplicationBytes)).States)
            {
                if (state.Collection == "e2e" && DocumentStateEncoding.Decode(state.State.Span).Materialize() is { } body)
                {
                    documents.Add(state.Id, JsonSerializer.Deserialize<JsonElement>(body));
                }
            }
        }

        return (incarnation, documents);
    }
}
