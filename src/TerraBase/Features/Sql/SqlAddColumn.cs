using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace TerraBase.Features.Sql;

/// <summary>Adds one nullable column without rewriting rows.</summary>
/// <param name="Table">Table name.</param>
/// <param name="Column">The stored column definition.</param>
/// <param name="Definition">The syntax tree used to update the catalog text.</param>
internal sealed record AddColumnSql(string Table, SqlColumnDefinition Column, ColumnDef Definition) : ParsedSql
{
    public override bool IsWrite => true;
}

internal static partial class SqlParse
{
    private static bool AlterIfNotExists(string sql)
    {
        var tokens = new Tokenizer().Tokenize(sql, new SQLiteDialect()).Where(token => token is not Whitespace).ToArray();
        for (var i = 0; i + 2 < tokens.Length; i++)
        {
            if (tokens[i] is Word { Keyword: Keyword.IF } && tokens[i + 1] is Word { Keyword: Keyword.NOT }
                && tokens[i + 2] is Word { Keyword: Keyword.EXISTS })
            {
                return true;
            }
        }

        return false;
    }

    private static AddColumnSql AddColumn(Statement.AlterTable alter)
    {
        const string supported = "ALTER TABLE supports ADD [COLUMN] name [type] [NULL or DEFAULT NULL] only";
        if (alter is not
            {
                IfExists: false, Only: false, Location: null, OnCluster: null,
                Operations: [AlterTableOperation.AddColumn { IfNotExists: false, ColumnPosition: null, ColumnDef: var column }],
            })
        {
            throw new FormatException(supported);
        }

        var name = ColumnName(column.Name);
        var type = column.DataType?.ToSql() ?? "";
        if (column.Collation is not null || SqlTable.IsSerial(type))
        {
            throw new FormatException(supported);
        }

        foreach (var option in column.Options ?? [])
        {
            if (option.Name is not null || option.Option is not ColumnOption.Null
                && option.Option is not ColumnOption.Default)
            {
                throw new FormatException(supported);
            }

            if (option.Option is ColumnOption.Default fallback && Literal(fallback.Expression) is not null)
            {
                throw new FormatException(supported);
            }
        }

        return new AddColumnSql(TableName(alter.Name), new SqlColumnDefinition(name, type, NotNull: false), column);
    }
}
