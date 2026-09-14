using System.Text;
using System.Text.Json;
using TerraBase;
using TerraBase.Replication;

internal static class ProductionReengineeringTests
{
    internal static async Task RunAsync()
    {
        await DurableEngineReopenRetainsEveryCommittedDocument();
        PartitionDefersOnlyTheMissingCausalDependency();
        DuplicateDeliveryIsIdempotent();
        ReorderedConcurrentDeliveryConvergesToOneCanonicalBody();
        ClockSkewDoesNotOverrideCausalOrder();
        MixedBodyRepresentationsRemainReadableDuringRollingUpgrade();
    }

    private static async Task DurableEngineReopenRetainsEveryCommittedDocument()
    {
        var path = Path.Combine(Path.GetTempPath(), $"terrabase-production-durable-{Guid.NewGuid():N}.json");
        var tenant = new TenantId("tenant-production-durable");
        var collection = new CollectionId("records");
        var firstKey = new DocumentKey(tenant, collection, "first");
        var secondKey = new DocumentKey(tenant, collection, "second");

        try
        {
            await using (var engine = TerraBaseEngine.Open(path))
            {
                using var firstBody = JsonDocument.Parse("{\"value\":\"first\"}");
                using var secondBody = JsonDocument.Parse("{\"value\":\"second\"}");
                var first = await engine.Documents.UpsertAsync(firstKey, firstBody.RootElement, DocumentPrecondition.Missing);
                var second = await engine.Documents.UpsertAsync(secondKey, secondBody.RootElement, DocumentPrecondition.Missing);

                Assert(first.Outcome == WriteOutcome.Created && first.Document?.Version == 1,
                    "a durable engine must assign version one to its first document");
                Assert(second.Outcome == WriteOutcome.Created && second.Document?.Version == 1,
                    "document versions must be independent per key");
            }

            await using (var reopened = TerraBaseEngine.Open(path))
            {
                var first = await reopened.Documents.ReadAsync(firstKey);
                var second = await reopened.Documents.ReadAsync(secondKey);
                Assert(first is { Version: 1 } && first.Body.GetProperty("value").GetString() == "first",
                    "the first committed body and version must survive engine close and reopen");
                Assert(second is { Version: 1 } && second.Body.GetProperty("value").GetString() == "second",
                    "the second committed body and version must survive engine close and reopen");

                var scanned = new List<Document>();
                await foreach (var document in reopened.Documents.ScanAsync(tenant, collection))
                {
                    scanned.Add(document);
                }

                Assert(scanned.Select(document => document.Key.DocumentId).SequenceEqual(["first", "second"]),
                    "a reopened durable collection must retain deterministic key order");
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void PartitionDefersOnlyTheMissingCausalDependency()
    {
        var tenant = new TenantId("tenant-partition");
        var collection = new CollectionId("records");
        var key = new DocumentKey(tenant, collection, "causal");
        var unrelatedKey = new DocumentKey(tenant, collection, "unrelated");
        var prerequisite = Canonical(key, "origin-a", 1, new Dictionary<string, ulong>(), 10, "{\"phase\":\"base\"}");
        var dependent = Canonical(key, "origin-b", 1, new Dictionary<string, ulong> { ["origin-a"] = 1 }, 1,
            "{\"phase\":\"dependent\",\"complete\":true}");
        var unrelated = Canonical(unrelatedKey, "origin-c", 1, new Dictionary<string, ulong>(), 11, "{\"available\":true}");
        var replica = new LeaderlessReplicationEngine();

        var deferred = replica.ImportDelta([dependent, unrelated, prerequisite]);
        Assert(deferred.Count == 1 && deferred[0] == dependent,
            "a partitioned replica must report only the record whose causal dependency is absent");
        Assert(replica.Frontier.Count == 2
            && replica.Frontier["origin-a"] == 1
            && replica.Frontier["origin-c"] == 1,
            "deferred delivery must not advance the frontier for the missing origin");
        Assert(replica.Read(key)?.GetProperty("phase").GetString() == "base",
            "a dependent write must remain invisible until its prerequisite arrives");

        var repaired = replica.ImportDelta(deferred);
        Assert(repaired.Count == 0, "a deferred record must apply after its dependency is repaired");
        var read = replica.Read(key);
        Assert(read?.GetProperty("phase").GetString() == "dependent"
            && read.Value.GetProperty("complete").GetBoolean(),
            "repair must expose the causally dependent body");
        Assert(replica.Frontier["origin-b"] == 1,
            "applying a repaired dependency must advance its origin frontier");
    }

    private static void DuplicateDeliveryIsIdempotent()
    {
        var key = new DocumentKey(new TenantId("tenant-duplicates"), new CollectionId("records"), "one");
        var record = Canonical(key, "origin-a", 1, new Dictionary<string, ulong>(), 20, "{\"value\":1}");
        var replica = new LeaderlessReplicationEngine();

        var first = replica.Apply(record);
        var duplicate = replica.Apply(record);
        var sameDot = replica.Apply(Canonical(key, "origin-a", 1, new Dictionary<string, ulong>(), 99, "{\"value\":999}"));

        Assert(first.Outcome == ReplicationApplyOutcome.Applied,
            "the first delivery of a replication dot must apply");
        Assert(duplicate.Outcome == ReplicationApplyOutcome.Duplicate
            && sameDot.Outcome == ReplicationApplyOutcome.Duplicate,
            "retries and a replay with the same dot must be identified as duplicates");
        Assert(replica.ExportDelta(new Dictionary<string, ulong>()).Count == 1,
            "duplicate delivery must not append another operation-log entry");
        Assert(replica.Read(key)?.GetProperty("value").GetInt32() == 1,
            "duplicate delivery must not change the materialized body");
    }

    private static void ReorderedConcurrentDeliveryConvergesToOneCanonicalBody()
    {
        var key = new DocumentKey(new TenantId("tenant-reordering"), new CollectionId("records"), "one");
        var records = new[]
        {
            Canonical(key, "origin-a", 1, new Dictionary<string, ulong>(), 10, "{\"alpha\":\"a\"}"),
            Canonical(key, "origin-b", 1, new Dictionary<string, ulong>(), 30, "{\"beta\":\"b\"}"),
            Canonical(key, "origin-c", 1, new Dictionary<string, ulong>(), 20, "{\"gamma\":\"c\"}"),
        };
        var permutations = new[]
        {
            new[] { 0, 1, 2 },
            new[] { 2, 0, 1 },
            new[] { 1, 2, 0 },
        };
        var expected = Encoding.UTF8.GetBytes("{\"alpha\":\"a\",\"beta\":\"b\",\"gamma\":\"c\"}");

        foreach (var permutation in permutations)
        {
            var replica = new LeaderlessReplicationEngine();
            ReplicationApplyResult? last = null;
            foreach (var index in permutation)
            {
                last = replica.Apply(records[index]);
            }

            var read = replica.Read(key);
            Assert(read?.GetRawText() == Encoding.UTF8.GetString(expected),
                "reordered concurrent delivery must materialize the same canonical JSON body");
            Assert(last?.BodyBytes is { } bytes && bytes.Span.SequenceEqual(expected),
                "the final merge result must expose byte-identical canonical body bytes");
            Assert(replica.ExportDelta(new Dictionary<string, ulong>()).Count == records.Length,
                "reordering must retain each distinct operation exactly once");
        }
    }

    private static void ClockSkewDoesNotOverrideCausalOrder()
    {
        var key = new DocumentKey(new TenantId("tenant-clock-skew"), new CollectionId("records"), "one");
        var first = Canonical(key, "origin-a", 1, new Dictionary<string, ulong>(), 2_000_500, "{\"state\":\"first\"}");
        var causallyLaterWithOlderClock = Canonical(
            key,
            "origin-b",
            1,
            new Dictionary<string, ulong> { ["origin-a"] = 1 },
            2_000_000,
            "{\"state\":\"later\"}");
        var replica = new LeaderlessReplicationEngine();

        Assert(replica.Apply(first).Outcome == ReplicationApplyOutcome.Applied,
            "the first causally independent record must apply");
        Assert(replica.Apply(causallyLaterWithOlderClock).Outcome == ReplicationApplyOutcome.Applied,
            "a causally dependent record must apply even when its HLC is lower");
        Assert(replica.Read(key)?.GetProperty("state").GetString() == "later",
            "causal order must take precedence over a 500 millisecond clock skew");

        var fast = Canonical(key, "origin-fast", 1, new Dictionary<string, ulong>(), 3_000_500, "{\"state\":\"fast\"}");
        var slow = Canonical(key, "origin-slow", 1, new Dictionary<string, ulong>(), 3_000_000, "{\"state\":\"slow\"}");
        var firstOrder = new LeaderlessReplicationEngine();
        var secondOrder = new LeaderlessReplicationEngine();
        firstOrder.Apply(fast);
        firstOrder.Apply(slow);
        secondOrder.Apply(slow);
        secondOrder.Apply(fast);
        Assert(firstOrder.Read(key)?.GetProperty("state").GetString() == "fast"
            && secondOrder.Read(key)?.GetProperty("state").GetString() == "fast",
            "concurrent values within the skew bound must use the HLC tie-break independent of arrival order");
    }

    private static void MixedBodyRepresentationsRemainReadableDuringRollingUpgrade()
    {
        var key = new DocumentKey(new TenantId("tenant-rolling-upgrade"), new CollectionId("records"), "one");
        using var legacyBody = JsonDocument.Parse("{\"version\":1,\"state\":\"legacy\"}");
        var legacyRecord = new ReplicationRecord(
            key,
            new ReplicationDot("legacy-origin", 1),
            new Dictionary<string, ulong>(),
            10,
            false,
            legacyBody.RootElement);
        var upgradedBytes = Encoding.UTF8.GetBytes("{\"state\":\"upgraded\",\"version\":2}");
        var upgradedRecord = ReplicationRecord.FromCanonicalBytes(
            key,
            new ReplicationDot("upgraded-origin", 1),
            new Dictionary<string, ulong> { ["legacy-origin"] = 1 },
            1,
            false,
            upgradedBytes);
        var upgradedReplica = new LeaderlessReplicationEngine();
        var legacyReplica = new LeaderlessReplicationEngine();

        upgradedReplica.Apply(legacyRecord);
        var upgradedResult = upgradedReplica.Apply(upgradedRecord);
        legacyReplica.Apply(legacyRecord);
        var legacyResult = legacyReplica.Apply(upgradedRecord);

        Assert(upgradedResult.Body is null
            && upgradedResult.BodyBytes is { } upgradedBodyBytes
            && upgradedBodyBytes.Span.SequenceEqual(upgradedBytes),
            "a newer canonical-byte record must remain zero-copy on a mixed-version receiver");
        Assert(legacyResult.Body is null
            && legacyResult.BodyBytes is { } legacyBodyBytes
            && legacyBodyBytes.Span.SequenceEqual(upgradedBytes),
            "a legacy body receiver must accept the canonical-byte representation");
        Assert(upgradedReplica.Read(key)?.GetRawText() == Encoding.UTF8.GetString(upgradedBytes)
            && legacyReplica.Read(key)?.GetRawText() == Encoding.UTF8.GetString(upgradedBytes),
            "mixed body representations must converge to the same readable document during upgrade");
        Assert(upgradedReplica.Frontier.SequenceEqual(legacyReplica.Frontier),
            "mixed-version replicas must retain the same causal frontier");
    }

    private static ReplicationRecord Canonical(
        DocumentKey key,
        string origin,
        ulong sequence,
        IReadOnlyDictionary<string, ulong> context,
        long hlc,
        string body) =>
        ReplicationRecord.FromCanonicalBytes(
            key,
            new ReplicationDot(origin, sequence),
            context,
            hlc,
            false,
            Encoding.UTF8.GetBytes(body));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
