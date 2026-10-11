using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Client databases store each document as a register (docs/design/anna-core.md, Documents and Stored state): local writes
/// mint a dot under this member's replica id and stamp it with the member's clock, replicated deltas join into the stored
/// register, and the row keeps the register's last-writer-wins write for every read.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    // A register with concurrent values is sent whole by a baseline, alone in its group, and still fits one envelope.
    private const int MaxSingleChangeBytes = IStorageTransaction.MaxSingleChangeBytes;

    // A client database whose winner is unchanged keeps its row and only stores the joined register.
    private const string UpdateState = "UPDATE documents SET state = $state WHERE collection = $collection AND id = $id;";

    // Whether the last-writer-wins write of a collection's own register is its drop.
    private const string DroppedCollection = $"SELECT 1 FROM documents WHERE collection = '{DocumentDelta.Collections}' AND id = $collection AND body IS NULL;";

    // Whether this transaction applied replicated deltas: its history then takes one offset at commit.
    private bool _imported;

    private LocalReplica Member => store.Member ?? throw new InvalidOperationException("a client database writes with this member's replica id");

    /// <summary>The last-writer-wins write of <paramref name="state"/>: the greatest stamp, then replica id, then counter.</summary>
    internal static KeyValuePair<Dot, DocumentWrite>? Winner(MvRegister<DocumentWrite> state)
    {
        KeyValuePair<Dot, DocumentWrite>? winner = null;
        foreach (var value in state.Values)
        {
            if (winner is not { } best
                || (value.Value.Stamp.Value, value.Key.Replica.Value, value.Key.Counter).CompareTo((best.Value.Stamp.Value, best.Key.Replica.Value, best.Key.Counter)) > 0)
            {
                winner = value;
            }
        }

        return winner;
    }

    // Worker-only: the stored register of a document, or the empty register.
    private MvRegister<DocumentWrite> ReadState(string collection, string id)
    {
        var command = Command(SqliteStatements.GetState, collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is byte[] state ? Decode(state) : new MvRegister<DocumentWrite>();
    }

    private static MvRegister<DocumentWrite> Decode(ReadOnlySpan<byte> state)
    {
        try
        {
            return LatticeEncoding.DecodeDocument(state);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException("a stored document register is corrupt", error);
        }
    }

    // Worker-only: stores a register with its last-writer-wins write, and returns that write's version and body.
    private (long Version, string? Body) Store(string collection, string id, MvRegister<DocumentWrite> state, byte[] encoded)
    {
        var winner = Winner(state)?.Value ?? throw new InvalidOperationException("a stored register holds a write");
        var body = winner.IsTombstone ? null : StrictUtf8.GetString(winner.Body);
        var put = BodyCommand(SqliteStatements.PutState, collection, id);
        put.Parameters.AddWithValue("$state", encoded);
        put.Parameters.AddWithValue("$version", Version(winner.Stamp));
        put.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        put.ExecuteNonQuery();
        return (Version(winner.Stamp), body);
    }

    // Worker-only: whether the row of a stored register still reads as the register's last-writer-wins write. The row is
    // derived from the register, so a row that does not (damage to its version or body alone) is rewritten from it.
    private bool Materialized(string collection, string id, MvRegister<DocumentWrite> state)
    {
        var winner = Winner(state)?.Value ?? throw new InvalidDataException("a stored document register holds no write");
        var command = BodyCommand(SqliteStatements.Materialized, collection, id);
        command.Parameters.AddWithValue("$version", Version(winner.Stamp));
        command.Parameters.AddWithValue("$body", winner.IsTombstone ? DBNull.Value : StrictUtf8.GetString(winner.Body));
        return command.ExecuteScalar() is not null;
    }

    // A stamp's 48 bits of milliseconds keep it below 2^63.
    private static long Version(Hlc stamp) => checked((long)stamp.Value);

    // Worker-only, inside a mutation: a local write of body (null for a tombstone) over every value of the document's
    // register this member knows. The clock first passes every stamp of the register (each at most the skew limit ahead),
    // so the write's stamp follows the document's, and its dot is the next of this replica id for the document.
    private long WriteRegister(string collection, string id, ReadOnlySpan<byte> body, bool tombstone)
    {
        var member = Member;
        var state = ReadState(collection, id);
        foreach (var write in state.Values.Values)
        {
            member.Clock.Observe(write.Stamp);
        }

        var stamp = member.Clock.Tick();
        // The write replaces every value the register holds and keeps its whole context, so the delta is the new state.
        var next = state.WriteDelta(member.Id, tombstone ? DocumentWrite.Tombstone(stamp) : DocumentWrite.Live(stamp, body));
        var encoded = LatticeEncoding.Encode(next);
        Store(collection, id, next, encoded);
        if (_deltas is not null)
        {
            CaptureDelta(collection, id, next, encoded);
        }

        return Version(stamp);
    }

    // Capacity errors occur during the mutation, as well as at commit; the mutation marks the transaction rollback-only.
    private void CaptureDelta(string collection, string id, MvRegister<DocumentWrite> delta, byte[] encoded)
    {
        if (!DocumentDelta.HasValidKey(collection, id))
        {
            throw new InvalidDataException("a captured change has an invalid collection or id");
        }

        // A later write of the document in this transaction was made over the stored state of the earlier one, so its
        // delta covers the earlier capture and replaces it.
        var index = _changeIndex.GetValueOrDefault((collection, id), -1);
        var captured = new CapturedDelta(collection, id, delta, encoded);
        var bytes = captured.Bytes - (index >= 0 ? _deltas![index].Bytes : 0);
        CheckCaptureCapacity(bytes, replacing: index >= 0);
        if (index >= 0)
        {
            if (_savepoint is { } saved && index < saved.Changes)
            {
                _replacedSinceSavepoint.Add((index, _deltas![index]));
            }

            _deltas![index] = captured;
        }
        else
        {
            _changeIndex[(collection, id)] = _deltas!.Count;
            _deltas.Add(captured);
        }

        _changeBytes += bytes;
    }

    // Worker-only, at commit: appends the captured deltas as the next group of this database's own stream, with the
    // applied position of every other origin as its dependency vector. The writer gate serializes the next sequence.
    private long AppendGroup(IReadOnlyList<CapturedDelta> changes, CancellationToken cancellationToken)
    {
        var bytes = changes.Sum(change => change.Bytes);
        if (changes.Count > IStorageTransaction.MaxChangeGroupChanges
            || bytes > (changes.Count == 1 ? MaxSingleChangeBytes : IStorageTransaction.MaxChangeGroupBytes))
        {
            throw new ChangeGroupCapacityException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }

        var sequence = checked((long)Command(SqliteStatements.LastGroup).ExecuteScalar()! + 1);
        var group = Command(SqliteStatements.AppendGroup);
        group.Parameters.AddWithValue("$sequence", sequence);
        group.Parameters.AddWithValue("$dependencies", OriginPositions.Encode(Dependencies()));
        group.ExecuteNonQuery();
        var ordinal = 0;
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var append = BodyCommand(SqliteStatements.AppendGroupChange, change.Collection, change.Id);
            append.Parameters.AddWithValue("$sequence", sequence);
            append.Parameters.AddWithValue("$ordinal", ordinal++);
            append.Parameters.AddWithValue("$state", change.Encoded);
            append.ExecuteNonQuery();
        }

        return sequence;
    }

    // Worker-only: the applied position of every other origin, but only the latest epoch of each member's stream of this
    // database: a later epoch's stream starts with a baseline of everything its member held (docs/design/anna-core.md,
    // Per-origin streams).
    private List<OriginPosition> Dependencies()
    {
        var command = Command(SqliteStatements.AppliedPositions);
        command.Parameters.AddWithValue("$limit", (OriginPositions.MaxCount * 4) + 1);
        var latest = new Dictionary<(ulong Member, ulong Incarnation), (ushort Epoch, OriginPosition Position)>();
        var rows = 0;
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                if (++rows > OriginPositions.MaxCount * 4)
                {
                    throw new InvalidDataException("this database has applied more origins than a dependency vector can name");
                }

                var origin = reader.GetFieldValue<byte[]>(0);
                if (origin.Length != OriginPositions.OriginBytes)
                {
                    throw new InvalidDataException("a stored applied origin is invalid");
                }

                var replica = new ReplicaId(BinaryPrimitives.ReadUInt64LittleEndian(origin));
                var key = (replica.Value >> 16, BinaryPrimitives.ReadUInt64LittleEndian(origin.AsSpan(8)));
                if (!latest.TryGetValue(key, out var known) || known.Epoch < replica.Epoch)
                {
                    latest[key] = (replica.Epoch, new OriginPosition(origin, reader.GetInt64(1)));
                }
            }
        }

        if (latest.Count > OriginPositions.MaxCount)
        {
            throw new InvalidDataException("this database has applied more origins than a dependency vector can name");
        }

        var positions = latest.Values.Select(entry => entry.Position).ToList();
        positions.Sort((left, right) => left.Origin.Span.SequenceCompareTo(right.Origin.Span));
        return positions;
    }

    /// <summary>
    /// The origin of a client database's own stream: this member's replica id and the random last 8 bytes of the file's
    /// incarnation, a ULID whose first bytes are its creation time.
    /// </summary>
    private byte[] ClientOrigin()
    {
        var origin = new byte[OriginPositions.OriginBytes];
        BinaryPrimitives.WriteUInt64LittleEndian(origin, Member.Id.Value);
        store.Incarnation.ToByteArray().AsSpan(8, 8).CopyTo(origin.AsSpan(8));
        return origin;
    }

    // Worker-only: reads one captured group, within the group bounds.
    private CapturedGroup? ReadGroup(long sequence)
    {
        var dependencies = Command(SqliteStatements.GroupDependencies);
        dependencies.Parameters.AddWithValue("$sequence", sequence);
        if (dependencies.ExecuteScalar() is not byte[] vector)
        {
            return null;
        }

        var positions = OriginPositions.Read(vector, out var read);
        if (read != vector.Length)
        {
            throw new InvalidDataException("a stored dependency vector is followed by unexpected bytes");
        }

        var command = Command(SqliteStatements.GroupChanges);
        command.Parameters.AddWithValue("$sequence", sequence);
        command.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges + 1);
        command.Parameters.AddWithValue("$maxBytes", MaxSingleChangeBytes);
        var changes = new List<DocumentDelta>();
        long bytes = 0;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            bytes += reader.GetInt64(3);
            if (changes.Count >= IStorageTransaction.MaxChangeGroupChanges || reader.IsDBNull(2)
                || bytes > (changes.Count == 0 ? MaxSingleChangeBytes : IStorageTransaction.MaxChangeGroupBytes))
            {
                throw new InvalidDataException("the stored change group exceeds the journal limits");
            }

            changes.Add(new DocumentDelta(reader.GetString(0), reader.GetString(1), Decode(reader.GetFieldValue<byte[]>(2))));
        }

        return changes.Count == 0 ? throw new InvalidDataException("a stored change group holds no change") : new CapturedGroup(positions, changes);
    }

    private bool ApplyDelta(DocumentDelta change, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (store.IsSystem)
        {
            throw new NotSupportedException("the system database replicates from its catalog primary");
        }

        if (!DocumentDelta.HasValidKey(change.Collection, change.Id) || change.State.Values.Count == 0
            || change.State.Values.Values.Any(write => write.Stamp.Value > long.MaxValue))
        {
            throw new InvalidDataException("a replicated change has an invalid collection or id, holds no write, or a stamp beyond any version");
        }

        var collections = change.Collection == DocumentDelta.Collections;
        var unique = change.Collection == DocumentDelta.Unique;
        var metadata = collections || unique || change.Collection == DocumentDelta.Indexes;
        if (metadata && (collections || unique ? !DatabaseName.TryParse(change.Id, out _) : !IndexName().IsMatch(change.Id)))
        {
            throw new InvalidDataException("replicated metadata names an invalid collection or index");
        }

        foreach (var write in change.State.Values.Values.Where(write => !write.IsTombstone))
        {
            var text = Admit(write.Body.ToArray());
            if (unique)
            {
                _ = ReadUniqueSchema(text);
            }
            else if (metadata)
            {
                _ = ReadMetadata(change.Id, text, collections, cancellationToken);
            }
        }

        _imported = true;
        var encodedDelta = LatticeEncoding.Encode(change.State);
        var bytes = (long)Encoding.UTF8.GetByteCount(change.Collection) + Encoding.UTF8.GetByteCount(change.Id) + encodedDelta.Length;
        if (_importedCount == IStorageTransaction.MaxChangeGroupChanges
            || bytes > (_importedCount == 0 ? MaxSingleChangeBytes : IStorageTransaction.MaxChangeGroupBytes - _importedBytes))
        {
            throw new InvalidOperationException(
                $"a replicated transaction holds at most {IStorageTransaction.MaxChangeGroupChanges} changes and {IStorageTransaction.MaxChangeGroupBytes} bytes");
        }

        _importedCount++;
        _importedBytes += bytes;
        var member = Member;
        foreach (var write in change.State.Values.Values)
        {
            member.Clock.Observe(write.Stamp);
        }

        var stored = ReadState(change.Collection, change.Id);
        MvRegister<DocumentWrite> joined;
        try
        {
            joined = stored.Join(change.State);
        }
        catch (InvalidOperationException error)
        {
            // Two values under one dot: some replica minted it twice. Applying either would lose the other everywhere.
            throw new InvalidDataException("a replicated change carries a dot this database holds with another value", error);
        }

        var winner = Winner(joined)!.Value;
        // A row that no longer reads as its unchanged winner takes the full path below, which rewrites it from the register.
        var materialized = Winner(stored) is { } previous && previous.Key == winner.Key && Materialized(change.Collection, change.Id, stored);
        if (materialized && joined.Equals(stored))
        {
            return false;
        }

        var encoded = LatticeEncoding.Encode(joined);
        if (materialized)
        {
            // Only values that lose to the last-writer-wins write changed: nothing anyone reads did.
            var update = BodyCommand(UpdateState, change.Collection, change.Id);
            update.Parameters.AddWithValue("$state", encoded);
            update.ExecuteNonQuery();
            return true;
        }

        var body = winner.Value.IsTombstone ? null : StrictUtf8.GetString(winner.Value.Body);
        if (metadata)
        {
            Store(change.Collection, change.Id, joined, encoded);
            if (unique)
            {
                ApplyUniqueMetadata(change.Id, body, cancellationToken);
            }
            else
            {
                ApplyMetadata(collections, change.Id, body, cancellationToken);
            }

            return true;
        }

        if (body is not null)
        {
            // A live document implies its collection, whether or not its creation was applied yet.
            CreateCollection(new CollectionInfo(change.Collection, 0));
        }

        // Bound the complete imported commit, including shape framing.
        var historyBytes = (body is null ? 0 : (long)Encoding.UTF8.GetByteCount(body)) + JsonEncodedText.Encode(change.Id).EncodedUtf8Bytes.Length + OpFramingBytes;
        if (_writtenBytes + historyBytes > MaxWrittenBytes)
        {
            throw new StorageWriteLimitException("a replicated transaction's shape history fits at most 7 MiB with framing");
        }

        _writtenBytes += historyBytes;
        StoreWinner(change.Collection, change.Id, joined, encoded, body, cancellationToken);
        ImportHistory(UniqueVisibleChange(new DocumentChange(
            change.Collection, change.Id, Version(winner.Value.Stamp), body is null ? null : (ReadOnlyMemory<byte>?)winner.Value.Body.ToArray())));
        if (body is null && Command(DroppedCollection, change.Collection).ExecuteScalar() is not null)
        {
            // The collection's drop arrived before this tombstone of its last live document (a baseline or a repair page
            // sends registers in key order): the drop completes now, whatever order the registers came in.
            DropReplicatedCollection(change.Collection, cancellationToken);
        }

        return true;
    }

    // Metadata joins like a document: its last-writer-wins write creates or drops the collection or index it describes.
    private void ApplyMetadata(bool collections, string name, string? body, CancellationToken cancellationToken)
    {
        switch (body is null ? null : ReadMetadata(name, body, collections, cancellationToken))
        {
            case CollectionInfo collection:
                _metadataChanged = true;
                _collectionsChanged = true;
                CreateCollection(collection);
                // Winning metadata also replaces an inferred or concurrently created collection's identity.
                var identity = Command(SqliteStatements.AdoptCollectionIdentity, collection.Name);
                identity.Parameters.AddWithValue("$entityId", collection.Id);
                identity.Parameters.AddWithValue("$created", collection.CreatedAt);
                identity.ExecuteNonQuery();
                break;
            case IndexDefinition index:
                DropIndex(index.Name, cancellationToken);
                CreateIndex(index, cancellationToken);
                break;
            case null when collections:
                DropReplicatedCollection(name, cancellationToken);
                break;
            default:
                DropIndex(name, cancellationToken);
                break;
        }
    }

    // A replicated drop wrote tombstones over the documents its member knew, in its own group; the collection goes once
    // nothing in it is live, a retained uniqueness loser included, and a document another member wrote meanwhile keeps it.
    private void DropReplicatedCollection(string name, CancellationToken cancellationToken)
    {
        if (Command(UniqueLiveDocument, name).ExecuteScalar() is not null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        DropUniqueCollection(name);
        Command(SqliteStatements.DropHistory, name).ExecuteNonQuery();
        if (Command(SqliteStatements.DropCollection, name).ExecuteNonQuery() > 0)
        {
            _metadataChanged = true;
            _collectionsChanged = true;
        }

        DropImportHistory(name);
    }

    /// <summary>
    /// Makes this client database's journal its member's (see <see cref="SqliteDatabaseStore.SeedAsync"/>): a journal of
    /// another replica id is dropped and marked incomplete. Returns whether the journal covers every register.
    /// </summary>
    internal async Task<bool> ClaimJournalAsync(CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            bool complete;
            long replica;
            using (var reader = Command(SqliteStatements.JournalState).ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new InvalidDataException("the journal state is missing");
                }

                (replica, complete) = (reader.GetInt64(0), reader.GetInt64(1) != 0);
            }

            var current = unchecked((long)Member.Id.Value);
            if (replica == current)
            {
                return complete;
            }

            if (replica != 0)
            {
                PlainCommand(SqliteStatements.ResetJournal).ExecuteNonQuery();
                complete = false;
            }

            var claim = Command(SqliteStatements.ClaimJournal);
            claim.Parameters.AddWithValue("$replica", current);
            claim.Parameters.AddWithValue("$complete", complete ? 1 : 0);
            claim.ExecuteNonQuery();
            return complete;
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    internal async Task MarkCompleteAsync(CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            return Command(SqliteStatements.MarkComplete).ExecuteNonQuery();
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    /// <summary>
    /// Appends one bounded baseline group: the registers after <paramref name="after"/> in key order, live or tombstone,
    /// as they are stored. <see langword="null"/> when none follow.
    /// </summary>
    internal async Task<BaselineChunk?> AppendBaselineAsync((string Collection, string Id)? after, CancellationToken cancellationToken) =>
        await RunAsync(() =>
        {
            ThrowIfReadOnly();
            var command = Command(after is null ? SqliteStatements.BaselineFirst : SqliteStatements.BaselineAfter, after?.Collection);
            if (after is { } cursor)
            {
                command.Parameters.AddWithValue("$id", cursor.Id);
            }

            command.Parameters.AddWithValue("$limit", IStorageTransaction.MaxChangeGroupChanges);
            command.Parameters.AddWithValue("$maxBytes", MaxSingleChangeBytes);
            var group = new List<CapturedDelta>();
            long bytes = 0;
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var (collection, id) = (reader.GetString(0), reader.GetString(1));
                    var size = (long)Encoding.UTF8.GetByteCount(collection) + Encoding.UTF8.GetByteCount(id) + reader.GetInt64(3);
                    if (group.Count > 0 && bytes + size > IStorageTransaction.MaxChangeGroupBytes)
                    {
                        break;
                    }

                    if (reader.IsDBNull(2) || size > MaxSingleChangeBytes)
                    {
                        // ponytail: a register holds a few concurrent values of at most 1 MiB each, one per member that
                        // wrote without seeing the others; one past 3 MiB cannot be sent, so the baseline stops loudly.
                        throw new InvalidDataException("a document register is too large for a change group");
                    }

                    var encoded = reader.GetFieldValue<byte[]>(2);
                    group.Add(new CapturedDelta(collection, id, null!, encoded));
                    bytes += size;
                }
            }

            if (group.Count == 0)
            {
                return (BaselineChunk?)null;
            }

            var last = group[^1];
            return new BaselineChunk((last.Collection, last.Id), AppendGroup(group, cancellationToken));
        }, cancellationToken, mutation: true).ConfigureAwait(false);

    // One document's captured delta and its encoding. A baseline sends stored registers as they are, without decoding them.
    private readonly record struct CapturedDelta(string Collection, string Id, MvRegister<DocumentWrite> State, byte[] Encoded)
    {
        public long Bytes => (long)Encoding.UTF8.GetByteCount(Collection) + Encoding.UTF8.GetByteCount(Id) + Encoded.Length;
    }
}

/// <summary>Last baseline key and the appended group's sequence.</summary>
internal readonly record struct BaselineChunk((string Collection, string Id) Last, long Sequence);
