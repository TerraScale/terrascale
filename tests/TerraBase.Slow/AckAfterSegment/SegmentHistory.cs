using System.Globalization;
using System.Runtime.CompilerServices;
using TerraBase.Engine;
using TerraBase.Features.Backup;

namespace TerraBase.Slow.AckAfterSegment;

/// <summary>
/// Restores one <see cref="SegmentAckGate"/> generation from the bucket alone, on a fresh file: its snapshot rolled forward
/// through its segments, after checking they cover the log from 0 with no gap and no overlap. Prototype-local: the
/// product's restore-history proof is the backup restore's own (Beta's P1/P8), not this.
/// </summary>
internal static class SegmentHistory
{
    /// <summary>The restored file and the log bytes it replayed; throws when the history is not contiguous or not sound.</summary>
    public static async Task<(string File, long Position, int Segments)> RestoreAsync(
        IObjectStore store, IWalBackup backup, string prefix, string scratch, CancellationToken cancellationToken)
    {
        List<(string Key, long Start, long End)> segments = [];
        await foreach (var stored in store.ListAsync(prefix + "/segment-", null, cancellationToken).ConfigureAwait(false))
        {
            var range = stored.Key[(prefix.Length + "/segment-".Length)..].Split('-');
            segments.Add((stored.Key, long.Parse(range[0], CultureInfo.InvariantCulture), long.Parse(range[1], CultureInfo.InvariantCulture)));
        }

        long position = 0;
        foreach (var (key, start, end) in segments)
        {
            if (start != position || end <= start)
            {
                throw new InvalidDataException($"{key} does not continue the log at {position}: a gap or an overlap");
            }

            position = end;
        }

        var file = Path.Combine(scratch, $"restored-{Guid.NewGuid():N}.db");
        var found = await store.ReadAsync(prefix + "/snapshot", async (content, token) =>
        {
            await SnapshotBundle.ReadAsync(content, file, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        if (!found)
        {
            throw new InvalidDataException($"{prefix} has no snapshot");
        }

        if (!await backup.RollForwardAsync(file, ReadAllAsync(store, segments, cancellationToken), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidDataException($"{prefix} does not restore to a sound database");
        }

        return (file, position, segments.Count);
    }

    private static async IAsyncEnumerable<Stream> ReadAllAsync(
        IObjectStore store, List<(string Key, long Start, long End)> segments, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var (key, start, end) in segments)
        {
            var content = new MemoryStream();
            _ = await store.ReadAsync(key, (stream, token) => stream.CopyToAsync(content, token), cancellationToken).ConfigureAwait(false);
            if (content.Length != end - start)
            {
                throw new InvalidDataException($"{key} holds {content.Length} bytes, not {end - start}");
            }

            content.Position = 0;
            yield return content;
        }
    }
}
