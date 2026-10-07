using System.Text;

namespace TerraBase.Engine.Sqlite;

/// <summary>Capture and replication of TerraBase's own database (docs/decisions/0009).</summary>
internal sealed partial class SqliteStorageTransaction
{
    // Set by a replica write, so its commit signals waiters like a local one.
    private bool _replicaChanged;

    // The primary's incarnation a snapshot adopts; published as the store's right after the SQL commit.
    private Guid? _adoptedIncarnation;

    // A system group replays every mutation in order, so a follower reaches the same state without a merge: no
    // final-state collapse and no client key or object rules. Keys must fit the u16 lengths of the wire encoding.
    private void CaptureSystem(DocumentChange change)
    {
        if (Encoding.UTF8.GetByteCount(change.Collection) > ushort.MaxValue || Encoding.UTF8.GetByteCount(change.Id) > ushort.MaxValue)
        {
            throw new InvalidDataException("a captured system change has a collection or id over 65535 bytes");
        }

        var bytes = ContentBytes(change);
        if (_changes!.Count >= ISystemReplication.MaxGroupChanges || bytes > ISystemReplication.MaxGroupBytes - _changeBytes)
        {
            throw new ChangeGroupCapacityException(
                $"a system transaction holds at most {ISystemReplication.MaxGroupChanges} changes and {ISystemReplication.MaxGroupBytes} bytes");
        }

        _changes.Add(change with { Json = change.Json is { } json ? json.ToArray() : (ReadOnlyMemory<byte>?)null });
        _changeBytes += bytes;
    }

