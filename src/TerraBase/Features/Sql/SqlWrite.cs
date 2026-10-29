using System.Buffers;
using System.Globalization;
using System.Text.Json;
using SqlParser;
using SqlParser.Ast;

namespace TerraBase.Features.Sql;

/// <summary>How an <c>INSERT</c> treats an existing row.</summary>
internal enum InsertMode
{
    /// <summary>An existing primary key fails with <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>.</summary>
    Plain,

    /// <summary><c>OR IGNORE</c>: uniqueness and NOT NULL violations skip the input row.</summary>
    Ignore,

    /// <summary><c>ON CONFLICT DO NOTHING</c>: only the selected uniqueness violation skips the row.</summary>
    DoNothing,

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

    /// <summary>The <c>RETURNING</c> list, or <see langword="null"/>.</summary>
    public IReadOnlyList<SqlReturning>? Returning { get; init; }

    /// <summary>UPSERT target columns; null means any primary or unique key.</summary>
    public IReadOnlyList<string>? ConflictColumns { get; init; }
}

/// <summary>A <c>RETURNING</c> item: a column of the written table, or every column for <c>*</c>.</summary>
/// <param name="Column">The column as written, or <see langword="null"/> for <c>*</c>.</param>
/// <param name="Alias">The result column name given with <c>AS</c>.</param>
internal sealed record SqlReturning(string? Column, string? Alias);

/// <summary><c>UPDATE</c> or <c>DELETE</c>: an engine read of the row ids and new values, then one patch or delete per row.</summary>
/// <param name="Table">Target table.</param>
/// <param name="Rows">
/// The read: column 0 is <c>_id</c>, then one column per <c>set</c> or <c>inc</c> entry of <see cref="Assignments"/>, then
/// with <see cref="Returning"/> every column of the table (<c>*</c>) as it was.
/// </param>
/// <param name="Assignments">Per assigned column: <c>set</c> (value from the read), <c>unset</c>, or <c>inc</c> by a literal; empty for a delete.</param>
internal sealed record ChangeSql(string Table, ReadSql Rows, IReadOnlyList<(string Column, string Op, long By)> Assignments) : ParsedSql
{
    public override bool IsWrite => true;

    /// <summary>Whether this is a <c>DELETE</c>.</summary>
    public bool IsDelete => Assignments.Count == 0;

    /// <summary>The <c>RETURNING</c> list, or <see langword="null"/>: new values for an update, old ones for a delete.</summary>
    public IReadOnlyList<SqlReturning>? Returning { get; init; }
}

/// <summary>Translates writes and converts values by column affinity.</summary>
internal static class SqlWrite
{
    /// <summary>Most rows one statement writes: one change group.</summary>
    public const int MaxRows = 1024;

    public static InsertSql Insert(InsertOperation insert)
    {
        if (insert.Or is SqliteOnConflict.Fail or SqliteOnConflict.Rollback || ((insert.Or != SqliteOnConflict.None || insert.ReplaceInto) && insert.On is not null))
        {
            throw new FormatException("OR FAIL, OR ROLLBACK and combining OR with ON CONFLICT are not supported");
        }

        var mode = insert.On switch
        {
            null => insert.Or switch
            {
                SqliteOnConflict.Ignore => InsertMode.Ignore,
                SqliteOnConflict.Replace => InsertMode.Replace,
                _ => insert.ReplaceInto ? InsertMode.Replace : InsertMode.Plain,
            },
            OnInsert.Conflict { OnConflict.OnConflictAction: OnConflictAction.DoNothing } => InsertMode.DoNothing,
            _ => throw new FormatException("ON CONFLICT supports DO NOTHING only"),
        };
        var columns = insert.Columns is { Count: > 0 } named ? named.Select(SqlParse.ColumnName).ToList() : null;
        var table = SqlParse.TableName(insert.Name);
        return new InsertSql(table, columns, insert.Source is { Query: var query } ? SqlRead.Read(query) : null, mode)
        {
            Returning = Returning(insert.Returning, table),
            ConflictColumns = insert.On is OnInsert.Conflict { OnConflict.ConflictTarget: { } target } ? target switch
            {
                ConflictTarget.Column { Columns: var names } => names.Select(SqlParse.ColumnName).ToList(),
                _ => throw new FormatException("ON CONFLICT targets a primary or unique column list only"),
            } : null,
        };
    }

