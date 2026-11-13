using System.Buffers.Binary;
using System.Text;

namespace TerraBase.Features.Postgres;

internal sealed class PgReader(byte[] data)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private int _position;

    public byte Byte() => Take(1)[0];

    public short Int16() => BinaryPrimitives.ReadInt16BigEndian(Take(2));

    public int Int32() => BinaryPrimitives.ReadInt32BigEndian(Take(4));

    public int Count() => Int16() is >= 0 and var count ? count : throw new InvalidDataException("negative count");

    public byte[]? Bytes() => Int32() is var length and >= 0 ? Take(length).ToArray() : null;

    public string CString()
    {
        var end = Array.IndexOf(data, (byte)0, _position);
        if (end < 0)
        {
            throw new InvalidDataException("unterminated string");
        }

        try
        {
            return Utf8.GetString(Take(end - _position + 1)[..^1]);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException("invalid UTF-8");
        }
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count < 0 || count > data.Length - _position)
        {
            throw new InvalidDataException("message shorter than its contents");
        }

        _position += count;
        return data.AsSpan(_position - count, count);
    }
}
