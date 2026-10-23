using TerraBase.Engine.Lattices;
using TerraBase.Features.Replication;
using TerraBase.Tenancy;

namespace TerraBase.Engine.Sqlite;

internal sealed partial class SqliteStorageTransaction
{
    internal static void ValidateRefillPage(
        TenantContext database, IReadOnlyList<int> buckets, BucketCursor? after, IReadOnlyList<RepairState> states)
    {
        ArgumentNullException.ThrowIfNull(buckets);
        ArgumentNullException.ThrowIfNull(states);
        if (states.Count > ChangeGroups.MaxDeltas || buckets.Count > Buckets || buckets.Any(bucket => bucket is < 0 or >= Buckets)
            || after is not null && (after.Bucket != Bucket(after.Collection, after.Id)
                || !ChangeGroups.ValidKey(database, after.Collection, after.Id)))
        {
            throw new ArgumentException("a refill page names valid bounded buckets, states and cursor", nameof(states));
        }

        var previous = after;
        foreach (var state in states)
        {
            var key = new BucketCursor(Bucket(state.Collection, state.Id), state.Collection, state.Id);
            if (!buckets.Contains(key.Bucket) || !ChangeGroups.ValidKey(database, state.Collection, state.Id)
                || state.State.MaxStamp.Value == 0 || previous is not null && CompareRefillKeys(key, previous) <= 0)
            {
                throw new ArgumentException("a refill page holds stamped, strictly ordered keys in its requested buckets", nameof(states));
            }

            previous = key;
        }
    }

    // One bounded local page only; the caller disposes its transaction before asking for the next one.
    internal async ValueTask<(int Forgotten, BucketCursor? Next)?> ReconcileRefillAsync(
        IReadOnlyList<int> buckets, BucketCursor? after, IReadOnlyList<RepairState> source, Hlc collectedBefore, CancellationToken cancellationToken)
    {
        var (local, next) = await ReadBucketStatesAsync(buckets, after, ChangeGroups.MaxDeltas, ChangeGroups.MaxBatchBytes, cancellationToken).ConfigureAwait(false);
        var present = source.Select(state => (state.Collection, state.Id)).ToHashSet();
        var end = source.Count > 0 ? new BucketCursor(Bucket(source[^1].Collection, source[^1].Id), source[^1].Collection, source[^1].Id) : null;
        List<(string Collection, string Id)> absent = [];
        foreach (var state in local)
        {
            var key = new BucketCursor(Bucket(state.Collection, state.Id), state.Collection, state.Id);
            if (end is not null && CompareRefillKeys(key, end) > 0)
            {
                next = null;
                break;
            }

            if (state.State.MaxStamp.Value < collectedBefore.Value && !present.Contains((state.Collection, state.Id)))
            {
                // Absence alone cannot distinguish collection from a source that never received this live state.
                if (state.State.Exists)
                {
                    return null;
                }

                absent.Add((state.Collection, state.Id));
            }

            if (end is not null && CompareRefillKeys(key, end) == 0)
            {
                next = null;
                break;
            }
        }

        var forgotten = await ForgetRefillAsync(absent, collectedBefore, cancellationToken).ConfigureAwait(false);
        return (forgotten, next);
    }

    // SQLite's BINARY key order compares UTF-8 bytes, including supplementary Unicode characters.
    private static int CompareRefillKeys(BucketCursor left, BucketCursor right)
    {
        var bucket = left.Bucket.CompareTo(right.Bucket);
        if (bucket != 0)
        {
            return bucket;
        }

        var collection = StrictUtf8.GetBytes(left.Collection).AsSpan().SequenceCompareTo(StrictUtf8.GetBytes(right.Collection));
        return collection != 0 ? collection : StrictUtf8.GetBytes(left.Id).AsSpan().SequenceCompareTo(StrictUtf8.GetBytes(right.Id));
    }
}
