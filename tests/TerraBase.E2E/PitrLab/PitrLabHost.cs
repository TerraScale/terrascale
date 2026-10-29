// PitrLab: an independent backup and point-in-time-restore verification harness for TerraBase's continuous backup.
//
// Run one job (build first: nice -n 19 dotnet build tests/TerraBase.E2E -c Release):
//   TERRABASE_PITR_JOB=/path/job.json dotnet run --project tests/TerraBase.E2E -c Release --no-build -- \
//     --treenode-filter '/*/*/PitrLabHost/*'
// Without TERRABASE_PITR_JOB the test skips (it is a host, not a scenario). Console lines show with --output detailed.
//
// Job JSON is snake_case; unknown fields are refused. Modes:
//
//   s3-serve   {"mode":"s3-serve","root":"<dir>","bucket":"<name>","ready_file":"<path>","ca_pem":"<path, optional>",
//               "faults":{"put_delay_ms":N,"put_fail_every":N,"put_store_then_fail_every":N,"put_store_then_fail_from":N,
//                         "put_store_then_fail_count":N,"part_fail_at":N,"part_fail_every":N,"key_regex":"..."}}
//              A disk-backed S3 bucket on https://127.0.0.1:<random port> for a separate terrabase process (S3Serve).
//              The CA PEM goes to ca_pem (default <root>/../s3-ca.pem): point SSL_CERT_FILE at it. When listening it
//              writes ready_file: {"endpoint","region":"local-1","bucket","ca_pem","log","pid"}, and serves until
//              ready_file is deleted. Faults apply to object PUTs matching key_regex (all when absent), counted
//              together: every put_fail_every-th answers 500 without storing, every put_store_then_fail_every-th stores
//              and then answers 500 (an ambiguous success), and so do the put_store_then_fail_count PUTs from the
//              put_store_then_fail_from-th on (a burst that outlasts the SDK's own retries). Multipart uploads (create,
//              upload part, complete, abort) keep their parts on disk under <root>/uploads/; parts matching key_regex
//              are counted separately: the part_fail_at-th part request, and every part_fail_every-th, answers 500
//              without storing; put_delay_ms delays every part too. Keys under catalog-anchor/ (R10a catalog anchors,
//              outside v1/) are stored and served like any key and never faulted. <ready_file>.log gets a start line,
//              one line per applied fault (time, fault, key) and one per multipart create, complete and abort. Any
//              credentials are accepted; none is logged.
//
//   download   {"mode":"download","source":{"kind":"s3"}|{"kind":"dir","root":"<dir>"},"prefixes":["v1/<org>/<project>/<db>/",
//               "v1/keys/<org>/<project>/"],"root":"<dest dir>","manifest":"<path>"}
//              Byte-exact copy into a root s3-serve can serve. An s3 source reads AWS_ENDPOINT_URL_S3, AWS_REGION,
//              BUCKET_NAME, AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY from the environment and only lists and gets.
//              Manifest: [{"key","size","sha256","etag"}], rewritten every 500 objects; a rerun skips objects whose
//              listed size and ETag match the manifest and whose local file still hashes to it.
//
//   audit      {"mode":"audit","source":...,"database_prefix":"v1/<org>/<project>/<db>/","keys_prefix":"v1/keys/<org>/<project>/",
//               "out":"<path>"}
//              Opens every snapshot and segment with the product's reader under TERRABASE_MASTER_KEY (environment only),
//              counts each segment's change records and walks each generation as a restore does. out:
//              {"verdict":"ok"|"fail","problems":[{"kind","key","detail"}],"notes","totals","data_keys","unparseable_keys",
//               "generations":[{"generation","snapshots":[...],"segments":[...]}]}. Problem kinds: unauthenticated,
//              missing_data_key, data_key_unwrap, trailing_bytes, malformed_log, open_transaction, unparseable_key, gap,
//              overlap, duplicate_start, snapshot_not_on_boundary, restore_break, time_order, no_snapshot.
//
//   mutate     {"mode":"mutate","from":"<root>","to":"<new root, must not exist>","mutation":{"kind":"delete_segment"|
//               "corrupt_segment"|"truncate_segment"|"duplicate_segment"|"stray_key"|"delete_snapshot"|"corrupt_snapshot",
//               "key":"<exact object key>"},"out":"<manifest path>"}
//              A hard-linked copy of from with one object changed (always through a new file; from never changes); the
//              key must be under v1/ (catalog-anchor/ keys and the rest outside v1/ are copied byte-exact, never mutated). out
//              records the change, both fingerprints and what the mutation proves (PitrMutate).
//
//   inventory  {"mode":"inventory","source":{"kind":"s3"}|{"kind":"dir","root":"<dir>"},"prefixes":["<prefix>",...],"out":"<path>"}
//              Lists the prefixes (default the whole bucket) and nothing else: {"objects","bytes","list_requests",
//              "prefixes":[{"prefix","objects","bytes","list_requests"}]}, list requests counted as 1,000-key pages. An s3
//              source reads its bucket and credentials from the environment, like download. The G1 spend monitor
//              (pitr-bench monitor) runs it every 10 minutes.
//
//   self-check {"mode":"self-check","root":"<dir>"}
//              In <root>/<ulid>/: the node's own S3 client against s3-serve (listing pages, restart, faults), then an
//              in-process node shipping its backup over HTTPS into s3-serve, a point-in-time restore from it, audits
//              (expect ok) and every mutation (expect its problem). Results in self-check.json. The node's master key
//              is TERRABASE_MASTER_KEY when set (so later audit jobs can open serve-node/), else a random one.
//
// Exit codes (Microsoft.Testing.Platform): 0 the job ran and wrote its output (an audit verdict of "fail" is still 0:
// read the verdict), or the test skipped because TERRABASE_PITR_JOB is unset (so check the output file exists);
// 2 the job failed (bad job file, unknown field, missing setting, I/O error; the message names settings, never values);
// 8 the filter matched no test.

