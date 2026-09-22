using System.Text.Json;
using System.Security.Cryptography;
using TerraBase.Backup;
using TerraBase.Storage.ChangeFeed;
using TerraBase.StorageEngine;
using Engine = TerraBase.StorageEngine.StorageEngine;

namespace TerraBase.Benchmarks;

/// <summary>Exercises genuine filesystem ENOSPC inside a caller-mounted, bounded tmpfs.</summary>
public static class DiskFullQualificationWorkload
{
    public static async Task<int> RunAsync(string[] args)
    {
        var root = Path.GetFullPath(Argument(args, "--root"));
        var output = Path.GetFullPath(Argument(args, "--output"));
        var disk = new DriveInfo(root);
        if (disk.DriveFormat != "tmpfs" || disk.TotalSize > 64L * 1024 * 1024)
            throw new ArgumentException("Disk-full qualification requires a dedicated tmpfs of at most 64 MiB.");
        var engineRoot = Path.Combine(root, "engine");
        if (Directory.Exists(engineRoot)) throw new IOException("Use a fresh tmpfs directory.");
        Directory.CreateDirectory(output);
        var objects = new FileSystemBackupObjectStore(Path.Combine(output, "objects"));
        var segments = new TieredStorageSegmentStore(new(objects, Path.Combine(engineRoot, "cache"), "segments", 0));
        var payload = new string('x', 1024);
        var walFailure = 0;
        var compactionFailure = 0;
        var uncertainRequest = new StorageCommitRequest(EntityId.New(), SHA256.HashData("disk-full-unknown-write"u8));
        var uncertainPayload = new string('y', 1024 * 1024);
        var uncertainCommitted = false;
        var filler = Path.Combine(root, "filler");
        await using (var engine = new Engine(engineRoot, segmentStore: segments))
        {
            for (var number = 0; number < 100; number++)
                await engine.UpsertAsync(Key(number), JsonSerializer.SerializeToElement(new { number, payload }), DocumentPrecondition.Missing);
            await engine.CompactAsync();
            _ = Fill(filler);
            try
            {
                await engine.UpsertAsync(Key(100), JsonSerializer.SerializeToElement(new { payload = uncertainPayload }), DocumentPrecondition.Missing, uncertainRequest);
                throw new InvalidOperationException("A write unexpectedly succeeded on the full filesystem.");
            }
            catch (Exception exception) when (DiskFullCode(exception) is not null) { walFailure = DiskFullCode(exception)!.Value; }
            finally { File.Delete(filler); }
        }
        await using (var engine = new Engine(engineRoot, segmentStore: segments))
        {
            uncertainCommitted = await VerifyAsync(engine, payload, uncertainRequest, uncertainPayload);
            var manifest = await File.ReadAllBytesAsync(Path.Combine(engineRoot, "MANIFEST"));
            _ = Fill(filler);
            try
            {
                await engine.CompactAsync();
                throw new InvalidOperationException("Compaction unexpectedly succeeded on the full filesystem.");
            }
            catch (Exception exception) when (DiskFullCode(exception) is not null) { compactionFailure = DiskFullCode(exception)!.Value; }
            finally { File.Delete(filler); }
            var afterFailure = await File.ReadAllBytesAsync(Path.Combine(engineRoot, "MANIFEST"));
            if (!manifest.AsSpan().SequenceEqual(afterFailure))
                throw new InvalidDataException("Failed compaction changed the authoritative manifest.");
            await engine.CompactAsync();
        }
        await using (var reopened = new Engine(engineRoot, segmentStore: segments))
        {
            if (await VerifyAsync(reopened, payload, uncertainRequest, uncertainPayload) != uncertainCommitted)
                throw new InvalidDataException("The reconciled request outcome changed after compaction.");
        }

        await File.WriteAllTextAsync(Path.Combine(output, "disk-full-summary.md"), $"""
# Filesystem-full qualification

| Check | Result |
| --- | --- |
| Filesystem | Dedicated tmpfs, {disk.TotalSize:N0} bytes |
| Seed | 100 synced records, compacted to an external filesystem object store |
| Full-filesystem WAL append | No acknowledgement; unknown outcome, underlying HRESULT 0x{walFailure:X8} |
| Reopen after freeing filler | 100 acknowledged bodies/versions exact |
| Unacknowledged request reconciliation | {(uncertainCommitted ? "Committed, exact body and committed receipt verified" : "Absent, commit status unknown")} |
| Full-filesystem segment staging | Rejected, HRESULT 0x{compactionFailure:X8} |
| Failed compaction manifest | Byte-for-byte unchanged |
| Retry after freeing space | Compaction and second exact reopen passed |

This is real Linux ENOSPC evidence for local WAL and segment staging. Object
storage used the filesystem fixture outside the quota; this is not a RustFS
disk-full, device power-loss, or larger-than-memory performance qualification.
""");
        return 0;
    }

    private static async Task<bool> VerifyAsync(Engine engine, string payload, StorageCommitRequest request, string uncertainPayload)
    {
        for (var number = 0; number < 100; number++)
        {
            var document = await engine.ReadAsync(Key(number));
            if (document?.Version != 1 || document.Body.GetProperty("number").GetInt32() != number
                || document.Body.GetProperty("payload").GetString() != payload)
                throw new InvalidDataException($"Committed record {number} did not survive ENOSPC.");
        }
        var uncertain = await engine.ReadAsync(Key(100));
        var status = await engine.GetCommitStatusAsync(request);
        if (uncertain is null)
        {
            if (status.Status != StorageCommitStatus.Unknown)
                throw new InvalidDataException("An absent uncertain write has a committed receipt.");
            return false;
        }
        if (uncertain.Version != 1 || uncertain.Body.GetProperty("payload").GetString() != uncertainPayload
            || status.Status != StorageCommitStatus.Committed
            || status.Result?.Writes[0].Document?.Body.GetProperty("payload").GetString() != uncertainPayload)
            throw new InvalidDataException("The uncertain append was not recovered as one complete transaction.");
        return true;
    }

    private static int Fill(string path)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0,
        });
        var bytes = new byte[64 * 1024];
        try
        {
            for (var index = 0; index < 1024; index++) stream.Write(bytes);
        }
        catch (IOException exception) when (DiskFullCode(exception) is not null) { return exception.HResult; }
        throw new IOException("The bounded filesystem did not report ENOSPC within 64 MiB.");
    }

    private static int? DiskFullCode(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current is IOException && (current.HResult & 0xffff) is 28 or 112) return current.HResult;
        return null;
    }
    private static DocumentKey Key(int number) => new(new TenantId("disk-full"), new CollectionId("docs"), $"doc-{number:D4}");
    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1]
            : throw new ArgumentException($"{name} is required.");
    }
}
