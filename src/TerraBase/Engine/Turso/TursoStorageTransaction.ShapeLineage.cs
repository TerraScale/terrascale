using System.Buffers;
using System.Globalization;
using System.Text;
using TerraBase.Features.Shapes;
using TerraBase.Primitives;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeSweepTransaction
{
    private const string ShapeLineagePrefix = "~lineage:";
    private const int LineageBodyBytes = 256;
    private const int ShapeKeyChars = 64;
    private const string Sweep = "shape_sweep";
    private static readonly SearchValues<char> ShapeHandleHex = SearchValues.Create("0123456789abcdef");

    private void PublishShapeLineage(string handle, string table, string generation, Ulid incarnation)
    {
        var expected = ShapeLineageBody(incarnation, generation, table);
        if (CurrentShapeLineage(table, "shape") is not { } current || !current.AsSpan().SequenceEqual(expected))
        {
            throw new ShapeSealOffsetException();
        }

        var lineage = Command("SELECT body FROM _tb_shape_chunk WHERE handle = $key AND offset = 0 AND octet_length(body) <= $max;");
        lineage.Parameters.AddWithValue("$key", ShapeLineagePrefix + handle);
        lineage.Parameters.AddWithValue("$max", LineageBodyBytes);
        CountRead("shape");
        if (lineage.ExecuteScalar() is byte[] stored)
        {
            if (!stored.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException("a shape handle's lineage record names another lineage");
            }

            return;
        }

        var insert = Command("INSERT INTO _tb_shape_chunk (handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, 0, $body, 0);");
        insert.Parameters.AddWithValue("$key", ShapeLineagePrefix + handle);
        insert.Parameters.AddWithValue("$body", expected);
        insert.ExecuteNonQuery();
    }

    private byte[]? CurrentShapeLineage(string table, string path)
    {
        CountRead(path);
        if (ShapeTable(table) is not { Status: ShapeTableStatus.Current, Generation: { } generation })
        {
            return null;
        }

        CountRead(path);
        return Command(TursoStatements.Incarnation).ExecuteScalar() is string incarnation && EntityIds.IsValid(incarnation)
            ? ShapeLineageBody(Ulid.Parse(incarnation), generation, table)
            : throw new InvalidDataException("the database has no valid incarnation");
    }

    private static byte[] ShapeLineageBody(Ulid incarnation, string generation, string table) =>
        Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture, $"2 {incarnation} {generation} {table}"));

    public ValueTask<IReadOnlyList<string>> ListShapeHandlesAsync(string? after, int limit, CancellationToken cancellationToken) =>
        RunAsync<IReadOnlyList<string>>(() =>
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
            List<string> handles = [];
            var cursor = after ?? "";
            while (handles.Count < limit)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? next = null;
                foreach (var candidate in (ReadOnlySpan<string?>)[
                    NextShapeKey("_tb_shape_chunk", "", cursor, "~"), NextShapeKey("_tb_shape_chunk", ShapeFloorPrefix, cursor, "~floor;"),
                    NextShapeKey("_tb_shape_chunk", ShapeLineagePrefix, cursor, "~lineage;"),
                    NextShapeKey("_tb_shape_chunk", "", string.CompareOrdinal(cursor, "\u007f") > 0 ? cursor : "\u007f", null),
                    NextShapeKey("_tb_shape_snapshot", "", cursor, null)])
                {
                    if (candidate is not null && (next is null || string.CompareOrdinal(candidate, next) < 0))
                    {
                        next = candidate;
                    }
                }

                if (next is null || string.CompareOrdinal(next, cursor) <= 0)
                {
                    break;
                }

                handles.Add(next);
                cursor = next;
            }

            return handles;
        }, cancellationToken);

    private string? NextShapeKey(string table, string prefix, string after, string? below)
    {
        var command = Command(below is null
            ? $"SELECT substr(handle, $skip, $chars), length(handle) FROM {table} WHERE handle > $low ORDER BY handle LIMIT 1;"
            : $"SELECT substr(handle, $skip, $chars), length(handle) FROM {table} WHERE handle > $low AND handle < $high ORDER BY handle LIMIT 1;");
        command.Parameters.AddWithValue("$skip", prefix.Length + 1);
        command.Parameters.AddWithValue("$chars", ShapeKeyChars);
        command.Parameters.AddWithValue("$low", prefix + after);
        if (below is not null)
        {
            command.Parameters.AddWithValue("$high", below);
        }

        CountRead(Sweep);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var key = reader.GetString(0);
        return reader.GetInt64(1) - prefix.Length > ShapeKeyChars ? key + "\U0010FFFF" : key;
    }

    public async ValueTask<ShapeSweepStep> SweepShapeHandleAsync(string handle, long before, int maxRows, long maxBytes, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        return await RunAsync(() =>
        {
            var state = ClassifyShapeHandle(handle);
            if (state == ShapeHandleState.Current)
            {
                var (rows, bytes, floor, more) = TrimShape(handle, before, maxRows, maxBytes, cancellationToken);
                return new ShapeSweepStep(state, rows, bytes, floor, rows > 0 || more);
            }

            if (state != ShapeHandleState.Retired)
            {
                return new ShapeSweepStep(state, 0, 0, 0, false);
            }

            var (deleted, dropped) = DropShape(handle, maxRows, maxBytes, cancellationToken);
            var remains = Command("""
                SELECT EXISTS (SELECT 1 FROM _tb_shape_chunk WHERE handle IN ($handle, $floor, $lineage))
                    OR EXISTS (SELECT 1 FROM _tb_shape_snapshot WHERE handle = $handle);
                """);
            remains.Parameters.AddWithValue("$handle", handle);
            remains.Parameters.AddWithValue("$floor", ShapeFloorPrefix + handle);
            remains.Parameters.AddWithValue("$lineage", ShapeLineagePrefix + handle);
            CountRead(Sweep);
            return new ShapeSweepStep(state, deleted, dropped, 0, (long)remains.ExecuteScalar()! != 0);
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    private ShapeHandleState ClassifyShapeHandle(string handle)
    {
        if (handle.Length != 19 || !handle.StartsWith("h1-", StringComparison.Ordinal) || handle.AsSpan(3).ContainsAnyExcept(ShapeHandleHex))
        {
            return ShapeHandleState.Unknown;
        }

        var command = Command("SELECT CASE WHEN octet_length(body) <= $max THEN body END, end_offset, sealed_at FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        command.Parameters.AddWithValue("$key", ShapeLineagePrefix + handle);
        command.Parameters.AddWithValue("$max", LineageBodyBytes);
        byte[] body;
        CountRead(Sweep);
        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
            {
                return ShapeHandleState.Unclassified;
            }

            if (reader.IsDBNull(0) || reader.GetInt64(1) != 0 || reader.GetInt64(2) != 0)
            {
                return ShapeHandleState.Malformed;
            }

            body = TursoSession.Bytes(reader, 0);
        }

        if (body.AsSpan().ContainsAnyExceptInRange((byte)' ', (byte)'~') || Encoding.ASCII.GetString(body).Split(' ') is not ["2", var incarnation, var generation, var table]
            || !EntityIds.IsValid(incarnation) || !EntityIds.IsValid(generation) || !ShapeTables.IsValidName(table))
        {
            return ShapeHandleState.Malformed;
        }

        return CurrentShapeLineage(table, Sweep) is { } current && current.AsSpan().SequenceEqual(body) ? ShapeHandleState.Current : ShapeHandleState.Retired;
    }
}
