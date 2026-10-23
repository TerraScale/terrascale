using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TerraBase.Features.Backup;

namespace TerraBase.E2E.PitrLab;

/// <summary>
/// Checks every backup object of one database: each is opened with the product's own reader (BackupObject.ReadAsync,
/// AES-256-GCM per chunk under the data key it names, unwrapped by the master key), each segment's change records are
/// counted, and each generation's chain is walked as BackupReader walks it. Only the record walk is reimplemented here:
/// ChangeLog's readers are private.
/// </summary>
internal static class PitrAudit
{
    private const int Parallelism = 8;
    private const string MissingKey = "the object names a data key that is not under keys_prefix";

    public static async Task<AuditReport> RunAsync(IPitrSource source, string databasePrefix, string keysPrefix, byte[] masterKey, string scratch, CancellationToken cancellationToken)
    {
        var file = databasePrefix.TrimEnd('/');
        var prefix = file + "/";
        keysPrefix = keysPrefix.TrimEnd('/') + "/";
        var problems = new List<AuditProblem>();
        var notes = new List<string>();
        Directory.CreateDirectory(scratch);

        var master = new MasterKey(masterKey);
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var keyIds = new List<string>();
        await foreach (var item in source.ListAsync(keysPrefix, cancellationToken))
        {
            var id = item.Key[keysPrefix.Length..];
            keyIds.Add(id);
            string? wrapped = null;
            await source.ReadAsync(item.Key, async (content, token) =>
            {
                using var reader = new StreamReader(content, Encoding.ASCII);
                wrapped = await reader.ReadToEndAsync(token);
            }, cancellationToken);
            try
            {
                keys[id] = master.Unwrap(wrapped ?? "");
            }
            catch (InvalidDataException error)
            {
                problems.Add(new("data_key_unwrap", item.Key, error.Message));
            }
        }

        var snapshots = new List<SnapshotKey>();
        var segments = new List<(Ulid Generation, SegmentKey Segment)>();
        var unparseable = new List<string>();
        await foreach (var item in source.ListAsync(prefix, cancellationToken))
        {
            var rest = item.Key[prefix.Length..];
            var parts = rest.Split('/');
            if (parts is ["snapshots", _] && BackupLayout.ParseSnapshot(item.Key) is { } snapshot
                && BackupLayout.Snapshot(file, snapshot.At, snapshot.Generation, snapshot.Position) == item.Key)
            {
                snapshots.Add(snapshot);
            }
            else if (parts is ["wal", var generation, _] && Ulid.TryParse(generation, out var id) && BackupLayout.ParseSegment(item.Key) is { } segment
                && BackupLayout.Segment(file, id, segment.Start, segment.At) == item.Key)
            {
                segments.Add((id, segment));
            }
            else
            {
                unparseable.Add(item.Key);
                problems.Add(new("unparseable_key", item.Key, "not a snapshot or segment key of layout 1; a restore skips it"));
            }
        }

        var opened = new ConcurrentDictionary<string, Opened>(StringComparer.Ordinal);
        var work = snapshots.Select(snapshot => (snapshot.Key, Segment: false)).Concat(segments.Select(item => (item.Segment.Key, Segment: true)));
        await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
            async (item, token) => opened[item.Key] = await OpenAsync(source, item.Key, item.Segment, keys, scratch, token));

        foreach (var (key, result) in opened.OrderBy(pair => pair.Key, S3KeyOrder.Instance))
        {
            if (result.Error is not null)
            {
                problems.Add(new(result.Error == MissingKey ? "missing_data_key" : "unauthenticated", key, result.Error));
            }

            if (result.Trailing > 0)
            {
                problems.Add(new("trailing_bytes", key, $"{result.Trailing} bytes after the last chunk; a restore ignores them"));
            }

            if (result.Records is { Error: { } recordsError })
            {
                problems.Add(new("malformed_log", key, recordsError));
            }
        }

