using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TerraBase;
using TerraBase.Backup;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Schema;
using TerraBase.Storage.Backup;
using TerraBase.Storage.DatabaseBinding;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace RecoveryRpoRtoDrill;

internal static class Program
{
    private const string Tenant = "recovery-drill";
    private const string Collection = "documents";
    private const string SourceId = "recovery-drill-source";
    private const int DocumentCount = 32;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var output = Required(args, "--output");
            var subject = Path.GetFullPath(Required(args, "--subject-root"));
            var manifest = Path.GetFullPath(Required(args, "--source-manifest"));
            var samples = int.Parse(Required(args, "--samples"));
            var archivalDelay = int.Parse(Required(args, "--archival-delay-ms"));
            if (samples < 1 || archivalDelay < 0) throw new ArgumentOutOfRangeException(nameof(args));
            var verification = VerifySourceManifest(subject, manifest);
            Directory.CreateDirectory(output);
            var run = new RunIdentity(subject, manifest, verification.ManifestSha256,
                ReadDeclaredManifestDigest(manifest), verification.Files, verification.Mismatches, RuntimeInformation.OSDescription,
                RuntimeInformation.ProcessArchitecture.ToString(), Environment.Version.ToString(),
                ReadCpuModel(), Environment.ProcessorCount, ReadMemTotalKb(),
                ReadCgroupValue("cpu.max"), ReadCgroupValue("memory.max"),
                HashFile(typeof(Program).Assembly.Location),
                HashFile(typeof(DurableStorageEngine).Assembly.Location),
                DateTimeOffset.UtcNow, samples, archivalDelay);
            await File.WriteAllTextAsync(Path.Combine(output, "run-identity.json"),
                JsonSerializer.Serialize(run, Json) + Environment.NewLine);
            if (verification.Mismatches.Count > 0)
                throw new InvalidDataException("Held source copy does not match its file manifest.");

            var results = new List<Sample>();
            await using var raw = new StreamWriter(Path.Combine(output, "samples.jsonl"), false, new UTF8Encoding(false));
            for (var i = 0; i < samples; i++)
            {
                var sample = await ExecuteSampleAsync(i, archivalDelay);
                results.Add(sample);
                await raw.WriteLineAsync(JsonSerializer.Serialize(sample, Json));
                await raw.FlushAsync();
                Console.WriteLine($"sample {i + 1}/{samples}: RPO {sample.RecoverableAgeAtFaultMs:F1} ms; verified RTO {sample.FaultToVerifiedReopenMs:F1} ms");
            }

