using System.Buffers.Binary;
using TerraBase.Engine.Sqlite;

namespace TerraBase.Features.Backup;

/// <summary>The authenticated backup payload carries both the wrapped database key and its encrypted pages.</summary>
internal static class SnapshotBundle
{
    private static ReadOnlySpan<byte> Magic => "TERRABASE-SNAPSHOT-1\n"u8;

    internal static async Task WriteAsync(string database, Stream output, CancellationToken cancellationToken)
    {
        DatabaseKeys.RejectLink(database);
        var record = DatabaseKeys.ReadRecord(database);
        var header = new byte[Magic.Length + sizeof(int)];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(Magic.Length), record.Length);
        await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await output.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        await using var pages = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await pages.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task ReadAsync(Stream bundle, string database, CancellationToken cancellationToken)
    {
        var header = new byte[Magic.Length + sizeof(int)];
        await bundle.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(Magic.Length));
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic) || length is <= 0 or > DatabaseKeys.MaxFileBytes)
        {
            throw new InvalidDataException("the encrypted database snapshot header is malformed");
        }

        var record = new byte[length];
        await bundle.ReadExactlyAsync(record, cancellationToken).ConfigureAwait(false);
        DatabaseKeys.ValidateRecord(record);
        DatabaseKeys.RejectLink(database);
        DatabaseKeys.RejectLink(database + DatabaseKeys.Suffix);
        await using (var key = new FileStream(database + DatabaseKeys.Suffix, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
        {
            await key.WriteAsync(record, cancellationToken).ConfigureAwait(false);
        }

        await using var pages = new FileStream(database, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        await bundle.CopyToAsync(pages, cancellationToken).ConfigureAwait(false);
        if (pages.Length == 0)
        {
            throw new InvalidDataException("the encrypted database snapshot contains no pages");
        }
    }
}
