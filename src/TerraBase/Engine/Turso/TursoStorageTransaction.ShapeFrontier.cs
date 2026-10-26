using System.Globalization;
using System.Text;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Memory;
using TerraBase.Features.Replication;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <summary>
/// The remote durable frontier of shape reads (FR-25, shape-reads.md section 10): the highest feed <c>seq</c> such that every
/// commit at or below it is held by a machine of another region. An own commit is held once an eligible live
/// acknowledgement range of the current incarnation covers its commit row; a joined commit is held when every origin its
/// state rows carry is one of the walk's held origins (exact replica ids of machines of another placed region, from live
/// links: that origin committed it first and holds it). Origin-0 rows and deletes follow the commit's origin rows; a repair
/// (only those) and a join of any other origin prove nothing, and stop it. Remote eligibility never comes from replica id
/// region bits (all 0 until TS-643). It is a contiguous prefix in commit order, built only from
/// this incarnation's capture: it starts at 0 and stops for good at a feed seq capture does not hold (a copy's or a
/// restore's rows, or capture trimmed before it was scanned).
/// </summary>
/// <remarks>
/// Kept in a sentinel <c>_tb_shape_chunk</c> row under <see cref="ShapeFrontierHandle"/> (offset 0, sealed_at 0): end_offset
/// is the frontier and the body is <c>2 incarnation scanned from:to ...</c>: the last commit change id it covers, then the
/// eligible own acknowledgement ranges (From, To] of the incarnation that may still prove a commit after it, as their
/// disjoint union (<see cref="RemoteAckWaiter.Keep"/>: at most <see cref="RemoteAckWaiter.MaxRanges"/>, the lowest dropped
/// past that). A walk that stops at a commit not yet held (an origin not held yet, a pass's budget, memory) proves the own
/// commits after it with these later, without a new acknowledgement. A canonical <c>h1-</c> handle never starts with
/// <c>~</c>, and a row of another incarnation, or a body of another format, counts as absent.
/// </remarks>
internal sealed partial class TursoStorageTransaction
{
    internal const string ShapeFrontierHandle = "~frontier";

    // The largest body written: the header, then 16 ranges of two 19-digit longs (" from:to", 40 bytes each), within this.
    private const long FrontierBodyBytes = 1024;

    // Captured rows read per page while advancing, and pages per pass; the next pass goes on.
    private const int FrontierRows = 512;
    private const int FrontierPages = 64;

    /// <summary>
    /// Advances the frontier over held commits, own ones held by <paramref name="proof"/>: an eligible live acknowledgement
    /// range (From, To] of this file's incarnation, persisted with the earlier ones, and joined ones by <paramref name="held"/>
    /// origins. Uncaptured. True when it moved.
    /// </summary>
    internal async ValueTask<bool> AdvanceShapeFrontierAsync(ReplicaId self, (long From, long To)? proof, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken)
    {
        if (store.IsSystem || store.Rewriting)
        {
            return false;
        }

        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() => AdvanceShapeFrontier(self, proof, held, cancellationToken), cancellationToken, mutation: true).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public ValueTask<bool> AdvanceShapeFrontierAsync(IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken) =>
        store.Member is { } member ? AdvanceShapeFrontierAsync(member.Id, null, held, cancellationToken) : ValueTask.FromResult(false);

    /// <summary>The frontier: with <paramref name="remote"/> the persisted remote durable one (0 when none), else the committed feed head.</summary>
    public ValueTask<long> ReadShapeFrontierAsync(bool remote, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeFrontierOrHead(remote), cancellationToken);

    /// <summary>See <see cref="ShapeFrontierHold"/>.</summary>
    internal ValueTask<long?> ShapeFrontierHoldAsync(long now, CancellationToken cancellationToken) =>
        RunAsync(() => !store.RequiresRemoteProof || store.IsSystem ? null : ShapeFrontierHold(session.Connection, transaction, now), cancellationToken);

    // Worker-only.
    private long ShapeFrontierOrHead(bool remote) => remote ? ShapeFrontier()?.Frontier ?? 0 : ShapeFeedHead();

