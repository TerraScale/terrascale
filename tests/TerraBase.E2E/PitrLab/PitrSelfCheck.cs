using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Bogus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using TerraBase.Features.Backup;
using TerraBase.Testing;

namespace TerraBase.E2E.PitrLab;

/// <summary>
/// Checks PitrLab end to end on this machine: the node's own S3 client (S3ObjectStore) against s3-serve, then an
/// in-process node (the real Program, manual shipping on a fake clock) backing up over HTTPS into s3-serve, a
/// point-in-time restore through the API from those objects, audits of what landed on disk and one audit per mutation;
/// and multipart uploads through the SDK (a failed part, a server restart mid-upload, abort, S3's completion errors).
/// </summary>
internal static class PitrSelfCheck
{
    private static readonly Faker Faker = new();

    public static async Task<List<SelfCheckResult>> RunAsync(string root, CancellationToken cancellationToken)
    {
        var work = Path.Combine(Path.GetFullPath(root), Ulid.NewUlid().ToString());
        Directory.CreateDirectory(work);
        var results = new List<SelfCheckResult>();
        void Check(string name, bool ok, string detail)
        {
            results.Add(new(name, ok, detail));
            Console.WriteLine($"self-check {(ok ? "ok  " : "FAIL")} {name}: {detail}");
        }

        try
        {
            await ClientAsync(work, Check, cancellationToken);
            await FaultsAsync(work, Check, cancellationToken);
            await MultipartAsync(work, Check, cancellationToken);
            await NodeAsync(work, Check, cancellationToken);
        }
        finally
        {
            await PitrJson.WriteAtomicAsync(Path.Combine(work, "self-check.json"), results, PitrJson.Default.ListSelfCheckResult, cancellationToken);
        }

        if (results.Any(result => !result.Ok))
        {
            throw new InvalidOperationException($"self-check failed: {string.Join(", ", results.Where(result => !result.Ok).Select(result => result.Name))} ({work})");
        }

        Console.WriteLine($"self-check: {results.Count} checks passed ({work})");
        return results;
    }

    // The node's S3 client, unchanged, over HTTPS to s3-serve: HeadBucket, pages of ListObjectsV2, a non-ASCII key, delete, restart.
    private static async Task ClientAsync(string work, Action<string, bool, string> check, CancellationToken cancellationToken)
    {
        var root = Path.Combine(work, "serve-client");
        const string bucket = "pitrlab-client";
        List<string> keys = [.. Enumerable.Range(0, 1001).Select(i => $"smoke/k{i:D4}"), "smoke/ü é+%/x"];
        await using (var serve = await S3Serve.StartAsync(root, bucket, null, Path.Combine(work, "serve-client.log"), cancellationToken))
        {
            using var services = Trusting(serve.Authority);
            using var store = Store(serve, bucket, services);
            check("s3.head_bucket", !await store.SnapshotsEnabledAsync(cancellationToken), "HeadBucket reports no Tigris snapshots, so the node's bucket snapshot job skips");
            await Parallel.ForEachAsync(keys, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = cancellationToken }, async (key, token) =>
            {
                using var content = new MemoryStream(Encoding.UTF8.GetBytes("object " + key));
                await store.PutAsync(key, content, token);
            });
            var listed = await ListAsync(store, "smoke/", null, cancellationToken);
            check("s3.list_pages", listed.SequenceEqual(keys.Order(S3KeyOrder.Instance)), $"{listed.Count} keys over two ListObjectsV2 pages in UTF-8 byte order");
            var after = await ListAsync(store, "smoke/", "smoke/k0999", cancellationToken);
            check("s3.start_after", after.SequenceEqual(["smoke/k1000", "smoke/ü é+%/x"]), string.Join(", ", after));
            var read = await ReadAsync(store, "smoke/ü é+%/x", cancellationToken);
            check("s3.get", read == "object smoke/ü é+%/x", "GET returns the stored bytes of a key that needs percent-encoding");
            check("s3.get_missing", !await store.ReadAsync("smoke/none", (_, _) => Task.CompletedTask, cancellationToken), "GET of a missing key is a 404");
            await store.DeleteAsync("smoke/k0000", cancellationToken);
            await store.DeleteAsync("smoke/none", cancellationToken);
            check("s3.delete", (await ListAsync(store, "smoke/", null, cancellationToken)).Count == keys.Count - 1, "DELETE removes one object; deleting a missing key succeeds");
        }

