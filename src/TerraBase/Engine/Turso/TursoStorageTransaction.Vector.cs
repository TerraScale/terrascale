using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

/// <remarks>
/// A vector index keeps one sign bit per dimension of each document's vector (positive = 1), packed 128 slots to a chunk
/// row, so a near query reads the whole index in a few hundred rows. The closest codes by Hamming distance become the
/// candidates of the ordinary near statement, which reranks them exactly with <c>tb_near</c> (docs/terrabase/vector-index.md).
/// Turso 0.8.2 has no dense vector index method; its <c>toy_vector_sparse_ivf</c> serves sparse Jaccard only.
/// </remarks>
internal sealed partial class TursoStorageTransaction
{
    private const int SlotsPerChunk = 128;
    private const int ChunkBitmapBytes = SlotsPerChunk / 8;

    // Exact reranking reads this many candidates at least; a filtered query reads more, since the filter runs after them.
    private const int MinVectorCandidates = 400;
    private const int FilteredVectorCandidates = 4096;

    // Existing documents added per background write transaction.
    private const int VectorBuildBatch = 256;

    private static string VectorSlots(string name) => $"\"_tb_vec_{name}\"";

    private static string VectorChunks(string name) => $"\"_tb_vec_{name}__codes\"";

    private static int CodeBytes(int dimensions) => (dimensions + 63) / 64 * 8;

    // Existing documents are added per batch (BuildVectorIndexAsync); `built` is the last id added, NULL once complete.
    private List<IndexDefinition> ReadVectorIndexes(string? collection = null)
    {
        var indexes = new List<IndexDefinition>();
        var command = VectorCommand("SELECT name, collection, field, dimensions, built IS NOT NULL FROM _tb_vector_indexes WHERE $collection IS NULL OR collection = $collection;");
        command.Parameters.AddWithValue("$collection", (object?)collection ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            indexes.Add(new IndexDefinition(reader.GetString(0), reader.GetString(1), [DocumentField.TryParse(reader.GetString(2), out var field)
                ? field : throw new InvalidDataException("invalid stored vector index field")], "vector", (int)reader.GetInt64(3), reader.GetInt64(4) != 0));
        }

        return indexes;
    }

    // Documents written from now on are indexed with their writes; the existing ones in background batches.
    private void CreateVectorIndex(IndexDefinition index)
    {
        if (index.Fields is not [{ IsMetadata: false } field] || index.Dimensions is < 1 or > 4096)
        {
            throw new ArgumentException("a vector index has one member path and 1 to 4096 dimensions", nameof(index));
        }

        using var create = PlainCommand($"""
            CREATE TABLE {VectorSlots(index.Name)} (slot INTEGER PRIMARY KEY, id TEXT NOT NULL UNIQUE, vector BLOB NOT NULL);
            CREATE TABLE {VectorChunks(index.Name)} (chunk INTEGER PRIMARY KEY, codes BLOB NOT NULL);
            INSERT INTO _tb_vector_indexes (name, collection, field, dimensions, built)
                SELECT $name, $collection, $field, $dimensions, CASE WHEN EXISTS (SELECT 1 FROM {Table(index.Collection)}) THEN '' END;
            """);
        create.Parameters.AddWithValue("$name", index.Name);
        create.Parameters.AddWithValue("$collection", index.Collection);
        create.Parameters.AddWithValue("$field", field.Path);
        create.Parameters.AddWithValue("$dimensions", (long)index.Dimensions);
        create.ExecuteNonQuery();
    }