    // Worker-only.
    private bool AdvanceShapeFrontier(ReplicaId self, (long From, long To)? proof, IReadOnlySet<ReplicaId> held, CancellationToken cancellationToken)
    {
        if (Command("SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';").ExecuteScalar() is null)
        {
            return false;
        }

        var (frontier, scanned, ranges) = ShapeFrontier() ?? (0, 0, []);
        var start = frontier;
        var startScanned = scanned;
        // Retention deletes a prefix: the row after the scanned commit still being here means nothing past it was trimmed.
        if (Command("SELECT min(change_id) FROM turso_cdc;").ExecuteScalar() is long oldest && oldest > scanned + 1)
        {
            return false;
        }

        // A range at or below the scanned commit proves nothing more: dropped first, before the bound drops a live one.
        _ = ranges.RemoveAll(range => range.To <= scanned);
        var kept = RangesText(ranges);
        if (proof is { } acked && acked.From >= 0 && acked.From < acked.To)
        {
            RemoteAckWaiter.Keep(ranges, acked.From, acked.To);
        }

        try
        {
            (frontier, scanned) = AdvanceCaptured(self, ranges, held, frontier, scanned, cancellationToken);
        }
        catch (InvalidDataException)
        {
            // A damaged change log row holds the frontier where it is, as it holds the stream (section 7).
        }

        _ = ranges.RemoveAll(range => range.To <= scanned);
        var text = RangesText(ranges);
        // An acknowledgement persists even when the walk did not move, for a later pass to prove with.
        if (scanned == startScanned && text == kept)
        {
            return false;
        }

        WriteShapeFrontier(frontier, scanned, text);
        return frontier > start;
    }