        await using (var again = await S3Serve.StartAsync(root, bucket, null, Path.Combine(work, "serve-client.log"), cancellationToken))
        {
            using var services = Trusting(again.Authority);
            using var store = Store(again, bucket, services);
            var listed = await ListAsync(store, "smoke/", null, cancellationToken);
            check("s3.restart", listed.Count == keys.Count - 1 && await ReadAsync(store, "smoke/k1000", cancellationToken) == "object smoke/k1000",
                $"a new server on the same root lists {listed.Count} objects and reads them back");
        }
    }

    // PUT faults: the SDK retries the 500s; every object ends up stored, and each applied fault is one log line naming its key.
    private static async Task FaultsAsync(string work, Action<string, bool, string> check, CancellationToken cancellationToken)
    {
        const string bucket = "pitrlab-faults";
        var log = Path.Combine(work, "serve-faults.log");
        await using var serve = await S3Serve.StartAsync(Path.Combine(work, "serve-faults"), bucket, new PitrFaults(PutDelayMs: 5, PutFailEvery: 3, PutStoreThenFailEvery: 5, KeyRegex: "^fault/"),
            log, cancellationToken);
        using var services = Trusting(serve.Authority);
        using var store = Store(serve, bucket, services);
        List<string> keys = [.. Enumerable.Range(0, 8).Select(i => $"fault/{i}"), "clean/0"];
        foreach (var key in keys)
        {
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("object " + key));
            await store.PutAsync(key, content, cancellationToken);
        }

        var stored = 0;
        foreach (var key in keys)
        {
            stored += await ReadAsync(store, key, cancellationToken) == "object " + key ? 1 : 0;
        }

        var lines = (await File.ReadAllLinesAsync(log, cancellationToken)).Where(line => line.Contains(" fault ", StringComparison.Ordinal)).ToList();
        check("s3.faults", stored == keys.Count && lines.Any(line => line.Contains("put_fail fault/", StringComparison.Ordinal))
            && lines.Any(line => line.Contains("put_store_then_fail fault/", StringComparison.Ordinal)) && !lines.Any(line => line.Contains("clean/", StringComparison.Ordinal)),
            $"{stored}/{keys.Count} objects stored after the SDK's retries; {lines.Count} fault lines, none for keys outside key_regex");

        // Catalog anchors (R10a, outside v1/) are never faulted, even when every PUT fails, and never mutated.
        var anchorRoot = Path.Combine(work, "serve-anchor");
        var anchorLog = Path.Combine(work, "serve-anchor.log");
        string? anchor;
        await using (var all = await S3Serve.StartAsync(anchorRoot, bucket, new PitrFaults(PutFailEvery: 1), anchorLog, cancellationToken))
        {
            using var anchorServices = Trusting(all.Authority);
            using var anchors = Store(all, bucket, anchorServices);
            using var content = new MemoryStream(Encoding.UTF8.GetBytes("anchor"));
            await anchors.PutAsync("catalog-anchor/machine-1", content, cancellationToken);
            anchor = await ReadAsync(anchors, "catalog-anchor/machine-1", cancellationToken);
        }

        var refused = false;
        try
        {
            await PitrMutate.RunAsync(anchorRoot, Path.Combine(work, "mutated-anchor"), new PitrMutation("stray_key", "catalog-anchor/machine-2"), cancellationToken);
        }
        catch (InvalidDataException)
        {
            refused = true;
        }

        var anchorFaults = (await File.ReadAllLinesAsync(anchorLog, cancellationToken)).Count(line => line.Contains(" fault ", StringComparison.Ordinal));
        check("s3.catalog_anchor", anchor == "anchor" && anchorFaults == 0 && refused && !Directory.Exists(Path.Combine(work, "mutated-anchor")),
            $"put_fail_every 1 without key_regex: the anchor stored and read back ({anchor is not null}), {anchorFaults} fault lines; mutate refused it: {refused}");
    }

    // Multipart uploads through the SDK with the node's settings (signed whole payloads): part 2's first attempt fails
    // (part_fail_at) and the SDK's retry stores it; the server restarts between the parts and the completion (parts are on
    // disk); then an abort, a completion with a part under 5 MiB that is not the last, and one naming a wrong ETag.
    private static async Task MultipartAsync(string work, Action<string, bool, string> check, CancellationToken cancellationToken)
    {
        const string bucket = "pitrlab-multipart";
        const string key = "mp/object";
        var root = Path.Combine(work, "serve-multipart");
        var log = Path.Combine(work, "serve-multipart.log");
        var first = RandomNumberGenerator.GetBytes(5 << 20);
        var last = Encoding.UTF8.GetBytes(Faker.Lorem.Paragraphs(4));
        // The node's multipart completion proof reads this from HEAD (x-amz-meta-terrabase-upload).
        var marker = Ulid.NewUlid().ToString();
        string uploadId;
        List<PartETag> parts = [];
        await using (var serve = await S3Serve.StartAsync(root, bucket, new PitrFaults(PartFailAt: 2, KeyRegex: "^mp/"), log, cancellationToken))
        {
            using var s3 = Client(serve);
            var initiate = new InitiateMultipartUploadRequest { BucketName = bucket, Key = key };
            initiate.Metadata.Add("terrabase-upload", marker);
            uploadId = (await s3.InitiateMultipartUploadAsync(initiate, cancellationToken)).UploadId;
            parts.Add(new(1, await UploadPartAsync(s3, bucket, key, uploadId, 1, first, cancellationToken)));
            parts.Add(new(2, await UploadPartAsync(s3, bucket, key, uploadId, 2, last, cancellationToken)));
        }

        var faults = (await File.ReadAllLinesAsync(log, cancellationToken)).Count(line => line.Contains($" fault part_fail {key} part 2", StringComparison.Ordinal));
        await using var again = await S3Serve.StartAsync(root, bucket, null, log, cancellationToken);
        using var client = Client(again);
        using var services = Trusting(again.Authority);
        using var store = Store(again, bucket, services);
        var completed = await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest { BucketName = bucket, Key = key, UploadId = uploadId, PartETags = parts }, cancellationToken);
        byte[]? read = null;
        await store.ReadAsync(key, async (content, token) =>
        {
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, token);
            read = copy.ToArray();
        }, cancellationToken);
        check("multipart.complete", read is not null && read.AsSpan().SequenceEqual([.. first, .. last]) && faults == 1 && !Directory.Exists(Path.Combine(root, "uploads", uploadId)),
            $"{read?.Length} bytes read back byte-exact after a restart between the parts and the completion; {faults} part_fail line, the SDK's retry stored part 2; the upload's parts are gone");

        // S3's ETags: each part's is its MD5 (as the SDK received them), the object's the MD5 of those MD5s, then -2.
        var composite = $"\"{Convert.ToHexStringLower(Md5([.. Md5(first), .. Md5(last)]))}-2\"";
        var partsAreMd5 = parts[0].ETag == $"\"{Convert.ToHexStringLower(Md5(first))}\"" && parts[1].ETag == $"\"{Convert.ToHexStringLower(Md5(last))}\"";
        var head = await client.GetObjectMetadataAsync(bucket, key, cancellationToken);
        check("multipart.etag", partsAreMd5 && completed.ETag == composite && head.ETag == composite,
            $"parts {string.Join(", ", parts.Select(part => part.ETag))}; CompleteMultipartUpload {completed.ETag} and HEAD {head.ETag}, want {composite}");
        string? got;
        using (var get = await client.GetObjectAsync(bucket, key, cancellationToken))
        {
            got = get.Metadata["x-amz-meta-terrabase-upload"];
        }

        var reopened = DiskBucket.Open(root, create: false);
        var file = new FileInfo(reopened.PathOf(key));
        var (diskETag, diskMetadata) = reopened.Describe(key, file.Length, file.LastWriteTimeUtc);
        check("multipart.metadata", head.Metadata["x-amz-meta-terrabase-upload"] == marker && got == marker && diskETag == composite
            && diskMetadata.GetValueOrDefault("x-amz-meta-terrabase-upload") == marker,
            $"x-amz-meta-terrabase-upload {marker}, set at CreateMultipartUpload before a server restart: HEAD {head.Metadata["x-amz-meta-terrabase-upload"]}, GET {got}, reloaded from disk {diskMetadata.GetValueOrDefault("x-amz-meta-terrabase-upload")} with ETag {diskETag}");

        using var body = new MemoryStream(last);
        var single = new PutObjectRequest { BucketName = bucket, Key = "single/object", InputStream = body, UseChunkEncoding = false };
        single.Metadata.Add("terrabase-upload", marker);
        var put = await client.PutObjectAsync(single, cancellationToken);
        var singleHead = await client.GetObjectMetadataAsync(bucket, "single/object", cancellationToken);
        var md5 = $"\"{Convert.ToHexStringLower(Md5(last))}\"";
        check("s3.single_put", put.ETag == md5 && singleHead.ETag == md5 && singleHead.Metadata["x-amz-meta-terrabase-upload"] == marker,
            $"PutObject {put.ETag}, HEAD {singleHead.ETag} (want the MD5 {md5}) and x-amz-meta-terrabase-upload {singleHead.Metadata["x-amz-meta-terrabase-upload"]}");

        var aborted = (await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "mp/aborted" }, cancellationToken)).UploadId;
        await UploadPartAsync(client, bucket, "mp/aborted", aborted, 1, last, cancellationToken);
        await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = "mp/aborted", UploadId = aborted }, cancellationToken);
        var afterAbort = await ErrorCodeAsync(() => UploadPartAsync(client, bucket, "mp/aborted", aborted, 2, last, cancellationToken));
        var listed = await ListAsync(store, "mp/", null, cancellationToken);
        check("multipart.abort", afterAbort == "NoSuchUpload" && listed.SequenceEqual([key]) && !Directory.Exists(Path.Combine(root, "uploads", aborted)),
            $"after the abort a part answers {afterAbort}; the bucket lists only {string.Join(", ", listed)}");

        var small = (await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest { BucketName = bucket, Key = "mp/small" }, cancellationToken)).UploadId;
        List<PartETag> smallParts = [new(1, await UploadPartAsync(client, bucket, "mp/small", small, 1, last, cancellationToken)),
            new(2, await UploadPartAsync(client, bucket, "mp/small", small, 2, last, cancellationToken))];
        var tooSmall = await ErrorCodeAsync(() => client.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest { BucketName = bucket, Key = "mp/small", UploadId = small, PartETags = smallParts }, cancellationToken));
        var wrongETag = await ErrorCodeAsync(() => client.CompleteMultipartUploadAsync(
            new CompleteMultipartUploadRequest { BucketName = bucket, Key = "mp/small", UploadId = small, PartETags = [new(1, "\"0-0\"")] }, cancellationToken));
        await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest { BucketName = bucket, Key = "mp/small", UploadId = small }, cancellationToken);
        check("multipart.errors", tooSmall == "EntityTooSmall" && wrongETag == "InvalidPart" && !await store.ReadAsync("mp/small", (_, _) => Task.CompletedTask, cancellationToken),
            $"a small part before the last: {tooSmall}; a wrong ETag: {wrongETag}; nothing stored");
    }

    // S3's ETags are MD5s: the protocol's, not a security use.
    private static byte[] Md5(byte[] data)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(data);
        return md5.GetHashAndReset();
    }

    private static async Task<string> UploadPartAsync(AmazonS3Client s3, string bucket, string key, string uploadId, int number, byte[] content, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(content);
        var response = await s3.UploadPartAsync(new UploadPartRequest
        {
            BucketName = bucket,
            Key = key,
            UploadId = uploadId,
            PartNumber = number,
            InputStream = stream,
            UseChunkEncoding = false,
        }, cancellationToken);
        return response.ETag;
    }

    private static async Task<string?> ErrorCodeAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (AmazonS3Exception error)
        {
            return error.ErrorCode;
        }
    }

    // The node's S3 client settings (S3ObjectStore), trusting only the s3-serve CA.
    private static AmazonS3Client Client(S3Serve serve) => new(new BasicAWSCredentials("pitrlab", "pitrlab-local"), new AmazonS3Config
    {
        ServiceURL = serve.Endpoint.ToString(),
        AuthenticationRegion = S3Serve.Region,
        ForcePathStyle = true,
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        HttpClientFactory = new TrustingClients(serve.Authority),
    });

    private sealed class TrustingClients(X509Certificate2 authority) : HttpClientFactory
    {
        public override HttpClient CreateHttpClient(IClientConfig clientConfig) => new(Handler(authority));
    }

    // An in-process node ships its backup over HTTPS into s3-serve; a restore reads it back; audits and mutations check the root.
    private static async Task NodeAsync(string work, Action<string, bool, string> check, CancellationToken cancellationToken)
    {
        const string bucket = "pitrlab-node";
        var root = Path.Combine(work, "serve-node");
        var masterKey = Environment.GetEnvironmentVariable("TERRABASE_MASTER_KEY") is { Length: > 0 } ? PitrLabHost.MasterKeyFromEnvironment() : RandomNumberGenerator.GetBytes(32);
        await using var serve = await S3Serve.StartAsync(root, bucket, null, Path.Combine(work, "serve-node.log"), cancellationToken);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        await using var api = await ManagementApi.StartAsync(settings =>
        {
            settings["backup-endpoint"] = $"https://127.0.0.1:{serve.Endpoint.Port}";
            settings["backup-region"] = S3Serve.Region;
            settings["backup-bucket"] = bucket;
            settings["backup-access-key-id"] = "pitrlab";
            settings["backup-secret-key"] = "pitrlab-local";
            settings["master-key"] = Convert.ToBase64String(masterKey);
        }, services =>
        {
            services.AddHttpClient(S3ObjectStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Handler(serve.Authority));
            ManualBackupHosting.Configure(services);
        }, time);
        Directory.CreateDirectory(Path.Combine(api.Server.DataDirectory, ".backup"));
        var backup = api.Server.Services.GetRequiredService<IContinuousBackup>();
        var database = await api.CreateManagedDatabaseAsync("main");
        using var data = await api.CreateClientAsync(database.Organization, "documents:read", "documents:write");
        await ManagementApi.CreateCollectionAsync(data, database, "people", cancellationToken);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        async Task WriteAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var name = $"person-{texts.Count}";
                texts[name] = Faker.Lorem.Sentence(12);
                using var put = await data.PutAsJsonAsync(ManagementApi.Uri(ManagementApi.DocumentPath(database, "people", name)), new { text = texts[name] }, cancellationToken);
                put.EnsureSuccessStatusCode();
            }
        }

        // A generation's first snapshot, three segments, the next day's snapshot (after the segment it ends), one more segment.
        await WriteAsync(5);
        time.Advance(TimeSpan.FromSeconds(10));
        await backup.RunOnceAsync(cancellationToken);
        for (var round = 0; round < 3; round++)
        {
            await WriteAsync(5);
            time.Advance(TimeSpan.FromSeconds(1));
            await backup.RunOnceAsync(cancellationToken);
        }

        time.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        await WriteAsync(5);
        await backup.RunOnceAsync(cancellationToken);
        await WriteAsync(5);
        time.Advance(TimeSpan.FromSeconds(1));
        await backup.RunOnceAsync(cancellationToken);

        var file = $"v1/{database.Organization}/{database.Project}/{database.Id}/nodes/local";
        var status = backup.Status.Files.Single(item => item.File == file);
        using var restore = await api.Client.PostAsJsonAsync(ManagementApi.Uri($"{database.Path}/point-in-time/restore"),
            new { pointInTime = status.LastShippedAt!.Value, name = "main-restored" }, cancellationToken);
        var restored = 0;
        foreach (var (name, text) in texts)
        {
            using var read = await data.GetAsync(ManagementApi.Uri(
                $"/v1/projects/{database.Project}/databases/main-restored/collections/people/documents/by-key?id={ManagementApi.DocumentId(name)}"), cancellationToken);
            if (read.StatusCode == HttpStatusCode.OK
                && (await read.Content.ReadFromJsonAsync<JsonElement>(cancellationToken)).GetProperty("document").GetProperty("text").GetString() == text)
            {
                restored++;
            }
        }

        check("node.restore", restore.StatusCode == HttpStatusCode.Created && restored == texts.Count,
            $"point-in-time restore from s3-serve: {(int)restore.StatusCode}, {restored}/{texts.Count} documents with their latest text; drill passed: {status.LastDrill?.Passed}");

        var scratch = Path.Combine(work, "scratch");
        var keysPrefix = $"v1/keys/{database.Organization}/{database.Project}/";
        var original = await AuditAsync(root, file + "/", keysPrefix, masterKey, scratch, Path.Combine(work, "audit-original.json"), cancellationToken);
        var generation = original.Generations.Count > 0 ? original.Generations[0] : null;
        check("audit.original", original.Verdict == "ok" && original.Generations.Count == 1 && original.Totals.Snapshots == 2 && original.Totals.Segments >= 4
            && original.Totals.Upserts >= texts.Count,
            $"{original.Verdict}: {original.Totals.Snapshots} snapshots, {original.Totals.Segments} segments, {original.Totals.Upserts} upserts, {original.Totals.Commits} commits, " +
            $"problems: {string.Join("; ", original.Problems.Select(problem => problem.Kind + " " + problem.Detail))}");
        var system = await AuditAsync(root, "v1/_system/nodes/local/", "v1/keys/_system/", masterKey, scratch, Path.Combine(work, "audit-system.json"), cancellationToken);
        check("audit.system", system.Verdict == "ok" && system.Totals.Snapshots >= 1, $"{system.Verdict}: the system database's {system.Totals.Snapshots} snapshots, {system.Totals.Segments} segments");
        if (generation is null || generation.Segments.Count < 4)
        {
            return;
        }

        var segments = generation.Segments;
        (string Kind, string Key, string[] Expected)[] mutations =
        [
            ("delete_segment", segments[1].Key, ["gap", "restore_break"]),
            ("corrupt_segment", segments[0].Key, ["unauthenticated", "restore_break"]),
            ("truncate_segment", segments[2].Key, ["unauthenticated", "restore_break"]),
            ("duplicate_segment", segments[0].Key, ["unauthenticated", "duplicate_start"]),
            ("stray_key", $"{file}/wal/stray-object", ["unparseable_key"]),
            ("corrupt_snapshot", generation.Snapshots[0].Key, ["unauthenticated"]),
            // The earliest snapshot gone: the later one still restores, so the verdict stays ok; the window shrinks.
            ("delete_snapshot", generation.Snapshots[0].Key, []),
        ];
        foreach (var (kind, key, expected) in mutations)
        {
            var to = Path.Combine(work, "mutated-" + kind);
            var manifest = await PitrMutate.RunAsync(root, to, new PitrMutation(kind, key), cancellationToken);
            await PitrJson.WriteAtomicAsync(to + ".mutation.json", manifest, PitrJson.Default.MutationManifest, cancellationToken);
            var report = await AuditAsync(to, file + "/", keysPrefix, masterKey, scratch, to + ".audit.json", cancellationToken);
            var kinds = report.Problems.Select(problem => problem.Kind).Distinct().Order(StringComparer.Ordinal).ToList();
            var ok = expected.Length == 0
                ? report.Verdict == "ok" && report.Totals.Snapshots == original.Totals.Snapshots - 1 && report.Notes.Count > 0
                : report.Verdict == "fail" && expected.All(kinds.Contains);
            var unchanged = (await AuditAsync(root, file + "/", keysPrefix, masterKey, scratch, Path.Combine(work, "audit-source-after-" + kind + ".json"), cancellationToken)).Verdict == "ok";
            check("mutate." + kind, ok && unchanged,
                $"audit {report.Verdict} [{string.Join(", ", kinds)}], expected [{string.Join(", ", expected)}]; linked {manifest.Linked}; source still ok: {unchanged}");
        }
    }

    private static async Task<AuditReport> AuditAsync(
        string root, string prefix, string keysPrefix, byte[] masterKey, string scratch, string output, CancellationToken cancellationToken)
    {
        var report = await PitrAudit.RunAsync(DiskBucket.Open(root, create: false), prefix, keysPrefix, masterKey, scratch, cancellationToken);
        await PitrJson.WriteAtomicAsync(output, report, PitrJson.Default.AuditReport, cancellationToken);
        return report;
    }

    private static async Task<List<string>> ListAsync(S3ObjectStore store, string prefix, string? after, CancellationToken cancellationToken)
    {
        var keys = new List<string>();
        await foreach (var item in store.ListAsync(prefix, after, cancellationToken))
        {
            keys.Add(item.Key);
        }

        return keys;
    }

    private static async Task<string?> ReadAsync(S3ObjectStore store, string key, CancellationToken cancellationToken)
    {
        string? text = null;
        await store.ReadAsync(key, async (content, token) =>
        {
            using var reader = new StreamReader(content, Encoding.UTF8);
            text = await reader.ReadToEndAsync(token);
        }, cancellationToken);
        return text;
    }

    private static S3ObjectStore Store(S3Serve serve, string bucket, ServiceProvider services) =>
        new(serve.Endpoint, S3Serve.Region, bucket, "pitrlab", "pitrlab-local", services.GetRequiredService<IHttpClientFactory>(), S3ObjectStore.HttpClientName, TimeProvider.System);

    private static ServiceProvider Trusting(X509Certificate2 authority)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(S3ObjectStore.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Handler(authority));
        return services.BuildServiceProvider();
    }

    // Trusts the s3-serve CA only, and still requires the server certificate's name to match (SAN 127.0.0.1).
    private static SocketsHttpHandler Handler(X509Certificate2 authority) => new()
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, presented, _, errors) =>
            {
                if (presented is not X509Certificate2 certificate || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
                {
                    return false;
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(authority);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return chain.Build(certificate);
            },
        },
    };
}