        var generations = new List<GenerationReport>();
        var generationIds = snapshots.Select(snapshot => snapshot.Generation).Concat(segments.Select(item => item.Generation)).Distinct().Order().ToList();
        foreach (var generation in generationIds)
        {
            generations.Add(Generation(generation, [.. snapshots.Where(snapshot => snapshot.Generation == generation)],
                [.. segments.Where(item => item.Generation == generation).Select(item => item.Segment)], opened, problems, notes));
        }

        var referenced = opened.Values.Where(result => result.KeyId is not null).GroupBy(result => result.KeyId!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        List<DataKeyReport> dataKeys = [.. keyIds.Select(id => new DataKeyReport(id, keys.ContainsKey(id), referenced.GetValueOrDefault(id)))];
        foreach (var key in keys.Values)
        {
            CryptographicOperations.ZeroMemory(key);
        }

        var allSegments = generations.SelectMany(generation => generation.Segments).ToList();
        var totals = new AuditTotals(generations.Count, allSegments.Count, snapshots.Count, opened.Values.Sum(result => result.Size),
            allSegments.Sum(segment => (segment.End ?? segment.Start) - segment.Start), allSegments.Sum(segment => segment.Upserts),
            allSegments.Sum(segment => segment.Deletes), allSegments.Sum(segment => segment.Commits), allSegments.Sum(segment => segment.Creates),
            allSegments.Sum(segment => segment.Drops));
        if (snapshots.Count == 0)
        {
            problems.Add(new("no_snapshot", null, "the database prefix holds no snapshot: nothing restores"));
        }

        return new(problems.Count == 0 ? "ok" : "fail", prefix, keysPrefix, problems, notes, totals, dataKeys, unparseable, generations);
    }

    // One generation: its chain in key order, every snapshot on a boundary, a restore walked from each snapshot, and time
    // order against log position.
    private static GenerationReport Generation(
        Ulid generation, List<SnapshotKey> snapshotKeys, List<SegmentKey> segmentKeys, ConcurrentDictionary<string, Opened> opened, List<AuditProblem> problems,
        List<string> notes)
    {
        List<SegmentReport> segments = [.. segmentKeys.OrderBy(segment => segment.Key, S3KeyOrder.Instance).Select(segment =>
        {
            var result = opened[segment.Key];
            var records = result.Records ?? default;
            return new SegmentReport(segment.Key, segment.Start, result.Error is null ? segment.Start + result.Plain : null, segment.At.ToUnixTimeMilliseconds(),
                result.Size, result.Sha256, result.Error is null, result.KeyId, result.Error, result.Trailing, records.Records, records.Upserts, records.Deletes,
                records.Commits, records.Creates, records.Drops, records.EndsOnCommit, records.Error);
        })];
        var ordered = snapshotKeys.OrderBy(snapshot => snapshot.Position).ThenBy(snapshot => snapshot.At).ToList();
        if (ordered.Count == 0)
        {
            problems.Add(new("no_snapshot", segments.FirstOrDefault()?.Key, $"generation {generation} has segments but no snapshot to replay them on"));
        }

        long? expected = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var segment = segments[i];
            if (expected is { } end && segment.Start != end)
            {
                var kind = segment.Start > end ? "gap" : segments[i - 1].Start == segment.Start ? "duplicate_start" : "overlap";
                problems.Add(new(kind, segment.Key, $"starts at {segment.Start}; the segment before it ends at {end}"));
            }

            if (i > 0 && segment.AtMs < segments[i - 1].AtMs)
            {
                problems.Add(new("time_order", segment.Key, "read earlier than the segment before it; a restore stops at the first segment after its time"));
            }

            if (segment.Authenticated && segment.RecordsError is null && !segment.EndsOnCommit)
            {
                problems.Add(new("open_transaction", segment.Key, "ends inside a transaction; a restore to its time fails"));
            }

            expected = segment.End;
        }

