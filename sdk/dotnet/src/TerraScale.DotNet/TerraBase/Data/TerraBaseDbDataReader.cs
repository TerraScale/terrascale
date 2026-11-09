using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A reader over fully materialized results: one result set per statement that returns columns.</summary>
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

    /// <summary>Creates a reader over one statement's result.</summary>
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

    /// <summary>Always 0: results are one level deep.</summary>
    public override int Depth => 0;

    /// <summary>The number of result columns.</summary>
    public override int FieldCount => result.Columns.Count;

    /// <summary>Whether the result has rows.</summary>
    public override bool HasRows => rows.Count > 0;

    /// <summary>Whether the reader is closed.</summary>
    public override bool IsClosed => closed;

    /// <summary>The rows a write changed.</summary>
    public override int RecordsAffected => affected;

    /// <summary>The value of a column of the current row.</summary>
    public override object this[int ordinal] => Row().Cell(ordinal).Box();

    /// <summary>The value of a column of the current row.</summary>
    public override object this[string name] => this[GetOrdinal(name)];

    /// <summary>Advances to the next row.</summary>
    public override bool Read()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        return ++position < rows.Count;
    }

    /// <summary>Advances to the next statement result with columns.</summary>
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

    /// <summary>A column's name.</summary>
    public override string GetName(int ordinal) => result.Columns[ordinal].Name;

    /// <summary>The ordinal of the first column with this name.</summary>
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

    /// <summary>The value of a column of the current row.</summary>
    public override object GetValue(int ordinal) => Row().Cell(ordinal).Box();

    /// <summary>Copies the current row's values into <paramref name="values"/> and returns the number copied.</summary>
    public override int GetValues(object[] values)
    {
        var row = Row();
        var count = Math.Min(values.Length, result.Columns.Count);
        for (var i = 0; i < count; i++) values[i] = row.Cell(i).Box();
        return count;
    }

    /// <summary>Whether a column of the current row is SQL NULL.</summary>
    public override bool IsDBNull(int ordinal) => Row().IsNull(ordinal);

    /// <summary>The .NET type of a column's values, from the current row (the first row before reading).</summary>
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

    /// <summary>A column's declared type, or the value's .NET type for an expression.</summary>
    public override string GetDataTypeName(int ordinal) =>
        result.Columns[ordinal].Decltype is { } decltype ? decltype : GetFieldType(ordinal).Name;

    /// <summary>The column value as a 64-bit integer.</summary>
    public override long GetInt64(int ordinal) => Row().GetInt64(ordinal);

    /// <summary>The column value as a double.</summary>
    public override double GetDouble(int ordinal) => Row().GetDouble(ordinal);

    /// <summary>The column value as text.</summary>
    public override string GetString(int ordinal) => Row().GetString(ordinal);

    /// <summary>The column value as a boolean: a nonzero integer is true.</summary>
    public override bool GetBoolean(int ordinal) => Row().GetBoolean(ordinal);

    /// <summary>The column value parsed from ISO 8601 text.</summary>
    public override DateTime GetDateTime(int ordinal) =>
        DateTime.Parse(Row().GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    internal DateTimeOffset GetDateTimeOffset(int ordinal) =>
        DateTimeOffset.Parse(Row().GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>Copies a blob column into <paramref name="buffer"/> and returns the copied length.</summary>
    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var blob = Row().GetBytes(ordinal);
        if (buffer is null) return blob.Length;
        var count = Math.Min(length, blob.Length - (int)dataOffset);
        Array.Copy(blob, (int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>The column value as a byte.</summary>
    public override byte GetByte(int ordinal) => (byte)GetInt64(ordinal);

    /// <summary>The column value as a character.</summary>
    public override char GetChar(int ordinal) => Row().Cell(ordinal) is { Text: { } text } ? text[0] : (char)GetInt64(ordinal);

    /// <summary>The column value as a 16-bit integer.</summary>
    public override short GetInt16(int ordinal) => (short)GetInt64(ordinal);

    /// <summary>The column value as a 32-bit integer.</summary>
    public override int GetInt32(int ordinal) => (int)GetInt64(ordinal);

    /// <summary>The column value as a float.</summary>
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);

    /// <summary>The column value as a decimal parsed from the exact JSON number text.</summary>
    public override decimal GetDecimal(int ordinal) => Row().Cell(ordinal) switch
    {
        { Text: { } text } => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
        { RealText: { } real } => decimal.Parse(real, NumberStyles.Float, CultureInfo.InvariantCulture),
        { IsInteger: true } integer => integer.Integer,
        _ => throw new InvalidOperationException($"Column {ordinal} is not a number."),
    };

    /// <summary>The column value as a GUID parsed from text.</summary>
    public override Guid GetGuid(int ordinal) =>
        Row().Cell(ordinal) is { Blob: { } blob } ? new Guid(blob) : Guid.Parse(GetString(ordinal));

    /// <summary>The column value as <typeparamref name="T"/>, converted like SQLite's ADO.NET provider.</summary>
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

    /// <summary>Copies a text column into <paramref name="buffer"/> and returns the copied length.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = Row().GetString(ordinal);
        if (buffer is null) return text.Length;
        var count = Math.Min(length, text.Length - (int)dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>Enumerates the rows with change notification.</summary>
    public override IEnumerator GetEnumerator() => new DbEnumerator(this);

    /// <summary>Closes the reader.</summary>
    public override void Close() => closed = true;

    private IReadOnlyList<TerraBaseSqlRecord> Rows(TerraBaseSqlStatementResult statement) =>
        singleRow && statement.Rows.Count > 0 ? [statement.Rows[0]] : statement.Rows;

    private TerraBaseSqlRecord Row() =>
        position >= 0 && position < rows.Count ? rows[position] : throw new InvalidOperationException("The reader is before the first row or past the last.");
}
