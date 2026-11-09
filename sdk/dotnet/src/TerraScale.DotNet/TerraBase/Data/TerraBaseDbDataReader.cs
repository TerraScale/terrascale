using System.Collections;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A reader over one statement's fully materialized result.</summary>
public sealed class TerraBaseDbDataReader(TerraBaseSqlStatementResult result, bool singleRow = false) : DbDataReader
{
    private readonly IReadOnlyList<TerraBaseSqlRecord> rows = singleRow && result.Rows.Count > 0 ? [result.Rows[0]] : result.Rows;
    private int position = -1;
    private bool closed;

    /// <summary>Always 0: results are one level deep.</summary>
    public override int Depth => 0;

    /// <summary>The number of result columns.</summary>
    public override int FieldCount => result.Columns.Count;

    /// <summary>Whether the result has rows.</summary>
    public override bool HasRows => rows.Count > 0;

    /// <summary>Whether the reader is closed.</summary>
    public override bool IsClosed => closed;

    /// <summary>The rows a write changed.</summary>
    public override int RecordsAffected => (int)result.RowsAffected;

    /// <summary>The value of a column of the current row.</summary>
    public override object this[int ordinal] => Box(Row()[ordinal]);

    /// <summary>The value of a column of the current row.</summary>
    public override object this[string name] => this[GetOrdinal(name)];

    /// <summary>Advances to the next row.</summary>
    public override bool Read()
    {
        ObjectDisposedException.ThrowIf(closed, this);
        return ++position < rows.Count;
    }

    /// <summary>Always false: one statement has one result.</summary>
    public override bool NextResult() => false;

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
    public override object GetValue(int ordinal) => Box(Row()[ordinal]);

    /// <summary>Copies the current row's values into <paramref name="values"/> and returns the number copied.</summary>
    public override int GetValues(object[] values)
    {
        var row = Row();
        var count = Math.Min(values.Length, result.Columns.Count);
        for (var i = 0; i < count; i++) values[i] = Box(row[i]);
        return count;
    }

    /// <summary>Whether a column of the current row is SQL NULL.</summary>
    public override bool IsDBNull(int ordinal) => Row().IsNull(ordinal);

    /// <summary>The .NET type of a column's values, from the current row (the first row before reading).</summary>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicFields)]
    public override Type GetFieldType(int ordinal)
    {
        var row = position >= 0 && position < rows.Count ? rows[position] : rows.Count > 0 ? rows[0] : null;
        return row is null ? typeof(object) : row[ordinal].ValueKind switch
        {
            JsonValueKind.Number => row[ordinal].TryGetInt64(out _) ? typeof(long) : typeof(double),
            JsonValueKind.String => typeof(string),
            JsonValueKind.Object => typeof(byte[]),
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
    public override char GetChar(int ordinal) => (char)GetInt64(ordinal);

    /// <summary>The column value as a 16-bit integer.</summary>
    public override short GetInt16(int ordinal) => (short)GetInt64(ordinal);

    /// <summary>The column value as a 32-bit integer.</summary>
    public override int GetInt32(int ordinal) => (int)GetInt64(ordinal);

    /// <summary>The column value as a float.</summary>
    public override float GetFloat(int ordinal) => (float)GetDouble(ordinal);

    /// <summary>The column value as a decimal.</summary>
    public override decimal GetDecimal(int ordinal) => (decimal)GetDouble(ordinal);

    /// <summary>The column value as a GUID parsed from text.</summary>
    public override Guid GetGuid(int ordinal) => Guid.Parse(GetString(ordinal));

    /// <summary>Copies a text column into <paramref name="buffer"/> and returns the copied length.</summary>
    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var text = Row().GetString(ordinal);
        if (buffer is null) return text.Length;
        var count = Math.Min(length, text.Length - (int)dataOffset);
        text.CopyTo((int)dataOffset, buffer, bufferOffset, count);
        return count;
    }

    /// <summary>Enumerates the rows.</summary>
    public override IEnumerator GetEnumerator() => rows.GetEnumerator();

    /// <summary>Closes the reader.</summary>
    public override void Close() => closed = true;

    internal static object Box(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null => DBNull.Value,
        JsonValueKind.Number => value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Object => value.GetProperty("base64").GetBytesFromBase64(),
        _ => throw new InvalidOperationException($"A {value.ValueKind} value cannot be boxed."),
    };

    private TerraBaseSqlRecord Row() =>
        position >= 0 && position < rows.Count ? rows[position] : throw new InvalidOperationException("The reader is before the first row or past the last.");
}