    public ValueTask<bool> BuildVectorIndexAsync(CancellationToken cancellationToken) =>
        RunAsync(() =>
        {
            ThrowIfReadOnly();
            if (store.IsSystem || ReadVectorIndexes().Find(index => index.Building) is not { } index)
            {
                return false;
            }

            var cursor = VectorCommand("SELECT built FROM _tb_vector_indexes WHERE name = $name;");
            cursor.Parameters.AddWithValue("$name", index.Name);
            var after = (string)cursor.ExecuteScalar()!;
            var page = new List<(string Id, byte[] Json)>();
            var documents = VectorCommand($"SELECT id, doc FROM {Table(index.Collection)} WHERE id > $after ORDER BY id LIMIT {VectorBuildBatch};");
            {
                documents.Parameters.AddWithValue("$after", after);
                using var reader = documents.ExecuteReader();
                while (reader.Read())
                {
                    page.Add((reader.GetString(0), TursoSession.Bytes(reader, 1)));
                }
            }

            // Not max(slot): Turso scans the table for an aggregate, but walks the rowid tree from its end here.
            var next = VectorCommand($"SELECT slot + 1 FROM {VectorSlots(index.Name)} ORDER BY slot DESC LIMIT 1;");
            var slot = next.ExecuteScalar() as long? ?? 0;
            var path = Segments(index.Fields[0]);
            var code = new byte[CodeBytes(index.Dimensions)];
            var chunks = new Dictionary<long, byte[]>();
            var known = VectorCommand($"SELECT 1 FROM {VectorSlots(index.Name)} WHERE id = $id;");
            var insert = VectorCommand($"INSERT INTO {VectorSlots(index.Name)} (slot, id, vector) VALUES ($slot, $id, $vector);");
            foreach (var (id, json) in page)
            {
                cancellationToken.ThrowIfCancellationRequested();
                known.Parameters.Clear();
                known.Parameters.AddWithValue("$id", id);
                // A document written since the index was created is in it already.
                if (VectorValues(json, path, index.Dimensions) is not { } values || known.ExecuteScalar() is not null)
                {
                    continue;
                }

                SignCode(values, code);
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("$slot", slot);
                insert.Parameters.AddWithValue("$id", id);
                insert.Parameters.AddWithValue("$vector", values);
                insert.ExecuteNonQuery();
                if (!chunks.TryGetValue(slot / SlotsPerChunk, out var chunk))
                {
                    chunks[slot / SlotsPerChunk] = chunk = ReadChunk(index.Name, slot / SlotsPerChunk, code.Length);
                }

                SetSlot(chunk, slot++, code);
            }

            foreach (var (number, chunk) in chunks)
            {
                WriteChunk(index.Name, number, chunk);
            }

            var advance = VectorCommand("UPDATE _tb_vector_indexes SET built = $built WHERE name = $name;");
            advance.Parameters.AddWithValue("$name", index.Name);
            advance.Parameters.AddWithValue("$built", page.Count < VectorBuildBatch ? DBNull.Value : page[^1].Id);
            advance.ExecuteNonQuery();
            return ReadVectorIndexes().Exists(other => other.Building);
        }, cancellationToken, mutation: true);

    private byte[] ReadChunk(string name, long chunk, int codeBytes)
    {
        var read = VectorCommand($"SELECT codes FROM {VectorChunks(name)} WHERE chunk = $chunk;");
        read.Parameters.AddWithValue("$chunk", chunk);
        return read.ExecuteScalar() as byte[] ?? new byte[ChunkBitmapBytes + (SlotsPerChunk * codeBytes)];
    }

    // Occupies the slot with the code, or frees it when the code is null.
    private static void SetSlot(byte[] chunk, long slot, byte[]? code)
    {
        var bit = (byte)(1 << (int)(slot % 8));
        ref var used = ref chunk[(int)(slot % SlotsPerChunk) / 8];
        used = code is null ? (byte)(used & ~bit) : (byte)(used | bit);
        code?.CopyTo(Code(chunk, slot));
    }

    private void DropVectorIndex(string name)
    {
        using var drop = PlainCommand($"DROP TABLE {VectorSlots(name)}; DROP TABLE {VectorChunks(name)}; DELETE FROM _tb_vector_indexes WHERE name = $name;");
        drop.Parameters.AddWithValue("$name", name);
        drop.ExecuteNonQuery();
        _statisticsChanged = true;
    }

