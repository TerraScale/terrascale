using System.Text;
using System.Text.Json;
using TerraBase;

IdentifierTests.Run();
InterNodeContractTests.Run();
await StoragePortIsAtomicAndVersioned();
await DurableStorageSurvivesCloseAndReopen();
await ConcurrentFileStorageProvidersRetainWrites();
await DurableStorageRecoversSnapshotsAndRejectsNullDocuments();
await DurableStorageRetainsVersionAfterDeleteAndRecreate();
await ProductionReengineeringTests.RunAsync();
await TransactionTests.RunAsync();
MetricsAreBoundedAndPrometheusCompatible();
InFlightMetricsAreSnapshotAndPrometheusCompatible();
HealthReportsRequireEveryRequiredComponent();
LifecycleAndReadinessFailClosed();
Console.WriteLine("TerraBase focused tests passed.");

static async Task StoragePortIsAtomicAndVersioned()
{
    await using var engine = TerraBaseEngine.Open();
    var key = new DocumentKey(new TenantId("tenant-a"), new CollectionId("users"), "user-1");
    using var body = JsonDocument.Parse("{\"name\":\"Ada\"}");

    var created = await engine.Documents.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
    Assert(created.Outcome == WriteOutcome.Created, "the first write must create a document");
    Assert(created.Document?.Version == 1, "the first version must be one");

    var conflict = await engine.Documents.UpsertAsync(key, body.RootElement, DocumentPrecondition.Version(99));
    Assert(conflict.Outcome == WriteOutcome.Conflict, "a stale version must be refused");

    using var changedBody = JsonDocument.Parse("{\"name\":\"Grace\"}");
    var updated = await engine.Documents.UpsertAsync(key, changedBody.RootElement, DocumentPrecondition.Version(1));
    Assert(updated.Outcome == WriteOutcome.Updated && updated.Document?.Version == 2, "a matching version must update atomically");

    var read = await engine.Documents.ReadAsync(key);
    Assert(read?.Body.GetProperty("name").GetString() == "Grace", "reads must return the committed body");

    var scanned = new List<Document>();
    await foreach (var document in engine.Documents.ScanAsync(key.Tenant, key.Collection))
    {
        scanned.Add(document);
    }

    Assert(scanned.Count == 1 && scanned[0].Key == key, "collection scans must be deterministic");
    var deleted = await engine.Documents.DeleteAsync(key, DocumentPrecondition.Version(2));
    Assert(deleted.Outcome == WriteOutcome.Deleted, "a matching delete must remove the document");
    Assert(await engine.Documents.ReadAsync(key) is null, "deleted documents must not be readable");

    var recreated = await engine.Documents.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
    Assert(recreated.Outcome == WriteOutcome.Created && recreated.Document?.Version == 3, "recreated documents must retain monotonic versions");
    var staleRecreate = await engine.Documents.UpsertAsync(key, body.RootElement, DocumentPrecondition.Version(2));
    Assert(staleRecreate.Outcome == WriteOutcome.Conflict, "a deleted document version must not authorize its recreation");
}