            var summary = BuildSummary(run, results);
            await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), summary);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 2;
        }
    }

    private static async Task<Sample> ExecuteSampleAsync(int number, int delayMs)
    {
        var root = Path.Combine(Path.GetTempPath(), $"terrabase-rpo-rto-{Environment.ProcessId}-{number}-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        var storeRoot = Path.Combine(root, "objects");
        var targetRoot = Path.Combine(root, "target");
        var tenant = new TenantId(Tenant);
        var collection = new CollectionId(Collection);
        var controlKey = new DocumentKey(tenant, collection, "control");
        var registry = new SchemaRegistry();
        var schema = new SchemaMetadata(Collection, 1, typeof(object), []);
        registry.Register(tenant, collection, schema);
        var database = EntityId.New();
        var binding = new StorageBackupBindingDescriptor(
            tenant, new DatabaseRouteId("recovery-drill-route"), database, sourceRoot);
        var catalog = new StorageBackupBindingCatalog(binding);
        catalog.Freeze();
        using var signer = new HmacSha256BackupManifestSignatureProvider(
            "terrabase-recovery-rpo-rto-local-drill-2026"u8.ToArray(), "local-drill-v1");
        var encryption = new NoBackupEncryption();
        using var encryptionResolver = new BackupEncryptionKeyResolver([
            new BackupEncryptionKeyResolverEntry(SourceId, null, encryption),
            new BackupEncryptionKeyResolverEntry(SourceId, Tenant, encryption),
        ]);
        using var signatureResolver = new BackupManifestSignatureKeyResolver([
            new BackupManifestSignatureKeyResolverEntry(SourceId, null, signer),
            new BackupManifestSignatureKeyResolverEntry(SourceId, Tenant, signer),
        ]);
        var manager = new BackupManager(new FileSystemBackupObjectStore(storeRoot), encryption,
            signer, encryptionResolver, signatureResolver);
        var options = new BackupOptions { TenantId = Tenant,
            RecoveryPointObjective = TimeSpan.FromMinutes(5),
            RecoveryTimeObjective = TimeSpan.FromMinutes(30) };
        try
        {
            BackupManifest full;
            BackupManifest first;
            BackupManifest tip;
            StorageBackupAutomaticIndexDecisionAuthority authority;
            ulong sourceAtFault;
            DateTimeOffset lostCommitAt;
            DateTimeOffset faultAt;
            long faultClock;
            await using (var engine = new DurableStorageEngine(sourceRoot))
            {
                await engine.InitializeJournalIdentityAsync();
                for (var i = 0; i < DocumentCount; i++)
                {
                    using var body = JsonDocument.Parse($"{{\"value\":{i},\"name\":\"doc-{i:D2}\"}}");
                    var key = i == 0 ? controlKey : new DocumentKey(tenant, collection, $"doc-{i:D2}");
                    await engine.UpsertAsync(key, body.RootElement, DocumentPrecondition.Missing);
                }

                var localAuthority = CreateAuthority(
                    new AutomaticIndexScope(Tenant, database.ToString(), Collection, 1),
                    engine.LastCommitSequence, schema.Fingerprint);
                using var localDecisions = new FileAutomaticIndexDecisionStore(
                    FileAutomaticIndexDecisionStore.DefaultPath(sourceRoot));
                await localDecisions.AppendAsync(localAuthority.Provenance);
                // Publish a later document cut so the durable decision approval is
                // included in the source timestamp seen by the backup provider.
                await WriteControlAsync(engine, controlKey, 0, 1);
                using var metadata = new StorageBackupMetadataProvider(catalog,
                    localDecisions, schemaRegistry: registry);
                using var source = new StorageEngineBackupSource(SourceId, engine, metadata);
                full = await manager.CreateFullBackupAsync(source, options);
                authority = (await metadata.CaptureAsync(
                    checked((ulong)full.Boundary.Sequence), full.Boundary.Timestamp))
                    .AutomaticIndexDecisions.Single();
                await WriteControlAsync(engine, controlKey, 1, 2);
                first = await manager.CreateIncrementalBackupAsync(source, full, options);
                await WriteControlAsync(engine, controlKey, 2, 3);
                tip = await manager.CreateIncrementalBackupAsync(source, first, options);
                await WriteControlAsync(engine, controlKey, 3, 4);
                lostCommitAt = DateTimeOffset.UtcNow;
                sourceAtFault = engine.LastCommitSequence;
                if (sourceAtFault != checked((ulong)tip.Boundary.Sequence + 1))
                    throw new InvalidDataException("The source must have exactly one unarchived durable write.");
                await Task.Delay(delayMs);
                faultAt = DateTimeOffset.UtcNow;
                faultClock = Stopwatch.GetTimestamp();
            }

            Directory.Delete(sourceRoot, recursive: true);
            var target = new StorageEngineRestoreTarget(targetRoot);
            var plan = await manager.CreateRestorePlanAsync(tip);
            var restoreResult = await manager.RestoreAsync(plan, target);
            var activePath = target.ResolveActivePath();
            var archived = StorageBackupMetadataComposer.ReadOptional(activePath)
                ?? throw new InvalidDataException("Restored backup metadata is absent.");
            var restoredRegistry = new SchemaRegistry();
            StorageBackupMetadataComposer.RegisterSchemas(archived, restoredRegistry);
            if (restoredRegistry.GetVersion(tenant, collection, 1).Fingerprint != schema.Fingerprint)
                throw new InvalidDataException("Restored schema fingerprint differs from source.");
            if (archived.AutomaticIndexDecisions.Count != 1
                || archived.AutomaticIndexDecisions[0].Checksum != authority.Checksum)
                throw new InvalidDataException(
                    $"Restored automatic index authority differs from source: count={archived.AutomaticIndexDecisions.Count}, source={authority.Checksum}, restored={archived.AutomaticIndexDecisions.FirstOrDefault()?.Checksum}.");
            archived.AutomaticIndexDecisions[0].Validate();
            var decisionPath = FileAutomaticIndexDecisionStore.DefaultPath(activePath);
            await StorageBackupMetadataComposer.ImportAutomaticIndexDecisionsAsync(activePath, decisionPath);
            using (var decisionStore = new FileAutomaticIndexDecisionStore(decisionPath))
            {
                var imported = await decisionStore.ReadAsync();
                if (imported.Count != 1 || !imported[0].ImportedFromBackup
                    || imported[0].Profile.SchemaFingerprint != schema.Fingerprint)
                    throw new InvalidDataException("Restored authority did not import as reprofile-required evidence.");
            }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            await using (var restored = new DurableStorageEngine(activePath))
            {
                if ((long)restored.LastCommitSequence != tip.Boundary.Sequence)
                    throw new InvalidDataException("Restored frontier differs from backup tip.");
                await foreach (var document in restored.ScanAsync(tenant, collection))
                {
                    var id = document.Key.DocumentId;
                    var expectedValue = id == "control" ? 2 : ParseDocumentNumber(id);
                    var expectedName = id == "control" ? "doc-00" : id;
                    if (!seen.Add(id) || document.Body.GetProperty("value").GetInt32() != expectedValue
                        || document.Body.GetProperty("name").GetString() != expectedName)
                        throw new InvalidDataException($"Restored document differs: {id}.");
                }
                if (seen.Count != DocumentCount)
                    throw new InvalidDataException($"Expected {DocumentCount} documents; got {seen.Count}.");
                var control = await restored.ReadAsync(controlKey);
                if (control?.Body.GetProperty("value").GetInt32() != 2)
                    throw new InvalidDataException("Unarchived control write appeared after source loss.");
            }

            var verifiedRtoMs = Stopwatch.GetElapsedTime(faultClock).TotalMilliseconds;
            if (tip.Kind != BackupKind.Incremental || first.Kind != BackupKind.Incremental
                || full.Kind != BackupKind.Full || first.BaseBackupId != full.BackupId
                || first.BaseManifestSha256 != full.Sha256
                || tip.BaseBackupId != first.BackupId
                || tip.BaseManifestSha256 != first.Sha256 || plan.Chain.Count != 3
                || restoreResult.AppliedLogEntries != 2)
                throw new InvalidDataException("Backup chain or applied log count differs from fixture.");
            if (archived.Schemas.Count != 1 || archived.Schemas[0].Fingerprint != schema.Fingerprint)
                throw new InvalidDataException("Archived schema metadata differs from source.");
            return new Sample(number, faultAt, delayMs,
                (faultAt - tip.Boundary.Timestamp).TotalMilliseconds,
                (faultAt - lostCommitAt).TotalMilliseconds,
                verifiedRtoMs, full.Boundary.Sequence, first.Boundary.Sequence,
                tip.Boundary.Sequence, sourceAtFault, plan.FinalBoundary.Sequence,
                restoreResult.AppliedLogEntries, DocumentCount, 1, 2, 3,
                schema.Fingerprint, authority.Checksum,
                [ToManifest(full), ToManifest(first), ToManifest(tip)],
                "filesystem object store; source directory deleted at fault; fresh filesystem restore target; full scan, schema registration, and automatic index decision authority import verified");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task WriteControlAsync(DurableStorageEngine engine, DocumentKey key, int value, ulong version)
    {
        using var body = JsonDocument.Parse($"{{\"value\":{value},\"name\":\"doc-00\"}}");
        await engine.UpsertAsync(key, body.RootElement, DocumentPrecondition.Version(version));
    }

    private static StorageBackupAutomaticIndexDecisionAuthority CreateAuthority(
        AutomaticIndexScope scope, ulong commitSequence, string schemaFingerprint)
    {
        var policy = new QueryIndependentIndexPolicy { MaximumIndexes = 1 };
        var field = new FieldIndexProfile("value", ProfileValueKinds.Number,
            1, 0, 1, false, 1, 1, 0, false);
        var profile = new CollectionIndexProfile(scope, commitSequence, DocumentCount,
            true, false, [field])
        {
            SourceIdentity = Guid.NewGuid(),
            SchemaFingerprint = schemaFingerprint,
        };
        var candidate = new AutomaticIndexCandidate(scope, ["value"]);
        var candidates = new IndexCandidateSet(profile, policy,
            [new IndexCandidateEvidence(candidate, field, 1, "drill")],
            "schema-statistics", "1");
        var provenance = new IndexDecisionProvenance(profile, policy,
            new AutoIndexAdvice(candidates.Identity, "schema-statistics", "1",
                [new IndexRecommendation(candidate.CandidateId, 1, "drill")], "drill"))
        {
            CandidateSourceProvider = "schema-statistics",
            CandidateSourceVersion = "1",
            CandidateDefinitions = [candidate],
            SourceCommitSequence = commitSequence,
        };
        var authority = StorageBackupAutomaticIndexDecisionAuthority.Create(provenance);
        authority.Validate();
        return authority;
    }

    private static int ParseDocumentNumber(string id) =>
        id.StartsWith("doc-", StringComparison.Ordinal) && int.TryParse(id.AsSpan(4), out var number)
            && number is > 0 and < DocumentCount
            ? number : throw new InvalidDataException($"Unexpected document id {id}.");

    private static ManifestReceipt ToManifest(BackupManifest manifest) =>
        new(manifest.BackupId, manifest.Kind.ToString(), manifest.Sha256,
            manifest.BaseBackupId, manifest.BaseManifestSha256, manifest.Boundary.Sequence,
            manifest.Boundary.Timestamp, manifest.Artifacts.Select(a =>
                new ArtifactReceipt(a.Key, a.Length, a.Sha256, a.PlaintextSha256)).ToArray());

    private static (string ManifestSha256, int Files, List<string> Mismatches) VerifySourceManifest(string subject, string manifest)
    {
        var mismatches = new List<string>();
        var lines = File.ReadAllLines(manifest);
        foreach (var line in lines)
        {
            if (line.Length < 67 || line[64..66] != "  ")
            {
                mismatches.Add($"malformed manifest line: {line}");
                continue;
            }
            var path = Path.Combine(subject, line[66..]);
            if (!File.Exists(path) || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), line[..64],
                    StringComparison.OrdinalIgnoreCase))
                mismatches.Add(line[66..]);
        }
        return (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifest))).ToLowerInvariant(),
            lines.Length, mismatches);
    }

    private static string BuildSummary(RunIdentity run, List<Sample> samples)
    {
        static double Percentile(IEnumerable<double> values, double p)
        {
            var sorted = values.Order().ToArray();
            return sorted[(int)Math.Ceiling(p * sorted.Length) - 1];
        }
        var builder = new StringBuilder();
        builder.AppendLine("# Local backup and restore RPO/RTO drill");
        builder.AppendLine();
        builder.AppendLine($"Held subject: `{run.SubjectRoot}`; raw source manifest SHA-256 `{run.ManifestSha256}` ({run.VerifiedFiles} verified files, {run.Mismatches.Count} mismatches). Snapshot JSON declares `sourceFilesManifestSha256={run.DeclaredSourceFilesManifestSha256}`; its encoding semantics were not documented, and this value differs from the raw file hash.");
        builder.AppendLine($"Runner DLL SHA-256 `{run.RunnerDllSha256}`; loaded TerraBase DLL SHA-256 `{run.LoadedTerraBaseDllSha256}`.");
        builder.AppendLine($"Runtime: .NET {run.DotNetVersion}; {run.OS}; {run.Architecture}; CPU {run.CpuModel}; {run.LogicalProcessors} visible logical processors; host MemTotal {run.HostMemoryTotalKb} KiB; cgroup cpu.max `{run.CgroupCpuMax}`; memory.max `{run.CgroupMemoryMax}`.");
        builder.AppendLine($"Fault: declare source loss after an unarchived durable write and {run.ArchivalDelayMs} ms injected archival delay, then close the source engine and delete its directory. Object store and restore target are local filesystems. RTO starts at fault declaration and ends after restore, clean reopen, exact document scan, schema registration, and authority import.");
        builder.AppendLine();
        builder.AppendLine("| Sample | Recoverable age at fault (RPO) | Lost write age at fault | Verified RTO | Restored frontier | Exact data | Schema and authority |");
        builder.AppendLine("| ---: | ---: | ---: | ---: | ---: | --- | --- |");
        foreach (var sample in samples)
            builder.AppendLine($"| {sample.Number + 1} | {sample.RecoverableAgeAtFaultMs:F1} ms | {sample.LostWriteAgeAtFaultMs:F1} ms | {sample.FaultToVerifiedReopenMs:F1} ms | {sample.RestoredSequence} | {sample.VerifiedDocuments} documents; value {sample.RestoredControlValue}; {sample.LostWrites} lost write | fingerprint, checksum, import verified |");
        builder.AppendLine($"| p50 | {Percentile(samples.Select(s => s.RecoverableAgeAtFaultMs), .5):F1} ms | — | {Percentile(samples.Select(s => s.FaultToVerifiedReopenMs), .5):F1} ms | — | — | — |");
        builder.AppendLine($"| p95 | {Percentile(samples.Select(s => s.RecoverableAgeAtFaultMs), .95):F1} ms | — | {Percentile(samples.Select(s => s.FaultToVerifiedReopenMs), .95):F1} ms | — | — | — |");
        builder.AppendLine();
        builder.AppendLine("Backup chain per sample: full + 2 incrementals; manifest and artifact hashes, parent IDs, frontiers, source-at-fault frontier, and raw timestamps are in `samples.jsonl`. Restore plan and execution verify the signed manifest chain and artifact integrity. The source had one durable write after the tip, intentionally never archived; the restored value is 2 and lost value is 3. Schema fingerprint is rehydrated into a fresh registry. One automatic index decision authority checksum is verified and imported as evidence requiring local reprofile before activation.");
        builder.AppendLine();
        builder.AppendLine("These are single-host local filesystem measurements. The controlled fault includes clean engine disposal and source-directory removal; it does not model abrupt power loss, remote object-store delay, region failure, detection time, or human response. Percentiles are nearest-rank across the listed independent samples.");
        return builder.ToString();
    }

    private static string Required(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1]
            : throw new ArgumentException($"Missing {key}.");
    }

    private static string ReadFirstLine(string path) => File.Exists(path) ? File.ReadLines(path).FirstOrDefault() ?? "empty" : "unavailable";
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string ReadCgroupValue(string file)
    {
        var cgroup = ReadFirstLine("/proc/self/cgroup");
        if (!cgroup.StartsWith("0::/", StringComparison.Ordinal)) return "unavailable";
        return ReadFirstLine(Path.Combine("/sys/fs/cgroup", cgroup[4..].TrimStart('/'), file));
    }
    private static long ReadMemTotalKb()
    {
        var line = File.ReadLines("/proc/meminfo").FirstOrDefault(line => line.StartsWith("MemTotal:", StringComparison.Ordinal));
        return line is null ? 0 : long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
    }
    private static string ReadDeclaredManifestDigest(string manifest)
    {
        var snapshotPath = Path.Combine(Path.GetDirectoryName(manifest)!, "snapshot.json");
        if (!File.Exists(snapshotPath)) return "unavailable";
        using var snapshot = JsonDocument.Parse(File.ReadAllBytes(snapshotPath));
        return snapshot.RootElement.GetProperty("repos").GetProperty("terrabase")
            .GetProperty("sourceFilesManifestSha256").GetString() ?? "unavailable";
    }
    private static string ReadCpuModel() => File.Exists("/proc/cpuinfo")
        ? File.ReadLines("/proc/cpuinfo").FirstOrDefault(line => line.StartsWith("model name", StringComparison.Ordinal))?.Split(':', 2)[1].Trim() ?? "unknown"
        : "unknown";

    private sealed record RunIdentity(string SubjectRoot, string ManifestPath, string ManifestSha256,
        string DeclaredSourceFilesManifestSha256, int VerifiedFiles, IReadOnlyList<string> Mismatches,
        string OS, string Architecture, string DotNetVersion, string CpuModel,
        int LogicalProcessors, long HostMemoryTotalKb, string CgroupCpuMax, string CgroupMemoryMax,
        string RunnerDllSha256, string LoadedTerraBaseDllSha256,
        DateTimeOffset StartedAtUtc, int Samples, int ArchivalDelayMs);
    private sealed record ArtifactReceipt(string Key, long Length, string Sha256, string? PlaintextSha256);
    private sealed record ManifestReceipt(string BackupId, string Kind, string Sha256, string? BaseBackupId,
        string? BaseManifestSha256, long Sequence, DateTimeOffset Timestamp, IReadOnlyList<ArtifactReceipt> Artifacts);
    private sealed record Sample(int Number, DateTimeOffset FaultAtUtc, int InjectedArchivalDelayMs,
        double RecoverableAgeAtFaultMs, double LostWriteAgeAtFaultMs, double FaultToVerifiedReopenMs,
        long FullSequence, long FirstIncrementalSequence, long TipSequence, ulong SourceSequenceAtFault,
        long RestoredSequence, int AppliedLogEntries, int VerifiedDocuments, int LostWrites,
        int RestoredControlValue, int LostControlValue, string SchemaFingerprint, string AuthorityChecksum,
        IReadOnlyList<ManifestReceipt> Chain, string FaultAndVerificationModel);

}
