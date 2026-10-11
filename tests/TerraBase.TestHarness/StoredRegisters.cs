using System.Text;
using Microsoft.Data.Sqlite;
using TerraBase.Engine.Lattices;

namespace TerraBase.Testing;

/// <summary>
/// Document rows a test writes into a database file directly (docs/design/anna-core.md, Stored state): a client database
/// row keeps the encoded register of its document beside the register's last-writer-wins JSON and stamp.
/// </summary>
public static class StoredRegisters
{
    /// <summary>The stamp of every write this helper makes, and so the version of its rows: 1 ms after the Unix epoch.</summary>
    public const long Version = 1 << 16;

    /// <summary>The replica id of every write this helper makes; no node draws region 0xFFFE.</summary>
    public static ReplicaId Replica { get; } = ReplicaId.Of(ushort.MaxValue - 1, 1, 0);

    /// <summary>The encoded register of one write of <paramref name="body"/> (a tombstone when it is <see langword="null"/>).</summary>
    public static byte[] Encode(string? body)
    {
        var stamp = new Hlc(Version);
        var write = body is null ? DocumentWrite.Tombstone(stamp) : DocumentWrite.Live(stamp, Encoding.UTF8.GetBytes(body));
        return LatticeEncoding.Encode(new MvRegister<DocumentWrite>().WriteDelta(Replica, write));
    }

    /// <summary>
    /// Adds <c>tb_state(body)</c> to <paramref name="connection"/>: the <see cref="Encode"/> of a body, for statements such
    /// as <c>INSERT INTO documents VALUES ($collection, $id, tb_state($body), 65536, $body)</c>.
    /// </summary>
    public static void Register(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        connection.CreateFunction("tb_state", (string? body) => Encode(body), isDeterministic: true);
    }
}
