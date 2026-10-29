using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>Compacts optional Unicode escapes in generated JSON without buffering an expanded request.</summary>
internal sealed class TerraBaseRequestJson : Stream
{
    private readonly BoundedHttpBodyStream body = new(64 * 1024 * 1024,
        static () => new JsonException("The JSON request exceeds the SDK's 64 MiB serialization limit."), CancellationToken.None);
    private bool quoted;
    private int state, digits, scalar, highSurrogate;

    internal static byte[] Serialize<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        using var output = new TerraBaseRequestJson();
        JsonSerializer.Serialize(output, value, typeInfo);
        return output.Finish();
    }

    internal byte[] Finish()
    {
        if (quoted || state != 0) throw new JsonException("Incomplete JSON string.");
        return body.ToArray();
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        foreach (var b in buffer)
        {
            switch (state)
            {
                case 1:
                    if (b == 'u') { state = 2; digits = scalar = 0; }
                    else
                    {
                        if (b is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t'))
                            throw new JsonException("Invalid JSON escape.");
                        if (b != '/') body.WriteByte((byte)'\\');
                        body.WriteByte(b);
                        state = 0;
                    }
                    break;
                case 2:
                    scalar = scalar * 16 + Hex(b);
                    if (++digits == 4) WriteScalar();
                    break;
                case 3:
                    if (b != '\\') throw new JsonException("Unpaired JSON surrogate.");
                    state = 4;
                    break;
                case 4:
                    if (b != 'u') throw new JsonException("Unpaired JSON surrogate.");
                    state = 2;
                    digits = scalar = 0;
                    break;
                default:
                    if (quoted && b == '\\') state = 1;
                    else
                    {
                        if (b == '"') quoted = !quoted;
                        body.WriteByte(b);
                    }
                    break;
            }
        }
    }

    private void WriteScalar()
    {
        state = 0;
        if (highSurrogate != 0)
        {
            if (!char.IsLowSurrogate((char)scalar)) throw new JsonException("Unpaired JSON surrogate.");
            scalar = char.ConvertToUtf32((char)highSurrogate, (char)scalar);
            highSurrogate = 0;
        }
        else if (char.IsHighSurrogate((char)scalar)) { highSurrogate = scalar; state = 3; return; }
        else if (char.IsLowSurrogate((char)scalar)) throw new JsonException("Unpaired JSON surrogate.");

        if (scalar < 32)
        {
            var shortEscape = scalar switch { 8 => 'b', 9 => 't', 10 => 'n', 12 => 'f', 13 => 'r', _ => '\0' };
            if (shortEscape != '\0') { body.WriteByte((byte)'\\'); body.WriteByte((byte)shortEscape); return; }
            ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
            Span<byte> escaped = stackalloc byte[] { (byte)'\\', (byte)'u', (byte)'0', (byte)'0', hex[scalar >> 4], hex[scalar & 15] };
            body.Write(escaped);
        }
        else if (scalar is '"' or '\\')
        {
            body.WriteByte((byte)'\\');
            body.WriteByte((byte)scalar);
        }
        else
        {
            Span<byte> utf8 = stackalloc byte[4];
            var count = new Rune(scalar).EncodeToUtf8(utf8);
            body.Write(utf8[..count]);
        }
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => throw new JsonException("Invalid JSON Unicode escape."),
    };

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => body.Length;
    public override long Position { get => body.Position; set => throw new NotSupportedException(); }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) body.Dispose();
        base.Dispose(disposing);
    }
}
