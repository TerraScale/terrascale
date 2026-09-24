using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using TerraBase;
using TerraBase.Backup;
using TerraBase.Indexing;
using TerraBase.Indexing.Automatic;
using TerraBase.Schema;
using TerraBase.Storage.Backup;
using TerraBase.Storage.DatabaseBinding;
using DurableStorageEngine = TerraBase.StorageEngine.StorageEngine;

namespace RecoveryS3AbruptDrill;

public static class Program
{
    private const string Tenant = "recovery-drill";
    private const string Collection = "documents";
    private const int DocumentCount = 32;
    private const string SigningKey = "terrabase-recovery-rpo-rto-local-drill-2026";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<int> Main(string[] args)
    {
        var output = Optional(args, "--output");
        try
        {
            return args.Contains("--child", StringComparer.Ordinal)
                ? await RunChildAsync(args)
                : await RunParentAsync(args);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            if (output is not null)
            {
                Directory.CreateDirectory(output);
                await File.WriteAllTextAsync(Path.Combine(output, "failure.json"),
                    JsonSerializer.Serialize(new { type = exception.GetType().FullName, exception.Message, exception.StackTrace }, Json));
            }
            return 2;
        }
    }

    private static async Task<int> RunParentAsync(string[] args)
    {
        var output = Path.GetFullPath(Required(args, "--output"));
        var subject = Path.GetFullPath(Required(args, "--subject-root"));
        var manifestPath = Path.GetFullPath(Required(args, "--source-manifest"));
        var samples = int.Parse(Required(args, "--samples"));
        var archivalDelayMs = int.Parse(Required(args, "--archival-delay-ms"));
        var endpoint = Required(args, "--s3-endpoint");
        var container = Required(args, "--container-name");
        if (samples < 1 || archivalDelayMs < 0)
            throw new ArgumentOutOfRangeException(nameof(args));
        Directory.CreateDirectory(output);
        var sourceVerification = VerifySourceManifest(subject, manifestPath);
        if (sourceVerification.Mismatches.Count != 0)
            throw new InvalidDataException("Held source copy does not match its file manifest.");

        var service = await ReadLocalRustFsAsync(container);
        var buckets = Enumerable.Range(0, samples)
            .Select(_ => "tb-abrupt-" + Guid.NewGuid().ToString("N")[..16])
            .ToArray();
        if (buckets.Distinct(StringComparer.Ordinal).Count() != samples)
            throw new InvalidOperationException("Sample bucket names must be distinct.");
        using var client = OpenClient(service, endpoint);
        var identity = new RunIdentity(subject, manifestPath, sourceVerification.Digest,
            sourceVerification.Files, sourceVerification.Mismatches,
            HashFile(typeof(Program).Assembly.Location),
            HashFile(typeof(DurableStorageEngine).Assembly.Location),
            RuntimeInformation.OSDescription, RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(), Environment.ProcessorCount, DateTimeOffset.UtcNow,
            samples, archivalDelayMs, endpoint, container, service.ImageId, buckets);
        await WriteJsonAsync(Path.Combine(output, "run-identity.json"), identity);
        var results = new List<Sample>();
        await using var raw = new StreamWriter(Path.Combine(output, "samples.jsonl"),
            false, new UTF8Encoding(false));
        for (var number = 0; number < samples; number++)
        {
            var bucket = buckets[number];
            await client.PutBucketAsync(new PutBucketRequest { BucketName = bucket });
            try
            {
                var sample = await ExecuteSampleAsync(number, output, endpoint, container,
                    client, bucket, archivalDelayMs);
                results.Add(sample);
                await raw.WriteLineAsync(JsonSerializer.Serialize(sample, Json));
                await raw.FlushAsync();
                Console.WriteLine($"sample {number + 1}/{samples}: SIGKILL exit {sample.ExitCode}; " +
                    $"tip {sample.TipSequence}, source {sample.SourceSequenceAtFault}, " +
                    $"restored {sample.RestoredSequence}; verified RTO {sample.TotalMs:F1} ms");
            }
            catch (Exception exception)
            {
                await WriteJsonAsync(Path.Combine(output, $"sample-{number + 1}-failure.json"),
                    new { sample = number + 1, bucket, type = exception.GetType().FullName,
                        exception.Message, exception.StackTrace });
                throw;
            }
            finally
            {
                await DeleteFixtureBucketAsync(client, bucket);
            }
        }
        await File.WriteAllTextAsync(Path.Combine(output, "summary.md"), BuildSummary(identity, results));
        return 0;
    }

