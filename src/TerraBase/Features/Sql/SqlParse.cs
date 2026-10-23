using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

/// <summary>A validated statement.</summary>
internal abstract record ParsedSql
{
    /// <summary>Whether it writes rows or changes the schema, so it needs <c>documents:write</c>.</summary>
    public abstract bool IsWrite { get; }

    /// <summary>Parameter names in SQLite's number order (see <see cref="SqlRead.Number"/>).</summary>
    public IReadOnlyList<string?> Params { get; init; } = [];
}

/// <summary><c>CREATE TABLE [IF NOT EXISTS]</c>.</summary>
/// <param name="Name">Table (collection) name.</param>
/// <param name="Table">The definition.</param>
/// <param name="Unique">Unique keys, enforced by the unique-constraint service.</param>
/// <param name="IfNotExists">Whether an existing table is a no-op.</param>
internal sealed record CreateTableSql(string Name, SqlTableDefinition Table, IReadOnlyList<UniqueConstraint> Unique, bool IfNotExists) : ParsedSql
{
    public override bool IsWrite => true;
}

/// <summary><c>DROP TABLE [IF EXISTS]</c>.</summary>
/// <param name="Name">Table name.</param>
/// <param name="IfExists">Whether a missing table is a no-op.</param>
internal sealed record DropTableSql(string Name, bool IfExists) : ParsedSql
{
    public override bool IsWrite => true;
}

/// <summary>A statement, or why it is refused.</summary>
internal readonly union SqlParseResult(ParsedSql, SqlError);

/// <summary>SQLite error codes and the errors this slice answers.</summary>
internal static class SqlErrors
{
    public static SqlError Error(string message) => new("SQLITE_ERROR", message);

    public static SqlError Auth { get; } = new("SQLITE_AUTH", "writes and schema changes need the documents:write scope");

    public static SqlError Busy(string message) => new("SQLITE_BUSY", message);

    public static SqlError TooBig(string message) => new("SQLITE_TOOBIG", message);

    public static SqlError Interrupt { get; } = new("SQLITE_INTERRUPT", "query too expensive: it exceeds the work budget");

    public static SqlError Constraint(string kind, string message) => new("SQLITE_CONSTRAINT_" + kind, message);
}

/// <summary>
/// Parses one statement with SqlParserCS (SQLite dialect) and validates its syntax tree. Like <c>SqlSubset</c>, the
/// translators reject a node by throwing <see cref="FormatException"/>; <see cref="Parse"/> catches it with the parser's own
/// exceptions, and no message quotes the client's text.
/// </summary>
internal static partial class SqlParse
{
    public const int MaxSqlLength = 16 * 1024;