static async Task DurableStorageSurvivesCloseAndReopen()
{
    var path = Path.Combine(Path.GetTempPath(), $"terrabase-storage-{Guid.NewGuid():N}.json");
    var key = new DocumentKey(new TenantId("tenant-durable"), new CollectionId("users"), "user-1");
    try
    {
        await using (var provider = new FileStorageProvider(path))
        {
            using var body = JsonDocument.Parse("{\"name\":\"Ada\"}");
            var result = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
            Assert(result.Outcome == WriteOutcome.Created && result.Document?.Version == 1, "durable writes must commit the first version");
        }

        await using (var provider = new FileStorageProvider(path))
        {
            var read = await provider.ReadAsync(key);
            Assert(read is not null && read.Version == 1 && read.Body.GetProperty("name").GetString() == "Ada", "durable documents must survive close and reopen");
            var scanned = new List<Document>();
            await foreach (var document in provider.ScanAsync(key.Tenant, key.Collection))
            {
                scanned.Add(document);
            }

            Assert(scanned.Count == 1 && scanned[0].Key == key, "reopened durable scans must expose committed documents");
        }
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task ConcurrentFileStorageProvidersRetainWrites()
{
    var path = Path.Combine(Path.GetTempPath(), $"terrabase-storage-concurrent-{Guid.NewGuid():N}.json");
    var firstKey = new DocumentKey(new TenantId("tenant-concurrent"), new CollectionId("users"), "user-1");
    var secondKey = new DocumentKey(new TenantId("tenant-concurrent"), new CollectionId("users"), "user-2");
    try
    {
        await using (var first = new FileStorageProvider(path))
        await using (var second = new FileStorageProvider(path))
        {
            using var firstBody = JsonDocument.Parse("{\"name\":\"Ada\"}");
            using var secondBody = JsonDocument.Parse("{\"name\":\"Grace\"}");
            var writes = await Task.WhenAll(
                Task.Run(async () => await first.UpsertAsync(firstKey, firstBody.RootElement, DocumentPrecondition.Missing)),
                Task.Run(async () => await second.UpsertAsync(secondKey, secondBody.RootElement, DocumentPrecondition.Missing)));

            Assert(writes.All(result => result.Outcome == WriteOutcome.Created), "concurrent providers must commit both first writes");
            Assert((await first.ReadAsync(secondKey))?.Body.GetProperty("name").GetString() == "Grace", "a provider must reload writes committed by another provider");
            Assert((await second.ReadAsync(firstKey))?.Body.GetProperty("name").GetString() == "Ada", "each provider must retain the other provider's write");
        }

        await using (var reopened = new FileStorageProvider(path))
        {
            Assert(await reopened.ReadAsync(firstKey) is not null, "the first concurrent write must remain durable");
            Assert(await reopened.ReadAsync(secondKey) is not null, "the second concurrent write must remain durable");
        }
    }
    finally
    {
        File.Delete(path);
    }
}

static async Task DurableStorageRecoversSnapshotsAndRejectsNullDocuments()
{
    var path = Path.Combine(Path.GetTempPath(), $"terrabase-storage-recovery-{Guid.NewGuid():N}.json");
    var temporaryPath = $"{path}.orphan.tmp";
    var key = new DocumentKey(new TenantId("tenant-recovery"), new CollectionId("users"), "user-1");
    try
    {
        await using (var provider = new FileStorageProvider(path))
        {
            using var body = JsonDocument.Parse("{\"name\":\"Ada\"}");
            await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
        }

        File.WriteAllText(temporaryPath, "not-json");
        await using (var provider = new FileStorageProvider(path))
        {
            Assert(await provider.ReadAsync(key) is not null, "a valid snapshot must win over an orphan temporary file");
        }

        File.WriteAllText(path, "{\"FormatVersion\":1,\"Documents\":null}");
        File.WriteAllText(
            temporaryPath,
            "{\"FormatVersion\":1,\"Documents\":[{\"Tenant\":\"tenant-recovery\",\"Collection\":\"users\",\"DocumentId\":\"user-1\",\"Body\":\"{\\\"name\\\":\\\"Grace\\\"}\",\"Version\":2,\"UpdatedAt\":\"2026-09-13T00:00:00+00:00\"}]}");
        await using (var provider = new FileStorageProvider(path))
        {
            var recovered = await provider.ReadAsync(key);
            Assert(recovered?.Version == 2 && recovered.Body.GetProperty("name").GetString() == "Grace", "a valid orphan snapshot must recover a corrupt primary snapshot");
        }

        Assert(!File.Exists(temporaryPath), "recovery must remove the promoted temporary snapshot");

        File.WriteAllText(path, "{\"FormatVersion\":1,\"Documents\":[null]}");
        var rejected = false;
        try
        {
            await using var provider = new FileStorageProvider(path);
        }
        catch (InvalidDataException)
        {
            rejected = true;
        }

        Assert(rejected, "null persisted documents must fail closed as invalid data");
    }
    finally
    {
        File.Delete(path);
        File.Delete(temporaryPath);
    }
}

static async Task DurableStorageRetainsVersionAfterDeleteAndRecreate()
{
    var path = Path.Combine(Path.GetTempPath(), $"terrabase-storage-versions-{Guid.NewGuid():N}.json");
    var key = new DocumentKey(new TenantId("tenant-versions"), new CollectionId("users"), "user-1");
    try
    {
        await using (var provider = new FileStorageProvider(path))
        {
            using var body = JsonDocument.Parse("{\"name\":\"Ada\"}");
            var created = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
            Assert(created.Document?.Version == 1, "durable version sequence must start at one");
            var deleted = await provider.DeleteAsync(key, DocumentPrecondition.Version(1));
            Assert(deleted.Outcome == WriteOutcome.Deleted, "the current durable version must authorize delete");
            var recreated = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
            Assert(recreated.Document?.Version == 2, "durable recreation must advance the version watermark");
            var stale = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Version(1));
            Assert(stale.Outcome == WriteOutcome.Conflict, "a deleted version must remain stale after recreation");
        }

        await using (var provider = new FileStorageProvider(path))
        {
            var deleted = await provider.DeleteAsync(key, DocumentPrecondition.Version(2));
            Assert(deleted.Outcome == WriteOutcome.Deleted, "the recreated durable document must be deletable at its current version");
            using var body = JsonDocument.Parse("{\"name\":\"Grace\"}");
            var recreated = await provider.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
            Assert(recreated.Document?.Version == 3, "durable version watermarks must survive close and reopen");
        }
    }
    finally
    {
        File.Delete(path);
    }
}

static void MetricsAreBoundedAndPrometheusCompatible()
{
    var metrics = new MetricsRegistry();
    metrics.SetComponentHealth(Component.Storage, HealthStatus.Healthy);
    metrics.SetComponentHealth(Component.Replication, HealthStatus.Healthy);
    metrics.SetComponentHealth(Component.Membership, HealthStatus.Healthy);
    metrics.SetServiceState(ServiceState.Serving);
    metrics.RecordRequest(Region.Gru, Operation.Read, Outcome.Success, TimeSpan.FromMilliseconds(8));
    metrics.RecordRequest(Region.Gru, Operation.Read, Outcome.Success, TimeSpan.FromMilliseconds(60));

    var snapshot = metrics.Snapshot();
    var series = snapshot.Requests.Single();
    Assert(snapshot.Ready, "serving with required healthy components must be ready");
    Assert(series.Total == 2 && series.Duration.CumulativeBuckets[1] == 1, "histograms must expose cumulative buckets");

    var exposition = snapshot.RenderPrometheus();
    Assert(exposition.Contains("terrabase_requests_total{region=\"GRU\",operation=\"read\",outcome=\"success\"} 2", StringComparison.Ordinal), "request counters must use closed labels");
    Assert(exposition.Contains("terrabase_request_duration_seconds_bucket", StringComparison.Ordinal), "request histograms must be rendered");
    Assert(!exposition.Contains("tenant-a", StringComparison.Ordinal), "customer identities must not become metric labels");
}

static void InFlightMetricsAreSnapshotAndPrometheusCompatible()
{
    var metrics = new MetricsRegistry();
    using var request = metrics.BeginRequest(Region.Fra, Operation.Query);

    var active = metrics.Snapshot();
    Assert(active.InFlight.Single() is { Operation: Operation.Query, Count: 1 }, "a live request must be represented in the in-flight snapshot");
    Assert(active.RenderPrometheus().Contains("terrabase_in_flight_operations{operation=\"query\"} 1", StringComparison.Ordinal), "in-flight gauges must use the closed operation label");

    request.Complete(Outcome.Success);
    var completed = metrics.Snapshot();
    Assert(completed.InFlight.Single() is { Operation: Operation.Query, Count: 0 }, "completed requests must leave a zero in-flight gauge");
}

static void LifecycleAndReadinessFailClosed()
{
    var metrics = new MetricsRegistry();
    Assert(!metrics.Snapshot().Ready, "starting services must not be ready");
    Assert(metrics.Snapshot().IsLive, "starting services must be live");
    metrics.SetComponentHealth(Component.Storage, HealthStatus.Healthy);
    metrics.SetComponentHealth(Component.Replication, HealthStatus.Healthy);
    metrics.SetComponentHealth(Component.Membership, HealthStatus.Healthy);
    metrics.SetServiceState(ServiceState.Serving);
    metrics.SetServiceState(ServiceState.Draining);
    metrics.SetServiceState(ServiceState.Stopped);
    Assert(!metrics.Snapshot().IsLive, "stopped services must not be live");

    var invalid = false;
    try
    {
        metrics.SetServiceState(ServiceState.Serving);
    }
    catch (InvalidOperationException)
    {
        invalid = true;
    }

    Assert(invalid, "a stopped service must not return to serving");
}

static void HealthReportsRequireEveryRequiredComponent()
{
    var empty = new HealthReport(HealthStatus.Healthy, []);
    Assert(!empty.IsReady, "a report without required checks must not be ready");

    var duplicate = new HealthReport(
        HealthStatus.Healthy,
        [
            new HealthCheck(Component.Storage, HealthStatus.Healthy),
            new HealthCheck(Component.Storage, HealthStatus.Healthy),
            new HealthCheck(Component.Replication, HealthStatus.Healthy),
            new HealthCheck(Component.Membership, HealthStatus.Healthy),
        ]);
    Assert(!duplicate.IsReady, "duplicate required checks must not be treated as a trustworthy readiness observation");
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}