    private static async Task<int> RunChildAsync(string[] args)
    {
        var sourceRoot = Required(args, "--source-root");
        var readyPath = Required(args, "--ready");
        var bucket = Required(args, "--bucket");
        var sourceId = Required(args, "--source-id");
        var endpoint = Required(args, "--s3-endpoint");
        var container = Required(args, "--container-name");
        var archivalDelayMs = int.Parse(Required(args, "--archival-delay-ms"));
        var service = await ReadLocalRustFsAsync(container);
        using var client = OpenClient(service, endpoint);
        var tenant = new TenantId(Tenant);
        var collection = new CollectionId(Collection);
        var controlKey = new DocumentKey(tenant, collection, "control");
        var schema = new SchemaMetadata(Collection, 1, typeof(object), []);
        var registry = new SchemaRegistry();
        registry.Register(tenant, collection, schema);
        var database = EntityId.New();
        var binding = new StorageBackupBindingDescriptor(
            tenant, new DatabaseRouteId("recovery-drill-route"), database, sourceRoot);
        var catalog = new StorageBackupBindingCatalog(binding);
        catalog.Freeze();
        using var signer = CreateSigner();
        using var encryptionResolver = CreateEncryptionResolver(sourceId);
        using var signatureResolver = CreateSignatureResolver(sourceId, signer);
        var manager = CreateManager(client, bucket, sourceRoot, signer,
            encryptionResolver, signatureResolver);
        var options = new BackupOptions
        {
            TenantId = Tenant,
            RecoveryPointObjective = TimeSpan.FromMinutes(5),
            RecoveryTimeObjective = TimeSpan.FromMinutes(30),
        };
        await using var engine = new DurableStorageEngine(sourceRoot);
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
        await WriteControlAsync(engine, controlKey, 0, 1);
        using var metadata = new StorageBackupMetadataProvider(catalog, localDecisions,
            schemaRegistry: registry);
        using var backupSource = new StorageEngineBackupSource(sourceId, engine, metadata);
        var full = await manager.CreateFullBackupAsync(backupSource, options);
        var authority = (await metadata.CaptureAsync(
            checked((ulong)full.Boundary.Sequence), full.Boundary.Timestamp))
            .AutomaticIndexDecisions.Single();
        await WriteControlAsync(engine, controlKey, 1, 2);
        var first = await manager.CreateIncrementalBackupAsync(backupSource, full, options);
        await WriteControlAsync(engine, controlKey, 2, 3);
        var tip = await manager.CreateIncrementalBackupAsync(backupSource, first, options);
        await WriteControlAsync(engine, controlKey, 3, 4);
        var lostCommitAt = DateTimeOffset.UtcNow;
        var sourceSequence = engine.LastCommitSequence;
        if (sourceSequence != checked((ulong)tip.Boundary.Sequence + 1))
            throw new InvalidDataException("Source must have exactly one unarchived durable write.");
        await Task.Delay(archivalDelayMs);
        var ready = new ChildReady(sourceId, full.BackupId, full.Sha256, full.Boundary.Sequence,
            first.BackupId, first.Sha256, first.Boundary.Sequence,
            tip.BackupId, tip.Sha256, tip.Boundary.Sequence, tip.Boundary.Timestamp,
            sourceSequence, lostCommitAt, schema.Fingerprint, authority.Checksum,
            DateTimeOffset.UtcNow, Environment.ProcessId);
        await WriteReadyAtomicallyAsync(readyPath, ready);
        Console.WriteLine("READY");
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return 0;
    }

