using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Management;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Client databases keep each document's replicated state (docs/terrabase/multi-region-replication.md sections 4 to 6): a
/// <see cref="DocumentState"/> in <c>_tb_state</c>, and its materialized JSON in the document row every read uses. A
/// request mints one stamp per document it touches, applies its operations to that document in order at that stamp, and
/// writes the document's <c>_tb_state</c> row once, when the request ends (<see cref="FlushStatesAsync"/>), so the
/// change feed sees one state per document per operation.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    // The documents this request changed, with the version their operations share; their state rows are not written yet.
    private readonly Dictionary<(string Collection, string Id), (DocumentState State, EntryVersion At, int BodyBytes)> _pending = [];

    // The state rows of this machine's own writes this transaction wrote: the tailer ships them as one change group, so they
    // stay within a group's bounds (the bytes are counted in _changeBytes, which only the system database uses otherwise).
    private int _captured;
    private int? _catalogProofBytes;

    private LocalReplica Member => store.Member ?? throw new InvalidOperationException("a database writes with this member's replica id");

    // A stamp's 48 bits of milliseconds keep it below 2^63.
    private static long Version(Hlc stamp) => checked((long)stamp.Value);

    // The version a read reports from `ordinal` (a stored stamp, NULL when none): a document this request wrote has its
    // version in memory until the request ends and writes its state row.
    private long Version(string collection, string id, SqliteDataReader reader, int ordinal) =>
        _pending.TryGetValue((collection, id), out var pending) ? Version(pending.At.Stamp)
            : reader.IsDBNull(ordinal) ? throw new InvalidDataException("a stored document has no state") : reader.GetInt64(ordinal);

    /// <summary>
    /// Bucket of a document for anti-entropy: the first 12 bits of SHA-256 over the collection's UTF-8 byte length (4 bytes,
    /// little-endian), the collection and the id, so no two (collection, id) pairs share an input.
    /// </summary>
    internal static int Bucket(string collection, string id)
    {
        var name = Encoding.UTF8.GetBytes(collection);
        var input = new byte[4 + name.Length + Encoding.UTF8.GetByteCount(id)];
        BinaryPrimitives.WriteInt32LittleEndian(input, name.Length);
        name.CopyTo(input, 4);
        Encoding.UTF8.GetBytes(id, input.AsSpan(4 + name.Length));
        var hash = SHA256.HashData(input);
        return (hash[0] << 4) | (hash[1] >> 4);
    }

    // Worker-only: the state of a document and the version this request writes it at. The stamp is minted once per
    // document per request, above every stamp the state holds, so a write after a remote stamp from the future still
    // reads its own result.
    private (DocumentState State, EntryVersion At) Begin(string collection, string id)
    {
        if (_pending.TryGetValue((collection, id), out var pending))
        {
            return (pending.State, pending.At);
        }

        var state = ReadState(collection, id);
        var member = Member;
        var stamp = member.Clock.Tick(state.MaxStamp);
        return (state, new EntryVersion(stamp, member.Id));
    }

    // Worker-only: the stored state of a document, or the empty state.
    private DocumentState ReadState(string collection, string id)
    {
        if (!HasTable(collection))
        {
            return DocumentState.Bottom;
        }

        using var reader = TableCommand(store.IsSystem ? SqliteStatements.SystemGetState : SqliteStatements.GetState, collection, id).ExecuteReader();
        if (!reader.Read())
        {
            return DocumentState.Bottom;
        }

        try
        {
            var document = reader.IsDBNull(1) ? [] : SqliteSession.Bytes(reader, 1);
            return DocumentStateEncoding.DecodeStored(Base64Url.DecodeFromChars(reader.GetString(0)), document);
        }
        catch (FormatException error)
        {
            throw new InvalidDataException("a stored document state is corrupt", error);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException("a stored document state is corrupt", error);
        }
    }

    // Worker-only: writes the document row of a state an operation at `at` produced, and keeps the state for the end of
    // the request. Returns the version of the write.
    private long Store(string collection, string id, DocumentState state, EntryVersion at, string? body)
    {
        _pending[(collection, id)] = (state, at, body is null ? 0 : StrictUtf8.GetByteCount(body));
        PutRow(collection, id, body);
        return Version(at.Stamp);
    }

    // Worker-only: a document's row in its collection's table, or none when `body` is null. Plain UPDATE, INSERT and
    // DELETE: the change feed reads after-images only.
    private void PutRow(string collection, string id, string? body)
    {
        if (body is null)
        {
            TableCommand(store.IsSystem ? SqliteStatements.SystemDeleteRow : SqliteStatements.DeleteRow, collection, id).ExecuteNonQuery();
            return;
        }

        var update = TableCommand(store.IsSystem ? SqliteStatements.SystemUpdateRow : SqliteStatements.UpdateRow, collection, id);
        update.Parameters.AddWithValue("$body", body);
        if (update.ExecuteNonQuery() == 0)
        {
            var insert = TableCommand(store.IsSystem ? SqliteStatements.SystemInsertRow : SqliteStatements.InsertRow, collection, id);
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
        }
    }

    // The materialization a PUT of `json` stores: canonical JSON, fields in name order.
    private static string Canonical(string json)
    {
        using var parsed = JsonDocument.Parse(json, DocumentState.JsonOptions);
        return Materialize(DocumentState.Bottom.Put(default, parsed.RootElement))!;
    }

    private static string? Materialize(DocumentState state) => state.Materialize() is { } json ? StrictUtf8.GetString(json) : null;

    /// <summary>
    /// Writes the <c>_tb_state</c> row of every document the request changed, then forgets them: the end of one group
    /// commit write (before its savepoint is released) or of a transaction (before it commits).
    /// </summary>
    internal async ValueTask FlushStatesAsync()
    {
        if (_pending.Count > 0)
        {
            await RunAsync(() =>
            {
                FlushStates();
                return true;
            }, CancellationToken.None, mutation: true).ConfigureAwait(false);
        }
    }

    // Worker-only: the _tb_state row of a document, stamped with the operation `at` that last changed it and that
    // operation's node, and its changes feed row. A plain UPDATE, then an INSERT when no row exists: the CDC tailer reads
    // after-images only.
    private void WriteState(string collection, string id, DocumentState state, EntryVersion at, byte[]? stored = null)
    {
        var digest = DocumentStateEncoding.Digest(collection, id, state);
        var update = BodyCommand(SqliteStatements.UpdateState, collection, id);
        Bind(update);
        if (update.ExecuteNonQuery() == 0)
        {
            var insert = BodyCommand(SqliteStatements.InsertState, collection, id);
            Bind(insert);
            insert.Parameters.AddWithValue("$bucket", Bucket(collection, id));
            insert.ExecuteNonQuery();
        }

        AppendFeed(collection, id, at, state.Exists);

        void Bind(SqliteCommand command)
        {
            command.Parameters.AddWithValue("$state", Base64Url.EncodeToString(stored ?? DocumentStateEncoding.EncodeStored(state)));
            command.Parameters.AddWithValue("$stamp", Version(at.Stamp));
            command.Parameters.AddWithValue("$origin", unchecked((long)at.Node.Value));
            command.Parameters.AddWithValue("$live", state.Exists ? 1 : 0);
            command.Parameters.AddWithValue("$h1", unchecked((long)(ulong)(digest >> 64)));
            command.Parameters.AddWithValue("$h2", unchecked((long)(ulong)digest));
        }
    }

    // Worker-only: admits one local state row into this transaction's change group: at most 1024 of them, and 2 MiB of
    // collections, ids, stored states and documents (16,384 and 3 MiB in the system database), which bounds the group's encoded deltas. A transaction holding
    // others is refused with LimitsReached, so group commit moves the write to the next batch.
    private void Capture(string collection, string id, int storedBytes, int bodyBytes)
    {
        var bytes = StrictUtf8.GetByteCount(collection) + StrictUtf8.GetByteCount(id) + (long)storedBytes + bodyBytes;
        var (changes, limit) = store.IsSystem
            ? (IStorageTransaction.MaxSystemGroupChanges, IStorageTransaction.MaxSystemGroupBytes)
            : (IStorageTransaction.MaxChangeGroupChanges, IStorageTransaction.MaxChangeGroupBytes);
        if (store.IsSystem && !_pending.ContainsKey((ManagementCatalog.Collection, ManagementCatalog.VersionId)))
        {
            // Leave room for the immutable dependency the tailer adds, including its encoded state and keys.
            _catalogProofBytes ??= ReadCatalogVersionProof() is { } proof
                ? DocumentStateEncoding.Encode(proof).Length + StrictUtf8.GetByteCount(ManagementCatalog.Collection) + StrictUtf8.GetByteCount(ManagementCatalog.VersionId)
                : 0;
            if (_catalogProofBytes > 0)
            {
                changes--;
                limit -= _catalogProofBytes.Value;
            }
        }

        if (_captured == changes || (_captured > 0 && _changeBytes + bytes > limit))
        {
            LimitsReached = true;
            throw new ChangeGroupCapacityException(
                $"a transaction changes at most {changes} documents and {limit} bytes of documents and their states");
        }

        _captured++;
        _changeBytes += bytes;
    }

    // Worker-only.
    private void FlushStates()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (var ((collection, id), (state, at, bodyBytes)) in _pending)
        {
            var stored = DocumentStateEncoding.EncodeStored(state);
            Capture(collection, id, stored.Length, bodyBytes);
            WriteState(collection, id, state, at, stored);
        }

        _pending.Clear();
    }
}
