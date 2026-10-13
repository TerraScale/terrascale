using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using TerraBase.Features.Backup;

namespace TerraBase.E2E.PitrLab;

/// <summary>
/// Makes an isolated mutated copy of a root for the fault matrix. The copy is a hard-linked tree (<c>cp -al</c>, a full
/// copy where links fail); the mutated object is always written to a new file and renamed over its link, so the source
/// root never changes. Every other object of the copy keeps its source bytes.
/// </summary>
internal static class PitrMutate
{
    // BackupObject format 1: magic, 26-character key id, 32-byte salt; then per chunk two big-endian words and the ciphertext.
    private const int HeaderBytes = 62;

    public static async Task<MutationManifest> RunAsync(string from, string to, PitrMutation mutation, CancellationToken cancellationToken)
    {
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        if (Directory.Exists(to) || File.Exists(to))
        {
            throw new InvalidDataException("to must not exist");
        }

        var source = DiskBucket.Open(from, create: false);
        var key = mutation.Key;
        if (!key.StartsWith("v1/", StringComparison.Ordinal))
        {
            // Catalog anchors (catalog-anchor/, R10a) and every other key outside v1/ are copied byte-exact, never mutated.
            throw new InvalidDataException("mutation.key must be under v1/");
        }

        var segment = Segment(key);
        var snapshot = BackupLayout.ParseSnapshot(key) is not null && key.Contains("/snapshots/", StringComparison.Ordinal);
        var exists = source.Contains(key);
        var valid = mutation.Kind switch
        {
            "delete_segment" or "corrupt_segment" or "truncate_segment" or "duplicate_segment" => exists && segment is not null,
            "delete_snapshot" or "corrupt_snapshot" => exists && snapshot,
            "stray_key" => !exists,
            _ => throw new InvalidDataException("mutation.kind is not a known mutation"),
        };
        if (!valid)
        {
            throw new InvalidDataException(mutation.Kind == "stray_key"
                ? "mutation.key must not exist for stray_key"
                : $"mutation.key must be an existing {(mutation.Kind.EndsWith("_segment", StringComparison.Ordinal) ? "segment" : "snapshot")} of the root");
        }

        Directory.CreateDirectory(to);
        var linked = await LinkAsync(source.Objects, Path.Combine(to, "objects"), cancellationToken);
        var copy = DiskBucket.Open(to, create: false);
        if (copy.Count != source.Count)
        {
            throw new InvalidDataException("the copy does not hold every object of the source");
        }

        var before = exists ? await PitrSources.FingerprintAsync(source.PathOf(key), cancellationToken) : null;
        string? newKey = null;
        long? offset = null;
        string proves;
        switch (mutation.Kind)
        {
            case "delete_segment":
            case "delete_snapshot":
                copy.Delete(key);
                proves = mutation.Kind == "delete_segment"
                    ? "A missing segment: the restore refuses to replay across the hole (BackupReader: segment.Start must equal the expected position); " +
                      "restores to times before the segment still succeed, audit reports a gap."
                    : "A missing snapshot: restores fall back to an earlier snapshot of the generation, or have no base at all; audit reports no_snapshot or a " +
                      "shorter chain.";
                break;
            case "corrupt_segment":
            case "corrupt_snapshot":
                offset = await ReplaceAsync(copy, source.PathOf(key), key, Flip, cancellationToken);
                proves = "One flipped ciphertext byte: AES-GCM authentication fails, so the restore stops with an error instead of applying altered data.";
                break;
            case "truncate_segment":
                await ReplaceAsync(copy, source.PathOf(key), key, file =>
                {
                    file.SetLength(file.Length / 2);
                    return null;
                }, cancellationToken);
                proves = "A torn upload (half the object): the last-chunk flag and the chunk tags catch it; the restore fails instead of replaying a prefix.";
                break;
            case "duplicate_segment":
                var (generation, start, at) = segment!.Value;
                do
                {
                    at = at.AddMilliseconds(1);
                    newKey = BackupLayout.Segment(key[..key.LastIndexOf("/wal/", StringComparison.Ordinal)], generation, start, at);
                }
                while (copy.Contains(newKey));

                var duplicate = copy.NewTemp();
                File.Copy(source.PathOf(key), duplicate);
                copy.Commit(duplicate, newKey);
                // Re-sealing under the new key needs the data key; a byte copy is what a misbehaving writer or tool could produce.
                proves = "A second object at an existing start position, later in time: its bytes are the original's, which fail authentication under " +
                    "another key (HKDF binds the object key), and its start breaks contiguity; a restore to its time or later fails, earlier restores do not read it.";
                break;
            default:
                var stray = copy.NewTemp();
                await File.WriteAllTextAsync(stray, "stray object written by PitrLab mutate\n", Encoding.ASCII, cancellationToken);
                copy.Commit(stray, key);
                proves = "An unparseable key under the database prefix: a restore skips it (BackupReader ignores keys it cannot parse); audit lists it.";
                break;
        }

        var changed = newKey ?? key;
        var after = copy.Contains(changed) ? await PitrSources.FingerprintAsync(copy.PathOf(changed), cancellationToken) : null;
        if (exists && await PitrSources.FingerprintAsync(source.PathOf(key), cancellationToken) != before)
        {
            throw new InvalidOperationException("the source object changed while mutating its copy");
        }

        return new(mutation.Kind, key, newKey, from, to, linked, copy.Count, before, after, offset, proves);
    }