    /// <summary>
    /// Raises the frontier to <paramref name="seq"/>, the feed head read in the same snapshot as <paramref name="changeId"/>
    /// (its newest captured change id), once a machine of another placed region answered bucket digests equal to that
    /// snapshot's: it holds every value at it, including what the walk cannot prove (a repair, an origin of a past epoch).
    /// Uncaptured. True when it moved.
    /// </summary>
    internal async ValueTask<bool> RebaseShapeFrontierAsync(long changeId, long seq, CancellationToken cancellationToken)
    {
        if (store.IsSystem || store.Rewriting || changeId <= 0)
        {
            return false;
        }

        await SuspendCaptureAsync(cancellationToken).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var (frontier, scanned, ranges) = ShapeFrontier() ?? (0, 0, []);
            // Only forward, and only where the walk can go on: the rows past the snapshot are still captured.
            if (scanned >= changeId || seq <= frontier || seq > ShapeFeedHead()
                || (Command("SELECT min(change_id) FROM turso_cdc;").ExecuteScalar() is long oldest && oldest > changeId + 1))
            {
                return false;
            }

            _ = ranges.RemoveAll(range => range.To <= changeId);
            WriteShapeFrontier(seq, changeId, RangesText(ranges));
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    // Worker-only: persists the sentinel row under this file's incarnation.
    private void WriteShapeFrontier(long frontier, long scanned, string ranges)
    {
        var incarnation = Command(TursoStatements.Incarnation).ExecuteScalar() as string ?? throw new InvalidDataException("database incarnation is missing");
        var body = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"2 {incarnation} {scanned}{ranges}"));
        var update = Command("UPDATE _tb_shape_chunk SET end_offset = $frontier, body = $body WHERE handle = $handle AND offset = 0;");
        update.Parameters.AddWithValue("$handle", ShapeFrontierHandle);
        update.Parameters.AddWithValue("$frontier", frontier);
        update.Parameters.AddWithValue("$body", body);
        if (update.ExecuteNonQuery() == 0)
        {
            var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($handle, 0, $frontier, $body, 0);");
            insert.Parameters.AddWithValue("$handle", ShapeFrontierHandle);
            insert.Parameters.AddWithValue("$frontier", frontier);
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
        }
    }

    // The ranges in body form, ordered: " from:to" each.
    private static string RangesText(List<(long From, long To)> ranges) =>
        string.Concat(ranges.OrderBy(range => range.From).Select(range => string.Create(CultureInfo.InvariantCulture, $" {range.From}:{range.To}")));

    // Worker-only. Walks captured transactions after `scanned` in commit order and stops before the first one not held. The
    // page budget ends a pass only at a commit boundary: a pass that stopped inside one transaction would restart there for
    // good. A row's after-image is admitted before it is read.
    private (long Frontier, long Scanned) AdvanceCaptured(ReplicaId self, List<(long From, long To)> proof, IReadOnlySet<ReplicaId> held, long frontier, long scanned, CancellationToken cancellationToken)
    {
        var commit = new FrontierCommit();
        var after = scanned;
        var open = false;
        for (var page = 0; page < FrontierPages || open; page++)
        {
            var command = Command("SELECT change_id, change_type, table_name, after, octet_length(after), after IS NULL FROM turso_cdc WHERE change_id > $after ORDER BY change_id LIMIT $limit;");
            command.Parameters.AddWithValue("$after", after);
            command.Parameters.AddWithValue("$limit", FrontierRows);
            var rows = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows++;
                var id = reader.GetInt64(0);
                after = id;
                var type = reader.GetInt64(1);
                // The driver's IsDBNull on a blob copies it: test nullness in SQL before admitting its image.
                var missing = reader.GetInt64(5) != 0;
                // The tailer's checks (ReadChangeGroups): a damaged type could otherwise split or merge transactions.
                if ((type == 2 && (!reader.IsDBNull(2) || !missing)) || (type == -1 && !missing))
                {
                    throw new InvalidDataException($"a captured change of type {type} has the shape of another type");
                }

                if (type is not (-1 or 0 or 1 or 2))
                {
                    throw new InvalidDataException($"a captured change has change type {type}, which no capture writes");
                }

                if (type == 2)
                {
                    // Origin-0 rows and deletes (repairs, and the projections of collection clears and drops) follow the
                    // commit's origin rows; a commit with only those is a repair, which proves nothing.
                    var proven = (!commit.Own || proof.Exists(range => range.From < id && id <= range.To))
                        && !commit.Unknown && !commit.Gap && (commit.Own || commit.Joined || !commit.Derived);
                    // A feed seq this incarnation's capture does not hold has no proof either.
                    if (!proven || (commit.FirstSeq is { } first && (first != frontier + 1 || (!commit.Own && !commit.Joined))))
                    {
                        return (frontier, scanned);
                    }

                    frontier = commit.LastSeq ?? frontier;
                    scanned = id;
                    commit = new FrontierCommit();
                    open = false;
                    continue;
                }

                open = true;
                var table = reader.IsDBNull(2) ? throw new InvalidDataException("a captured change has no table") : reader.GetString(2);
                if (type != -1 && missing && !ChangeLog.EngineTable(table) && table is not ("_tb_feed" or "_tb_feed_floor"))
                {
                    throw new InvalidDataException("a captured insert or update has no after-image");
                }

                if (type == -1 || (table != "_tb_state" && table != "_tb_feed"))
                {
                    commit.Derived |= table == "_tb_state";
                    continue;
                }

                if (table == "_tb_feed" && (type != 1 || missing))
                {
                    continue;
                }

                // The image and numeric proof fields; unused text is validated without materializing it.
                using var admission = store.SchemaMemory?.TryReserve(MemoryLane.Bulk, checked(reader.GetInt64(4) + 4096), store.Tenant);
                if (store.SchemaMemory is not null && admission is null)
                {
                    // Busy: the walk goes on from here on a later pass.
                    return (frontier, scanned);
                }

                if (table == "_tb_state")
                {
                    // collection, id, bucket, state, stamp, origin, live, h1, h2
                    if (SqliteRecord.Decode(TursoSession.Bytes(reader, 3), 6, materializeContent: false) is not [string, string, _, string, long, long origin])
                    {
                        throw new InvalidDataException("a captured _tb_state row does not have the state table's columns");
                    }

                    var node = new ReplicaId(unchecked((ulong)origin));
                    if (origin == 0)
                    {
                        commit.Derived = true;
                    }
                    else if (node.Region == self.Region && node.Member == self.Member)
                    {
                        // The tailer's own groups (any epoch of this machine): only an ack range covering the commit proves it.
                        commit.Own = true;
                    }
                    else if (held.Contains(node))
                    {
                        commit.Joined = true;
                    }
                    else
                    {
                        // Any other origin: one no live link of another placed region welcomed with exactly this id (another
                        // machine of this region, another epoch, a gone machine). Nothing says another region holds it.
                        commit.Unknown = true;
                    }
                }
                else
                {
                    // seq, commit_offset, origin, stamp, collection, id, deleted
                    if (SqliteRecord.Decode(TursoSession.Bytes(reader, 3), 1, materializeContent: false) is not [long seq])
                    {
                        throw new InvalidDataException("a captured _tb_feed row does not have the feed table's columns");
                    }

                    // One transaction's feed rows take consecutive seqs; anything else is not this capture's feed.
                    commit.Gap |= commit.LastSeq is { } last && seq != last + 1;
                    commit.FirstSeq ??= seq;
                    commit.LastSeq = seq;
                }
            }

            if (rows < FrontierRows)
            {
                break;
            }
        }

        return (frontier, scanned);
    }

    // Worker-only.
    private long ShapeFeedHead()
    {
        CountRead("shape");
        return Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar() is long seq ? seq : 0;
    }

    // Worker-only. The persisted frontier of this file's incarnation, or null.
    private (long Frontier, long Scanned, List<(long From, long To)> Ranges)? ShapeFrontier()
    {
        CountRead("shape");
        return ReadShapeFrontier(session.Connection, transaction);
    }

    // The persisted frontier of the file's incarnation, or null: also null past the feed head, which no walk reaches, and for
    // ranges that are not a disjoint union within the bound.
    private static (long Frontier, long Scanned, List<(long From, long To)> Ranges)? ReadShapeFrontier(SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT end_offset, body, (SELECT incarnation FROM commit_offset WHERE id = 1), (SELECT seq FROM _tb_feed_sequence WHERE id = 1), octet_length(body) FROM _tb_shape_chunk WHERE handle = $handle AND offset = 0;";
        command.Parameters.AddWithValue("$handle", ShapeFrontierHandle);
        using var reader = command.ExecuteReader();
        // A damaged body is sized before it is read: past the largest one written, nothing of it is decoded.
        if (!reader.Read() || reader.IsDBNull(2) || reader.IsDBNull(4) || reader.GetInt64(4) > FrontierBodyBytes)
        {
            return null;
        }

        var frontier = reader.GetInt64(0);
        var head = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
        var parts = Encoding.UTF8.GetString(TursoSession.Bytes(reader, 1)).Split(' ');
        if (parts is not ["2", var owner, var scannedText, ..] || parts.Length > 3 + RemoteAckWaiter.MaxRanges
            || owner != reader.GetString(2) || frontier < 0 || frontier > head
            || !long.TryParse(scannedText, NumberStyles.None, CultureInfo.InvariantCulture, out var scanned))
        {
            return null;
        }

        var ranges = new List<(long From, long To)>(parts.Length - 3);
        foreach (var part in parts.AsSpan(3))
        {
            // Written ordered and disjoint: anything else is not this format.
            if (part.Split(':') is not [var fromText, var toText]
                || !long.TryParse(fromText, NumberStyles.None, CultureInfo.InvariantCulture, out var from)
                || !long.TryParse(toText, NumberStyles.None, CultureInfo.InvariantCulture, out var to)
                || from >= to || (ranges.Count > 0 && from <= ranges[^1].To))
            {
                return null;
            }

            ranges.Add((from, to));
        }

        return (frontier, scanned, ranges);
    }

    /// <summary>
    /// The change id CDC retention may delete below without taking rows the frontier has not walked yet (null: no hold).
    /// The hold has a peer's own bounds (see <see cref="RetentionCut"/> and <see cref="TrimAsync"/>): it lasts while the row
    /// after the walked prefix is younger than 24 h when any peer has been heard from, else 60 s (a peer
    /// whose stream has not arrived yet), and while the unwalked rows stay within both the far-behind row and byte bounds.
    /// The measurement reads at most one row beyond the row bound. Past either bound, or once
    /// the walk can no longer go on (rows past it trimmed, or a first unwalked feed seq other than F + 1), retention is
    /// unchanged and the frontier stops for good. Shared by the tailer's trim, the peer-ack path and the backup tap.
    /// </summary>
    internal static long? ShapeFrontierHold(SqliteConnection connection, SqliteTransaction? transaction, long now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'turso_cdc';";
        if (command.ExecuteScalar() is null)
        {
            return null;
        }

        var (frontier, scanned, _) = ReadShapeFrontier(connection, transaction) ?? (0, 0, []);
        command.CommandText = """
            SELECT (SELECT min(change_id) FROM turso_cdc),
                (SELECT change_time FROM turso_cdc WHERE change_id > $scanned ORDER BY change_id LIMIT 1),
                EXISTS (SELECT 1 FROM _tb_peers WHERE seen > 0);
            """;
        command.Parameters.AddWithValue("$scanned", scanned);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read() || reader.IsDBNull(0) || reader.GetInt64(0) > scanned + 1 || reader.IsDBNull(1)
                || reader.GetInt64(1) <= now - (reader.GetInt64(2) != 0 ? 86_400 : 60))
            {
                return null;
            }
        }

        // Image sizes only, streamed: past either bound it stops at once.
        command.CommandText = "SELECT octet_length(after) FROM turso_cdc WHERE change_id > $scanned ORDER BY change_id LIMIT $limit;";
        command.Parameters.AddWithValue("$limit", FarBehindRows + 1);
        using var measured = command.ExecuteReader();
        long rows = 0;
        long bytes = 0;
        while (measured.Read())
        {
            rows = checked(rows + 1);
            bytes = checked(bytes + (measured.IsDBNull(0) ? 0 : measured.GetInt64(0)));
            if (rows > FarBehindRows || bytes > FarBehindBytes)
            {
                return null;
            }
        }

        measured.Close();
        // The walk passes a feed row only at F + 1, and only the walk moves F: a first unwalked feed seq past it (a copy's or a
        // restore's feed) stops the walk for good, so there is nothing to hold for. Within the row bound just measured. Only
        // the image's bounded prefix is read (nullness tested in SQL): it holds the record header and the seq.
        command.CommandText = """
            SELECT substr(after, 1, $prefix) FROM turso_cdc
            WHERE change_id > $scanned AND table_name = '_tb_feed' AND change_type = 1 AND after IS NOT NULL ORDER BY change_id LIMIT 1;
            """;
        command.Parameters.Clear();
        command.Parameters.AddWithValue("$scanned", scanned);
        command.Parameters.AddWithValue("$prefix", FeedSeqPrefixBytes);
        using var feed = command.ExecuteReader();
        if (!feed.Read())
        {
            return scanned;
        }

        try
        {
            return SqliteRecord.Decode(TursoSession.Bytes(feed, 0), 1) is [long seq] && seq != frontier + 1 ? null : scanned;
        }
        catch (InvalidDataException)
        {
            // A damaged row holds the walk, which retries it (section 7): the hold stays.
            return scanned;
        }
    }

    // A feed row's header (seven serial types of at most 9 bytes, and its size) and its seq fit in this.
    private const int FeedSeqPrefixBytes = 128;

    // One captured transaction while the frontier walks it.
    private sealed class FrontierCommit
    {
        // State rows of this machine, of a held origin, of any other origin, and origin-0 rows or deletes.
        public bool Own { get; set; }

        public bool Joined { get; set; }

        public bool Unknown { get; set; }

        public bool Derived { get; set; }

        public long? FirstSeq { get; set; }

        public long? LastSeq { get; set; }

        // Feed rows whose seqs are not consecutive.
        public bool Gap { get; set; }
    }
}
