using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace TerraBase.Engine.Revisions;

internal static class RevisionManifestFile
{
    internal const string Suffix = ".revisions";
    internal const string StagedSuffix = Suffix + ".tmp";
    private const uint Magic = 0x56525354;
    private const byte Format = 1;
    private const int ChecksumBytes = 8;
    private const int MaxBytes = 8 << 20;

    public static RevisionManifest? Load(string databasePath)
    {
        var path = databasePath + Suffix;
        byte[] bytes;
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > MaxBytes })
            {
                throw new InvalidDataException("the revision manifest is larger than its bound");
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        if (bytes.Length < 4 + 1 + ChecksumBytes
            || !SHA256.HashData(bytes.AsSpan(0, bytes.Length - ChecksumBytes)).AsSpan(0, ChecksumBytes).SequenceEqual(bytes.AsSpan(bytes.Length - ChecksumBytes))
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || bytes[4] != Format)
        {
            throw new InvalidDataException("the revision manifest is damaged or of another format");
        }

        var reader = new Reader(bytes.AsSpan(5, bytes.Length - 5 - ChecksumBytes));
        var manifest = new RevisionManifest { Incarnation = new Ulid(reader.Bytes(16)), Generation = reader.U64() };
        if (reader.U8() == 1)
        {
            manifest.WalSalt = (reader.U32(), reader.U32());
        }

        if (reader.U8() == 1)
        {
            manifest.DatabaseFile = new FileStamp((long)reader.U64(), (long)reader.U64());
        }

        for (var count = reader.U16(); count > 0; count--)
        {
            var generation = reader.U64();
            var end = reader.U64();
            var full = reader.U8() == 1;
            var pages = new Dictionary<uint, ulong>();
            for (var n = reader.U32(); n > 0; n--)
            {
                pages[reader.U32()] = reader.U64();
            }

            manifest.Sealed.Add(new SealedGeneration(generation, end, full, pages));
        }

        for (var count = reader.U16(); count > 0; count--)
        {
            var bucket = new HorizonBucket(reader.U64());
            for (var n = reader.U16(); n > 0; n--)
            {
                bucket.EndFrames.Add(reader.U64());
            }

            for (var n = reader.U32(); n > 0; n--)
            {
                bucket.Pages.Add(reader.U32());
            }

            manifest.Horizon.Add(bucket);
        }

        if (!reader.End || manifest.Generation == 0 || manifest.Horizon.Any(bucket => bucket.EndFrames.Count == 0))
        {
            throw new InvalidDataException("the revision manifest is damaged or of another format");
        }

        return manifest;
    }

    public static void Store(string databasePath, RevisionManifest manifest)
    {
        var writer = new ArrayBufferWriter<byte>(4096);
        U32(writer, Magic);
        writer.Write([Format]);
        manifest.Incarnation.TryWriteBytes(writer.GetSpan(16));
        writer.Advance(16);
        U64(writer, manifest.Generation);
        writer.Write([(byte)(manifest.WalSalt is null ? 0 : 1)]);
        if (manifest.WalSalt is var (first, second))
        {
            U32(writer, first);
            U32(writer, second);
        }

        writer.Write([(byte)(manifest.DatabaseFile is null ? 0 : 1)]);
        if (manifest.DatabaseFile is { } stamp)
        {
            U64(writer, (ulong)stamp.Length);
            U64(writer, (ulong)stamp.WriteTicks);
        }

        U16(writer, checked((ushort)manifest.Sealed.Count));
        foreach (var entry in manifest.Sealed)
        {
            U64(writer, entry.Generation);
            U64(writer, entry.EndFrame);
            writer.Write([(byte)(entry.Full ? 1 : 0)]);
            U32(writer, (uint)entry.Pages.Count);
            foreach (var (page, last) in entry.Pages.OrderBy(pair => pair.Key))
            {
                U32(writer, page);
                U64(writer, last);
            }
        }

        U16(writer, checked((ushort)manifest.Horizon.Count));
        foreach (var bucket in manifest.Horizon)
        {
            U64(writer, bucket.FirstGeneration);
            U16(writer, checked((ushort)bucket.EndFrames.Count));
            foreach (var end in bucket.EndFrames)
            {
                U64(writer, end);
            }

            U32(writer, (uint)bucket.Pages.Count);
            foreach (var page in bucket.Pages.Order())
            {
                U32(writer, page);
            }
        }

        writer.Write(SHA256.HashData(writer.WrittenSpan).AsSpan(0, ChecksumBytes));

        var path = databasePath + Suffix;
        var staged = databasePath + StagedSuffix;
        using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            file.Write(writer.WrittenSpan);
            file.Flush(flushToDisk: true);
        }

        File.Move(staged, path, overwrite: true);
        DirectorySync.Sync(Path.GetDirectoryName(Path.GetFullPath(path))!);
    }

    private static void U16(ArrayBufferWriter<byte> writer, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(writer.GetSpan(2), value);
        writer.Advance(2);
    }

    private static void U32(ArrayBufferWriter<byte> writer, uint value)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(writer.GetSpan(4), value);
        writer.Advance(4);
    }

    private static void U64(ArrayBufferWriter<byte> writer, ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(writer.GetSpan(8), value);
        writer.Advance(8);
    }

    private ref struct Reader(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _rest = bytes;

        public readonly bool End => _rest.IsEmpty;

        public ReadOnlySpan<byte> Bytes(int count)
        {
            if (_rest.Length < count)
            {
                throw new InvalidDataException("the revision manifest is truncated");
            }

            var value = _rest[..count];
            _rest = _rest[count..];
            return value;
        }

        public byte U8() => Bytes(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Bytes(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Bytes(4));

        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Bytes(8));
    }
}