    private static async Task<Sample> ExecuteSampleAsync(int number, string output,
        string endpoint, string container, IAmazonS3 client, string bucket, int delayMs)
    {
        var root = Path.Combine(Path.GetTempPath(),
            $"terrabase-abrupt-recovery-{Environment.ProcessId}-{number}-{Guid.NewGuid():N}");
        var sourceRoot = Path.Combine(root, "source");
        var targetRoot = Path.Combine(root, "target");
        var readyPath = Path.Combine(root, "ready.json");
        var sourceId = "abrupt-s3-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[]
        {
            typeof(Program).Assembly.Location, "--child", "--source-root", sourceRoot,
            "--ready", readyPath, "--bucket", bucket, "--source-id", sourceId,
            "--s3-endpoint", endpoint, "--container-name", container,
            "--archival-delay-ms", delayMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
        })
            start.ArgumentList.Add(argument);
        using var child = Process.Start(start)
            ?? throw new InvalidOperationException("Could not start source child process.");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        try
        {
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (!File.Exists(readyPath) && !child.HasExited && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(20);
            if (!File.Exists(readyPath) || child.HasExited)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync();
                }
                throw new InvalidOperationException(
                    $"Source child did not reach kill barrier; exit={child.ExitCode}; " +
                    $"stdout={await stdout}; stderr={await stderr}");
            }
            var ready = JsonSerializer.Deserialize<ChildReady>(await File.ReadAllTextAsync(readyPath), Json)
                ?? throw new InvalidDataException("Source child readiness receipt is empty.");
            if (ready.ProcessId != child.Id || ready.SourceId != sourceId)
                throw new InvalidDataException("Source child readiness identity differs from the live process.");
            var faultAt = DateTimeOffset.UtcNow;
            var faultClock = Stopwatch.GetTimestamp();
            child.Kill(); // Process.Kill sends SIGKILL on this Linux host.
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var killWaitMs = Stopwatch.GetElapsedTime(faultClock).TotalMilliseconds;
            var exitCode = child.ExitCode;
            if (exitCode == 0)
                throw new InvalidDataException("Killed source child exited successfully.");
            var childOutput = await stdout;
            var childError = await stderr;
            await WriteJsonAsync(Path.Combine(output, $"sample-{number + 1}-kill.json"),
                new { faultAt, child.Id, exitCode, killWaitMs, childOutput, childError,
                    signal = "SIGKILL", sourceId, ready.TipId, ready.SourceSequence });

