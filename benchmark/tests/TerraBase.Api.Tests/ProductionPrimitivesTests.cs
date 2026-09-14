using System.Net;
using System.Text;
using System.Text.Json;
using TerraBase.Replication;
using TerraBase.Sync;

namespace TerraBase.Api.Tests;

public sealed class ProductionPrimitivesTests
{
    [Test]
    public async Task LeaderlessReplicationDefersDependenciesAndConvergesWithoutParsingCleanBytes()
    {
        var key = new DocumentKey(new TenantId("tenant"), new CollectionId("items"), "one");
        var bytesA = Encoding.UTF8.GetBytes("{\"a\":1}");
        // A causally-following write carries the complete canonical document,
        // so the receiver can apply it without reparsing the predecessor.
        var bytesB = Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}");
        var a = new LeaderlessReplicationEngine();
        var b = new LeaderlessReplicationEngine();
        var first = ReplicationRecord.FromCanonicalBytes(key, new("a", 1), new Dictionary<string, ulong>(), 1, false, bytesA);
        var dependent = ReplicationRecord.FromCanonicalBytes(key, new("b", 1), new Dictionary<string, ulong> { ["a"] = 1 }, 2, false, bytesB);
        await Assert.That(b.Apply(dependent).Outcome).IsEqualTo(ReplicationApplyOutcome.Deferred);
        var applied = a.Apply(first);
        await Assert.That(applied.Body).IsNull();
        await Assert.That(applied.BodyBytes).IsNotNull();
        b.Apply(first);
        b.Apply(dependent);
        var merged = a.Apply(dependent);
        await Assert.That(merged.Outcome).IsEqualTo(ReplicationApplyOutcome.Applied);
        await Assert.That(merged.Body).IsNull();
        await Assert.That(merged.BodyBytes).IsNotNull();
        var left = a.Read(key);
        var right = b.Read(key);
        await Assert.That(left?.GetProperty("a").GetInt32()).IsEqualTo(1);
        await Assert.That(left?.GetProperty("b").GetInt32()).IsEqualTo(2);
        await Assert.That(right?.GetRawText()).IsEqualTo(left?.GetRawText());

        var concurrentA = ReplicationRecord.FromCanonicalBytes(key, new("region-a", 2), new Dictionary<string, ulong>(), 10, false, Encoding.UTF8.GetBytes("{\"left\":true}"));
        var concurrentB = ReplicationRecord.FromCanonicalBytes(key, new("region-b", 2), new Dictionary<string, ulong>(), 11, false, Encoding.UTF8.GetBytes("{\"right\":true}"));
        var c1 = new LeaderlessReplicationEngine();
        var c2 = new LeaderlessReplicationEngine();
        c1.Apply(concurrentA); c1.Apply(concurrentB);
        c2.Apply(concurrentB); c2.Apply(concurrentA);
        await Assert.That(c1.Read(key)?.GetRawText()).IsEqualTo(c2.Read(key)?.GetRawText());
    }

    [Test]
    public async Task SyncTransportRequiresTls()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => Task.Run(() => new SyncTransportOptions(new Uri("http://localhost"))));
        await using var transport = new SyncTransport(new SyncTransportOptions(new Uri("https://localhost")), new HttpClient(new StubHandler()));
        await Assert.That(await transport.GetSnapshotAsync("/snapshot")).IsNotNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
    }


}
