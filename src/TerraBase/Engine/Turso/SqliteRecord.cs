using System.Buffers;
using System.Buffers.Binary;
using System.Data.Common;
using System.Globalization;
using System.Text;
using Turso;
using Turso.Data.Sqlite;
using Turso.Raw.Public;

namespace TerraBase.Engine.Turso;

internal static class SqliteRecord
{

    public static object?[] Decode(ReadOnlySpan<byte> record, int columns = int.MaxValue, bool materializeContent = true)
    {
        var headerSize = Varint(record, out var used);
        if (headerSize < used || headerSize > record.Length)
        {
            throw new InvalidDataException("a record header is out of range");
        }

        var header = record[used..(int)headerSize];
        var body = record[(int)headerSize..];
        var values = new List<object?>();
        while (!header.IsEmpty && values.Count < columns)
        {
            var type = Varint(header, out var read);
            header = header[read..];
            var size = type switch
            {
                0 or 8 or 9 => 0,
                1 => 1,
                2 => 2,
                3 => 3,
                4 => 4,
                5 => 6,
                6 or 7 => 8,
                >= 12 => (int)((type - 12) / 2),
                _ => throw new InvalidDataException("a record uses a reserved serial type"),
            };
            if (size > body.Length)
            {
                throw new InvalidDataException("a record value runs past its end");
            }

            var value = body[..size];
            body = body[size..];

            values.Add(type switch
            {
                0 => null,
                8 => 0L,
                9 => 1L,
                7 => BinaryPrimitives.ReadDoubleBigEndian(value),
                <= 6 => Integer(value),
                _ when type % 2 == 0 => materializeContent ? value.ToArray() : [],
                _ => materializeContent ? Encoding.UTF8.GetString(value) : string.Empty,
            });
        }

        return [.. values];
    }

    private static long Integer(ReadOnlySpan<byte> bytes)
    {
        long value = (sbyte)bytes[0];
        foreach (var next in bytes[1..])
        {
            value = (value << 8) | next;
        }

        return value;
    }

    private static long Varint(ReadOnlySpan<byte> bytes, out int read)
    {
        long value = 0;
        for (read = 0; read < 9; read++)
        {
            if (read >= bytes.Length)
            {
                throw new InvalidDataException("a record varint is truncated");
            }

            var next = bytes[read];
            if (read == 8)
            {
                read++;
                return (value << 8) | next;
            }

            value = (value << 7) | (long)(next & 0x7f);
            if ((next & 0x80) == 0)
            {
                read++;
                return value;
            }
        }

        throw new InvalidDataException("a record varint is malformed");
    }
}
