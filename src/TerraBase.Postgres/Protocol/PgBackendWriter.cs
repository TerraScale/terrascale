using System.Buffers;
using System.Globalization;
using System.IO.Pipelines;
using TerraBase.Postgres.Hosting;

namespace TerraBase.Postgres.Protocol;

internal sealed record PgColumn(string Name, int Oid);

internal sealed class PgBackendWriter(PipeWriter output, TimeProvider timeProvider) : IDisposable
{
    private readonly PgMessageBuffer _buffer = new();

    private bool _aborted;

    public bool Aborted => _aborted;

    public PgMessageBuffer Buffer => _buffer;

    public bool ShouldFlush => _buffer.Length >= PostgresLimits.OutputFlushBytes;

    public ValueTask<bool> FlushAsync(CancellationToken cancellationToken) => FlushAsync(PostgresLimits.WriteTimeout, cancellationToken);

    public async ValueTask<bool> FlushAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (_aborted)
        {
            return false;
        }

        if (_buffer.Length > 0)
        {
            output.Write(_buffer.WrittenSpan);
            _buffer.Clear();
        }

        var effectiveTimeout = timeout < PostgresLimits.WriteTimeout ? timeout : PostgresLimits.WriteTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            _aborted = true;
            return false;
        }

        var flushTask = output.FlushAsync(cancellationToken);
        if (flushTask.IsCompletedSuccessfully)
        {
            var flushed = await flushTask;
            if (flushed.IsCompleted || flushed.IsCanceled)
            {
                _aborted = true;
                return false;
            }

            return true;
        }

        try
        {
            var flushed = await flushTask.AsTask().WaitAsync(effectiveTimeout, timeProvider, cancellationToken);
            if (flushed.IsCompleted || flushed.IsCanceled)
            {
                _aborted = true;
                return false;
            }

            return true;
        }
        catch (TimeoutException)
        {
            _aborted = true;
            return false;
        }
    }

    public ValueTask<bool> FlushIfFullAsync(CancellationToken cancellationToken) =>
        ShouldFlush ? FlushAsync(cancellationToken) : ValueTask.FromResult(true);

    public void Raw(byte value) => _buffer.WriteByte(value);

    public void Authentication(int code)
    {
        _buffer.Begin('R');
        _buffer.WriteInt32(code);
        _buffer.End();
    }

    public void ParameterStatus(string name, string value)
    {
        _buffer.Begin('S');
        _buffer.WriteCString(name);
        _buffer.WriteCString(value);
        _buffer.End();
    }

    public void BackendKeyData(int processId, int secretKey)
    {
        _buffer.Begin('K');
        _buffer.WriteInt32(processId);
        _buffer.WriteInt32(secretKey);
        _buffer.End();
    }

    public void NegotiateProtocolVersion(int newestMinor, IReadOnlyList<string> unsupported)
    {
        _buffer.Begin('v');
        _buffer.WriteInt32(newestMinor);
        _buffer.WriteInt32(unsupported.Count);
        foreach (var option in unsupported)
        {
            _buffer.WriteCString(option);
        }

        _buffer.End();
    }

    public void ReadyForQuery(char status)
    {
        _buffer.Begin('Z');
        _buffer.WriteByte((byte)status);
        _buffer.End();
    }

    public void Empty(char type)
    {
        _buffer.Begin(type);
        _buffer.End();
    }

    public void CommandComplete(string tag)
    {
        _buffer.Begin('C');
        _buffer.WriteCString(tag);
        _buffer.End();
    }

    public void ParameterDescription(IReadOnlyList<int> oids)
    {
        _buffer.Begin('t');
        _buffer.WriteInt16((short)oids.Count);
        foreach (var oid in oids)
        {
            _buffer.WriteInt32(oid);
        }

        _buffer.End();
    }

    public void RowDescription(IReadOnlyList<PgColumn> columns, IReadOnlyList<short> formats)
    {
        _buffer.Begin('T');
        _buffer.WriteInt16((short)columns.Count);
        for (var index = 0; index < columns.Count; index++)
        {
            var column = columns[index];
            _buffer.WriteCString(column.Name);
            _buffer.WriteInt32(0);
            _buffer.WriteInt16(0);
            _buffer.WriteInt32(column.Oid);
            _buffer.WriteInt16(PgOid.TryGet(column.Oid, out var type) ? type.Length : (short)-1);
            _buffer.WriteInt32(-1);
            _buffer.WriteInt16(PgValueCodec.ResultFormat(formats, index, column.Oid));
        }

        _buffer.End();
    }

    public PgError? DataRow(IReadOnlyList<SqlValue> row, IReadOnlyList<PgColumn> columns, IReadOnlyList<short> formats)
    {
        var start = _buffer.Begin('D');
        var count = row.Count;
        var columnCount = columns.Count;
        _buffer.WriteInt16((short)count);
        var formatCount = formats.Count;
        var singleBinary = formatCount == 1 && formats[0] == 1;
        for (var index = 0; index < count; index++)
        {
            var oid = index < columnCount ? columns[index].Oid : PgOid.Text;
            var binary = formatCount == 0
                ? false
                : formatCount == 1
                    ? singleBinary && PgValueCodec.SupportsBinary(oid)
                    : PgValueCodec.ResultFormat(formats, index, oid) == 1;
            if (!PgValueCodec.TryWrite(_buffer, row[index], oid, binary))
            {
                _buffer.Truncate(start);
                var name = index < columnCount ? columns[index].Name : (index + 1).ToString(CultureInfo.InvariantCulture);
                return new PgError(PgValueCodec.EncodeFailureState(row[index], oid), $"value of column \"{name}\" cannot be represented as {PgOid.FormatName(oid)}");
            }
        }

        _buffer.End();
        return null;
    }

    public void Error(PgError error) => Fields('E', error.Fatal ? "FATAL" : "ERROR", error.SqlState, error.Message, error.Detail, error.Hint);

    public void Notice(PgNotice notice) => Fields('N', "WARNING", notice.SqlState, notice.Message, null, null);

    public void CopyResponse(char type, bool binary, int columns)
    {
        _buffer.Begin(type);
        _buffer.WriteByte(binary ? (byte)1 : (byte)0);
        _buffer.WriteInt16((short)columns);
        for (var index = 0; index < columns; index++)
        {
            _buffer.WriteInt16(binary ? (short)1 : (short)0);
        }

        _buffer.End();
    }

    public void Notification(int processId, string channel, string payload)
    {
        _buffer.Begin('A');
        _buffer.WriteInt32(processId);
        _buffer.WriteCString(channel);
        _buffer.WriteCString(payload);
        _buffer.End();
    }

    public void Dispose() => _buffer.Dispose();

    private void Fields(char type, string severity, string code, string message, string? detail, string? hint)
    {
        _buffer.Begin(type);
        WriteField('S', severity);
        WriteField('V', severity);
        WriteField('C', code);
        WriteField('M', message);
        if (detail is not null)
        {
            WriteField('D', detail);
        }

        if (hint is not null)
        {
            WriteField('H', hint);
        }

        _buffer.WriteByte(0);
        _buffer.End();
    }

    private void WriteField(char field, string value)
    {
        _buffer.WriteByte((byte)field);
        _buffer.WriteCString(value);
    }
}