    // Worker-only, beside the document row: a document without a vector of the index's length leaves the index.
    private void PutVectorRows(string collection, string id, string? body)
    {
        foreach (var index in ReadVectorIndexes(collection))
        {
            var code = new byte[CodeBytes(index.Dimensions)];
            var values = body is null ? null : VectorValues(Encoding.UTF8.GetBytes(body), Segments(index.Fields[0]), index.Dimensions);
            var find = VectorCommand($"SELECT slot FROM {VectorSlots(index.Name)} WHERE id = $id;");
            find.Parameters.AddWithValue("$id", id);
            var slot = find.ExecuteScalar() as long?;
            if (slot is null && values is null)
            {
                continue;
            }

            var change = VectorCommand(values is null ? $"DELETE FROM {VectorSlots(index.Name)} WHERE id = $id;"
                : slot is null ? $"INSERT INTO {VectorSlots(index.Name)} (id, vector) VALUES ($id, $vector);"
                : $"UPDATE {VectorSlots(index.Name)} SET vector = $vector WHERE id = $id;");
            change.Parameters.AddWithValue("$id", id);
            change.Parameters.AddWithValue("$vector", (object?)values ?? DBNull.Value);
            change.ExecuteNonQuery();
            slot ??= find.ExecuteScalar() as long?;
            if (values is not null)
            {
                SignCode(values, code);
            }

            var chunk = ReadChunk(index.Name, slot!.Value / SlotsPerChunk, code.Length);
            SetSlot(chunk, slot.Value, values is null ? null : code);
            WriteChunk(index.Name, slot.Value / SlotsPerChunk, chunk);
        }
    }

    private void WriteChunk(string name, long chunk, byte[] codes)
    {
        var write = VectorCommand($"INSERT INTO {VectorChunks(name)} (chunk, codes) VALUES ($chunk, $codes) ON CONFLICT (chunk) DO UPDATE SET codes = excluded.codes;");
        write.Parameters.AddWithValue("$chunk", chunk);
        write.Parameters.AddWithValue("$codes", codes);
        write.ExecuteNonQuery();
    }

    /// <summary>
    /// The ids of the documents whose codes are closest to a near query's, as a JSON array for the near statement to rerank;
    /// <see langword="null"/> when no vector index of the collection covers the query's path and length, or the read is
    /// owner-scoped, or the index is still building, so the statement scans the collection instead.
    /// </summary>
    private string? VectorCandidates(DocumentQuery query, CancellationToken cancellationToken)
    {
        if (query.Near is not { Point: null } near || store.IsSystem || query.Filter is AuthorizedFilter
            || ReadVectorIndexes(query.Collection).Find(index => !index.Building && index.Dimensions == near.Vector.Count
                && index.Fields[0].Path.Split('.').SequenceEqual(near.Path.Segments!, StringComparer.Ordinal)) is not { } index)
        {
            return null;
        }

        var target = new byte[CodeBytes(index.Dimensions)];
        for (var i = 0; i < near.Vector.Count; i++)
        {
            target[i / 8] |= (byte)((float)near.Vector[i] > 0 ? 1 << (i % 8) : 0);
        }

        var wanted = query.Filter is null ? Math.Max(MinVectorCandidates, 4 * query.Limit) : FilteredVectorCandidates;
        var closest = new PriorityQueue<long, int>(wanted + 1, Comparer<int>.Create(static (left, right) => right.CompareTo(left)));
        var words = MemoryMarshal.Cast<byte, ulong>(target);
        var chunks = VectorCommand($"SELECT chunk, codes FROM {VectorChunks(index.Name)};");
        {
            CountRead("query");
            using var reader = chunks.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                session.ThrowIfStopped();
                if (_queryBudget!.Row())
                {
                    throw new QueryCapacityException("query execution budget exceeded");
                }

                var first = reader.GetInt64(0) * SlotsPerChunk;
                var codes = (byte[])reader.GetValue(1);
                for (var i = 0; i < SlotsPerChunk; i++)
                {
                    if ((codes[i / 8] & (1 << (i % 8))) == 0)
                    {
                        continue;
                    }

                    var code = MemoryMarshal.Cast<byte, ulong>(codes.AsSpan(ChunkBitmapBytes + (i * target.Length), target.Length));
                    var distance = 0;
                    for (var w = 0; w < words.Length; w++)
                    {
                        distance += BitOperations.PopCount(code[w] ^ words[w]);
                    }

                    if (closest.Count < wanted)
                    {
                        closest.Enqueue(first + i, distance);
                    }
                    else if (closest.TryPeek(out _, out var farthest) && distance < farthest)
                    {
                        closest.EnqueueDequeue(first + i, distance);
                    }
                }
            }
        }

