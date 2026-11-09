using System.Globalization;
using System.Text;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;
using static TerraBase.Engine.Turso.TursoShapeReader;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeStorageTransaction, IShapeTableStorage
{
    private const int ShapeBodyBytes = 1024 * 1024;
    private const int ShapeCommitBytes = 8 * 1024 * 1024;

    private const string ShapeTablePrefix = "~table:";

    public ValueTask<ShapeTableState> ReadShapeTableAsync(string table, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeTable(table), cancellationToken);

    public async ValueTask<ShapeTableState> RenewShapeTableAsync(string table, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var state = ShapeTable(table);
            if (state.Status != ShapeTableStatus.Stale)
            {
                return state;
            }

            var cookie = SchemaCookie();
            if (StoredShapeTable(table) is { } stored && stored.Sql == state.Sql && !SchemaChangedSince(table, stored.Since))
            {
                var touch = Command("UPDATE _tb_shape_chunk SET sealed_at = $cookie WHERE handle = $key AND offset = 0;");
                touch.Parameters.AddWithValue("$cookie", cookie);
                touch.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
                touch.ExecuteNonQuery();
                touch.Parameters.Clear();
                return state with { Status = ShapeTableStatus.Current, Generation = stored.Generation, Since = stored.Since };
            }

            var generation = EntityIds.New().ToString();
            var since = ShapeHead();
            RetireShapeTable(table, null);
            var upsert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $since, $body, $cookie);");
            upsert.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
            upsert.Parameters.AddWithValue("$body", Encoding.UTF8.GetBytes(generation + "\n" + state.Sql));
            upsert.Parameters.AddWithValue("$since", since);
            upsert.Parameters.AddWithValue("$cookie", cookie);
            upsert.ExecuteNonQuery();
            upsert.Parameters.Clear();
            return state with { Status = ShapeTableStatus.Current, Generation = generation, Since = since };
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    private TursoShapeReader ShapeReader => new(sql => Command(sql), sql => session.TableCommand(sql, transaction), store.Tenant, path => CountRead(path));

    private ShapeTableState ShapeTable(string table) => ShapeReader.ShapeTable(table);

    private (string Sql, string Generation, long Since, long Cookie)? StoredShapeTable(string table) => ShapeReader.StoredShapeTable(table);

    private long SchemaCookie() => ShapeReader.SchemaCookie();

    private bool SchemaChangedSince(string table, long since) => ShapeReader.SchemaChangedSince(table, since);

    private (string[] Columns, long Since)? CurrentShapeColumns(string table, string generation) => ShapeReader.CurrentShapeColumns(table, generation);

    private long ShapeHead() => ShapeReader.ShapeHead();

    private bool HasChangeTable() => ShapeReader.HasChangeTable();

    private bool ShapeBoundary(long offset) => ShapeReader.ShapeBoundary(offset);

    public ValueTask<ShapeFilterResult> ReadShapeFilterAsync(string table, string generation, ShapeQuery query, CancellationToken cancellationToken) =>
        RunAsync(() => ShapeReader.ReadFilter(table, generation, query), cancellationToken);

    private void RetireShapeTable(string table, string? generation)
    {
        if (generation is not null && StoredShapeTable(table)?.Generation != generation)
        {
            return;
        }

        var retire = Command("DELETE FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        retire.Parameters.AddWithValue("$key", ShapeTablePrefix + table);
        retire.ExecuteNonQuery();
        retire.Parameters.Clear();
    }

    public async ValueTask<(long Offset, long Head, ShapeSealedChunk? Chunk)> SealShapeChunkAsync(ShapeSealDefinition shape, long offset, long? through,
        IMemoryBudget memory, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ShapeTables.Validate(shape);
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() => SealShapeChunk(shape, offset, through, memory, cancellationToken), cancellationToken,
            mutation: true).ConfigureAwait(false);
    }

    private (long Offset, long Head, ShapeSealedChunk? Chunk) SealShapeChunk(ShapeSealDefinition shape, long offset, long? through,
        IMemoryBudget memory, CancellationToken cancellationToken)
    {
        var head = ShapeHead();
        var stored = Command("SELECT end_offset FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
        stored.Parameters.AddWithValue("$handle", shape.Handle);
        stored.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        if (stored.ExecuteScalar() is long existing)
        {
            return (existing, head, null);
        }

        if (offset == head)
        {
            return (offset, head, null);
        }

        var fresh = ShapeReader.ReadChanges(shape, offset, through, memory, cancellationToken, fresh: false);
        try
        {
            var covered = long.Parse(fresh.NextOffset, CultureInfo.InvariantCulture);
            PublishShapeLineage(shape.Handle, shape.Table, shape.Generation, shape.Incarnation);
            var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($handle, $offset, $end, $body, $at);");
            insert.Parameters.AddWithValue("$handle", shape.Handle);
            insert.Parameters.AddWithValue("$offset", offset);
            insert.Parameters.AddWithValue("$end", covered);
            insert.Parameters.AddWithValue("$body", fresh.Json.ToArray());
            var sealedAt = store.HostTime.GetUtcNow().ToUnixTimeMilliseconds();
            insert.Parameters.AddWithValue("$at", sealedAt);
            insert.ExecuteNonQuery();
            insert.Parameters.Clear();
            return (covered, head, new ShapeSealedChunk(offset, covered, covered, fresh.Json, (IMemoryReservation)fresh.Lease!, sealedAt));
        }
        catch
        {
            fresh.Lease?.Dispose();
            throw;
        }
    }

    public ValueTask<ShapeSealedChunk?> ReadShapeChunkAsync(string handle, long offset, IMemoryBudget memory,
        CancellationToken cancellationToken) => RunAsync(() =>
    {
        var size = Command("SELECT octet_length(body) FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
        size.Parameters.AddWithValue("$handle", handle);
        size.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        if (size.ExecuteScalar() is not long bytes)
        {
            return null;
        }

        if (bytes is <= 0 or > ShapeCommitBytes + ShapeBodyBytes)
        {
            throw new InvalidDataException("a sealed shape chunk exceeds its body bound");
        }

        var admission = memory.TryReserve(checked(bytes + 4096), store.Tenant) ?? throw new QosMemoryException();
        try
        {
            var command = Command("SELECT end_offset, body, sealed_at FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
            command.Parameters.AddWithValue("$handle", handle);
            command.Parameters.AddWithValue("$offset", offset);
            CountRead("shape_body");
            using var reader = command.ExecuteReader();
            if (!reader.Read())
            {
                throw new InvalidDataException("a sealed shape chunk disappeared within a read transaction");
            }

            var end = reader.GetInt64(0);
            return new ShapeSealedChunk(offset, end, end, TursoSession.Bytes(reader, 1), admission, reader.GetInt64(2));
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }, cancellationToken);

}

internal sealed class TursoShapeStorage(TursoStorageEngine engine) : IShapeStorage
{
    public async ValueTask<IStorageTransaction> BeginTransactionAsync(IDatabaseStore store, bool write, CancellationToken cancellationToken)
    {
        var underlying = store.Tenant is { } tenant
            ? await engine.OpenDatabaseAsync(tenant, cancellationToken).ConfigureAwait(false)
            : throw new InvalidOperationException("shapes belong to client databases");
        if (underlying is null || underlying.Incarnation != store.Incarnation || store.IsClosed)
        {
            throw new ShapeSealOffsetException();
        }

        return await underlying.BeginTransactionAsync(write, cancellationToken).ConfigureAwait(false);
    }
}