    private static (Ulid Generation, long Start, DateTimeOffset At)? Segment(string key)
    {
        var parts = key.Split('/');
        return parts.Length >= 3 && parts[^3] == "wal" && Ulid.TryParse(parts[^2], out var generation) && BackupLayout.ParseSegment(key) is { } segment
            ? (generation, segment.Start, segment.At)
            : null;
    }

    // Copies the object's bytes to a new file, changes that file, and renames it over the copy's link.
    private static async Task<long?> ReplaceAsync(DiskBucket copy, string original, string key, Func<FileStream, long?> change, CancellationToken cancellationToken)
    {
        var temp = copy.NewTemp();
        File.Copy(original, temp);
        long? offset;
        await using (var file = new FileStream(temp, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            offset = change(file);
            await file.FlushAsync(cancellationToken);
            file.Flush(flushToDisk: true);
        }

        copy.Commit(temp, key);
        return offset;
    }

    // Flips a byte in the middle of chunk 0's ciphertext (its tag when the chunk is empty).
    private static long? Flip(FileStream file)
    {
        var words = new byte[8];
        file.Position = HeaderBytes;
        file.ReadExactly(words);
        var stored = BinaryPrimitives.ReadUInt32BigEndian(words.AsSpan(4));
        var offset = HeaderBytes + 8 + (long)(stored / 2);
        if (offset >= file.Length)
        {
            throw new InvalidDataException("the object is too short to hold a chunk");
        }

        file.Position = offset;
        var value = (byte)file.ReadByte();
        file.Position = offset;
        file.WriteByte((byte)(value ^ 0x01));
        return offset;
    }

    // A hard-linked copy of the objects directory; a plain copy where hard links are not possible (another file system).
    private static async Task<bool> LinkAsync(string objects, string destination, CancellationToken cancellationToken)
    {
        using var link = Process.Start(new ProcessStartInfo("cp", ["-a", "-l", objects, destination]) { RedirectStandardError = true })
            ?? throw new InvalidOperationException("cp did not start");
        var errors = await link.StandardError.ReadToEndAsync(cancellationToken);
        await link.WaitForExitAsync(cancellationToken);
        if (link.ExitCode == 0)
        {
            return true;
        }

        Console.WriteLine($"mutate: hard links failed ({errors.Trim()}); copying instead");
        if (Directory.Exists(destination))
        {
            throw new InvalidOperationException("a partial hard-linked copy exists; remove it and run again");
        }

        foreach (var path in Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(objects, path));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }

        return false;
    }
}