using System.Security.Cryptography;
using System.Text.Json;

namespace TerraBase.E2E.PitrLab;

/// <summary>Runs the PitrLab job named by <c>TERRABASE_PITR_JOB</c>; see the comment at the top of this file.</summary>
[Category("benchmark-host")]
public sealed class PitrLabHost
{
    private const string JobVariable = "TERRABASE_PITR_JOB";

    [Test]
    [Scenario(Nodes.InProcess)]
    [Timeout(72 * 3600 * 1000)]
    public async Task Runs_the_PitrLab_job_named_by_TERRABASE_PITR_JOB(CancellationToken cancellationToken)
    {
        var path = Environment.GetEnvironmentVariable(JobVariable);
        Skip.When(string.IsNullOrEmpty(path), "the PitrLab host: set TERRABASE_PITR_JOB to a job file");
        PitrJob job;
        await using (var file = File.OpenRead(path!))
        {
            job = await JsonSerializer.DeserializeAsync(file, PitrJson.Default.PitrJob, cancellationToken)
                ?? throw new InvalidDataException("the job file is empty");
        }

        await RunAsync(job, cancellationToken);
    }

    internal static async Task RunAsync(PitrJob job, CancellationToken cancellationToken)
    {
        switch (job.Mode)
        {
            case "s3-serve":
                await ServeAsync(job, cancellationToken);
                break;
            case "download":
                {
                    var source = PitrSources.Open(job.Source);
                    try
                    {
                        var prefixes = job.Prefixes is { Count: > 0 } list ? list : throw new InvalidDataException("prefixes is required");
                        await PitrDownload.RunAsync(source, prefixes, Required(job.Root, "root"), Required(job.Manifest, "manifest"), cancellationToken);
                    }
                    finally
                    {
                        (source as IDisposable)?.Dispose();
                    }

                    break;
                }

            case "audit":
                {
                    var output = Path.GetFullPath(Required(job.Out, "out"));
                    var databasePrefix = Required(job.DatabasePrefix, "database_prefix");
                    var keysPrefix = Required(job.KeysPrefix, "keys_prefix");
                    var masterKey = MasterKeyFromEnvironment();
                    var source = PitrSources.Open(job.Source);
                    try
                    {
                        var scratch = output + ".scratch";
                        var report = await PitrAudit.RunAsync(source, databasePrefix, keysPrefix, masterKey, scratch, cancellationToken);
                        // Segment scratch files delete themselves on close; only the empty directory is left.
                        Directory.Delete(scratch);
                        await PitrJson.WriteAtomicAsync(output, report, PitrJson.Default.AuditReport, cancellationToken);
                        Console.WriteLine($"audit: {report.Verdict}, {report.Problems.Count} problems, {report.Totals.Segments} segments, {report.Totals.Snapshots} snapshots");
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(masterKey);
                        (source as IDisposable)?.Dispose();
                    }

                    break;
                }

            case "mutate":
                {
                    var manifest = await PitrMutate.RunAsync(Required(job.From, "from"), Required(job.To, "to"),
                        job.Mutation ?? throw new InvalidDataException("mutation is required"), cancellationToken);
                    await PitrJson.WriteAtomicAsync(Required(job.Out, "out"), manifest, PitrJson.Default.MutationManifest, cancellationToken);
                    Console.WriteLine($"mutate: {manifest.Kind} {manifest.NewKey ?? manifest.Key}, {manifest.Objects} objects, linked {manifest.Linked}");
                    break;
                }

            case "prepare-target":
                await PitrTarget.RunAsync(job, cancellationToken);
                break;
            case "self-check":
                await PitrSelfCheck.RunAsync(Required(job.Root, "root"), cancellationToken);
                break;
            case "inventory":
                {
                    var source = PitrSources.Open(job.Source);
                    try
                    {
                        List<PrefixInventory> prefixes = [];
                        foreach (var prefix in job.Prefixes is { Count: > 0 } list ? list : [""])
                        {
                            long objects = 0, bytes = 0;
                            await foreach (var item in source.ListAsync(prefix, cancellationToken))
                            {
                                objects++;
                                bytes += item.Size;
                            }

                            // ListObjectsV2 pages of 1,000 keys; an empty prefix still costs one request.
                            prefixes.Add(new(prefix, objects, bytes, Math.Max(1, (objects + 999) / 1000)));
                        }

                        var report = new InventoryReport(prefixes.Sum(item => item.Objects), prefixes.Sum(item => item.Bytes), prefixes.Sum(item => item.ListRequests), prefixes);
                        await PitrJson.WriteAtomicAsync(Required(job.Out, "out"), report, PitrJson.Default.InventoryReport, cancellationToken);
                        Console.WriteLine($"inventory: {report.Objects} objects, {report.Bytes} bytes, {report.ListRequests} list requests");
                    }
                    finally
                    {
                        (source as IDisposable)?.Dispose();
                    }

                    break;
                }

            default:
                throw new InvalidDataException("mode must be s3-serve, download, audit, mutate, inventory, prepare-target or self-check");
        }
    }

