using System.Buffers.Text;
using System.Text.Json;
using Turso.Data.Sqlite;
using TerraBase.Engine.Lattices;
using TerraBase.Features.Qos;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction
{

    private readonly Dictionary<(string Collection, string Id), (DocumentState State, EntryVersion At, int BodyBytes, DocumentOwnerProof? BeforeProof, DocumentState? Metadata)> _pending = [];

    private int _captured;

    private LocalReplica Member => store.Member ?? throw new InvalidOperationException("a database writes with this member's replica id");

    private static long Version(Hlc stamp) => checked((long)stamp.Value);

    private long Version(string collection, string id, SqliteDataReader reader, int ordinal) =>
        _pending.TryGetValue((collection, id), out var pending) ? Version(pending.At.Stamp)
            : reader.IsDBNull(ordinal) ? throw StoredDataCorruption.Create("a stored document has no state") : reader.GetInt64(ordinal);

    private (DocumentState State, EntryVersion At) Begin(string collection, string id)
    {
        if (_pending.TryGetValue((collection, id), out var pending))
        {
            return (pending.State, pending.At);
        }

        var state = ReadState(collection, id, localWrite: true);
        var member = Member;
        var stamp = member.Clock.Tick(new Hlc(state.MaxStamp.Value));
        return (state, new EntryVersion(stamp, member.Id));
    }

    private DocumentState ReadState(string collection, string id, QosContext.ResultMemory? memory = null, bool localWrite = false)
    {
        if (!HasTable(collection))
        {
            return DocumentState.Bottom;
        }

        CountRead("point");
        long stateTextBytes;
        long documentBytes;
        using (var sizes = TableCommand(
            store.IsSystem ? TursoStatements.SystemGetStateBytes : TursoStatements.GetStateBytes,
            collection, id).ExecuteReader())
        {
            if (!sizes.Read())
            {
                return DocumentState.Bottom;
            }

            if (sizes.GetInt64(2) != 1)
            {
                throw StoredDataCorruption.Create("a stored document state has non-integer digest columns");
            }

            if (sizes.IsDBNull(0))
            {
                throw StoredDataCorruption.Create("a stored document state has no encoding");
            }

            stateTextBytes = sizes.GetInt64(0);
            documentBytes = sizes.GetInt64(1);
        }

        if (stateTextBytes <= 0 || documentBytes < 0)
        {
            throw StoredDataCorruption.Create("a stored document state has invalid byte lengths");
        }

        var decodedBytes = 3L * (stateTextBytes / 4) + 3;
        var keyBytes = (long)StrictUtf8.GetByteCount(collection) + StrictUtf8.GetByteCount(id);
        if (stateTextBytes > int.MaxValue || documentBytes > int.MaxValue
            || decodedBytes > (long.MaxValue - 2048 - 2 * keyBytes) / 16 - documentBytes)
        {
            throw new StorageBusyException();
        }

        var owner = memory ?? (localWrite && QosContext.Current is not null ? null : _stateReadMemory);
        owner?.Reserve(16 * (decodedBytes + documentBytes) + 2 * keyBytes + 2048,
            store.IsSystem || memory is not null ? null : QosContext.Current);
        store.Checkpoint?.Invoke("state.payload", store.Tenant ?? SystemDatabase.Identity);

        CountRead("point");
        using var reader = TableCommand(store.IsSystem ? TursoStatements.SystemGetState : TursoStatements.GetState, collection, id).ExecuteReader();
        if (!reader.Read())
        {
            throw StoredDataCorruption.Create("a preflighted document state is missing");
        }

        try
        {
            var document = reader.IsDBNull(1) ? [] : TursoSession.Bytes(reader, 1);
            var state = DocumentStateEncoding.DecodeStored(Base64Url.DecodeFromChars(reader.GetString(0)), document);
            var stored = ((UInt128)unchecked((ulong)reader.GetInt64(2)) << 64) | unchecked((ulong)reader.GetInt64(3));
            if (DocumentStateEncoding.Digest(collection, id, state) != stored)
            {
                throw StoredDataCorruption.Create("a stored document state does not match its keyed digest");
            }

            return state;
        }
        catch (FormatException error)
        {
            throw StoredDataCorruption.Create("a stored document state is corrupt", error);
        }
        catch (InvalidDataException error) when (!StoredDataCorruption.Is(error))
        {
            throw StoredDataCorruption.Create("a stored document state is corrupt", error);
        }
    }

    private long Store(string collection, string id, DocumentState state, EntryVersion at, string? body, DocumentState? previous = null, DocumentState? metadata = null)
    {
        var beforeProof = _pending.TryGetValue((collection, id), out var pending) ? pending.BeforeProof
            : previous is not null && metadata is not null && !store.IsSystem && !IsMetadata(collection)
                ? OwnerProof(previous, metadata) : null;
        _pending[(collection, id)] = (state, at, body is null ? 0 : StrictUtf8.GetByteCount(body), beforeProof, metadata);
        PutRow(collection, id, body);
        return Version(at.Stamp);
    }

    private void PutRow(string collection, string id, string? body)
    {
        if (store.IsSystem || IsMetadata(collection))
        {
            PutUncountedRow(collection, id, body);
            return;
        }

        PutTextRows(collection, id, body);
        PutVectorRows(collection, id, body);
        var before = TableCommand(TursoStatements.RowBytes, collection, id).ExecuteScalar() as long?;
        (long Documents, long Bytes) delta;
        if (body is null)
        {
            if (before is not { } removed)
            {
                return;
            }

            TableCommand(TursoStatements.DeleteRow, collection, id).ExecuteNonQuery();
            delta = (-1, -removed);
        }
        else
        {
            var command = TableCommand(before is null ? TursoStatements.InsertRow : TursoStatements.UpdateRow, collection, id);
            command.Parameters.AddWithValue("$body", body);
            command.ExecuteNonQuery();
            var bytes = StrictUtf8.GetByteCount(body);
            delta = before is { } replaced ? (0, bytes - replaced) : (1, bytes);
        }

        if (delta != default)
        {
            var add = Command(TursoStatements.AddCollectionStats, collection);
            add.Parameters.AddWithValue("$documents", delta.Documents);
            add.Parameters.AddWithValue("$bytes", delta.Bytes);
            add.ExecuteNonQuery();
        }
    }

    private void PutUncountedRow(string collection, string id, string? body)
    {
        if (body is null)
        {
            TableCommand(store.IsSystem ? TursoStatements.SystemDeleteRow : TursoStatements.DeleteRow, collection, id).ExecuteNonQuery();
            return;
        }

        var update = TableCommand(store.IsSystem ? TursoStatements.SystemUpdateRow : TursoStatements.UpdateRow, collection, id);
        update.Parameters.AddWithValue("$body", body);
        if (update.ExecuteNonQuery() == 0)
        {
            var insert = TableCommand(store.IsSystem ? TursoStatements.SystemInsertRow : TursoStatements.InsertRow, collection, id);
            insert.Parameters.AddWithValue("$body", body);
            insert.ExecuteNonQuery();
        }
    }

    private static string Canonical(string json)
    {
        using var parsed = JsonDocument.Parse(json, DocumentState.JsonOptions);
        return Materialize(DocumentState.Bottom.Put(default, parsed.RootElement))!;
    }

    private static string? Materialize(DocumentState state) => state.Materialize() is { } json ? StrictUtf8.GetString(json) : null;

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

    private void WriteState(string collection, string id, DocumentState state, EntryVersion at, byte[]? stored = null, DocumentOwnerProof? beforeProof = null, DocumentState? metadata = null)
    {
        store.CollectionStateWritten?.Invoke(collection);
        metadata ??= !store.IsSystem && !IsMetadata(collection) ? CollectionState(collection) : DocumentState.Bottom;
        var generation = CollectionGeneration(metadata);

        if (generation is not null && (KnownCollection(collection)?.Id
                ?? Command("SELECT entity_id FROM collections WHERE name = $collection;", collection).ExecuteScalar() as string) != generation)
        {
            generation = null;
        }
        var afterProof = generation is null ? null : OwnerProof(state, metadata);
        if (generation is null)
        {
            beforeProof = null;
        }

        var digest = DocumentStateEncoding.Digest(collection, id, state);

        var update = BodyCommand(TursoStatements.UpdateState, collection, id);
        Bind(update);
        if (update.ExecuteNonQuery() == 0)
        {
            var insert = BodyCommand(TursoStatements.InsertState, collection, id);
            Bind(insert);
            insert.ExecuteNonQuery();
        }

        AppendFeed(collection, id, at, state.Exists, beforeProof, afterProof, generation);

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

    private void Capture(string collection, string id, int storedBytes, int bodyBytes)
    {
        var bytes = StrictUtf8.GetByteCount(collection) + StrictUtf8.GetByteCount(id) + (long)storedBytes + bodyBytes;
        var (changes, limit) = store.IsSystem
            ? (IStorageTransaction.MaxSystemGroupChanges, IStorageTransaction.MaxSystemGroupBytes)
            : (IStorageTransaction.MaxChangeGroupChanges, IStorageTransaction.MaxChangeGroupBytes);
        if (_captured == changes || (_captured > 0 && _changeBytes + bytes > limit))
        {
            LimitsReached = true;
            throw new ChangeGroupCapacityException(
                $"a transaction changes at most {changes} documents and {limit} bytes of documents and their states");
        }

        _captured++;
        _changeBytes += bytes;
    }

    private void FlushStates()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        foreach (var ((collection, id), (state, at, bodyBytes, beforeProof, metadata)) in _pending)
        {
            var stored = DocumentStateEncoding.EncodeStored(state);
            Capture(collection, id, stored.Length, bodyBytes);
            var currentMetadata = _pending.TryGetValue((DocumentDelta.Collections, collection), out var pendingMetadata) ? pendingMetadata.State
                : metadata;
            WriteState(collection, id, state, at, stored, beforeProof, currentMetadata);
        }

        _pending.Clear();
    }
}