        var slots = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(slots))
        {
            writer.WriteStartArray();
            foreach (var (slot, _) in closest.UnorderedItems)
            {
                writer.WriteNumberValue(slot);
            }

            writer.WriteEndArray();
        }

        // Exact distances from the stored float32 values; the near statement then reads only the documents it returns.
        var exact = new NearDistance(near);
        var ranked = new List<(string Id, double Distance)>(closest.Count);
        var lookup = VectorCommand($"SELECT v.id, v.vector FROM json_each($slots) AS s CROSS JOIN {VectorSlots(index.Name)} AS v ON v.slot = s.value;");
        {
            lookup.Parameters.AddWithValue("$slots", Encoding.UTF8.GetString(slots.WrittenSpan));
            using var reader = lookup.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var measured = exact.Measure((byte[])reader.GetValue(1));
                if (!double.IsPositiveInfinity(measured))
                {
                    ranked.Add((reader.GetString(0), measured));
                }
            }
        }

        // Without a filter the nearest `limit` are the answer; a filter runs in the statement, over every candidate.
        ranked.Sort(static (left, right) => left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : string.CompareOrdinal(left.Id, right.Id));
        var ids = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(ids))
        {
            writer.WriteStartArray();
            foreach (var (id, _) in query.Filter is null ? ranked.Take(query.Limit) : ranked)
            {
                writer.WriteStringValue(id);
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(ids.WrittenSpan);
    }

    // Kept on the session like a collection's table statements: one text per index and statement.
    private SqliteCommand VectorCommand(string sql) => session.TableCommand(sql, transaction);

    private static Span<byte> Code(byte[] chunk, long slot)
    {
        var bytes = (chunk.Length - ChunkBitmapBytes) / SlotsPerChunk;
        return chunk.AsSpan(ChunkBitmapBytes + ((int)(slot % SlotsPerChunk) * bytes), bytes);
    }

    private static byte[][] Segments(DocumentField field) => [.. field.Path.Split('.').Select(Encoding.UTF8.GetBytes)];

    // The numeric array at the path as little-endian float32 values, when it holds exactly `dimensions` finite ones.
    private static byte[]? VectorValues(byte[] json, byte[][] path, int dimensions)
    {
        var values = new byte[4 * dimensions];
        try
        {
            var reader = new Utf8JsonReader(json);
            if (!NearDistance.Seek(ref reader, path) || reader.TokenType != JsonTokenType.StartArray)
            {
                return null;
            }

            var count = 0;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.Number || count == dimensions || (float)reader.GetDouble() is var value && !float.IsFinite(value))
                {
                    return null;
                }

                BinaryPrimitives.WriteSingleLittleEndian(values.AsSpan(4 * count++), value);
            }

            return count == dimensions ? values : null;
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            return null;
        }
    }

    // One bit per dimension, set for a positive value.
    private static void SignCode(ReadOnlySpan<byte> values, Span<byte> code)
    {
        code.Clear();
        for (var i = 0; i < values.Length / 4; i++)
        {
            code[i / 8] |= (byte)(BinaryPrimitives.ReadSingleLittleEndian(values[(4 * i)..]) > 0 ? 1 << (i % 8) : 0);
        }
    }
}