    private static async Task ServeAsync(PitrJob job, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Required(job.Root, "root"));
        var ready = Path.GetFullPath(Required(job.ReadyFile, "ready_file"));
        var caPem = Path.GetFullPath(job.CaPem ?? Path.Combine(Path.GetDirectoryName(root)!, "s3-ca.pem"));
        var bucket = Required(job.Bucket, "bucket");
        await using var server = await S3Serve.StartAsync(root, bucket, job.Faults, ready + ".log", cancellationToken);
        var partial = $"{caPem}.{Ulid.NewUlid()}.partial";
        await File.WriteAllTextAsync(partial, server.Authority.ExportCertificatePem() + "\n", cancellationToken);
        File.Move(partial, caPem, overwrite: true);
        await PitrJson.WriteAtomicAsync(ready, new ReadyFile($"https://127.0.0.1:{server.Endpoint.Port}", S3Serve.Region, bucket, caPem, server.LogPath,
            Environment.ProcessId), PitrJson.Default.ReadyFile, cancellationToken);
        Console.WriteLine($"s3-serve: {server.Endpoint} ready ({ready})");
        while (File.Exists(ready))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
    }

    private static string Required(string? value, string name) => value is { Length: > 0 } ? value : throw new InvalidDataException($"{name} is required");

    internal static byte[] MasterKeyFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("TERRABASE_MASTER_KEY");
        var key = new byte[33];
        try
        {
            return value is { Length: > 0 } && Convert.TryFromBase64String(value, key, out var written) && written == 32
                ? key[..32]
                : throw new InvalidDataException("TERRABASE_MASTER_KEY must be set to 32 bytes, base64");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