            var stage = Stopwatch.GetTimestamp();
            Directory.Delete(sourceRoot, recursive: true);
            var sourceDeleteMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
            using var signer = CreateSigner();
            using var encryptionResolver = CreateEncryptionResolver(sourceId);
            using var signatureResolver = CreateSignatureResolver(sourceId, signer);
            var manager = CreateManager(client, bucket, root, signer,
                encryptionResolver, signatureResolver);
            stage = Stopwatch.GetTimestamp();
            var points = await manager.ListRestorePointsAsync(sourceId);
            var listMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
            if (points.Count != 3)
                throw new InvalidDataException($"Expected 3 committed manifests, got {points.Count}.");
            var full = points.Single(point => point.BackupId == ready.FullId);
            var first = points.Single(point => point.BackupId == ready.FirstId);
            var tip = points.Single(point => point.BackupId == ready.TipId);
            if (full.Kind != BackupKind.Full || first.Kind != BackupKind.Incremental
                || tip.Kind != BackupKind.Incremental
                || full.Sha256 != ready.FullSha256 || first.Sha256 != ready.FirstSha256
                || tip.Sha256 != ready.TipSha256
                || full.Boundary.Sequence != ready.FullSequence
                || first.Boundary.Sequence != ready.FirstSequence
                || tip.Boundary.Sequence != ready.TipSequence
                || first.BaseBackupId != full.BackupId
                || first.BaseManifestSha256 != full.Sha256
                || tip.BaseBackupId != first.BackupId
                || tip.BaseManifestSha256 != first.Sha256
                || ready.SourceSequence != checked((ulong)tip.Boundary.Sequence + 1))
                throw new InvalidDataException("Committed RustFS chain differs from source child readiness.");
            stage = Stopwatch.GetTimestamp();
            var plan = await manager.CreateRestorePlanAsync(tip);
            var planMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
            if (plan.Chain.Count != 3 || plan.FinalBoundary.Sequence != ready.TipSequence)
                throw new InvalidDataException("Restore plan differs from committed source frontier.");
            var target = new StorageEngineRestoreTarget(targetRoot);
            stage = Stopwatch.GetTimestamp();
            var restoredResult = await manager.RestoreAsync(plan, target);
            var restoreMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
            stage = Stopwatch.GetTimestamp();
            var (documents, restoredSequence, restoredControl) =
                await VerifyRestoredAsync(target, ready);
            var verifyMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
            var totalMs = Stopwatch.GetElapsedTime(faultClock).TotalMilliseconds;
            if (restoredResult.AppliedLogEntries != 2 || restoredSequence != ready.TipSequence)
                throw new InvalidDataException("Restored sequence or applied log count differs from source.");
            return new Sample(number + 1, bucket, faultAt, ready.ReadyAt, child.Id, exitCode,
                (faultAt - ready.TipTimestamp).TotalMilliseconds,
                (faultAt - ready.LostCommitAt).TotalMilliseconds,
                killWaitMs, sourceDeleteMs, listMs, planMs, restoreMs, verifyMs, totalMs,
                ready.FullSequence, ready.FirstSequence, ready.TipSequence,
                ready.SourceSequence, restoredSequence, restoredResult.AppliedLogEntries,
                documents, restoredControl, 3, ready.SchemaFingerprint, ready.AuthorityChecksum,
                full.Sha256, first.Sha256, tip.Sha256, full.BackupId, first.BackupId, tip.BackupId);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                await child.WaitForExitAsync();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(int Documents, long Sequence, int ControlValue)> VerifyRestoredAsync(
        StorageEngineRestoreTarget target, ChildReady source)
    {
        var activePath = target.ResolveActivePath();
        var archived = StorageBackupMetadataComposer.ReadOptional(activePath)
            ?? throw new InvalidDataException("Restored backup metadata is absent.");
        var tenant = new TenantId(Tenant);
        var collection = new CollectionId(Collection);
        var registry = new SchemaRegistry();
        StorageBackupMetadataComposer.RegisterSchemas(archived, registry);
        if (archived.Schemas.Count != 1
            || registry.GetVersion(tenant, collection, 1).Fingerprint != source.SchemaFingerprint)
            throw new InvalidDataException("Restored schema fingerprint differs from source.");
        if (archived.AutomaticIndexDecisions.Count != 1
            || archived.AutomaticIndexDecisions[0].Checksum != source.AuthorityChecksum)
            throw new InvalidDataException("Restored automatic index authority differs from source.");
        archived.AutomaticIndexDecisions[0].Validate();
        var decisionPath = FileAutomaticIndexDecisionStore.DefaultPath(activePath);
        await StorageBackupMetadataComposer.ImportAutomaticIndexDecisionsAsync(activePath, decisionPath);
        using (var decisions = new FileAutomaticIndexDecisionStore(decisionPath))
        {
            var imported = await decisions.ReadAsync();
            if (imported.Count != 1 || !imported[0].ImportedFromBackup
                || imported[0].Profile.SchemaFingerprint != source.SchemaFingerprint)
                throw new InvalidDataException("Restored decision authority import differs from source.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        await using var engine = new DurableStorageEngine(activePath);
        if ((long)engine.LastCommitSequence != source.TipSequence)
            throw new InvalidDataException("Fresh engine reopen differs from archived frontier.");
        await foreach (var document in engine.ScanAsync(tenant, collection))
        {
            var id = document.Key.DocumentId;
            var expected = id == "control" ? 2 : ParseDocumentNumber(id);
            var name = id == "control" ? "doc-00" : id;
            if (!seen.Add(id)
                || document.Body.GetProperty("value").GetInt32() != expected
                || document.Body.GetProperty("name").GetString() != name)
                throw new InvalidDataException($"Restored document differs: {id}.");
        }
        if (seen.Count != DocumentCount)
            throw new InvalidDataException($"Expected {DocumentCount} documents, got {seen.Count}.");
        var control = await engine.ReadAsync(new DocumentKey(tenant, collection, "control"));
        var value = control?.Body.GetProperty("value").GetInt32()
            ?? throw new InvalidDataException("Control document is missing.");
        if (value != 2)
            throw new InvalidDataException("Unarchived control value survived source deletion.");
        return (seen.Count, (long)engine.LastCommitSequence, value);
    }

    private static async Task WriteControlAsync(DurableStorageEngine engine,
        DocumentKey key, int value, ulong version)
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

    private static BackupManager CreateManager(IAmazonS3 client, string bucket, string checkpointRoot,
        HmacSha256BackupManifestSignatureProvider signer,
        BackupEncryptionKeyResolver encryptionResolver,
        BackupManifestSignatureKeyResolver signatureResolver) =>
        new(new S3NativeBackupObjectStore(client, bucket, maxAttempts: 3,
                checkpointStore: new FileBackupUploadCheckpointStore(
                    Path.Combine(checkpointRoot, "upload-checkpoints"))),
            new NoBackupEncryption(), signer, encryptionResolver, signatureResolver);

    private static HmacSha256BackupManifestSignatureProvider CreateSigner() =>
        new(Encoding.UTF8.GetBytes(SigningKey), "local-drill-v1");

    private static BackupEncryptionKeyResolver CreateEncryptionResolver(string sourceId) =>
        new([
            new BackupEncryptionKeyResolverEntry(sourceId, null, new NoBackupEncryption()),
            new BackupEncryptionKeyResolverEntry(sourceId, Tenant, new NoBackupEncryption()),
        ]);

    private static BackupManifestSignatureKeyResolver CreateSignatureResolver(
        string sourceId, HmacSha256BackupManifestSignatureProvider signer) =>
        new([
            new BackupManifestSignatureKeyResolverEntry(sourceId, null, signer),
            new BackupManifestSignatureKeyResolverEntry(sourceId, Tenant, signer),
        ]);

    private static AmazonS3Client OpenClient(LocalRustFs service, string endpoint) =>
        new(new BasicAWSCredentials(service.AccessKey, service.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = endpoint,
                AuthenticationRegion = "us-east-1",
                ForcePathStyle = true,
            });

    private static async Task<LocalRustFs> ReadLocalRustFsAsync(string container)
    {
        var environmentJson = await PodmanInspectFieldAsync(container, "{{json .Config.Env}}");
        var imageId = await PodmanInspectFieldAsync(container, "{{.Image}}");
        var entries = JsonSerializer.Deserialize<string[]>(environmentJson)
            ?? throw new InvalidDataException("Local RustFS container has no environment.");
        var values = entries.Select(value => value.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        if (!values.TryGetValue("RUSTFS_ACCESS_KEY", out var accessKey)
            || !values.TryGetValue("RUSTFS_SECRET_KEY", out var secretKey)
            || string.IsNullOrEmpty(accessKey) || string.IsNullOrEmpty(secretKey))
            throw new InvalidOperationException("Local RustFS container lacks local access credentials.");
        return new LocalRustFs(accessKey, secretKey, imageId);
    }

    private static async Task<string> PodmanInspectFieldAsync(string container, string format)
    {
        var start = new ProcessStartInfo("podman")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("inspect");
        start.ArgumentList.Add(container);
        start.ArgumentList.Add("--format");
        start.ArgumentList.Add(format);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Cannot inspect local RustFS container.");
        var output = await process.StandardOutput.ReadToEndAsync();
        _ = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException("Local RustFS container inspect failed.");
        return output.Trim();
    }

    private static async Task DeleteFixtureBucketAsync(IAmazonS3 client, string bucket)
    {
        string? continuation = null;
        do
        {
            var page = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                ContinuationToken = continuation,
            });
            foreach (var item in page.S3Objects)
                await client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = bucket,
                    Key = item.Key,
                });
            continuation = page.IsTruncated == true ? page.NextContinuationToken : null;
        } while (continuation is not null);
        await client.DeleteBucketAsync(new DeleteBucketRequest { BucketName = bucket });
    }

    private static async Task WriteReadyAtomicallyAsync(string path, ChildReady ready)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ready, Json);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                         FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path);
    }

    private static Task WriteJsonAsync<T>(string path, T value) =>
        File.WriteAllTextAsync(path, JsonSerializer.Serialize(value, Json) + Environment.NewLine);

    private static (string Digest, int Files, List<string> Mismatches) VerifySourceManifest(
        string subject, string manifest)
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
            if (!File.Exists(path) || !string.Equals(HashFile(path), line[..64],
                    StringComparison.OrdinalIgnoreCase))
                mismatches.Add(line[66..]);
        }
        return (HashFile(manifest), lines.Length, mismatches);
    }

    private static string BuildSummary(RunIdentity identity, List<Sample> samples)
    {
        static double Percentile(IEnumerable<double> values, double probability)
        {
            var sorted = values.Order().ToArray();
            return sorted[(int)Math.Ceiling(probability * sorted.Length) - 1];
        }
        var text = new StringBuilder();
        text.AppendLine("# Native RustFS abrupt source-loss recovery drill");
        text.AppendLine();
        text.AppendLine($"Held subject: {identity.SubjectRoot}; source manifest SHA-256 {identity.ManifestSha256}; " +
            $"{identity.VerifiedFiles} verified files and {identity.Mismatches.Count} mismatches. " +
            $"Runner DLL SHA-256 {identity.RunnerDllSha256}; TerraBase DLL SHA-256 {identity.LoadedTerraBaseDllSha256}.");
        text.AppendLine($"Provider: RustFS container {identity.ContainerName}, image {identity.ContainerImageId}, " +
            $"loopback {identity.S3Endpoint}; one isolated bucket per sample, removed after that sample. " +
            "Credentials were read from the local container at runtime and are absent from receipts.");
        text.AppendLine($"Runtime: .NET {identity.DotNetVersion}; {identity.OS}; {identity.Architecture}; " +
            $"{identity.LogicalProcessors} visible logical processors.");
        text.AppendLine("Fixture settings: 5 minute RPO, 30 minute RTO, NoBackupEncryption, " +
            "and HMAC-SHA256 signed manifests. The observed values are local measurements.");
        text.AppendLine($"Fault: a child process left the source storage engine open after one durable, " +
            $"unarchived write and {identity.ArchivalDelayMs} ms delay. The parent observed its ready barrier, " +
            "sent SIGKILL via Process.Kill, waited for a nonzero exit, deleted the source directory, " +
            "then restored from native RustFS objects into a fresh filesystem target.");
        text.AppendLine("RTO starts when the parent invokes the kill and ends after restore, fresh reopen, " +
            "exact document scan, schema registration, and decision authority import. " +
            "Recoverable age is kill time minus archived tip source timestamp.");
        text.AppendLine();
        text.AppendLine("| Sample / bucket | Child exit | Tip / source / restored frontier | Recoverable age | Lost write age | Kill wait | Source deletion | List | Plan | Restore | Verify | Verified RTO | Result |");
        text.AppendLine("| --- | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |");
        foreach (var sample in samples)
            text.AppendLine($"| {sample.Number} / {sample.Bucket} | {sample.ExitCode} | {sample.TipSequence} / {sample.SourceSequenceAtFault} / {sample.RestoredSequence} | " +
                $"{sample.RecoverableAgeAtFaultMs:F1} ms | {sample.LostWriteAgeAtFaultMs:F1} ms | " +
                $"{sample.KillWaitMs:F1} ms | {sample.SourceDeleteMs:F1} ms | {sample.ListMs:F1} ms | " +
                $"{sample.PlanMs:F1} ms | {sample.RestoreMs:F1} ms | {sample.VerifyMs:F1} ms | " +
                $"{sample.TotalMs:F1} ms | {sample.VerifiedDocuments} exact documents; control 2; 1 lost write; schema and authority match |");
        text.AppendLine($"| p50 | — | — | {Percentile(samples.Select(s => s.RecoverableAgeAtFaultMs), .5):F1} ms | — | — | — | — | — | — | — | {Percentile(samples.Select(s => s.TotalMs), .5):F1} ms | — |");
        text.AppendLine($"| p95 | — | — | {Percentile(samples.Select(s => s.RecoverableAgeAtFaultMs), .95):F1} ms | — | — | — | — | — | — | — | {Percentile(samples.Select(s => s.TotalMs), .95):F1} ms | — |");
        text.AppendLine();
        text.AppendLine($"Failures: 0 of {samples.Count} samples. All restored frontiers matched their source's " +
            "committed full plus two incremental chain. Raw timestamps, manifest identities and SHA-256 values, " +
            "and stage durations are in samples.jsonl; sample-N-kill.json files record child PID, exit code, and barrier output.");
        text.AppendLine("This is a single-host process-kill drill against a loopback S3-compatible provider. " +
            "It does not model host power loss, WAN or region failure, detection time, or human response. " +
            "NoBackupEncryption and local HMAC signing are fixture settings, not production security evidence. " +
            "Percentiles use nearest rank over these samples.");
        return text.ToString();
    }

    private static int ParseDocumentNumber(string id) =>
        id.StartsWith("doc-", StringComparison.Ordinal)
        && int.TryParse(id.AsSpan(4), out var number)
        && number is > 0 and < DocumentCount
            ? number : throw new InvalidDataException($"Unexpected document id {id}.");
    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
    private static string Required(string[] args, string key) =>
        Optional(args, key) ?? throw new ArgumentException($"Missing {key}.");
    private static string? Optional(string[] args, string key)
    {
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private sealed record LocalRustFs(string AccessKey, string SecretKey, string ImageId);
    private sealed record RunIdentity(string SubjectRoot, string ManifestPath, string ManifestSha256,
        int VerifiedFiles, IReadOnlyList<string> Mismatches, string RunnerDllSha256,
        string LoadedTerraBaseDllSha256, string OS, string Architecture, string DotNetVersion,
        int LogicalProcessors, DateTimeOffset StartedAtUtc, int Samples, int ArchivalDelayMs,
        string S3Endpoint, string ContainerName, string ContainerImageId, IReadOnlyList<string> Buckets);
    private sealed record ChildReady(string SourceId, string FullId, string FullSha256, long FullSequence,
        string FirstId, string FirstSha256, long FirstSequence, string TipId, string TipSha256,
        long TipSequence, DateTimeOffset TipTimestamp, ulong SourceSequence, DateTimeOffset LostCommitAt,
        string SchemaFingerprint, string AuthorityChecksum, DateTimeOffset ReadyAt, int ProcessId);
    private sealed record Sample(int Number, string Bucket, DateTimeOffset FaultAt, DateTimeOffset ChildReadyAt,
        int ProcessId, int ExitCode, double RecoverableAgeAtFaultMs, double LostWriteAgeAtFaultMs,
        double KillWaitMs, double SourceDeleteMs, double ListMs, double PlanMs, double RestoreMs,
        double VerifyMs, double TotalMs, long FullSequence, long FirstSequence, long TipSequence,
        ulong SourceSequenceAtFault, long RestoredSequence, int AppliedLogEntries,
        int VerifiedDocuments, int RestoredControlValue, int LostControlValue,
        string SchemaFingerprint, string AuthorityChecksum,
        string FullSha256, string FirstSha256, string TipSha256,
        string FullId, string FirstId, string TipId);
}
