using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TerraBase;
using TerraBase.Api;
using TerraBase.Backup;
using TerraBase.Contracts;
using TerraBase.Replication;
using TerraBase.Replication.Durable;
using TerraBase.Security;
using TerraBase.Storage.Backup;
using TerraBase.Transport;
using TerraBase.Transport.NativeGrpc;
using TerraBase.Transport.ReplicationCheckpoints;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;
using ContractsApplyOutcome = TerraBase.Contracts.ReplicationApplyOutcome;

namespace TerraBase.RestoreLagMeasurement;

/// <summary>
/// Measurement-only harness for backup/restore timing and loopback
/// replication lag against the real BackupManager, StorageEngine,
/// DurableReplicationRuntime, and native gRPC inter-node stack. Cases: backup,
/// restore, chain, lag. Every case prints one JSON line per sample plus one
/// summary line; nothing here qualifies production capacity.
/// </summary>
internal static class Program
{
    private const string Tenant = "restore-perf";
    private const string Collection = "documents";
    private const string SourceId = "restore-perf-source";
    private const int BodyPadding = 196;
    private const int SeedBatchSize = 5_000;
    private const int WarmupDocuments = 1_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "all";
            var runs = IntOption(args, "runs", 3);
            var samples = IntOption(args, "samples", 60);
            var maxDocs = IntOption(args, "max-docs", 50_000);
            var skipWarmup = HasFlag(args, "skip-warmup");
            switch (command)
            {
                case "backup":
                    if (!skipWarmup)
                        await WarmupAsync();
                    await BackupCaseAsync(runs, maxDocs, BackupCaseMode.BackupOnly);
                    break;
                case "restore":
                    if (!skipWarmup)
                        await WarmupAsync();
                    await BackupCaseAsync(runs, maxDocs, BackupCaseMode.RestoreOnly);
                    break;
                case "chain":
                    if (!skipWarmup)
                        await WarmupAsync();
                    await ChainCaseAsync(runs, IntOption(args, "chain-docs", 10_000));
                    break;
                case "lag":
                    await LagCaseAsync(samples);
                    break;
                case "all":
                    await WarmupAsync();
                    await BackupCaseAsync(runs, maxDocs, BackupCaseMode.BackupAndRestore);
                    await ChainCaseAsync(runs, 10_000);
                    await LagCaseAsync(samples);
                    break;
                default:
                    throw new ArgumentException($"Unknown command '{command}'. Use backup|restore|chain|lag|all.");
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"restore-lag-measurement: {exception}");
            return 2;
        }
    }

    /// <summary>One discarded 1k full backup so JIT paths are warm before timing.</summary>
    private static async Task WarmupAsync()
    {
        var root = CreateRoot("warmup");
        try
        {
            await using var engine = new DurableStorageEngine(root);
            await engine.InitializeJournalIdentityAsync();
            await SeedAsync(engine, 0, WarmupDocuments);
            using var source = CreateBackupSource(engine);
            using var kit = ManagerKit.Create();
            _ = await kit.Manager.CreateFullBackupAsync(source);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    // --- Cases 1+2: per-scale timed full backups plus restore of the tip. ----

    private static async Task BackupCaseAsync(int runs, int maxDocs, BackupCaseMode mode)
    {
        foreach (var documents in new[] { 1_000, 10_000, 50_000 })
        {
            if (documents > maxDocs)
                continue;

            var root = CreateRoot($"backup-{documents}");
            try
            {
                await using var engine = new DurableStorageEngine(root);
                await engine.InitializeJournalIdentityAsync();
                await SeedAsync(engine, 0, documents);
                using var source = CreateBackupSource(engine);
                using var kit = ManagerKit.Create();
                ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);
                var samples = new List<BackupSample>();
                BackupManifest manifest = null!;
                for (var run = 0; run < (mode == BackupCaseMode.RestoreOnly ? 0 : runs); run++)
                {
                    var allocatedBefore = GC.GetTotalAllocatedBytes();
                    Phase($"full backup begin documents={documents} run={run}");
                    var start = Stopwatch.GetTimestamp();
                    manifest = await kit.Manager.CreateFullBackupAsync(source);
                    Phase("full backup end");
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    var allocated = GC.GetTotalAllocatedBytes() - allocatedBefore;
                    samples.Add(new BackupSample(
                        documents,
                        run,
                        elapsed,
                        allocated,
                        manifest.Artifacts.Sum(artifact => artifact.Length),
                        manifest.Artifacts.Count,
                        manifest.Kind.ToString()));
                    Emit(samples[^1]);
                }

                if (mode == BackupCaseMode.RestoreOnly)
                {
                    // A restore-only invocation needs one manifest to restore,
                    // produced untimed outside the backup samples.
                    Phase($"full backup begin documents={documents} run=prepare");
                    manifest = await kit.Manager.CreateFullBackupAsync(source);
                    Phase("full backup end");
                }

                var scanned = 0;
                long bodyBytes = 0;
                await foreach (var document in engine.ScanAsync(new TenantId(Tenant), new CollectionId(Collection)))
                {
                    bodyBytes += document.Body.GetRawText().Length;
                    scanned++;
                }

                if (mode == BackupCaseMode.RestoreOnly)
                {
                    await RestoreRunsAsync(kit, manifest, documents, runs);
                    continue;
                }

                EmitSummary(
                    "backup-summary",
                    new Dictionary<string, object>
                    {
                        ["documents"] = documents,
                        ["scannedDocuments"] = scanned,
                        ["meanBodyBytes"] = scanned == 0 ? 0 : bodyBytes / (double)scanned,
                        ["runs"] = runs,
                        ["warmupsBeforeTimedRuns"] = 1,
                        ["wallMs"] = Stats(samples.Select(sample => sample.WallMilliseconds)),
                        ["allocatedBytes"] = Stats(samples.Select(sample => (double)sample.AllocatedBytes)),
                        ["artifactBytes"] = Stats(samples.Select(sample => (double)sample.ArtifactBytes)),
                        ["artifactCount"] = samples[0].ArtifactCount,
                        ["docsPerSecond"] = Stats(samples.Select(sample => documents / (sample.WallMilliseconds / 1000.0))),
                    });

                if (mode == BackupCaseMode.RestoreOnly || mode == BackupCaseMode.BackupAndRestore)
                    await RestoreRunsAsync(kit, manifest, documents, runs);
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    private static async Task RestoreRunsAsync(ManagerKit kit, BackupManifest manifest, int documents, int runs)
    {
        var samples = new List<RestoreSample>();
        for (var run = 0; run < runs; run++)
        {
            var targetRoot = CreateRoot($"restore-target-{documents}-{run}");
            try
            {
                var target = new StorageEngineRestoreTarget(targetRoot);
                Phase("restore plan begin");
                var planStart = Stopwatch.GetTimestamp();
                var plan = await kit.Manager.CreateRestorePlanAsync(manifest);
                var planMs = Stopwatch.GetElapsedTime(planStart).TotalMilliseconds;
                Phase("restore plan end");
                var executeStart = Stopwatch.GetTimestamp();
                Phase("restore execute begin");
                var result = await kit.Manager.RestoreAsync(plan, target);
                var executeMs = Stopwatch.GetElapsedTime(executeStart).TotalMilliseconds;
                Phase("restore execute end");

                var readBackStart = Stopwatch.GetTimestamp();
                var verified = 0;
                var mismatches = 0;
                await using (var restored = new DurableStorageEngine(target.ResolveActivePath()))
                {
                    await foreach (var document in restored.ScanAsync(
                        new TenantId(Tenant),
                        new CollectionId(Collection)))
                    {
                        if (BodiesEqual(document.Body, document.Key.DocumentId))
                            verified++;
                        else
                            mismatches++;
                    }
                }

                var readBackMs = Stopwatch.GetElapsedTime(readBackStart).TotalMilliseconds;
                samples.Add(new RestoreSample(
                    documents,
                    run,
                    planMs,
                    executeMs,
                    planMs + executeMs,
                    readBackMs,
                    verified,
                    mismatches,
                    verified / (readBackMs / 1000.0)));
                Emit(samples[^1]);
            }
            finally
            {
                DeleteRoot(targetRoot);
            }
        }

        EmitSummary(
            "restore-summary",
            new Dictionary<string, object>
            {
                ["documents"] = documents,
                ["runs"] = runs,
                ["planMs"] = Stats(samples.Select(sample => sample.PlanMilliseconds)),
                ["executeMs"] = Stats(samples.Select(sample => sample.ExecuteMilliseconds)),
                ["planPlusExecuteMs"] = Stats(samples.Select(sample => sample.PlanPlusExecuteMilliseconds)),
                ["readBackMs"] = Stats(samples.Select(sample => sample.ReadBackMilliseconds)),
                ["verifiedDocuments"] = samples.Select(sample => sample.Verified).Distinct().ToArray(),
                ["mismatches"] = samples.Select(sample => sample.Mismatches).Distinct().ToArray(),
                ["docsPerSecondReadBack"] = Stats(samples.Select(sample => sample.DocsPerSecondReadBack)),
            });
    }

    // --- Case 3: full backup plus an incremental chain. ---------------------

    private static async Task ChainCaseAsync(int runs, int documents)
    {
        const int incrementals = 3;
        const int incrementalDocuments = 100;
        for (var run = 0; run < runs; run++)
        {
            var root = CreateRoot($"chain-{run}");
            try
            {
                await using var engine = new DurableStorageEngine(root);
                await engine.InitializeJournalIdentityAsync();
                var seeded = await SeedAsync(engine, 0, documents);
                using var source = CreateBackupSource(engine);
                using var kit = ManagerKit.Create();

                var fullStart = Stopwatch.GetTimestamp();
                var full = await kit.Manager.CreateFullBackupAsync(source);
                var fullMs = Stopwatch.GetElapsedTime(fullStart).TotalMilliseconds;

                var incrementalTimes = new List<double>();
                var incrementalArtifactBytes = new List<double>();
                BackupManifest tip = full;
                for (var index = 0; index < incrementals; index++)
                {
                    seeded = await SeedAsync(engine, seeded, incrementalDocuments);
                    var incrementalStart = Stopwatch.GetTimestamp();
                    tip = await kit.Manager.CreateIncrementalBackupAsync(source, tip);
                    incrementalTimes.Add(Stopwatch.GetElapsedTime(incrementalStart).TotalMilliseconds);
                    incrementalArtifactBytes.Add(tip.Artifacts.Sum(artifact => artifact.Length));
                    Emit(new IncrementalSample(
                        run,
                        index,
                        incrementalTimes[^1],
                        tip.Artifacts.Sum(artifact => artifact.Length),
                        tip.Boundary.Sequence));
                }

                var targetRoot = CreateRoot($"chain-target-{run}");
                try
                {
                    var target = new StorageEngineRestoreTarget(targetRoot);
                    var planStart = Stopwatch.GetTimestamp();
                    var plan = await kit.Manager.CreateRestorePlanAsync(tip);
                    var planMs = Stopwatch.GetElapsedTime(planStart).TotalMilliseconds;
                    var executeStart = Stopwatch.GetTimestamp();
                    var result = await kit.Manager.RestoreAsync(plan, target);
                    var executeMs = Stopwatch.GetElapsedTime(executeStart).TotalMilliseconds;
                    var verified = 0;
                    var mismatches = 0;
                    await using (var restored = new DurableStorageEngine(target.ResolveActivePath()))
                    {
                        await foreach (var document in restored.ScanAsync(
                            new TenantId(Tenant),
                            new CollectionId(Collection)))
                        {
                            if (BodiesEqual(document.Body, document.Key.DocumentId))
                                verified++;
                            else
                                mismatches++;
                        }
                    }

                    EmitSummary(
                        "chain-summary",
                        new Dictionary<string, object>
                        {
                            ["run"] = run,
                            ["documents"] = documents,
                            ["incrementals"] = incrementals,
                            ["incrementalDocuments"] = incrementalDocuments,
                            ["fullMs"] = fullMs,
                            ["fullArtifactBytes"] = full.Artifacts.Sum(artifact => artifact.Length),
                            ["incrementalMs"] = Stats(incrementalTimes),
                            ["incrementalArtifactBytes"] = Stats(incrementalArtifactBytes),
                            ["tipBoundarySequence"] = tip.Boundary.Sequence,
                            ["tipPlanMs"] = planMs,
                            ["tipExecuteMs"] = executeMs,
                            ["tipAppliedLogEntries"] = result.AppliedLogEntries,
                            ["verifiedDocuments"] = verified,
                            ["mismatches"] = mismatches,
                            ["expectedDocuments"] = documents + incrementals * incrementalDocuments,
                        });
                }
                finally
                {
                    DeleteRoot(targetRoot);
                }
            }
            finally
            {
                DeleteRoot(root);
            }
        }
    }

    // --- Case 4: loopback replication lag over the real transport. ----------

    private static async Task LagCaseAsync(int samples)
    {
        var database = EntityId.New();
        var tenant = EntityId.New();
        await using var peer = await MeasurementPeer.StartAsync(database, tenant);
        var sourceRoot = CreateRoot("lag-source");
        try
        {
            await using var sourceEngine = new DurableStorageEngine(sourceRoot);
            await sourceEngine.InitializeJournalIdentityAsync();
            await using var sourceRuntime = await DurableReplicationRuntime.OpenAsync(sourceEngine, database);
            await using var client = peer.CreateClient("member-a");

            const string origin = "lag-source";
            var counter = new SequenceCounter();
            foreach (var batchSize in new[] { 1, 16, 128 })
            {
                // Five untimed round trips warm the codec/TLS paths for this
                // batch size so JIT startup does not pollute the percentiles.
                for (var warmup = 0; warmup < 5; warmup++)
                    await PushBatchAsync(client, sourceRuntime, peer, database, tenant, origin, counter, batchSize);

                var ackSamples = new List<double>();
                var visibleSamples = new List<double>();
                var endToEndSamples = new List<double>();
                var pollCounts = new List<double>();
                var durableAcks = 0;
                for (var sample = 0; sample < samples; sample++)
                {
                    var (records, request, context) = await PrepareBatchAsync(
                        sourceRuntime, database, tenant, origin, counter, batchSize);

                    var send = Stopwatch.GetTimestamp();
                    var response = await client
                        .InvokeAsync<ReplicationPushRequest, ReplicationPushResponse>(context, request);
                    var ack = Stopwatch.GetTimestamp();
                    if (response.Value.DurableAcknowledged)
                        durableAcks++;
                    foreach (var result in response.Value.Results)
                    {
                        if (result.Outcome != ContractsApplyOutcome.Applied)
                            throw new InvalidOperationException($"A pushed publication was not applied: {result.Outcome}.");
                    }

                    // Tight loop on the target read model; count attempts so the
                    // polling overhead itself stays visible in the report.
                    var probe = records[^1].Key;
                    var polls = 0;
                    while (peer.Engine.Replication.Read(probe, database) is null)
                        polls++;
                    var visible = Stopwatch.GetTimestamp();

                    ackSamples.Add(Stopwatch.GetElapsedTime(send, ack).TotalMilliseconds);
                    visibleSamples.Add(Stopwatch.GetElapsedTime(ack, visible).TotalMilliseconds);
                    endToEndSamples.Add(Stopwatch.GetElapsedTime(send, visible).TotalMilliseconds);
                    pollCounts.Add(polls);
                }

                EmitSummary(
                    "lag-summary",
                    new Dictionary<string, object>
                    {
                        ["batchSize"] = batchSize,
                        ["samples"] = samples,
                        ["durableAcks"] = durableAcks,
                        ["ackMs"] = Stats(ackSamples),
                        ["ackToVisibleMs"] = Stats(visibleSamples),
                        ["sendToVisibleMs"] = Stats(endToEndSamples),
                        ["pollsUntilVisible"] = Stats(pollCounts),
                        ["firstAttemptVisible"] = pollCounts.Count(value => value == 0),
                    });
            }
        }
        finally
        {
            DeleteRoot(sourceRoot);
        }
    }

    /// <summary>Builds one batch, admits it durably at the source, and returns the wire request.</summary>
    private static async Task<(ReplicationRecord[] Records, ReplicationPushRequest Request, InterNodeRequestContext Context)>
        PrepareBatchAsync(
            DurableReplicationRuntime sourceRuntime,
            EntityId database,
            EntityId tenant,
            string origin,
            SequenceCounter counter,
            int batchSize)
    {
        var hlc = DateTimeOffset.UtcNow.UtcTicks;
        var records = new ReplicationRecord[batchSize];
        var publications = new ReplicationPublication[batchSize];
        for (var index = 0; index < batchSize; index++)
        {
            var sequenceValue = counter.Next + (ulong)index + 1;
            var documentId = $"lag-{sequenceValue:D8}";
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                state = documentId,
                padding = new string('x', BodyPadding),
            });
            var key = new DocumentKey(
                new TenantId(tenant.ToString()),
                new CollectionId("items"),
                documentId);
            records[index] = new ReplicationRecord(
                key,
                new ReplicationDot(origin, sequenceValue),
                new Dictionary<string, ulong>(),
                hlc + (long)sequenceValue,
                false,
                null)
            {
                DatabaseUlid = database,
                CanonicalBody = body,
            };
            publications[index] = new ReplicationPublication
            {
                TenantUlid = tenant,
                Collection = "items",
                DocumentId = documentId,
                Origin = origin,
                Sequence = sequenceValue,
                Context = new Dictionary<string, ulong>(),
                Hlc = hlc + (long)sequenceValue,
                CanonicalBody = body,
            };
        }

        // Source-side durable admission of the same records first.
        foreach (var record in records)
        {
            var appliedAtSource = await sourceRuntime.ApplyAsync(record);
            if (!appliedAtSource.DurableAcknowledged)
                throw new InvalidOperationException("The source runtime did not durably acknowledge a record.");
        }

        counter.Advance(batchSize);
        var request = new ReplicationPushRequest
        {
            DatabaseUlid = database,
            Publications = publications,
        };
        var context = InterNodeRequestContext.Mutation(
            "TerraBase.InterNode",
            "InvokeAsync",
            $"lag-{origin}-{counter.Next}-{EntityId.New()}",
            ReplicationLattice.Digest(records[^1]));
        return (records, request, context);
    }

    /// <summary>One untimed source-admit plus push round trip for warmup.</summary>
    private static async Task PushBatchAsync(
        GrpcInterNodeClient client,
        DurableReplicationRuntime sourceRuntime,
        MeasurementPeer peer,
        EntityId database,
        EntityId tenant,
        string origin,
        SequenceCounter counter,
        int batchSize)
    {
        var (records, request, context) = await PrepareBatchAsync(
            sourceRuntime, database, tenant, origin, counter, batchSize);
        var response = await client.InvokeAsync<ReplicationPushRequest, ReplicationPushResponse>(context, request);
        if (!response.Value.DurableAcknowledged)
            throw new InvalidOperationException("A warmup push was not durably acknowledged.");
        while (peer.Engine.Replication.Read(records[^1].Key, database) is null)
            await Task.Yield();
    }

    // --- Shared plumbing. ----------------------------------------------------

    private static StorageEngineBackupSource CreateBackupSource(DurableStorageEngine engine) =>
        new(
            SourceId,
            engine,
            new StaticMetadataProvider(new StorageBackupMetadataSnapshot(
                [new StorageBackupCollectionScope(Tenant, Collection)],
                [],
                [])));

    /// <summary>Wires the manager exactly like the backup key-rotation test factory.</summary>
    private sealed class ManagerKit : IDisposable
    {
        private readonly HmacSha256BackupManifestSignatureProvider signer;
        private readonly BackupEncryptionKeyResolver encryptionResolver;
        private readonly BackupManifestSignatureKeyResolver signatureResolver;

        private ManagerKit(
            HmacSha256BackupManifestSignatureProvider signer,
            BackupEncryptionKeyResolver encryptionResolver,
            BackupManifestSignatureKeyResolver signatureResolver,
            BackupManager manager)
        {
            this.signer = signer;
            this.encryptionResolver = encryptionResolver;
            this.signatureResolver = signatureResolver;
            Manager = manager;
        }

        public BackupManager Manager { get; }

        public static ManagerKit Create()
        {
            var encryption = new NoBackupEncryption();
            var signer = new HmacSha256BackupManifestSignatureProvider(
                "terrabase-restore-lag-trust-root-2026"u8.ToArray(),
                "restore-lag-root-v1");
            var encryptionResolver = new BackupEncryptionKeyResolver(
                [new BackupEncryptionKeyResolverEntry(SourceId, null, encryption)]);
            var signatureResolver = new BackupManifestSignatureKeyResolver(
                [new BackupManifestSignatureKeyResolverEntry(SourceId, null, signer)]);
            return new ManagerKit(
                signer,
                encryptionResolver,
                signatureResolver,
                new BackupManager(
                    new InMemoryBackupObjectStore(),
                    encryption,
                    signer,
                    encryptionResolver,
                    signatureResolver));
        }

        public void Dispose()
        {
            encryptionResolver.Dispose();
            signatureResolver.Dispose();
            signer.Dispose();
        }
    }

    /// <summary>One JSON body of roughly 256 bytes with a stable per-document identity.</summary>
    private static string ExpectedBody(string documentId) => JsonSerializer.Serialize(new
    {
        id = documentId,
        kind = "perf",
        padding = new string('x', BodyPadding),
    });

    private static bool BodiesEqual(JsonElement actual, string documentId)
    {
        using var expected = JsonDocument.Parse(ExpectedBody(documentId));
        return JsonNode.DeepEquals(
            JsonNode.Parse(actual.GetRawText()),
            JsonNode.Parse(expected.RootElement.GetRawText()));
    }

    private static void Phase(string message) =>
        Console.Error.WriteLine($"phase {Stopwatch.GetElapsedTime(ProcessStart).TotalMilliseconds,12:F1}ms {message}");

    private static readonly long ProcessStart = Stopwatch.GetTimestamp();

    private static async Task<int> SeedAsync(DurableStorageEngine engine, int start, int count)
    {
        var written = start;
        Phase($"seed begin +{count} at {start}");
        while (written < start + count)
        {
            var take = Math.Min(SeedBatchSize, start + count - written);
            var mutations = new StorageMutation[take];
            for (var index = 0; index < take; index++)
            {
                using var body = JsonDocument.Parse(ExpectedBody(DocumentId(written + index)));
                mutations[index] = StorageMutation.Upsert(
                    new DocumentKey(
                        new TenantId(Tenant),
                        new CollectionId(Collection),
                        DocumentId(written + index)),
                    body.RootElement,
                    DocumentPrecondition.Missing);
            }

            await engine.ApplyBatchAsync(mutations);
            written += take;
            Phase($"seed batch complete at {written}");
        }

        Phase("seed end");
        return written;
    }

    private static string DocumentId(int index) => $"doc-{index:D8}";

    private static Dictionary<string, object> Stats(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
            throw new InvalidOperationException("No samples were collected.");
        return new Dictionary<string, object>
        {
            ["n"] = sorted.Length,
            ["min"] = sorted[0],
            ["p50"] = Percentile(sorted, 50),
            ["p95"] = Percentile(sorted, 95),
            ["p99"] = Percentile(sorted, 99),
            ["max"] = sorted[^1],
            ["median"] = sorted[sorted.Length / 2],
        };
    }

    private static double Percentile(double[] sorted, int percentile)
    {
        var rank = (int)Math.Ceiling(percentile / 100.0 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static void Emit<T>(T sample)
    {
        Console.WriteLine(JsonSerializer.Serialize(sample, JsonOptions));
        Console.Out.Flush();
    }

    private static void EmitSummary(string kind, Dictionary<string, object> values)
    {
        var payload = new Dictionary<string, object>(values) { ["kind"] = kind };
        Console.WriteLine(JsonSerializer.Serialize(payload, JsonOptions));
        Console.Out.Flush();
    }

    private static int IntOption(string[] args, string name, int defaultValue)
    {
        for (var index = 1; index < args.Length - 1; index++)
        {
            if (string.Equals(args[index], $"--{name}", StringComparison.Ordinal)
                && int.TryParse(args[index + 1], CultureInfo.InvariantCulture, out var value))
                return value;
        }

        return defaultValue;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Contains($"--{name}", StringComparer.Ordinal);

    private static string CreateRoot(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"terrabase-{prefix}-{Guid.NewGuid():N}");

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private sealed class SequenceCounter
    {
        private ulong next;

        public ulong Next => next;

        public void Advance(int batchSize) => next += (ulong)batchSize;
    }

    private sealed class StaticMetadataProvider(StorageBackupMetadataSnapshot snapshot)
        : IStorageBackupMetadataProvider
    {
        public ValueTask<StorageBackupMetadataSnapshot> CaptureAsync(
            ulong commitSequence,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(snapshot);
        }
    }

    private enum BackupCaseMode
    {
        BackupOnly,
        RestoreOnly,
        BackupAndRestore,
    }

    private sealed record BackupSample(
        int Documents,
        int Run,
        double WallMilliseconds,
        long AllocatedBytes,
        long ArtifactBytes,
        int ArtifactCount,
        string Kind);

    private sealed record RestoreSample(
        int Documents,
        int Run,
        double PlanMilliseconds,
        double ExecuteMilliseconds,
        double PlanPlusExecuteMilliseconds,
        double ReadBackMilliseconds,
        int Verified,
        int Mismatches,
        double DocsPerSecondReadBack);

    private sealed record IncrementalSample(
        int Run,
        int Index,
        double WallMilliseconds,
        long ArtifactBytes,
        long BoundarySequence);

    /// <summary>
    /// One authenticated gRPC member on an ephemeral HTTPS loopback
    /// listener, wired exactly like the replication fault-test peer.
    /// </summary>
    private sealed class MeasurementPeer : IAsyncDisposable
    {
        private readonly WebApplication app;
        private readonly X509Certificate2 caCertificate;
        private readonly X509Certificate2 serverCertificate;
        private readonly X509Certificate2 clientCertificate;

        private MeasurementPeer(
            WebApplication app,
            X509Certificate2 caCertificate,
            X509Certificate2 serverCertificate,
            X509Certificate2 clientCertificate,
            Uri endpoint)
        {
            this.app = app;
            this.caCertificate = caCertificate;
            this.serverCertificate = serverCertificate;
            this.clientCertificate = clientCertificate;
            Endpoint = endpoint;
        }

        public Uri Endpoint { get; }

        public TerraBaseEngine Engine => app.Services.GetRequiredService<TerraBaseEngine>();

        public static async Task<MeasurementPeer> StartAsync(EntityId database, EntityId tenant)
        {
            var path = Path.Combine(Path.GetTempPath(), $"terrabase-lag-peer-{Guid.NewGuid():N}");
            var ca = CreateCertificate("terrabase-lag-ca", CertificateKind.CertificateAuthority);
            var server = CreateCertificate("localhost", CertificateKind.Server, ca);
            var client = CreateCertificate("member-a", CertificateKind.Client, ca);
            var signer = new HmacSha256ReplicationCheckpointSigner(
                "lag-active",
                new[]
                {
                    new ReplicationCheckpointSigningKey("lag-active", StableSigningKey(3)),
                    new ReplicationCheckpointSigningKey("lag-historical", StableSigningKey(4)),
                });
            var peerOptions = new PeerServerOptions(
                server,
                ca,
                "member-b",
                new Dictionary<string, InterNodePeerPrincipal>(StringComparer.Ordinal)
                {
                    [Fingerprint(client)] = new InterNodePeerPrincipal
                    {
                        MemberId = "member-a",
                        MembershipEpoch = 7,
                        AllowedDatabases = new HashSet<EntityId> { database },
                        AllowedTenants = new HashSet<EntityId> { tenant },
                    },
                })
            {
                ReplicaEpoch = 9,
                ReplicationCheckpointSigner = signer,
            };
            var app = TerraBaseHost.Create(
                "https://127.0.0.1:0",
                builder =>
                {
                    builder.Logging.ClearProviders();
                    builder.Configuration["Logging:LogLevel:Default"] = "Warning";
                    builder.Configuration["TERRABASE_DATABASE_PATH"] = path;
                    builder.Configuration["TERRABASE_REGION"] = "GRU";
                    builder.Configuration["TERRABASE_DATABASE_ROUTE"] = database.ToString();
                    builder.Configuration["TERRABASE_DATABASE_TENANT"] = tenant.ToString();
                    builder.Configuration["TERRABASE_DATABASE_ULID"] = database.ToString();
                    builder.Configuration["TERRABASE_INTERNAL_DATABASE_ID"] = database.ToString();
                    builder.Configuration["TERRABASE_DATABASE_BACKEND"] = "storage-engine";
                    builder.Configuration["TERRABASE_DATABASE_MODE"] = "explicit";
                    builder.Configuration["TERRABASE_DATABASE_HOME_REGION"] = "GRU";
                    builder.Configuration["TERRABASE_DATABASE_SELECTED_REGIONS"] = "GRU";
                },
                peerOptions);
            try
            {
                await app.StartAsync().ConfigureAwait(false);
                var address = app.Services
                    .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                    .Features
                    .Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
                    ?.Addresses
                    .Single()
                    ?? throw new InvalidOperationException("The loopback TLS listener did not publish an address.");
                return new MeasurementPeer(app, ca, server, client, new Uri(address));
            }
            catch
            {
                await app.DisposeAsync().ConfigureAwait(false);
                (signer as IDisposable)?.Dispose();
                client.Dispose();
                server.Dispose();
                ca.Dispose();
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                throw;
            }
        }

        public GrpcInterNodeClient CreateClient(string sourceNodeId) =>
            new(new GrpcInterNodeClientOptions(
                new AuthenticatedTlsOptions(
                    Endpoint,
                    clientCertificate,
                    requireClientCertificate: true)
                {
                    CheckCertificateRevocationList = false,
                },
                sourceNodeId)
            {
                TrustedServerCertificates = [caCertificate],
                ExpectedRemoteMemberId = "member-b",
                ExpectedRemoteCertificateFingerprints = [Fingerprint(serverCertificate)],
            });

        public async ValueTask DisposeAsync()
        {
            await app.StopAsync().ConfigureAwait(false);
            await app.DisposeAsync().ConfigureAwait(false);
            clientCertificate.Dispose();
            serverCertificate.Dispose();
            caCertificate.Dispose();
        }

        private static byte[] StableSigningKey(byte seed)
        {
            var key = new byte[32];
            for (var index = 0; index < key.Length; index++)
                key[index] = (byte)(seed + index);
            return key;
        }

        private static X509Certificate2 CreateCertificate(
            string name,
            CertificateKind kind,
            X509Certificate2? issuer = null)
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest(
                new X500DistinguishedName($"CN={name}"),
                key,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                kind == CertificateKind.CertificateAuthority,
                kind == CertificateKind.CertificateAuthority,
                0,
                true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(
                kind == CertificateKind.CertificateAuthority
                    ? X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign
                    : X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                true));
            var usages = new OidCollection();
            if (kind == CertificateKind.Server)
                usages.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            else if (kind == CertificateKind.Client)
                usages.Add(new Oid("1.3.6.1.5.5.7.3.2"));
            if (usages.Count > 0)
                request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));
            if (kind == CertificateKind.Server)
            {
                var names = new SubjectAlternativeNameBuilder();
                names.AddDnsName("localhost");
                names.AddIpAddress(IPAddress.Loopback);
                request.CertificateExtensions.Add(names.Build());
            }

            var notBefore = DateTimeOffset.UtcNow.AddMinutes(-5);
            var notAfter = issuer?.NotAfter.AddMinutes(-1) ?? DateTimeOffset.UtcNow.AddHours(1);
            if (issuer is null)
                return request.CreateSelfSigned(notBefore, notAfter);

            using var issued = request.Create(issuer, notBefore, notAfter, RandomNumberGenerator.GetBytes(16));
            return issued.CopyWithPrivateKey(key);
        }

        private static string Fingerprint(X509Certificate2 certificate) =>
            certificate.GetCertHashString(HashAlgorithmName.SHA256)
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();

        private enum CertificateKind
        {
            CertificateAuthority,
            Server,
            Client,
        }
    }
}
