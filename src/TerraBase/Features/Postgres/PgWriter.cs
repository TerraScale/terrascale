using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipelines;
using System.Text;

namespace TerraBase.Features.Postgres;

internal sealed class PgWriter(PipeWriter pipe)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly ArrayBufferWriter<byte> _body = new();
    private byte _type;
    public long Written { get; private set; }

    public void Begin(char type)
    {
        _type = (byte)type;
        _body.ResetWrittenCount();
    }

    public void Int16(int value)
    {
        BinaryPrimitives.WriteInt16BigEndian(_body.GetSpan(2), (short)value);
        _body.Advance(2);
    }

    public void Int32(int value)
    {
        BinaryPrimitives.WriteInt32BigEndian(_body.GetSpan(4), value);
        _body.Advance(4);
    }

    public void CString(string value)
    {
        Utf8.GetBytes(value, _body);
        _body.Write([(byte)0]);
    }

    public void Value(byte[]? value)
    {
        Int32(value?.Length ?? -1);
        _body.Write(value);
    }

    public void End()
    {
        var length = _body.WrittenCount;
        var span = pipe.GetSpan(length + 5);
        span[0] = _type;
        BinaryPrimitives.WriteInt32BigEndian(span[1..], length + 4);
        _body.WrittenSpan.CopyTo(span[5..]);
        pipe.Advance(length + 5);
        Written += length + 5;
    }

    public void Empty(char type)
    {
        Begin(type);
        End();
    }

    public void Raw(byte value)
    {
        pipe.GetSpan(1)[0] = value;
        pipe.Advance(1);
        Written++;
    }

    public void Ready(char status)
    {
        Begin('Z');
        _body.Write([(byte)status]);
        End();
    }

    public void Error(string code, string message, bool fatal = false, string? detail = null) => Fields('E', fatal ? "FATAL" : "ERROR", code, message, detail);

    public void Notice(PgError notice) => Fields('N', "WARNING", notice.Code, notice.Message, null);

    public ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken) => pipe.FlushAsync(cancellationToken);

    private void Fields(char type, string severity, string code, string message, string? detail)
    {
        Begin(type);
        foreach (var (field, value) in detail is null ? new[] { ('S', severity), ('V', severity), ('C', code), ('M', message) }
            : [('S', severity), ('V', severity), ('C', code), ('M', message), ('D', detail)])
        {
            _body.Write([(byte)field]);
            CString(value);
        }

        _body.Write([(byte)0]);
        End();
    }
}
