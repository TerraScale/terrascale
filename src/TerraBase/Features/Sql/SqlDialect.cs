using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace TerraBase.Features.Sql;

/// <summary>SQLite parsing at the client SQL boundary.</summary>
internal sealed class SqlDialect : SQLiteDialect
{
    // SQLite groups IS with equality/BETWEEN/IN/LIKE, below ordering comparisons and bitwise operators.
    public override short GetPrecedence(Precedence precedence) => precedence switch
    {
        Precedence.Is or Precedence.Like => 20,
        // The parser also uses this value for unary +/-; SQLite binds unary signs above concatenation.
        Precedence.MulDivModOp => 50,
        _ => base.GetPrecedence(precedence),
    };

    public override short? GetNextPrecedence(Parser parser) => parser.PeekToken() switch
    {
        Equal or DoubleEqual or NotEqual => 20,
        LessThan or LessThanOrEqual or GreaterThan or GreaterThanOrEqual => 22,
        Pipe or Ampersand or ShiftLeft or ShiftRight => 25,
        StringConcat => 45,
        Multiply or Divide or Modulo => 40,
        _ => base.GetNextPrecedence(parser),
    };

    public override Expression? ParseInfix(Parser parser, Expression expression, int precedence)
    {
        if (parser.PeekToken() is not Word { Keyword: Keyword.IS })
        {
            return base.ParseInfix(parser, expression, precedence);
        }

        // Preserve the parser's existing UNKNOWN predicate extension.
        if (parser.PeekNthToken(1) is Word { Keyword: Keyword.UNKNOWN }
            || (parser.PeekNthToken(1) is Word { Keyword: Keyword.NOT }
                && parser.PeekNthToken(2) is Word { Keyword: Keyword.UNKNOWN }))
        {
            return base.ParseInfix(parser, expression, precedence);
        }

        parser.ExpectKeyword(Keyword.IS);
        var negated = parser.ParseKeyword(Keyword.NOT);
        if (parser.ParseKeyword(Keyword.DISTINCT))
        {
            parser.ExpectKeyword(Keyword.FROM);
            negated = !negated;
        }

        var right = parser.ParseSubExpression((short)precedence);
        var value = right;
        while (value is Expression.Nested nested)
        {
            value = nested.Expression;
        }

        // Inspect the complete RHS: TRUE + 1 and +TRUE are values; (TRUE) is a truth test.
        return value switch
        {
            Expression.LiteralValue { Value: Value.Null } => negated ? new Expression.IsNotNull(expression) : new Expression.IsNull(expression),
            Expression.LiteralValue { Value: Value.Boolean { Value: true } } => negated ? new Expression.IsNotTrue(expression) : new Expression.IsTrue(expression),
            Expression.LiteralValue { Value: Value.Boolean { Value: false } } => negated ? new Expression.IsNotFalse(expression) : new Expression.IsFalse(expression),
            _ => negated ? new Expression.IsDistinctFrom(expression, right) : new Expression.IsNotDistinctFrom(expression, right),
        };
    }

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
        var returning = parser.ParseCommaSeparated(parser.ParseSelectItem);
        // ParseStatements treats END as an enclosing block terminator and otherwise silently discards the tail.
        if (parser.PeekToken() is not (EOF or SemiColon))
        {
            throw Parser.Expected("end of statement", parser.PeekToken());
        }

        return new Statement.Delete(new DeleteOperation(null, new FromTable.WithFromKeyword([table]),
            Returning: returning));
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
