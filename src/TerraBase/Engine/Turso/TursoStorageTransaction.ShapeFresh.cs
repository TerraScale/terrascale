using System.Text.Json;
using TerraBase.Features.Memory;
using TerraBase.Features.Qos;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : IShapeFreshStorage
{

    private const long FreshEnvelopeBytes = 32 * 1024;

    public ValueTask<long> ReadShapeHeadAsync(CancellationToken cancellationToken) => RunAsync(() =>
    {
        CountRead("shape");
        return (long)Command("SELECT seq FROM _tb_feed_sequence WHERE id = 1;").ExecuteScalar()!;
    }, cancellationToken);

    public ValueTask<ShapeBody?> ReadShapeFreshAsync(ShapeSealDefinition shape, long offset, IMemoryBudget memory,
        CancellationToken cancellationToken) => RunAsync(() => ShapeFresh(shape, offset, memory, cancellationToken), cancellationToken);

    private ShapeBody? ShapeFresh(ShapeSealDefinition shape, long offset, IMemoryBudget memory, CancellationToken cancellationToken)
    {
        var head = ShapeFeedHead();
        if (offset > head)
        {
            return null;
        }

        if (offset == head)
        {
            var empty = memory.TryReserve(MemoryLane.Foreground, FreshEnvelopeBytes, store.Tenant) ?? throw new QosMemoryException();
            try
            {
                var body = ShapeEnvelope(shape, offset, head, head, ShapeResume(shape.ResumeScope.Span, head, cancellationToken), [], upToDate: true);
                return new ShapeBody(body, ShapeNumber(head), true, empty);
            }
            catch
            {
                empty.Dispose();
                throw;
            }
        }

        var size = Command("SELECT octet_length(body) FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
        size.Parameters.AddWithValue("$handle", shape.Handle);
        size.Parameters.AddWithValue("$offset", offset);
        CountRead("shape");
        if (size.ExecuteScalar() is not long bytes)
        {
            return null;
        }

        if (bytes is <= 0 or > ShapeCommitBytes)
        {
            throw new InvalidDataException("a sealed shape chunk exceeds its body bound");
        }

        var admission = memory.TryReserve(MemoryLane.Foreground, checked(bytes * 3 + FreshEnvelopeBytes), store.Tenant) ?? throw new QosMemoryException();
        try
        {
            var command = Command("SELECT end_offset, body FROM _tb_shape_chunk WHERE handle = $handle AND offset = $offset;");
            command.Parameters.AddWithValue("$handle", shape.Handle);
            command.Parameters.AddWithValue("$offset", offset);
            CountRead("shape_body");
            long end;
            byte[] stored;
            using (var reader = command.ExecuteReader())
            {
                if (!reader.Read())
                {
                    throw new InvalidDataException("a sealed shape chunk disappeared within a read transaction");
                }

                end = reader.GetInt64(0);
                stored = TursoSession.Bytes(reader, 1);
            }

            var sealedAt = ReadShapeValuesAt(stored);
            if (Math.Max(end, sealedAt) > head)
            {

                admission.Dispose();
                return null;
            }

            var upToDate = end == head;

            var values = upToDate ? head : sealedAt;
            var body = ShapeEnvelope(shape, offset, end, values, ShapeResume(shape.ResumeScope.Span, end, cancellationToken),
                ShapeChangesOf(stored), upToDate);
            return new ShapeBody(body, ShapeNumber(end), upToDate, admission);
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    private static ReadOnlySpan<byte> ShapeChangesOf(ReadOnlySpan<byte> stored)
    {
        var reader = new Utf8JsonReader(stored);
        while (reader.Read())
        {
            if (reader.CurrentDepth == 1 && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("changes"u8) && reader.Read()
                && reader.TokenType == JsonTokenType.StartArray)
            {
                var open = (int)reader.TokenStartIndex;
                if (!reader.TrySkip())
                {
                    break;
                }

                return stored.Slice(open + 1, (int)reader.TokenStartIndex - open - 1);
            }
        }

        throw new InvalidDataException("a sealed shape chunk has no changes array");
    }

}
