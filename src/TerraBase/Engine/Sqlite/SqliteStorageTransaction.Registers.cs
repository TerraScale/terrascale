using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Client databases store each document as a register (docs/design/anna-core.md, Documents and Stored state): local writes
/// mint a dot under this member's replica id and stamp it with the member's clock, replicated deltas join into the stored
/// register, and the row keeps the register's last-writer-wins write for every read.
/// </summary>
internal sealed partial class SqliteStorageTransaction
{
    private LocalReplica Member => store.Member ?? throw new InvalidOperationException("a client database writes with this member's replica id");

    /// <summary>The last-writer-wins write of <paramref name="state"/>: the greatest stamp, then replica id, then counter.</summary>
    internal static KeyValuePair<Dot, DocumentWrite>? Winner(MvRegister<DocumentWrite> state)
    {
        KeyValuePair<Dot, DocumentWrite>? winner = null;
        foreach (var value in state.Values)
        {
            if (winner is not { } best
                || (value.Value.Stamp.Value, value.Key.Replica.Value, value.Key.Counter).CompareTo((best.Value.Stamp.Value, best.Key.Replica.Value, best.Key.Counter)) > 0)
            {
                winner = value;
            }
        }

        return winner;
    }

    // Worker-only: the stored register of a document, or the empty register.
    private MvRegister<DocumentWrite> ReadState(string collection, string id)
    {
        var command = Command(SqliteStatements.GetState, collection);
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is byte[] state ? Decode(state) : new MvRegister<DocumentWrite>();
    }

    private static MvRegister<DocumentWrite> Decode(ReadOnlySpan<byte> state)
    {
        try
        {
            return LatticeEncoding.DecodeDocument(state);
        }
        catch (InvalidDataException error)
        {
            throw new InvalidDataException("a stored document register is corrupt", error);
        }
    }

    // Worker-only: stores a register with its last-writer-wins write, and returns that write's version and body.
    private (long Version, string? Body) Store(string collection, string id, MvRegister<DocumentWrite> state, byte[] encoded)
    {
        var winner = Winner(state)?.Value ?? throw new InvalidOperationException("a stored register holds a write");
        var body = winner.IsTombstone ? null : StrictUtf8.GetString(winner.Body);
        var put = BodyCommand(SqliteStatements.PutState, collection, id);
        put.Parameters.AddWithValue("$state", encoded);
        put.Parameters.AddWithValue("$version", Version(winner.Stamp));
        put.Parameters.AddWithValue("$body", (object?)body ?? DBNull.Value);
        put.ExecuteNonQuery();
        return (Version(winner.Stamp), body);
    }

    // A stamp's 48 bits of milliseconds keep it below 2^63.
    private static long Version(Hlc stamp) => checked((long)stamp.Value);

    // Worker-only, inside a mutation: a local write of body (null for a tombstone) over every value of the document's
    // register this member knows. The clock first passes every stamp of the register (each at most the skew limit ahead),
    // so the write's stamp follows the document's, and its dot is the next of this replica id for the document.
    private long WriteRegister(string collection, string id, ReadOnlySpan<byte> body, bool tombstone)
    {
        var member = Member;
        var state = ReadState(collection, id);
        foreach (var write in state.Values.Values)
        {
            member.Clock.Observe(write.Stamp);
        }

        var stamp = member.Clock.Tick();
        // The write replaces every value the register holds and keeps its whole context, so the delta is the new state.
        var next = state.WriteDelta(member.Id, tombstone ? DocumentWrite.Tombstone(stamp) : DocumentWrite.Live(stamp, body));
        var encoded = LatticeEncoding.Encode(next);
        Store(collection, id, next, encoded);
        return Version(stamp);
    }
}
