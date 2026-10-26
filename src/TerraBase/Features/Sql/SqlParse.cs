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

/// <summary><c>CREATE [UNIQUE] INDEX [IF NOT EXISTS]</c>: a secondary index, and for UNIQUE also a unique key of the same name.</summary>
/// <param name="Name">Index name, unique in the database.</param>
/// <param name="Table">Table name.</param>
/// <param name="Columns">1 to 4 indexed columns, as written.</param>
/// <param name="Unique">Whether the columns are a unique key.</param>
/// <param name="IfNotExists">Whether an existing index name is a no-op.</param>
internal sealed record CreateIndexSql(string Name, string Table, IReadOnlyList<string> Columns, bool Unique, bool IfNotExists) : ParsedSql
{
    public override bool IsWrite => true;
}

/// <summary><c>DROP INDEX [IF EXISTS]</c>.</summary>
/// <param name="Name">Index name.</param>
/// <param name="IfExists">Whether a missing index is a no-op.</param>
internal sealed record DropIndexSql(string Name, bool IfExists) : ParsedSql
{
    public override bool IsWrite => true;
}

/// <summary>What a transaction statement does.</summary>
internal enum TransactionKind
{
    Begin,
    BeginReadOnly,
    Commit,
    Rollback,
}

/// <summary><c>BEGIN</c>, <c>BEGIN READONLY</c>, <c>COMMIT</c>/<c>END</c> or <c>ROLLBACK</c>: handled by the session.</summary>
/// <param name="Kind">What it does.</param>
internal sealed record TransactionSql(TransactionKind Kind) : ParsedSql
{
    public override bool IsWrite => false;
}

/// <summary>The pragmas ORMs and libSQL clients send.</summary>
internal enum PragmaKind
{
    /// <summary><c>PRAGMA foreign_keys = ON|OFF</c>: a no-op, since FOREIGN KEY clauses are accepted and never enforced.</summary>
    ForeignKeys,

    /// <summary><c>PRAGMA read_uncommitted</c>: always 0.</summary>
    ReadUncommitted,

    /// <summary><c>PRAGMA [main.]table_info(t)</c>: one row per column, as SQLite answers.</summary>
    TableInfo,

    /// <summary><c>PRAGMA temp.table_info(t)</c> (and every other <c>temp.</c> catalog pragma): no rows, since there are no temporary tables.</summary>
    TempTableInfo,

    /// <summary><c>PRAGMA table_xinfo(t)</c>: table_info with a <c>hidden</c> column.</summary>
    TableXinfo,

    /// <summary><c>PRAGMA index_list(t)</c>: the table's indexes.</summary>
    IndexList,

    /// <summary><c>PRAGMA index_info(i)</c>: the index's columns.</summary>
    IndexInfo,

    /// <summary><c>PRAGMA foreign_key_list(t)</c>: the table's FOREIGN KEY clauses.</summary>
    ForeignKeyList,
}

/// <summary>A pragma (see <see cref="PragmaKind"/>).</summary>
/// <param name="Kind">Which.</param>
/// <param name="Table">The pragma's argument: a table, or the index of <c>index_info</c>.</param>
internal sealed record PragmaSql(PragmaKind Kind, string? Table = null) : ParsedSql
{
    public override bool IsWrite => false;
}

/// <summary>A statement, or why it is refused.</summary>
internal readonly union SqlParseResult(ParsedSql, SqlError);

/// <summary>SQLite error codes and the errors this slice answers.</summary>
internal static class SqlErrors
{
    public static SqlError Error(string message) => new("SQLITE_ERROR", message);

    public static SqlError NonFinite { get; } = Error("nonfinite numbers are not supported");

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

        // SqlParserCS has no READONLY transactions.
        if (ReadOnlyBegin().IsMatch(sql))
        {
            return new TransactionSql(TransactionKind.BeginReadOnly);
        }

        if (Pragma(sql) is { } pragma)
        {
            return pragma;
        }

