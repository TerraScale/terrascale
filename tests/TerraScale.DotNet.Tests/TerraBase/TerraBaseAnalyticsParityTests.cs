using System.Net;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.DotNet.Tests.TerraBase;

public sealed class TerraBaseAnalyticsParityTests
{
    [Test]
    public async Task Regional_analytics_preserves_int64_counts_and_absent_observations()
    {
        var calls = new List<(string Path, string Query)>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls.Add((request.RequestUri!.AbsolutePath, request.RequestUri.Query));
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""
                {"projectId":"01JA0000000000000000000001","range":"1h","from":"2026-10-21T23:00:00Z","to":"2026-10-22T00:00:00Z","bucketSeconds":60,
                 "items":[{"databaseId":"01JA0000000000000000000002","databaseName":"app","region":"gru","series":[
                 {"start":"2026-10-21T23:00:00Z","requests":{"GET":9007199254740993},"operations":{"put":2},"errors":1,
                  "latency":{"p50Ms":null,"p95Ms":null,"p99Ms":null},"storageBytes":null,
                  "clientBandwidth":{"bytesSent":9007199254740994,"bytesReceived":1},"replicationBandwidth":{"bytesSent":3,"bytesReceived":4},
                  "statements":{"hrana":{"statements":5,"errors":2,"latency":{"p50Ms":1.5,"p95Ms":3,"p99Ms":4}}}}]}]}
                """));
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        var project = await client.Project(TerraBaseIntegrationFixture.ProjectId).GetAnalyticsAsync("1h", "01JA0000000000000000000002", "gru");
        await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").GetAnalyticsAsync("1h", "gru");
        var point = project.Items.Single().Series.Single();
        await Assert.That(point.Requests["GET"]).IsEqualTo(9007199254740993L);
        await Assert.That(point.ClientBandwidth.BytesSent).IsEqualTo(9007199254740994L);
        await Assert.That(point.ReplicationBandwidth.BytesReceived).IsEqualTo(4);
        await Assert.That(point.Statements["hrana"]).IsEqualTo(new TerraBaseStatementUsage(5, 2, new(1.5, 3, 4)));
        await Assert.That(point.Latency.P95Ms).IsNull();
        await Assert.That(point.StorageBytes).IsNull();
        await Assert.That(project.BucketSeconds).IsEqualTo(60);
        await Assert.That(calls[0].Path).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/analytics");
        await Assert.That(calls[0].Query).IsEqualTo("?range=1h&database=01JA0000000000000000000002&region=gru");
        await Assert.That(calls[1].Path).EndsWith("/databases/app/analytics");
        await Assert.That(calls[1].Query).IsEqualTo("?range=1h&region=gru");
    }

    [Test]
    public async Task Replication_unknown_health_remains_null_and_bandwidth_stays_directional()
    {
        var calls = new List<string>();
        using var handler = new TerraBaseTestHandler((request, _) =>
        {
            calls.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""
                {"projectId":"01JA0000000000000000000001","range":"24h","from":"2026-10-21T00:00:00Z","to":"2026-10-22T00:00:00Z","bucketSeconds":3600,
                 "items":[{"databaseId":"01JA0000000000000000000002","databaseName":"app","region":"gru","status":"stopped","replicatedOps":9007199254740993,
                  "lagMs":null,"divergence":null,"convergenceMs":null,"lastApplied":null,
                  "bandwidth":[{"sourceRegion":"gru","destinationRegion":"fra","kind":"changes","bytesSent":8,"bytesReceived":0}],
                  "series":[{"start":"2026-10-21T00:00:00Z","replicatedOps":0,"bandwidth":[]}]}]}
                """));
        });
        using var http = new HttpClient(handler);
        var client = TerraBaseIntegrationFixture.Client(http);
        var result = await client.Project(TerraBaseIntegrationFixture.ProjectId).GetReplicationAsync();
        await client.Database(TerraBaseIntegrationFixture.ProjectId, "app").GetReplicationAsync();
        var item = result.Items.Single();
        await Assert.That(item.ReplicatedOps).IsEqualTo(9007199254740993L);
        await Assert.That(item.LagMs).IsNull();
        await Assert.That(item.Divergence).IsNull();
        await Assert.That(item.LastApplied).IsNull();
        await Assert.That(item.Bandwidth.Single().DestinationRegion).IsEqualTo("fra");
        await Assert.That(item.Series.Single().Bandwidth).IsEmpty();
        await Assert.That(calls[0]).IsEqualTo($"/v1/projects/{TerraBaseIntegrationFixture.ProjectId}/replication");
        await Assert.That(calls[1]).EndsWith("/databases/app/replication");
    }

    [Test]
    public async Task Invalid_analytics_range_surfaces_server_error_once()
    {
        var calls = 0;
        using var handler = new TerraBaseTestHandler((_, _) =>
        {
            calls++;
            return Task.FromResult(TerraBaseIntegrationFixture.Json("""{"error":{"code":"invalid_query","message":"invalid range"}}""", HttpStatusCode.BadRequest));
        });
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsAsync<TerraBaseValidationException>(() => TerraBaseIntegrationFixture.Client(http)
            .Project(TerraBaseIntegrationFixture.ProjectId).GetAnalyticsAsync("invalid"));
        await Assert.That(error!.ErrorCode).IsEqualTo("invalid_query");
        await Assert.That(calls).IsEqualTo(1);
    }
}
