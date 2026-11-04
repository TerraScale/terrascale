using System.Text;
using TerraBase.Configuration;

namespace TerraBase.Engine.Turso;

internal sealed partial class TursoStorageTransaction : ICdcReplicaRetentionTransaction
{
    public async ValueTask SetReplicaCdcRegionsAsync(IReadOnlyList<string> regions, long version, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(regions.Count, 8);
        ArgumentOutOfRangeException.ThrowIfNegative(version);
        if (regions.Any(region => !TerraBaseOptions.IsRegion(region)))
        {
            throw new ArgumentException("CDC replica consumers must name valid regions", nameof(regions));
        }

        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        await RunAsync(() =>
        {
            var incarnation = (string)Command(TursoStatements.Incarnation).ExecuteScalar()!;
            var identity = ReplicaPlacement();
            var sameIncarnation = identity?.Revision == incarnation;
            if (sameIncarnation && identity?.Position >= version)
            {
                return false;
            }

            var existing = new List<string>();
            var list = Command("SELECT substr(handle, 14) FROM _tb_shape_chunk WHERE handle >= '~cdc:replica:' AND handle < '~cdc:replica;' AND offset = 0;");
            using (var reader = list.ExecuteReader())
            {
                while (reader.Read())
                {
                    existing.Add(reader.GetString(0));
                }
            }

            foreach (var region in existing.Where(region => !sameIncarnation || !regions.Contains(region)))
            {
                SetCdcConsumerFloor("replica:" + region, null);
            }

            foreach (var region in regions.Distinct().Where(region => !sameIncarnation || !existing.Contains(region)))
            {
                SetCdcConsumerFloor("replica:" + region, ShapeHead());
            }

            var mark = Command("INSERT INTO _tb_shape_chunk(handle, offset, end_offset, body, sealed_at) VALUES ('~replicas', 0, $version, $incarnation, 0) ON CONFLICT(handle, offset) DO UPDATE SET end_offset = excluded.end_offset, body = excluded.body;");
            mark.Parameters.AddWithValue("$version", version);
            mark.Parameters.AddWithValue("$incarnation", Encoding.UTF8.GetBytes(incarnation));
            _ = mark.ExecuteNonQuery();
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask AcknowledgeReplicaCdcAsync(string region, long? position, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        await RunAsync(() =>
        {
            if (position is not { } acknowledged || ReplicaCdcFloor(region) is not { } floor || acknowledged <= floor)
            {
                return false;
            }

            SetCdcConsumerFloor("replica:" + region, acknowledged);
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    private long? ReplicaCdcFloor(string region)
    {
        var read = Command("SELECT end_offset FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        read.Parameters.AddWithValue("$key", "~cdc:replica:" + region);
        return read.ExecuteScalar() as long?;
    }

    private (long Position, string Revision)? ReplicaPlacement()
    {
        var read = Command("SELECT end_offset, CAST(body AS TEXT) FROM _tb_shape_chunk WHERE handle = '~replicas' AND offset = 0;");
        using var reader = read.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }
}