        try
        {
            var (numbered, names) = SqlRead.Number(sql);
            var statements = new Parser().ParseSql(numbered, new SQLiteDialect());
            // SQLiteDialect silently drops ADD IF NOT EXISTS; inspect tokens without changing dialect or quoting.
            if (statements is [Statement.AlterTable] && AlterIfNotExists(numbered))
            {
                throw new FormatException("ALTER TABLE does not support IF NOT EXISTS");
            }

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

    // SqlParserCS takes no keyword (ON/OFF) as a pragma value, and no schema before a pragma name.
    private static SqlParseResult? Pragma(string sql)
    {
        if (ForeignKeysPragma().IsMatch(sql))
        {
            return new PragmaSql(PragmaKind.ForeignKeys);
        }

        if (ReadUncommittedPragma().IsMatch(sql))
        {
            return new PragmaSql(PragmaKind.ReadUncommitted);
        }

        if (TableInfoPragma().Match(sql) is not { Success: true } info)
        {
            return null;
        }

        var name = info.Groups["name"].Value.ToLowerInvariant();
        var kind = info.Groups["pragma"].Value.ToLowerInvariant() switch
        {
            _ when info.Groups["temp"].Success => PragmaKind.TempTableInfo,
            "table_xinfo" => PragmaKind.TableXinfo,
            "index_list" => PragmaKind.IndexList,
            "index_info" => PragmaKind.IndexInfo,
            "foreign_key_list" => PragmaKind.ForeignKeyList,
            _ => PragmaKind.TableInfo,
        };
        // An index name is answered as given; a table name follows the table name rules.
        return kind == PragmaKind.IndexInfo ? new PragmaSql(kind, name)
            : !DatabaseName.TryParse(name, out var table) || CollectionName.IsReserved(table.Value)
                ? SqlErrors.Error("table names are a lowercase letter, then letters, digits, _ or -, at most 63 characters")
                : new PragmaSql(kind, table.Value);
    }

    private static string Syntax(long line, long column) =>
        line > 0 ? $"SQL syntax error at line {line}, column {column}" : "SQL syntax error";

    private static ParsedSql Translate(Statement statement) => statement switch
    {
        Statement.StartTransaction => new TransactionSql(TransactionKind.Begin),
        Statement.Commit { Chain: false } => new TransactionSql(TransactionKind.Commit),
        Statement.Rollback { Chain: false, SavePoint: null } => new TransactionSql(TransactionKind.Rollback),
        Statement.Select { Query: var query } => SqlRead.Read(query),
        Statement.Insert { InsertOperation: var insert } => SqlWrite.Insert(insert),
        Statement.Update update => SqlWrite.Update(update),
        Statement.Delete { DeleteOperation: var delete } => SqlWrite.Delete(delete),
        Statement.CreateTable { Element: var create } => CreateTable(create),
        Statement.AlterTable alter => AddColumn(alter),
        Statement.Drop { ObjectType: ObjectType.Table } drop => DropTable(drop),
        Statement.CreateIndex { Element: var index } => CreateIndex(index),
        Statement.Drop { ObjectType: ObjectType.Index } drop => DropIndex(drop),
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

    /// <summary>An index name: lowercased, a lowercase letter, then lowercase letters, digits or <c>_</c>, at most 63 characters.</summary>
    public static string IndexName(ObjectName? name) => name?.Values switch
    {
        [var ident] when IndexPattern().IsMatch(ident.Value.ToLowerInvariant()) => ident.Value.ToLowerInvariant(),
        [_] or null => throw new FormatException("index names are a letter, then letters, digits or _, at most 63 characters"),
        _ => throw new FormatException("schema-qualified names are not supported"),
    };

    private static DropIndexSql DropIndex(Statement.Drop drop) =>
        drop != new Statement.Drop(drop.Names) { ObjectType = ObjectType.Index, IfExists = drop.IfExists }
            ? throw new FormatException("DROP INDEX takes no options")
            : drop.Names is [var name]
                ? new DropIndexSql(IndexName(name), drop.IfExists)
                : throw new FormatException("DROP INDEX drops one index");

    private static CreateIndexSql CreateIndex(CreateIndex create)
    {
        if (create != new CreateIndex(create.Name, create.TableName) { Columns = create.Columns, Unique = create.Unique, IfNotExists = create.IfNotExists })
        {
            throw new FormatException("CREATE INDEX supports UNIQUE, IF NOT EXISTS and columns only (no partial or expression indexes)");
        }

        if (create.Columns is not { Count: > 0 and <= 4 } columns)
        {
            throw new FormatException("an index has 1 to 4 columns");
        }

        var named = columns.Select(column => column is { Expression: Expression.Identifier { Ident: var ident }, NullsFirst: null, WithFill: null }
            ? ColumnName(ident)
            : throw new FormatException("an index lists columns, each optionally ASC or DESC")).ToList();
        return named.Distinct(StringComparer.OrdinalIgnoreCase).Count() == named.Count
            ? new CreateIndexSql(IndexName(create.Name), TableName(create.TableName), named, create.Unique, create.IfNotExists)
            : throw new FormatException("an index names each column once");
    }

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
        var foreignKeys = new List<SqlForeignKey>();
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
                    // Accepted and not enforced, as in SQLite with foreign keys off (its default); kept for the catalog.
                    case ColumnOption.ForeignKey reference:
                        foreignKeys.Add(ForeignKey(reference.Name, [columnName], reference.ReferredColumns, reference.OnUpdateAction, reference.OnDeleteAction));
                        break;
                    default:
                        throw new FormatException("columns support NOT NULL, NULL, DEFAULT <literal>, PRIMARY KEY, UNIQUE and REFERENCES only");
                }
            }