        if (ordered.Count > 0 && segments.Count > 0)
        {
            var first = ordered[0].Position;
            if (segments[0].Start > first)
            {
                problems.Add(new("gap", segments[0].Key, $"the first segment starts at {segments[0].Start}, after the earliest snapshot's position {first}"));
            }
            else if (segments[0].Start < first)
            {
                notes.Add($"generation {generation}: {segments.Count(segment => segment.Start < first)} segments before the earliest snapshot (retention removes them)");
            }
        }

        var boundaries = segments.Select(segment => segment.Start).Concat(segments.Where(segment => segment.End is not null).Select(segment => segment.End!.Value)).ToHashSet();
        var reports = new List<SnapshotReport>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var snapshot = ordered[i];
            var result = opened[snapshot.Key];
            var at = snapshot.At.ToUnixTimeMilliseconds();
            if (i > 0 && at < reports[^1].AtMs)
            {
                problems.Add(new("time_order", snapshot.Key, "taken earlier than a snapshot at a lower position"));
            }

            if (segments.Count > 0 && snapshot.Position >= segments[0].Start && !boundaries.Contains(snapshot.Position))
            {
                problems.Add(new("snapshot_not_on_boundary", snapshot.Key, $"position {snapshot.Position} is not where a segment starts or ends"));
            }

            foreach (var segment in segments)
            {
                if ((segment.End <= snapshot.Position && segment.AtMs > at) || (segment.Start >= snapshot.Position && segment.Start != segment.End && segment.AtMs < at))
                {
                    problems.Add(new("time_order", segment.Key, $"its time disagrees with snapshot {snapshot.Key} at position {snapshot.Position}"));
                }
            }

            // BackupReader: from the snapshot's position, each segment in key order must start where the last one ended.
            var position = snapshot.Position;
            string? stop = null;
            foreach (var segment in segments.Where(segment => segment.Start >= snapshot.Position))
            {
                if (segment.Start != position || segment.End is not { } end)
                {
                    stop = segment.Key;
                    break;
                }

                position = end;
            }

            if (stop is not null)
            {
                problems.Add(new("restore_break", snapshot.Key, $"a restore from this snapshot stops at {stop} (log position {position})"));
            }

