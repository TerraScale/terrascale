using System.Net;
using System.Text.Json.Nodes;
using TerraBase.Engine.Cluster;
using TerraBase.Engine.Turso;
using TerraBase.Features.Replication;
using TerraBase.Primitives;
using TerraBase.Testing;

namespace TerraBase.Slow.Replication;

public sealed partial class A6LocalRebuildE2ETests
{
    [Test]
    public async Task An_authenticated_retry_certifies_a_real_pending_fixture_with_fresh_peer_proofs()
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current!.Execution.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        var token = deadline.Token;
        await using var pair = await Pair.StartAsync(token);
        var id = EntityIds.New().ToString();
        var body = new JsonObject { ["recovery_drill"] = EntityIds.New().ToString() };
        await ManagementApi.CreateCollectionAsync(pair.Sjc.Data, pair.Managed, "items", token);
        await pair.Sjc.PutAsync(id, body, token);
        await pair.Gru.UntilAsync(id, body, token);
        await pair.ReplicatedAsync(token);
        var original = await pair.Sjc.IncarnationAsync(token);
        var storage = pair.Sjc.Service<IRebuildStorage>();
        var side = storage.SidePath(pair.Database, original);
        Directory.CreateDirectory(side);
        var store = (TursoDatabaseStore)(await pair.Sjc.Service<TursoStorageEngine>().OpenDatabaseAsync(pair.Database, token)
            ?? throw new InvalidOperationException("missing fixture copy"));
        _ = await store.WithWriterAsync(session =>
        {
            using var command = session.Connection.CreateCommand();
            command.CommandText = "INSERT INTO _tb_merkle(bucket,h1,h2) VALUES(-1,0,0)";
            return command.ExecuteNonQuery();
        }, "damage only the throwaway fixture's derived digest", token);
        // Exercise the same production read/record/wake path as the host-local drill's authenticated Digest RPC.
        _ = await Assert.ThrowsAsync<InvalidDataException>(async () => await pair.Sjc.Service<IReplicaStates>().DigestAsync(pair.Database, null, token));
        using var metrics = new HttpClient { BaseAddress = pair.Sjc.Api.Server.MetricsAddress };
        await pair.UntilAsync(async cancellation =>
            (await metrics.GetStringAsync(new Uri("/metrics", UriKind.Relative), cancellation)).Split('\n')
                .Any(line => line.StartsWith("terrabase_replication_rebuilds_total{", StringComparison.Ordinal)
                    && line.Contains("outcome=\"refused\"", StringComparison.Ordinal)), token);
        var pending = await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, token);
        await Assert.That(pending?.Phase).IsEqualTo(RebuildPhase.Pending);
        await Assert.That(pending?.OldIncarnation).IsEqualTo(original);
        var peer = (await pair.Sjc.Service<IMembership>().RefreshAsync(token)).Elsewhere.Single();
        await Assert.That(await pair.Sjc.Service<AntiEntropy>().PullAsync(ChangeGroups.System, peer, token)).IsNotNull();
        await Assert.That(await pair.Sjc.Service<ICatalogFreshness>().IsFreshAsync(TimeSpan.FromSeconds(30), token)).IsTrue();
        Directory.Delete(side);
        using var owner = pair.Sjc.Api.Service(pair.User);
        var path = ManagementApi.Uri(pair.Managed.Path + "/recovery/retry");
        var sending = owner.PostAsJsonAsync(path, new { faultedIncarnation = original.ToString(), reason = "Approved isolated recovery drill" }, token);
        await pair.UntilAsync(_ => Task.FromResult(sending.IsCompleted), token);
        using var response = await sending;
        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.NoContent);
        var rebuilt = await pair.Sjc.IncarnationAsync(token);
        await Assert.That(rebuilt).IsNotEqualTo(original);
        await Assert.That(await pair.Sjc.Service<IRebuildLedger>().GetAsync(pair.Database, token)).IsNull();
        await Assert.That(File.Exists(side) || Directory.Exists(side)).IsFalse();
        await pair.Sjc.AssertCertificateAsync(pair.Gru, rebuilt, token);
        await pair.Sjc.UntilAsync(id, body, token);
        await pair.Gru.UntilAsync(id, body, token);
        // This exact old-incarnation request cannot initiate another recovery after the record is retired.
        await Assert.That(await pair.Sjc.Service<AntiEntropy>().PullAsync(ChangeGroups.System, peer, token)).IsNotNull();
        using var replay = await owner.PostAsJsonAsync(path, new { faultedIncarnation = original.ToString(), reason = "Completed fixture replay" }, token);
        await Assert.That(replay.StatusCode).IsEqualTo(HttpStatusCode.Conflict);
        await Assert.That(await pair.Sjc.IncarnationAsync(token)).IsEqualTo(rebuilt);
    }
}