    // Worker-only, inside DeleteCollectionsAsync: each removed document is a captured deletion.
    private void CaptureSystemDeletions(string from, string to, CancellationToken cancellationToken)
    {
        var ids = Command(SqliteStatements.CollectionRangeIds);
        ids.Parameters.AddWithValue("$from", from);
        ids.Parameters.AddWithValue("$to", to);
        using var reader = ids.ExecuteReader();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (collection, id) = (reader.GetString(0), reader.GetString(1));
            Write(offset => Capture(new DocumentChange(collection, id, offset, null)), cancellationToken);
        }
    }

    /// <summary>See <see cref="ISystemReplication.ReadDocumentsAsync"/>.</summary>
    internal ValueTask<SystemDocumentPage> ReadSystemDocumentsAsync(
        string? afterCollection, string? afterId, int maxDocuments, int maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var command = Command(afterCollection is null ? SqliteStatements.SystemDocumentsFirst : SqliteStatements.SystemDocumentsAfter, afterCollection);
            if (afterCollection is not null)
            {
                command.Parameters.AddWithValue("$id", afterId ?? "");
            }

            command.Parameters.AddWithValue("$limit", maxDocuments + 1);
            var documents = new List<DocumentChange>();
            long bytes = 0;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (documents.Count == maxDocuments || bytes >= maxBytes)
                {
                    return new SystemDocumentPage(documents, HasMore: true);
                }

                cancellationToken.ThrowIfCancellationRequested();
                var document = Change(reader);
                bytes += ContentBytes(document);
                documents.Add(document);
            }

            return new SystemDocumentPage(documents, HasMore: false);
        }, cancellationToken, retry: true);

    /// <summary>See <see cref="ISystemReplication.ReadGroupAsync"/>.</summary>
    internal ValueTask<SystemGroupPage> ReadSystemGroupAsync(long sequence, int skip, int maxChanges, int maxBytes, CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            var size = Command(SqliteStatements.GroupSize);
            size.Parameters.AddWithValue("$sequence", sequence);
            var count = checked((int)(long)size.ExecuteScalar()!);
            var command = Command(SqliteStatements.ReadGroupPage);
            command.Parameters.AddWithValue("$sequence", sequence);
            command.Parameters.AddWithValue("$skip", skip);
            command.Parameters.AddWithValue("$limit", maxChanges);
            var changes = new List<DocumentChange>();
            long bytes = 0;
            using var reader = command.ExecuteReader();
            while (bytes < maxBytes && reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var change = Change(reader);
                bytes += ContentBytes(change);
                changes.Add(change);
            }

            return new SystemGroupPage(count, changes);
        }, cancellationToken, retry: true);

    /// <summary>
    /// Removes every document and this database's own journal, and adopts <paramref name="origin"/> (the primary's) as its
    /// incarnation, for a snapshot: the catalog then belongs to the primary's journal and ETags match the primary's.
    /// </summary>
    internal async ValueTask ResetReplicaAsync(ReadOnlyMemory<byte> origin, CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (origin.Length != 16)
            {
                throw new InvalidDataException("a replica adopts a 16-byte origin");
            }

            PlainCommand(SqliteStatements.ResetReplica).ExecuteNonQuery();
            var hex = Convert.ToHexStringLower(origin.Span);
            var incarnation = Command(SqliteStatements.RenewIncarnation);
            incarnation.Parameters.AddWithValue("$incarnation", hex);
            incarnation.ExecuteNonQuery();
            _adoptedIncarnation = Guid.ParseExact(hex, "N");
            _replicaChanged = true;
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    /// <summary>See <see cref="ISystemReplicaTransaction.ReplaceAsync"/>; never captured. The primary's bytes are kept as they are.</summary>
    internal async ValueTask ReplaceAsync(DocumentChange change, CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            ArgumentNullException.ThrowIfNull(change);
            if (change.Collection.Length == 0 || change.Id.Length == 0 || change.Version is <= 0 or > VersionClock.MaxVersion
                || Encoding.UTF8.GetByteCount(change.Collection) > ushort.MaxValue || Encoding.UTF8.GetByteCount(change.Id) > ushort.MaxValue)
            {
                throw new InvalidDataException("a replicated system change has an invalid collection, id or version");
            }

            if (change.Json is { } json)
            {
                var replace = BodyCommand(SqliteStatements.ReplaceDocument, change.Collection, change.Id);
                replace.Parameters.AddWithValue("$version", change.Version);
                replace.Parameters.AddWithValue("$body", StrictUtf8.GetString(json.Span));
                replace.ExecuteNonQuery();
            }
            else
            {
                var delete = Command(SqliteStatements.Delete, change.Collection);
                delete.Parameters.AddWithValue("$id", change.Id);
                delete.ExecuteNonQuery();
            }

            ObserveOffset(change.Version);
            _replicaChanged = true;
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    /// <summary>
    /// Records <paramref name="head"/> as this follower's journal head, its only stored digest, and raises the commit offset
    /// to at least <paramref name="offset"/>, so writes after a promotion continue the primary's journal and versions.
    /// </summary>
    internal async ValueTask SetReplicaHeadAsync(JournalPosition head, long offset, CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (head.Sequence < 0 || head.Digest.Length != JournalDigest.Length || !head.Origin.Span.SequenceEqual(GetOrigin()))
            {
                throw new InvalidDataException("a replica head needs this database's origin, a sequence and a 32-byte digest");
            }

            TruncateSystemJournal(long.MaxValue);
            if (head.Sequence > 0)
            {
                var prefix = Command(SqliteStatements.AppendChangeDigest);
                prefix.Parameters.AddWithValue("$sequence", head.Sequence);
                prefix.Parameters.AddWithValue("$digest", head.Digest.ToArray());
                prefix.ExecuteNonQuery();
            }

            ObserveOffset(offset);
            _replicaChanged = true;
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    // Worker-only: drops the system journal's groups and digests at or below floor.
    private void TruncateSystemJournal(long floor)
    {
        if (floor <= 0)
        {
            return;
        }

        foreach (var sql in (ReadOnlySpan<string>)[SqliteStatements.TruncateChanges, SqliteStatements.TruncatePrefixes])
        {
            var truncate = Command(sql);
            truncate.Parameters.AddWithValue("$floor", floor);
            truncate.ExecuteNonQuery();
        }
    }

    // Worker-only: a later local write must supersede every observed version.
    private void ObserveOffset(long version)
    {
        var offset = Command(SqliteStatements.ObserveOffset);
        offset.Parameters.AddWithValue("$version", version);
        offset.ExecuteNonQuery();
    }
}
