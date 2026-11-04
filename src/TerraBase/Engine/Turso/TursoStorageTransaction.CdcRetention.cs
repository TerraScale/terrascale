using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : ICdcRetentionTransaction
{
    public async ValueTask SetCdcConsumerFloorAsync(string consumer, long? position, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(consumer);
        if (consumer.Length > 64 || consumer.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or ':' or '_')))
        {
            throw new ArgumentException("a CDC consumer has a bounded internal name", nameof(consumer));
        }

        if (position is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        await RunAsync(() =>
        {
            var key = "~cdc:" + consumer;
            if (position is { } floor)
            {
                if (!ShapeBoundary(floor))
                {
                    throw new ShapeSealOffsetException();
                }

                var count = Command("SELECT count(*) FROM _tb_shape_chunk WHERE handle >= '~cdc:' AND handle < '~cdc;';");
                if ((long)count.ExecuteScalar()! >= 64)
                {
                    var present = Command("SELECT 1 FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
                    present.Parameters.AddWithValue("$key", key);
                    if (present.ExecuteScalar() is null)
                    {
                        throw new InvalidOperationException("the database's CDC consumers are at capacity");
                    }
                }

                var upsert = Command("INSERT INTO _tb_shape_chunk(handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $floor, X'', 0) ON CONFLICT(handle, offset) DO UPDATE SET end_offset = excluded.end_offset;");
                upsert.Parameters.AddWithValue("$key", key);
                upsert.Parameters.AddWithValue("$floor", floor);
                _ = upsert.ExecuteNonQuery();
            }
            else
            {
                var remove = Command("DELETE FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
                remove.Parameters.AddWithValue("$key", key);
                _ = remove.ExecuteNonQuery();
            }

            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }
}