            definitions.Add(new SqlColumnDefinition(columnName, column.DataType?.ToSql() ?? "", notNull, fallback));
        }

        foreach (var constraint in create.Constraints ?? [])
        {
            // Accepted and not enforced, as in SQLite with foreign keys off; its columns must exist.
            if (constraint is TableConstraint.ForeignKey { Columns: var foreign } reference)
            {
                if (foreign.Any(ident => !seen.Contains(ident.Value)))
                {
                    throw new FormatException("a constraint names a column the table does not have");
                }

                foreignKeys.Add(ForeignKey(reference.ForeignTable, [.. foreign.Select(ident => definitions.First(column => column.Name.Equals(ident.Value, StringComparison.OrdinalIgnoreCase)).Name)],
                    reference.ReferredColumns, reference.OnUpdate, reference.OnDelete));
                continue;
            }

            if (constraint is not TableConstraint.Unique { Characteristics: null, Conflict: null } table || table.Columns is not { Count: > 0 } keyColumns)
            {
                throw new FormatException("table constraints support PRIMARY KEY (…), UNIQUE (…) and FOREIGN KEY (…) only");
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
        // SQLite's catalog keeps the statement without IF NOT EXISTS.
        var text = (create with { IfNotExists = false }).ToSql();
        return new CreateTableSql(name, new SqlTableDefinition(definitions, key, foreignKeys.Count > 0 ? foreignKeys : null, text), constraints, create.IfNotExists);
    }

    // A FOREIGN KEY clause as the catalog shows it; the referenced table need not exist, as in SQLite.
    private static SqlForeignKey ForeignKey(ObjectName table, IReadOnlyList<string> from, Sequence<Ident>? to, ReferentialAction? onUpdate, ReferentialAction? onDelete) =>
        to is { Count: > 0 } && to.Count != from.Count
            ? throw new FormatException("a FOREIGN KEY names as many referenced columns as columns")
            : new SqlForeignKey(TableName(table), from, [.. (to ?? []).Select(ColumnName)], Action(onUpdate), Action(onDelete));

    private static string Action(ReferentialAction? action) => action switch
    {
        ReferentialAction.Restrict => "RESTRICT",
        ReferentialAction.Cascade => "CASCADE",
        ReferentialAction.SetNull => "SET NULL",
        ReferentialAction.SetDefault => "SET DEFAULT",
        _ => "NO ACTION",
    };

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

    [GeneratedRegex(@"\A[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex IndexPattern();

    [GeneratedRegex(@"\A\s*BEGIN(\s+TRANSACTION)?\s+READONLY\s*;?\s*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReadOnlyBegin();

    [GeneratedRegex(@"\A\s*PRAGMA\s+(main\s*\.\s*)?foreign_keys\s*=\s*(on|off|true|false|yes|no|0|1)\s*;?\s*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ForeignKeysPragma();

    [GeneratedRegex(@"\A\s*PRAGMA\s+(main\s*\.\s*)?read_uncommitted\s*;?\s*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex ReadUncommittedPragma();

    [GeneratedRegex(@"\A\s*PRAGMA\s+(?:main\s*\.\s*|(?<temp>temp)\s*\.\s*)?(?<pragma>table_info|table_xinfo|index_list|index_info|foreign_key_list)\s*\(\s*(?<q>[""'`]?)(?<name>[A-Za-z][A-Za-z0-9_-]{0,127})\k<q>\s*\)\s*;?\s*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TableInfoPragma();
}