    public static ChangeSql Update(Statement.Update update)
    {
        if (update is not { Table: { Joins: null or [], Relation: { } relation }, From: null })
        {
            throw new FormatException("UPDATE supports one table without FROM");
        }

        var table = Target(relation);
        var values = new List<string>();
        var assignments = new List<(string, string, long)>();
        foreach (var assignment in update.Assignments)
        {
            var column = assignment.Target is AssignmentTarget.ColumnName { Name.Values: [var ident] }
                ? SqlParse.ColumnName(ident)
                : throw new FormatException("SET assigns one column at a time");
            switch (assignment.Value)
            {
                case Expression.LiteralValue { Value: Value.Null }:
                    assignments.Add((column, "unset", 0));
                    break;
                case Expression.BinaryOp { Left: Expression.Identifier { Ident: var same }, Op: var op and (BinaryOperator.Plus or BinaryOperator.Minus), Right: Expression.LiteralValue { Value: Value.Number { Value: var text } } }
                    when same.Value.Equals(column, StringComparison.OrdinalIgnoreCase)
                        && long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var by) && by != 0:
                    // A counter where the column is an INTEGER NOT NULL (decided with the definition): concurrent increments
                    // add across regions. The computed value is read too, for any other column.
                    assignments.Add((column, "inc", op == BinaryOperator.Plus ? by : -by));
                    values.Add("(" + assignment.Value.ToSql() + ")");
                    break;
                case var value:
                    assignments.Add((column, "set", 0));
                    values.Add("(" + value.ToSql() + ")");
                    break;
            }
        }

        var returning = Returning(update.Returning, table);
        return new ChangeSql(table, Select(relation, values, update.Selection, returning is not null), assignments) { Returning = returning };
    }

    public static ChangeSql Delete(DeleteOperation delete)
    {
        if (delete is not { Tables: null or [], Using: null, OrderBy: null or [], Limit: null, From.From: [{ Joins: null or [], Relation: { } relation }] })
        {
            throw new FormatException("DELETE supports one table without USING, ORDER BY or LIMIT");
        }

        var table = Target(relation);
        var returning = Returning(delete.Returning, table);
        return new ChangeSql(table, Select(relation, [], delete.Selection, returning is not null), []) { Returning = returning };
    }

    // RETURNING lists columns of the written table (optionally qualified with its name) or *, each optionally AS a name.
    private static List<SqlReturning>? Returning(Sequence<SelectItem>? items, string table) => items?.Select(item => item switch
    {
        SelectItem.Wildcard => new SqlReturning(null, null),
        SelectItem.UnnamedExpression { Expression: var expression } => new SqlReturning(Returned(expression, table), null),
        SelectItem.ExpressionWithAlias { Expression: var expression, Alias: var alias } => new SqlReturning(Returned(expression, table), alias.Value),
        _ => throw new FormatException("RETURNING lists columns of the table or *, each optionally AS a name"),
    }).ToList();

    private static string Returned(Expression expression, string table) => expression switch
    {
        Expression.Identifier { Ident: var ident } => SqlParse.ColumnName(ident),
        Expression.CompoundIdentifier { Idents: [var name, var ident] } when name.Value.Equals(table, StringComparison.OrdinalIgnoreCase) => SqlParse.ColumnName(ident),
        _ => throw new FormatException("RETURNING lists columns of the table or *, each optionally AS a name"),
    };

    private static string Target(TableFactor relation) => relation is TableFactor.Table { Name: var name }
        ? SqlParse.TableName(name)
        : throw new FormatException("the target of a write is a table");

    // The rows a change addresses, read with their ids (and new values) from its validated syntax: one past the limit
    // tells that the statement is too big.
    private static ReadSql Select(TableFactor relation, List<string> values, Expression? where, bool everyColumn)
    {
        var sql = $"SELECT \"_id\"{string.Concat(values.Select(value => ", " + value))}{(everyColumn ? ", *" : "")} FROM {relation.ToSql()}"
            + (where is null ? "" : " WHERE " + where.ToSql()) + $" LIMIT {MaxRows + 1}";
        return new Parser().ParseSql(sql, new SqlDialect()) is [Statement.Select { Query: var query }]
            ? SqlRead.Read(query)
            : throw new FormatException("the statement's rows cannot be read");
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

    /// <summary>A value as a standalone JSON element.</summary>
    public static JsonElement Json(SqlValue value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            Write(writer, value);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }
}
