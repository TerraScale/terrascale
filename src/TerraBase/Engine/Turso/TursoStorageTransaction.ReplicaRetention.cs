using System.Text;
using TerraBase.Configuration;
using TerraBase.Engine.Revisions;

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
            var identity = ReplicaOffer("~placement");
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
                DeleteReplicaOffer(region);
            }

            foreach (var region in regions.Distinct().Where(region => !sameIncarnation || !existing.Contains(region)))
            {
                SetCdcConsumerFloor("replica:" + region, ShapeHead());
            }

            var mark = Command("INSERT INTO _tb_shape_chunk(handle, offset, end_offset, body, sealed_at) VALUES ('~replica:~placement', 0, $version, $incarnation, 0) ON CONFLICT(handle, offset) DO UPDATE SET end_offset = excluded.end_offset, body = excluded.body;");
            mark.Parameters.AddWithValue("$version", version);
            mark.Parameters.AddWithValue("$incarnation", Encoding.UTF8.GetBytes(incarnation));
            _ = mark.ExecuteNonQuery();
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask AcknowledgeReplicaCdcAsync(string region, string? revision, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        await RunAsync(() =>
        {
            if (ReplicaCdcFloor(region) is not { } floor || ReplicaOffer(region) is not { } offered || offered.Revision != revision)
            {
                return false;
            }

            if (offered.Position > floor)
            {
                SetCdcConsumerFloor("replica:" + region, offered.Position);
            }

            DeleteReplicaOffer(region);
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    public async ValueTask OfferReplicaCdcAsync(string region, RevisionPullResult offered, CancellationToken cancellationToken)
    {
        await SuspendCaptureAsync(cancellationToken, rewriteRefreshOnly: true).ConfigureAwait(false);
        await RunAsync(() =>
        {
            if (ReplicaCdcFloor(region) is not { } floor || offered.CdcPosition <= floor)
            {
                return false;
            }

            var revision = offered.Revision.ToString();
            if (ReplicaOffer(region) is { } previous && previous.Position >= offered.CdcPosition)
            {
                return false;
            }

            var save = Command("INSERT INTO _tb_shape_chunk(handle, offset, end_offset, body, sealed_at) VALUES ($key, 0, $floor, $revision, 0) ON CONFLICT(handle, offset) DO UPDATE SET end_offset = excluded.end_offset, body = excluded.body;");
            save.Parameters.AddWithValue("$key", "~replica:" + region);
            save.Parameters.AddWithValue("$floor", offered.CdcPosition);
            save.Parameters.AddWithValue("$revision", Encoding.UTF8.GetBytes(revision));
            _ = save.ExecuteNonQuery();
            return true;
        }, cancellationToken, mutation: true).ConfigureAwait(false);
    }

    private long? ReplicaCdcFloor(string region)
    {
        var read = Command("SELECT end_offset FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        read.Parameters.AddWithValue("$key", "~cdc:replica:" + region);
        return read.ExecuteScalar() as long?;
    }

    private (long Position, string Revision)? ReplicaOffer(string region)
    {
        var read = Command("SELECT end_offset, CAST(body AS TEXT) FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        read.Parameters.AddWithValue("$key", "~replica:" + region);
        using var reader = read.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
    }

    private void DeleteReplicaOffer(string region)
    {
        var delete = Command("DELETE FROM _tb_shape_chunk WHERE handle = $key AND offset = 0;");
        delete.Parameters.AddWithValue("$key", "~replica:" + region);
        _ = delete.ExecuteNonQuery();
    }
}
