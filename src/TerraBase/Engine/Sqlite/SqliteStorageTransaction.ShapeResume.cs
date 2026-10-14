using TerraBase.Engine.Lattices;
using TerraBase.Features.Shapes;

namespace TerraBase.Engine.Sqlite;

// Resume targets (shape-reads section 9, rules 3 and 4): the commit boundary before the first local row of the collection
// that the cursor does not cover, at most the sealed head.
internal sealed partial class SqliteStorageTransaction : IShapeResumeStorage
{
    public ValueTask<long?> ReadShapeResumeTargetAsync(string collection, IReadOnlyDictionary<ReplicaId, Hlc> covered, IReadOnlyDictionary<ReplicaId, FeedPrefix> prefixes, long head,
        CancellationToken cancellationToken) => RunAsync<long?>(() =>
    {
        long? first = null;
        foreach (var (origin, _) in Floors())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = Command("""
                SELECT min(seq) FROM _tb_feed WHERE origin = $origin AND (stamp > $stamp OR stamp <= $floor) AND collection = $collection AND seq <= $head;
                """);
            command.Parameters.AddWithValue("$origin", unchecked((long)origin.Value));
            // An origin the cursor does not name covers none of its rows.
            command.Parameters.AddWithValue("$stamp", covered.TryGetValue(origin, out var stamp) ? Version(stamp) : -1L);
            // A prefix proves no row at or below its floor; retain those rows for replay.
            command.Parameters.AddWithValue("$floor", prefixes.TryGetValue(origin, out var prefix) ? Version(prefix.Floor) : -1L);
            command.Parameters.AddWithValue("$collection", collection);
            command.Parameters.AddWithValue("$head", head);
            CountRead("shape");
            if (command.ExecuteScalar() is long seq && (first is null || seq < first))
            {
                first = seq;
            }
        }

        if (first is null)
        {
            return head;
        }

        var before = Command("""
            SELECT seq FROM _tb_feed WHERE seq < $first
            AND commit_offset <> (SELECT commit_offset FROM _tb_feed WHERE seq = $first) ORDER BY seq DESC LIMIT 1;
            """);
        before.Parameters.AddWithValue("$first", first.Value);
        CountRead("shape");
        var target = before.ExecuteScalar() is long end ? end : 0;
        // Retention can leave the commit's predecessor unproven: then there is no boundary to resume at.
        return ShapeBoundary(target) ? target : (long?)null;
    }, cancellationToken);
}
