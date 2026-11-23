using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using System.Text.Unicode;
using TerraBase.Postgres.Hosting;
using TerraBase.Postgres.Protocol;
using TerraBase.Postgres.Sql;

namespace TerraBase.Postgres.Sessions;

internal sealed class PgCopyReader(PgCopyOptions options, IReadOnlyList<int> oids) : IDisposable
{
    private static ReadOnlySpan<byte> Signature => [0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00];

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _start;
    private int _end;
    private bool _headerConsumed;
    private bool _finished;

    public Result<List<SqlValue[]>, PgError> Feed(ReadOnlySpan<byte> data)
    {
        if (_finished)
        {
            return new List<SqlValue[]>();
        }

        Append(data);
        return Drain(final: false);
    }

    public Result<List<SqlValue[]>, PgError> Complete()
    {
        if (_finished || _start == _end)
        {
            return new List<SqlValue[]>();
        }

        return Drain(final: true);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_buffer.Length - _end < data.Length)
        {
            var live = _end - _start;
            var target = _buffer.Length - live >= data.Length ? _buffer : ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, live + data.Length));
            _buffer.AsSpan(_start, live).CopyTo(target);
            if (!ReferenceEquals(target, _buffer))
            {
                ArrayPool<byte>.Shared.Return(_buffer);
                _buffer = target;
            }

            _start = 0;
            _end = live;
        }

        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    private Result<List<SqlValue[]>, PgError> Drain(bool final)
    {
        List<SqlValue[]> records = [];
        while (_start < _end && !_finished)
        {
            var pending = _buffer.AsSpan(_start, _end - _start);
            var consumed = options.Format switch
            {
                PgCopyFormat.Binary => ReadBinary(pending, records),
                PgCopyFormat.Csv => ReadCsv(pending, final, records),
                PgCopyFormat.Text => ReadText(pending, final, records),
                _ => throw new UnreachableException(),
            };
            switch (consumed)
            {
                case < 0:
                    return new PgError("22P04", "invalid COPY data", Detail: $"row {records.Count + 1}");
                case 0:
                    if (_end - _start > PostgresLimits.MaxCopyRecordBytes)
                    {
                        return new PgError("54000", "COPY row exceeds the maximum record size");
                    }

                    return records;
            }

            _start += consumed;
            if (records.Count > 0 && records[^1].Length != oids.Count)
            {
                return new PgError("22P04", records[^1].Length < oids.Count ? "missing data for column" : "extra data after last expected column");
            }
        }

        return records;
    }

    private int ReadText(ReadOnlySpan<byte> pending, bool final, List<SqlValue[]> records)
    {
        var newline = pending.IndexOf((byte)'\n');
        if (newline < 0 && !final)
        {
            return 0;
        }

        var length = newline < 0 ? pending.Length : newline;
        var line = pending[..length];
        if (line.EndsWith("\r"u8))
        {
            line = line[..^1];
        }

        var consumed = newline < 0 ? pending.Length : newline + 1;
        if (line.SequenceEqual("\\."u8))
        {
            _finished = true;
            return consumed;
        }

        if (options.Header && !_headerConsumed)
        {
            _headerConsumed = true;
            return consumed;
        }

        List<SqlValue> values = [];
        var delimiter = (byte)options.Delimiter;
        var nullText = Encoding.UTF8.GetBytes(options.Null);
        while (true)
        {
            var end = line.IndexOf(delimiter);
            var field = end < 0 ? line : line[..end];
            if (field.SequenceEqual(nullText))
            {
                values.Add(SqlValue.Null);
            }
            else if (UnescapeText(field) is not { } text || !Decode(values.Count, text, out var value))
            {
                return -1;
            }
            else
            {
                values.Add(value);
            }

            if (end < 0)
            {
                break;
            }

            line = line[(end + 1)..];
        }

        records.Add([.. values]);
        return consumed;
    }

    private int ReadCsv(ReadOnlySpan<byte> pending, bool final, List<SqlValue[]> records)
    {
        var quote = (byte)options.Quote;
        var escape = (byte)options.Escape;
        var delimiter = (byte)options.Delimiter;
        var quoted = false;
        var end = -1;
        for (var index = 0; index < pending.Length; index++)
        {
            var current = pending[index];
            if (quoted)
            {
                if (current == escape && escape != quote && index + 1 < pending.Length && pending[index + 1] == quote)
                {
                    index++;
                }
                else if (current == quote)
                {
                    if (index + 1 < pending.Length && pending[index + 1] == quote && escape == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
            }
            else if (current == quote)
            {
                quoted = true;
            }
            else if (current == '\n')
            {
                end = index;
                break;
            }
        }

        if (end < 0 && (!final || quoted))
        {
            return final ? -1 : 0;
        }

        var consumed = end < 0 ? pending.Length : end + 1;
        var record = pending[..(end < 0 ? pending.Length : end)];
        if (record.EndsWith("\r"u8))
        {
            record = record[..^1];
        }

        if (record.SequenceEqual("\\."u8))
        {
            _finished = true;
            return consumed;
        }

        if (options.Header && !_headerConsumed)
        {
            _headerConsumed = true;
            return consumed;
        }

        List<SqlValue> values = [];
        var field = new List<byte>();
        var fieldQuoted = false;
        quoted = false;
        for (var index = 0; index <= record.Length; index++)
        {
            if (index == record.Length || (!quoted && record[index] == delimiter))
            {
                var bytes = field.ToArray();
                if (!fieldQuoted && Encoding.UTF8.GetString(bytes) == options.Null)
                {
                    values.Add(SqlValue.Null);
                }
                else if (!Decode(values.Count, Encoding.UTF8.GetString(bytes), out var value))
                {
                    return -1;
                }
                else
                {
                    values.Add(value);
                }

                field.Clear();
                fieldQuoted = false;
                continue;
            }

            var current = record[index];
            if (quoted)
            {
                if (current == escape && index + 1 < record.Length && record[index + 1] == quote)
                {
                    field.Add(quote);
                    index++;
                }
                else if (current == quote)
                {
                    quoted = false;
                }
                else
                {
                    field.Add(current);
                }
            }
            else if (current == quote)
            {
                quoted = true;
                fieldQuoted = true;
            }
            else
            {
                field.Add(current);
            }
        }

        records.Add([.. values]);
        return consumed;
    }

    private int ReadBinary(ReadOnlySpan<byte> pending, List<SqlValue[]> records)
    {
        var consumed = 0;
        if (!_headerConsumed)
        {
            if (pending.Length < Signature.Length + 8)
            {
                return 0;
            }

            if (!pending[..Signature.Length].SequenceEqual(Signature))
            {
                return -1;
            }

            var extension = BinaryPrimitives.ReadInt32BigEndian(pending[(Signature.Length + 4)..]);
            if (extension < 0 || pending.Length < Signature.Length + 8 + extension)
            {
                return extension < 0 ? -1 : 0;
            }

            _headerConsumed = true;
            return Signature.Length + 8 + extension;
        }

        if (pending.Length < 2)
        {
            return 0;
        }

        var count = BinaryPrimitives.ReadInt16BigEndian(pending);
        if (count == -1)
        {
            _finished = true;
            return 2;
        }

        consumed = 2;
        var values = new SqlValue[count];
        for (var index = 0; index < count; index++)
        {
            if (pending.Length < consumed + 4)
            {
                return 0;
            }

            var length = BinaryPrimitives.ReadInt32BigEndian(pending[consumed..]);
            consumed += 4;
            if (length < 0)
            {
                values[index] = SqlValue.Null;
                continue;
            }

            if (pending.Length < consumed + length)
            {
                return 0;
            }

            var oid = index < oids.Count ? oids[index] : PgOid.Text;
            if (!PgValueCodec.TryDecode(oid, 1, pending.Slice(consumed, length).ToArray(), out values[index]))
            {
                return -1;
            }

            consumed += length;
        }

        records.Add(values);
        return consumed;
    }

    private bool Decode(int index, string text, out SqlValue value)
    {
        var oid = index < oids.Count ? oids[index] : PgOid.Text;
        return PgValueCodec.TryDecodeText(oid == 0 ? PgOid.Unknown : oid, text, out value);
    }

    private static string? UnescapeText(ReadOnlySpan<byte> field)
    {
        if (field.IndexOf((byte)'\\') < 0)
        {
            return Encoding.UTF8.GetString(field);
        }

        var bytes = new List<byte>(field.Length);
        for (var index = 0; index < field.Length; index++)
        {
            var current = field[index];
            if (current != '\\' || index + 1 >= field.Length)
            {
                bytes.Add(current);
                continue;
            }

            var escape = field[++index];
            switch (escape)
            {
                case (byte)'b':
                    bytes.Add(8);
                    break;
                case (byte)'f':
                    bytes.Add(12);
                    break;
                case (byte)'n':
                    bytes.Add(10);
                    break;
                case (byte)'r':
                    bytes.Add(13);
                    break;
                case (byte)'t':
                    bytes.Add(9);
                    break;
                case (byte)'v':
                    bytes.Add(11);
                    break;
                case >= (byte)'0' and <= (byte)'7':
                    var octal = escape - '0';
                    for (var digits = 1; digits < 3 && index + 1 < field.Length && field[index + 1] is >= (byte)'0' and <= (byte)'7'; digits++)
                    {
                        octal = (octal * 8) + (field[++index] - '0');
                    }

                    bytes.Add((byte)octal);
                    break;
                case (byte)'x' when index + 1 < field.Length && char.IsAsciiHexDigit((char)field[index + 1]):
                    var hex = HexValue(field[++index]);
                    if (index + 1 < field.Length && char.IsAsciiHexDigit((char)field[index + 1]))
                    {
                        hex = (hex * 16) + HexValue(field[++index]);
                    }

                    bytes.Add((byte)hex);
                    break;
                default:
                    bytes.Add(escape);
                    break;
            }
        }

        var array = bytes.ToArray();
        return Utf8.IsValid(array) ? Encoding.UTF8.GetString(array) : null;
    }

    private static int HexValue(byte digit) => digit switch
    {
        >= (byte)'0' and <= (byte)'9' => digit - '0',
        >= (byte)'a' and <= (byte)'f' => digit - 'a' + 10,
        _ => digit - 'A' + 10,
    };
}

internal static class PgCopyWriter
{
    private static ReadOnlySpan<byte> Signature => [0x50, 0x47, 0x43, 0x4F, 0x50, 0x59, 0x0A, 0xFF, 0x0D, 0x0A, 0x00];

    public static void Header(PgMessageBuffer buffer, PgCopyOptions options, IReadOnlyList<PgColumn> columns)
    {
        if (options.Format == PgCopyFormat.Binary)
        {
            buffer.Begin('d');
            buffer.WriteBytes(Signature);
            buffer.WriteInt32(0);
            buffer.WriteInt32(0);
            buffer.End();
            return;
        }

        if (!options.Header)
        {
            return;
        }

        buffer.Begin('d');
        for (var index = 0; index < columns.Count; index++)
        {
            if (index > 0)
            {
                buffer.WriteUtf8([options.Delimiter]);
            }

            WriteField(buffer, options, columns[index].Name);
        }

        buffer.WriteByte((byte)'\n');
        buffer.End();
    }

    public static PgError? Row(PgMessageBuffer buffer, PgCopyOptions options, IReadOnlyList<PgColumn> columns, IReadOnlyList<SqlValue> row)
    {
        var start = buffer.Begin('d');
        if (options.Format == PgCopyFormat.Binary)
        {
            buffer.WriteInt16((short)row.Count);
            for (var index = 0; index < row.Count; index++)
            {
                var oid = index < columns.Count ? columns[index].Oid : PgOid.Text;
                if (!PgValueCodec.TryWrite(buffer, row[index], oid, binary: true))
                {
                    buffer.Truncate(start);
                    return new PgError("22P02", $"value of column {index + 1} cannot be encoded as {PgOid.FormatName(oid)}");
                }
            }

            buffer.End();
            return null;
        }

        for (var index = 0; index < row.Count; index++)
        {
            if (index > 0)
            {
                buffer.WriteUtf8([options.Delimiter]);
            }

            var oid = index < columns.Count ? columns[index].Oid : PgOid.Text;
            if (PgValueCodec.FormatText(row[index], oid) is not { } text)
            {
                buffer.WriteUtf8(options.Null);
                continue;
            }

            WriteField(buffer, options, text);
        }

        buffer.WriteByte((byte)'\n');
        buffer.End();
        return null;
    }

    public static void Trailer(PgMessageBuffer buffer, PgCopyOptions options)
    {
        if (options.Format != PgCopyFormat.Binary)
        {
            return;
        }

        buffer.Begin('d');
        buffer.WriteInt16(-1);
        buffer.End();
    }

    private static void WriteField(PgMessageBuffer buffer, PgCopyOptions options, string text)
    {
        if (options.Format == PgCopyFormat.Csv)
        {
            var needsQuotes = text.Length == 0 || text == options.Null || text.Contains(options.Delimiter, StringComparison.Ordinal)
                || text.Contains(options.Quote, StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal) || text.Contains('\r', StringComparison.Ordinal);
            if (!needsQuotes)
            {
                buffer.WriteUtf8(text);
                return;
            }

            var quote = options.Quote.ToString();
            buffer.WriteUtf8(quote + text.Replace(quote, options.Escape.ToString() + quote, StringComparison.Ordinal) + quote);
            return;
        }

        var escaped = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            escaped.Append(character switch
            {
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' when options.Delimiter == '\t' => "\\t",
                _ when character == options.Delimiter => "\\" + character,
                _ => character.ToString(),
            });
        }

        buffer.WriteUtf8(escaped.ToString());
    }
}
