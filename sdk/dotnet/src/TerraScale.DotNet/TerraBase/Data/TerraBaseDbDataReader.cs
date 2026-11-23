using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

public sealed class TerraBaseDbDataReader : DbDataReader
{
    private static readonly TerraBaseSqlStatementResult NoResult = new([], [], 0, null);
    private readonly TerraBaseSqlStatementResult[] sets;
    private readonly int affected;
    private readonly bool singleRow;
    private int set;
    private TerraBaseSqlStatementResult result;
    private IReadOnlyList<TerraBaseSqlRecord> rows;
    private int position = -1;
    private bool closed;

    public TerraBaseDbDataReader(TerraBaseSqlStatementResult result, bool singleRow = false)
        : this([result], singleRow)
    {
    }

    internal TerraBaseDbDataReader(IReadOnlyList<TerraBaseSqlStatementResult> results, bool singleRow)
    {
        ArgumentNullException.ThrowIfNull(results);
        sets = [.. results.Where(statement => statement.Columns.Count > 0)];
        long changed = 0;
        foreach (var statement in results) changed += statement.RowsAffected;
        affected = (int)changed;
        this.singleRow = singleRow;
        result = sets.Length > 0 ? sets[0] : NoResult;
        rows = Rows(result);
    }

    public override int Depth => 0;

    public override int FieldCount => result.Columns.Count;

    public override bool HasRows => rows.Count > 0;

    public override bool IsClosed => closed;

    public override int RecordsAffected => affected;

    public override object this[int ordinal] => Row().Cell(ordinal).Box();

    public override object this[string name] => this[GetOrdinal(name)];

    public override bool Read()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        return ++position < rows.Count;
    }

    public override bool NextResult()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        if (set + 1 >= sets.Length)
        {
            set = sets.Length;
            result = NoResult;
            rows = [];
            position = -1;
            return false;
        }

        result = sets[++set];
        rows = Rows(result);
        position = -1;
        return true;
    }

    public override string GetName(int ordinal) => result.Columns[ordinal].Name;

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i].Name, name, StringComparison.Ordinal)) return i;
        }

        for (var i = 0; i < result.Columns.Count; i++)
        {
            if (string.Equals(result.Columns[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }

        throw new IndexOutOfRangeException($"The result has no column '{name}'.");
    }

    public override object GetValue(int ordinal) => Row().Cell(ordinal).Box();

    public override int GetValues(object[] values)
    {
        var row = Row();
        var count = Math.Min(values.Length, result.Columns.Count);
        for (var i = 0; i < count; i++) values[i] = row.Cell(i).Box();
        return count;
    }

    public override bool IsDBNull(int ordinal) => Row().IsNull(ordinal);

    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public override Type GetFieldType(int ordinal)
    {
        var row = position >= 0 && position < rows.Count ? rows[position] : rows.Count > 0 ? rows[0] : null;
        return row is null ? typeof(object) : row.Cell(ordinal) switch
        {
            { IsInteger: true } => typeof(long),
            { RealText: not null } => typeof(double),
            { Text: not null } => typeof(string),
            { Blob: not null } => typeof(byte[]),
            _ => typeof(object),
        };
    }

    public override string GetDataTypeName(int ordinal) =>
        result.Columns[ordinal].Decltype is { } decltype ? decltype : GetFieldType(ordinal).Name;

    public override long GetInt64(int ordinal) => Row().GetInt64(ordinal);

    public override double GetDouble(int ordinal) => Row().GetDouble(ordinal);

    public override string GetString(int ordinal) => Row().GetString(ordinal);

    public override bool GetBoolean(int ordinal) => Row().GetBoolean(ordinal);

    public override DateTime GetDateTime(int ordinal) =>
        DateTime.Parse(Row().GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal DateTimeOffset GetDateTimeOffset(int ordinal) =>
        DateTimeOffset.Parse(Row().GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var blob = Row().GetBytes(ordinal);
        if (buffer is null) return blob.Length;
        var count = Math.Min(length, blob.Length - (int)dataOffset);
        Array.Copy(blob, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override byte GetByte(int ordinal) => (byte)GetInt64(ordinal);

    public override char GetChar(int ordinal) => Row().Cell(ordinal) is { Text: { } text } ? text[0] : (char)GetInt64(ordinal);

    public override short GetInt16(int ordinal) => (short)GetInt64(ordinal);

    public override int GetInt32(int ordinal) => (int)GetInt64(ordinal);

    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);

    public override decimal GetDecimal(int ordinal) => Row().Cell(ordinal) switch
    {
        { Text: { } text } => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
        { RealText: { } real } => decimal.Parse(real, NumberStyles.Float, CultureInfo.InvariantCulture),
        { IsInteger: true } integer => integer.Integer,
        _ => throw new InvalidOperationException($"Column {ordinal} is not a number."),
    };

    public override Guid GetGuid(int ordinal) =>
        Row().Cell(ordinal) is { Blob: { } blob } ? new Guid(blob) : Guid.Parse(GetString(ordinal));

    public override T GetFieldValue<T>(int ordinal)
    {
        object value = typeof(T) == typeof(DateTimeOffset) ? GetDateTimeOffset(ordinal)
            : typeof(T) == typeof(DateTime) ? GetDateTime(ordinal)
            : typeof(T) == typeof(DateOnly) ? DateOnly.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
            : typeof(T) == typeof(TimeOnly) ? TimeOnly.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
            : typeof(T) == typeof(TimeSpan) ? TimeSpan.Parse(GetString(ordinal), CultureInfo.InvariantCulture)
            : typeof(T) == typeof(Guid) ? GetGuid(ordinal)
            : typeof(T) == typeof(decimal) ? GetDecimal(ordinal)
            : typeof(T) == typeof(byte[]) ? Row().GetBytes(ordinal)
            : typeof(T) == typeof(string) ? GetString(ordinal)
            : typeof(T) == typeof(bool) ? GetBoolean(ordinal)
            : typeof(T) == typeof(long) ? GetInt64(ordinal)
            : typeof(T) == typeof(int) ? GetInt32(ordinal)
            : typeof(T) == typeof(short) ? GetInt16(ordinal)
            : typeof(T) == typeof(byte) ? GetByte(ordinal)
            : typeof(T) == typeof(sbyte) ? (sbyte)GetInt64(ordinal)
            : typeof(T) == typeof(ushort) ? (ushort)GetInt64(ordinal)
            : typeof(T) == typeof(uint) ? (uint)GetInt64(ordinal)
            : typeof(T) == typeof(ulong) ? unchecked((ulong)GetInt64(ordinal))
            : typeof(T) == typeof(char) ? GetChar(ordinal)
            : typeof(T) == typeof(double) ? GetDouble(ordinal)
            : typeof(T) == typeof(float) ? GetFloat(ordinal)
            : GetValue(ordinal);
        return (T)value;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = Row().GetString(ordinal);
        if (buffer is null) return text.Length;
        var count = Math.Min(length, text.Length - (int)dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    public override void Close() => closed = true;

    private IReadOnlyList<TerraBaseSqlRecord> Rows(TerraBaseSqlStatementResult statement) =>
        singleRow && statement.Rows.Count > 0 ? [statement.Rows[0]] : statement.Rows;

    private TerraBaseSqlRecord Row() =>
        position >= 0 && position < rows.Count ? rows[position] : throw new InvalidOperationException("The reader is before the first row or past the last.");
}