    public static SqlParseResult Parse(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql) || sql.Length > MaxSqlLength)
        {
            return SqlErrors.Error("sql must be 1 to 16384 characters");
        }

        try
        {
            var (numbered, names) = SqlRead.Number(sql);
            var statements = new Parser().ParseSql(numbered, new SQLiteDialect());
            return statements.Count == 1
                ? Translate(statements[0]) with { Params = names }
                : throw new FormatException("exactly one statement is supported");
        }
        catch (ParserException exception)
        {
            return SqlErrors.Error(Syntax(exception.Line, exception.Column));
        }
        catch (TokenizeException exception)
        {
            return SqlErrors.Error(Syntax(exception.Line, exception.Column));
        }
        catch (FormatException exception)
        {
            return SqlErrors.Error(exception.Message);
        }
    }

    private static string Syntax(long line, long column) =>
        line > 0 ? $"SQL syntax error at line {line}, column {column}" : "SQL syntax error";

    private static ParsedSql Translate(Statement statement) => statement switch
    {
        Statement.Select { Query: var query } => SqlRead.Read(query),
        Statement.CreateTable { Element: var create } => CreateTable(create),
        Statement.Drop { ObjectType: ObjectType.Table } drop => DropTable(drop),
        _ => throw new FormatException("this statement is not supported"),
    };

    /// <summary>A table name: unquoted and quoted names are lowercased and follow the collection name rules.</summary>
    public static string TableName(ObjectName name) => name.Values switch
    {
        [var ident] when DatabaseName.TryParse(ident.Value.ToLowerInvariant(), out var table) =>
            CollectionName.IsReserved(table.Value) ? throw new FormatException("the table name is reserved") : table.Value,
        [_] => throw new FormatException("table names are a lowercase letter, then letters, digits, _ or -, at most 63 characters"),
        _ => throw new FormatException("schema-qualified names are not supported"),
    };

    private static DropTableSql DropTable(Statement.Drop drop) =>
        drop != new Statement.Drop(drop.Names) { ObjectType = ObjectType.Table, IfExists = drop.IfExists }
            ? throw new FormatException("DROP TABLE takes no options")
            : drop.Names is [var name]
                ? new DropTableSql(TableName(name), drop.IfExists)
                : throw new FormatException("DROP TABLE drops one table");

    private static CreateTableSql CreateTable(CreateTable create)
    {
        if (create != new CreateTable(create.Name, create.Columns) { IfNotExists = create.IfNotExists, Constraints = create.Constraints })
        {
            throw new FormatException("CREATE TABLE supports columns, PRIMARY KEY and UNIQUE only (no TEMP, AS SELECT, WITHOUT ROWID or STRICT)");
        }

        var name = TableName(create.Name);
        if (create.Columns is not { Count: > 0 and <= SqlTable.MaxColumns } columns)
        {
            throw new FormatException("a table has 1 to 256 columns");
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var key = new List<string>();
        var unique = new List<IReadOnlyList<string>>();
        var definitions = new List<SqlColumnDefinition>(columns.Count);
        foreach (var column in columns)
        {
            var columnName = ColumnName(column.Name);
            if (!seen.Add(columnName))
            {
                throw new FormatException("column names must be unique");
            }

            if (column.Collation is not null)
            {
                throw new FormatException("COLLATE is not supported");
            }

            var notNull = false;
            JsonElement? fallback = null;
            foreach (var option in column.Options ?? [])
            {
                switch (option.Option)
                {
                    case ColumnOption.NotNull:
                        notNull = true;
                        break;
                    case ColumnOption.Null:
                        break;
                    case ColumnOption.Default { Expression: var expression }:
                        fallback = Literal(expression);
                        break;
                    case ColumnOption.Unique { IsPrimary: true, Order: null, Conflict: null, Characteristics: null }:
                        key = key.Count == 0 ? [columnName] : throw new FormatException("a table has one PRIMARY KEY");
                        break;
                    case ColumnOption.Unique { IsPrimary: false, Order: null, Conflict: null, Characteristics: null }:
                        unique.Add([columnName]);
                        break;
                    case ColumnOption.DialectSpecific { Tokens: [{ } token] } when string.Equals(token.ToString(), "AUTOINCREMENT", StringComparison.OrdinalIgnoreCase):
                        break;
                    default:
                        throw new FormatException("columns support NOT NULL, NULL, DEFAULT <literal>, PRIMARY KEY and UNIQUE only");
                }
            }

            definitions.Add(new SqlColumnDefinition(columnName, column.DataType?.ToSql() ?? "", notNull, fallback));
        }

        foreach (var constraint in create.Constraints ?? [])
        {
            if (constraint is not TableConstraint.Unique { Characteristics: null, Conflict: null } table || table.Columns is not { Count: > 0 } keyColumns)
            {
                throw new FormatException("table constraints support PRIMARY KEY (…) and UNIQUE (…) only");
            }

            var named = keyColumns.Select(ident => definitions.Find(column => column.Name.Equals(ident.Value, StringComparison.OrdinalIgnoreCase))?.Name
                ?? throw new FormatException("a constraint names a column the table does not have")).ToList();
            if (named.Distinct(StringComparer.Ordinal).Count() != named.Count)
            {
                throw new FormatException("a constraint names each column once");
            }

            if (table.IsPrimaryKey)
            {
                key = key.Count == 0 ? named : throw new FormatException("a table has one PRIMARY KEY");
            }
            else
            {
                unique.Add(named);
            }
        }

        if (key.Count > 8 || unique.Count > 32 || unique.Any(fields => fields.Count > 4))
        {
            throw new FormatException("a primary key has at most 8 columns; a table has at most 32 UNIQUE constraints of at most 4 columns");
        }

        // Key columns become the document id, which is never null.
        definitions = [.. definitions.Select(column => key.Contains(column.Name) ? column with { NotNull = true } : column)];
        var constraints = unique.Select((fields, index) => new UniqueConstraint(
            string.Create(CultureInfo.InvariantCulture, $"sql_unique_{index + 1}"), fields)).ToList();
        return new CreateTableSql(name, new SqlTableDefinition(definitions, key), constraints, create.IfNotExists);
    }

    /// <summary>A column name: it names a document field, so it is a letter, then letters, digits or <c>_</c>.</summary>
    public static string ColumnName(Ident ident) => ColumnPattern().IsMatch(ident.Value)
        ? ident.Value
        : throw new FormatException("column names are a letter, then letters, digits or _, at most 64 characters");

    // A DEFAULT is a literal: a number, a string or NULL (absent).
    private static JsonElement? Literal(Expression expression) => expression switch
    {
        Expression.Nested { Expression: var inner } => Literal(inner),
        Expression.LiteralValue { Value: Value.Null } => null,
        Expression.LiteralValue { Value: Value.SingleQuotedString { Value: var text } } => JsonSerializer.SerializeToElement(text, Hosting.TerraBaseJsonContext.Default.String),
        Expression.LiteralValue { Value: Value.Boolean { Value: var flag } } => JsonSerializer.SerializeToElement(flag ? 1L : 0L, Hosting.TerraBaseJsonContext.Default.Int64),
        Expression.LiteralValue { Value: Value.Number { Value: var text } } => Number(text),
        Expression.UnaryOp { Op: UnaryOperator.Minus, Expression: Expression.LiteralValue { Value: Value.Number { Value: var text } } } => Number("-" + text),
        _ => throw new FormatException("DEFAULT must be a number, a single-quoted string or NULL"),
    };

    private static JsonElement Number(string text) =>
        long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer)
            ? JsonSerializer.SerializeToElement(integer, Hosting.TerraBaseJsonContext.Default.Int64)
            : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real)
                ? JsonSerializer.SerializeToElement(real, Hosting.TerraBaseJsonContext.Default.Double)
                : throw new FormatException("numbers must be finite");

    [GeneratedRegex(@"\A[A-Za-z][A-Za-z0-9_]{0,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnPattern();
}
