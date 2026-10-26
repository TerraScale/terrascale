using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace TerraBase.Features.Sql;

/// <summary>SQLite parsing at the client SQL boundary.</summary>
internal sealed class SqlDialect : SQLiteDialect
{
    public override Statement? ParseStatement(Parser parser)
    {
        if (!IsUnaliasedDeleteReturning(parser))
        {
            return base.ParseStatement(parser);
        }

        // SqlParserCS 0.6.5 omits RETURNING from ReservedForTableAlias, so ParseDelete consumes it as an alias.
        // Use its public grammar methods for this form, preserving tokens, locations and normal statement delimiters.
        parser.ExpectKeyword(Keyword.DELETE);
        parser.ExpectKeyword(Keyword.FROM);
        var table = new TableWithJoins(new TableFactor.Table(parser.ParseObjectName()));
        parser.ExpectKeyword(Keyword.RETURNING);
        return new Statement.Delete(new DeleteOperation(null, new FromTable.WithFromKeyword([table]),
            Returning: parser.ParseCommaSeparated(parser.ParseSelectItem)));
    }

    private static bool IsUnaliasedDeleteReturning(Parser parser)
    {
        if (parser.PeekToken() is not Word { Keyword: Keyword.DELETE }
            || parser.PeekNthToken(1) is not Word { Keyword: Keyword.FROM })
        {
            return false;
        }

        // Inspect only a possibly qualified, quoted table name. Whitespace and comments are skipped by the parser.
        var name = true;
        foreach (var token in parser.PeekTokensWithLocation(int.MaxValue).Skip(2))
        {
            if (name)
            {
                if (token is not Word)
                {
                    return false;
                }
            }
            else if (token is not Period)
            {
                return token is Word { Keyword: Keyword.RETURNING, QuoteStyle: null };
            }

            name = !name;
        }

        return false;
    }
}
