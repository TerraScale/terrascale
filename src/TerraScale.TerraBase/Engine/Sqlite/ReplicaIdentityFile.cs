using System.Buffers.Binary;
using System.Security.Cryptography;
using TerraBase.Configuration;
using TerraBase.Engine.Lattices;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// The member's replica identity in its data directory (docs/design/anna-core.md, Identities and dots): a format byte,
/// the member number (uint32, little-endian), the epoch (uint16) and whether the last process stopped cleanly. Read
/// and rewritten under the data directory's lock, before anything can mint a dot.
/// </summary>
internal static class ReplicaIdentityFile
{
    /// <summary>The file's name in the data directory's root.</summary>
    internal const string FileName = ".terrabase.replica";

    private const byte Format = 1;
    private const int Length = 8;
    private const byte Running = 0;
    private const byte Stopped = 1;

    /// <summary>
    /// Starts the member: a new identity for a new data directory, the same one after a clean stop and the next epoch
    /// after any other (an OS crash under <c>synchronous = NORMAL</c> can lose writes that peers already hold, and the
    /// recovered state must not mint their dots again). Past the last epoch it takes a new member number. The identity is
    /// written through to disk, marked running, before it is returned.
    /// </summary>
    public static ReplicaId Start(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, FileName);
        var id = New();
        // Empty: a first start died between creating the file and writing it, so nothing minted under it.
        if (File.Exists(path) && File.ReadAllBytes(path) is { Length: > 0 } bytes)
        {
            if (bytes.Length != Length || bytes[0] != Format || bytes[7] is not (Running or Stopped))
            {
                throw new TerraBaseConfigurationException("data directory has a replica identity this build does not support");
            }

            var member = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(1));
            var epoch = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(5));
            if (member == 0)
            {
                // Never minted: zero is the empty replica id.
                throw new TerraBaseConfigurationException("data directory has a replica identity this build does not support");
            }

            if (bytes[7] == Stopped)
            {
                id = Of(member, epoch);
            }
            else if (epoch < ushort.MaxValue)
            {
                id = Of(member, (ushort)(epoch + 1));
            }
        }

        Write(path, id, Running);
        return id;
    }

    /// <summary>
    /// Records that the member stopped cleanly, so its next start keeps <paramref name="id"/>: only once every committed
    /// write is on disk. A checkpoint SQLite completes at close syncs its database file and deletes the write-ahead log
    /// (<c>synchronous = NORMAL</c>); closing does not report one it skipped (another process held the file), so every log
    /// still present is synced first, lest an OS crash lose it and keep the mark.
    /// </summary>
    public static void Stop(string dataDirectory, ReplicaId id)
    {
        foreach (var log in Directory.EnumerateFiles(dataDirectory, "*.db-wal", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
        {
            using var stream = new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            stream.Flush(flushToDisk: true);
        }

        Write(Path.Combine(dataDirectory, FileName), id, Stopped);
    }

    // A random member number works with static peers as with NATS membership (design decision 8): 3 members collide with
    // odds of about 1 in 1.4 billion, and two members minting one dot would stop their stream on the same-dot refusal
    // instead of losing a write.
    private static ReplicaId New()
    {
        uint member;
        do
        {
            member = BinaryPrimitives.ReadUInt32LittleEndian(RandomNumberGenerator.GetBytes(4));
        }
        while (member == 0);
        return Of(member, 0);
    }

    // anna-full (TS-643): number regions too once dots cross regions; until then every region is region 0.
    private static ReplicaId Of(uint member, ushort epoch) => ReplicaId.Of(0, member, epoch);

    // Eight bytes in one write at offset 0 of a single sector, written through: the file holds either state, never a mix.
    private static void Write(string path, ReplicaId id, byte state)
    {
        Span<byte> bytes = stackalloc byte[Length];
        bytes[0] = Format;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[1..], id.Member);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[5..], id.Epoch);
        bytes[7] = state;
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
}
