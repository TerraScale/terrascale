using System.Text.Json;
using TerraBase.Features.Backup;

namespace TerraBase.Features.Management;

internal sealed partial class HistoryErasures
{
    private static async Task<bool> GenerationCleanAsync(IObjectStore store, string file, Ulid generation,
        IReadOnlyList<HistoryRewrite> rewrites, DataKeys keys, string scope, CancellationToken cancellationToken)
    {
        var key = BackupLayout.GenerationProof(file, generation);
        var clean = false;
        _ = await store.ReadAsync(key, async (stream, token) =>
        {
            // Small metadata, bounded before materialization even for a corrupt authenticated object.
            using var plain = new BoundedProof();
            await BackupObject.ReadAsync(stream, plain, key, id => keys.GetAsync(scope, id, token), token).ConfigureAwait(false);
            using var json = JsonDocument.Parse(plain.ToArray());
            var root = json.RootElement;
            if (root.GetProperty("version").GetInt32() != 1 || root.GetProperty("generation").GetString() != generation.ToString()
                || root.GetProperty("snapshot").GetString() is not { } snapshotKey
                || !snapshotKey.StartsWith(BackupLayout.Snapshots(file), StringComparison.Ordinal)
                || BackupLayout.ParseSnapshot(snapshotKey) is not { Position: 0 } snapshot || snapshot.Generation != generation)
            {
                return;
            }

            var incarnation = root.GetProperty("incarnation").GetString();
            var offset = root.GetProperty("offset").GetInt64();
            clean = offset >= 0 && rewrites.Any(rewrite => rewrite.Target == incarnation && offset >= rewrite.Offset);
        }, cancellationToken).ConfigureAwait(false);
        return clean;
    }

    private sealed class BoundedProof : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position + buffer.Length > 4096)
            {
                throw new InvalidDataException("backup generation proof exceeds its bound");
            }

            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
