using System.Globalization;
using System.Text.Json;
using SqlParser.Ast;

namespace TerraBase.Features.Sql;

/// <summary>How an <c>INSERT</c> treats an existing row.</summary>
internal enum InsertMode
{
    /// <summary>An existing primary key fails with <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>.</summary>
    Plain,

    /// <summary><c>OR IGNORE</c>, <c>ON CONFLICT DO NOTHING</c>: an existing row is skipped.</summary>
    Ignore,

    /// <summary><c>OR REPLACE</c>, <c>REPLACE</c>: an unconditional put.</summary>
    Replace,
}

/// <summary><c>INSERT</c>: the source rows come from one engine read, then one put per row.</summary>
/// <param name="Table">Target table.</param>
/// <param name="Columns">Target columns as written, or <see langword="null"/> for every column in order.</param>
/// <param name="Source">The rows (<c>VALUES</c> or <c>SELECT</c>), or <see langword="null"/> for <c>DEFAULT VALUES</c>.</param>
/// <param name="Mode">Conflict handling.</param>
internal sealed record InsertSql(string Table, IReadOnlyList<string>? Columns, ReadSql? Source, InsertMode Mode) : ParsedSql
{
    public override bool IsWrite => true;
}

/// <summary>Translates writes and converts values by column affinity.</summary>
internal static class SqlWrite
{
    /// <summary>Most rows one statement writes: one change group.</summary>
    public const int MaxRows = 1024;

    public static InsertSql Insert(InsertOperation insert)
    {
        if (insert.Returning is not null)
        {
            throw new FormatException("RETURNING is not supported");
        }

        var mode = insert.On switch
        {
            null => insert.Or switch
            {
                SqliteOnConflict.Ignore => InsertMode.Ignore,
                SqliteOnConflict.Replace => InsertMode.Replace,
                _ => insert.ReplaceInto ? InsertMode.Replace : InsertMode.Plain,
            },
            OnInsert.Conflict { OnConflict.OnConflictAction: OnConflictAction.DoNothing } => InsertMode.Ignore,
            _ => throw new FormatException("ON CONFLICT supports DO NOTHING only"),
        };
        var columns = insert.Columns is { Count: > 0 } named ? named.Select(SqlParse.ColumnName).ToList() : null;
        return new InsertSql(SqlParse.TableName(insert.Name), columns, insert.Source is { Query: var query } ? SqlRead.Read(query) : null, mode);
    }

    /// <summary>A value converted by a column's affinity (https://sqlite.org/datatype3.html section 3).</summary>
    public static SqlValue Apply(SqlAffinity affinity, SqlValue value) => (affinity, value.Kind) switch
    {
        (SqlAffinity.Integer or SqlAffinity.Numeric, SqlValueKind.Text) when Numeric(value.Text!) is { } number => Apply(affinity, number),
        // Integral reals become integers (clients such as JavaScript send 1 as 1.0).
        (SqlAffinity.Integer or SqlAffinity.Numeric, SqlValueKind.Real) when Math.Floor(value.Real) == value.Real && Math.Abs(value.Real) < 9.2e18 =>
            SqlValue.Of((long)value.Real),
        (SqlAffinity.Real, SqlValueKind.Integer) => SqlValue.Of((double)value.Integer),
        (SqlAffinity.Real, SqlValueKind.Text) when Numeric(value.Text!) is { } number => number.Kind == SqlValueKind.Integer ? SqlValue.Of((double)number.Integer) : number,
        (SqlAffinity.Text, SqlValueKind.Integer) => SqlValue.Of(value.Integer.ToString(CultureInfo.InvariantCulture)),
        (SqlAffinity.Text, SqlValueKind.Real) => SqlValue.Of(RealText(value.Real)),
        _ => value,
    };

    private static SqlValue? Numeric(string text) =>
        long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer) ? SqlValue.Of(integer)
        : double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real) ? SqlValue.Of(real)
        : null;

    private static string RealText(double real)
    {
        var text = real.ToString("R", CultureInfo.InvariantCulture);
        return text.AsSpan().IndexOfAny(".EeN") < 0 ? text + ".0" : text;
    }

    /// <summary>A literal <c>DEFAULT</c> stored in a definition as a JSON number or string.</summary>
    public static SqlValue Default(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => SqlValue.Of(value.GetString()!),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => SqlValue.Of(integer),
        JsonValueKind.Number => SqlValue.Of(value.GetDouble()),
        _ => SqlValue.Null,
    };

    /// <summary>Writes a value as JSON; null is never stored (a missing field).</summary>
    public static void Write(Utf8JsonWriter writer, SqlValue value)
    {
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                writer.WriteNumberValue(value.Integer);
                break;
            case SqlValueKind.Real:
                writer.WriteNumberValue(value.Real);
                break;
            case SqlValueKind.Text:
                writer.WriteStringValue(value.Text);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }
}