            reports.Add(new(snapshot.Key, at, snapshot.Position, result.Size, result.Sha256, result.Error is null, result.KeyId, result.Error, position, stop));
        }

        return new(generation.ToString(), ordered.FirstOrDefault()?.Position ?? segments.FirstOrDefault()?.Start, segments.LastOrDefault()?.End ?? ordered.LastOrDefault()?.Position,
            reports, segments);
    }

    private static async Task<Opened> OpenAsync(IPitrSource source, string key, bool segment, Dictionary<string, byte[]> keys, string scratch, CancellationToken cancellationToken)
    {
        var result = new Opened();
        var found = await source.ReadAsync(key, async (content, token) =>
        {
            using var sealedObject = new HashingStream(content);
            Stream plain = segment
                ? new FileStream(Path.Combine(scratch, Ulid.NewUlid() + ".log"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16,
                    FileOptions.Asynchronous | FileOptions.DeleteOnClose)
                : Stream.Null;
            await using (plain)
            {
                try
                {
                    await BackupObject.ReadAsync(sealedObject, plain, key, id =>
                    {
                        result.KeyId = id;
                        return keys.TryGetValue(id, out var dataKey) ? Task.FromResult(dataKey) : Task.FromException<byte[]>(new InvalidDataException(MissingKey));
                    }, token);
                    if (segment)
                    {
                        result.Plain = plain.Length;
                        plain.Position = 0;
                        result.Records = ChangeRecords.Count(plain);
                    }
                }
                catch (InvalidDataException error)
                {
                    result.Error = error.Message;
                }
            }

            // The rest of the stored object, if any: it counts in its size and hash.
            var buffer = new byte[1 << 16];
            var before = sealedObject.Count;
            while (await sealedObject.ReadAsync(buffer, token) > 0)
            {
            }

            result.Trailing = result.Error is null ? sealedObject.Count - before : 0;
            result.Size = sealedObject.Count;
            result.Sha256 = sealedObject.Hex();
        }, cancellationToken);
        if (!found)
        {
            result.Error = "the object disappeared during the audit";
        }

        return result;
    }

    private sealed class Opened
    {
        public string? KeyId { get; set; }
        public string? Error { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
        public long Plain { get; set; }
        public long Trailing { get; set; }
        public RecordCounts? Records { get; set; }
    }
}

internal readonly record struct RecordCounts(long Records, long Upserts, long Deletes, long Commits, long Creates, long Drops, bool EndsOnCommit, string? Error);

/// <summary>
/// Walks a change log segment, format 1 (TerraBase.Engine.Turso.ChangeLog): kind byte, then upsert (table, rowid, record),
/// delete (table, rowid), commit, create (SQL), drop (type, name), timed commit (two 8-byte times) or read mark (one 8-byte
/// time, only between transactions). Strings and records are LEB128-length-prefixed, rowids 8 bytes. Names are checked as
/// strict UTF-8 like the replay; record bodies are skipped. A timed commit counts as a commit.
/// </summary>
internal static class ChangeRecords
{
    private const int MaxLength = 1 << 30;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static RecordCounts Count(Stream log)
    {
        long upserts = 0, deletes = 0, commits = 0, creates = 0, drops = 0;
        var open = false;
        try
        {
            int kind;
            while ((kind = log.ReadByte()) >= 0)
            {
                switch (kind)
                {
                    case 1:
                        Text(log);
                        Skip(log, 8);
                        Skip(log, Length(log));
                        upserts++;
                        break;
                    case 2:
                        Text(log);
                        Skip(log, 8);
                        deletes++;
                        break;
                    case 3:
                        commits++;
                        open = false;
                        continue;
                    case 4:
                        Text(log);
                        creates++;
                        break;
                    case 5:
                        Text(log);
                        Text(log);
                        drops++;
                        break;
                    case 6:
                        Skip(log, 16);
                        commits++;
                        open = false;
                        continue;
                    case 7:
                        Skip(log, 8);
                        if (open)
                        {
                            throw new InvalidDataException($"a read mark inside a transaction at byte {log.Position - 9}");
                        }

                        continue;
                    default:
                        throw new InvalidDataException($"unknown change record kind {kind} at byte {log.Position - 1}");
                }

                open = true;
            }

            return new(upserts + deletes + commits + creates + drops, upserts, deletes, commits, creates, drops, !open, null);
        }
        catch (Exception error) when (error is InvalidDataException or DecoderFallbackException)
        {
            return new(upserts + deletes + commits + creates + drops, upserts, deletes, commits, creates, drops, !open, error.Message);
        }
    }

    private static void Text(Stream log)
    {
        var bytes = new byte[Length(log)];
        if (log.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) < bytes.Length)
        {
            throw new InvalidDataException("a change record is truncated");
        }

        _ = StrictUtf8.GetString(bytes);
    }

    private static void Skip(Stream log, long bytes)
    {
        if (log.Length - log.Position < bytes)
        {
            throw new InvalidDataException("a change record is truncated");
        }

        log.Seek(bytes, SeekOrigin.Current);
    }

    private static int Length(Stream log)
    {
        long value = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var next = log.ReadByte();
            if (next < 0)
            {
                throw new InvalidDataException("a change record is truncated");
            }

            value |= (long)(next & 0x7f) << shift;
            if ((next & 0x80) == 0)
            {
                return value <= MaxLength ? (int)value : throw new InvalidDataException("a change record length is out of range");
            }
        }

        throw new InvalidDataException("a change record length is malformed");
    }
}

/// <summary>Reads through to another stream, counting and hashing (SHA-256) every byte read.</summary>
internal sealed class HashingStream(Stream inner) : Stream
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public long Count { get; private set; }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => Count; set => throw new NotSupportedException(); }

    public string Hex() => Convert.ToHexStringLower(_hash.GetCurrentHash());

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);
        Append(buffer[..read]);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        Append(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        _hash.AppendData(bytes);
        Count += bytes.Length;
    }
}
