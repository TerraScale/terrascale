namespace TerraBase.Postgres.Sql;

internal sealed partial class PostgresSqlRewriter
{
    private void AliasSelectList()
    {
        if (!_tokens.IsWord(0, "SELECT"))
        {
            return;
        }

        var index = 1;
        while (_tokens.IsAnyWord(index, "DISTINCT", "ALL"))
        {
            index = _tokens.IsWord(index + 1, "ON") && _tokens.Kind(index + 2) == PgTokenKind.LeftParen ? _tokens.Match(index + 2) + 1 : index + 1;
        }

        var start = index;
        for (; index <= _tokens.Count; index++)
        {
            var atEnd = index == _tokens.Count
                || _tokens.IsAnyWord(index, "FROM", "WHERE", "GROUP", "ORDER", "LIMIT", "UNION", "INTO", "HAVING", "WINDOW", "OFFSET", "FETCH", "FOR", "EXCEPT", "INTERSECT");
            if (!atEnd && _tokens.Kind(index) is PgTokenKind.LeftParen or PgTokenKind.LeftBracket)
            {
                index = _tokens.Match(index);
                continue;
            }

            if (atEnd || _tokens.Kind(index) == PgTokenKind.Comma)
            {
                AliasItem(start, index - 1);
                start = index + 1;
                if (atEnd)
                {
                    return;
                }
            }
        }
    }

    private void AliasItem(int start, int end)
    {
        if (end < start || _tokens.IsDead(start) || HasAlias(start, end))
        {
            return;
        }

        var name = ItemName(start, end);
        if (name is not null)
        {
            _tokens.Append(end, " AS \"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        }
    }

    private bool HasAlias(int start, int end)
    {
        if (end > start && _tokens.IsWord(end - 1, "AS"))
        {
            return true;
        }

        return end > start && _tokens.IsName(end) && _tokens.Kind(end - 1) != PgTokenKind.Period
            && !(_tokens.Kind(end) == PgTokenKind.Word && (Reserved.Contains(_tokens.Text(end)) || _tokens.IsAnyWord(end, "TRUE", "FALSE")))
            && _tokens.Kind(end - 1) is PgTokenKind.RightParen or PgTokenKind.Word or PgTokenKind.QuotedIdentifier or PgTokenKind.String or PgTokenKind.Number
            && !(_tokens.Kind(end - 1) == PgTokenKind.Word && Reserved.Contains(_tokens.Text(end - 1)));
    }

    private string? ItemName(int start, int end)
    {
        if (start == end && _tokens.IsAnyWord(start, "current_user", "session_user", "current_role", "current_schema", "current_catalog", "localtimestamp",
            "current_timestamp", "current_date", "current_time"))
        {
            return _tokens.Text(start).ToLowerInvariant();
        }

        var cursor = start;
        while (_tokens.IsName(cursor) || _tokens.Kind(cursor) == PgTokenKind.Period)
        {
            if (_tokens.Kind(cursor + 1) != PgTokenKind.Period)
            {
                break;
            }

            cursor += 2;
        }

        if (cursor == end && _tokens.IsName(cursor))
        {
            return null;
        }

        if (cursor == end && _tokens.IsOperator(cursor, "*"))
        {
            return null;
        }

        if (_tokens.IsName(cursor) && _tokens.Kind(cursor + 1) == PgTokenKind.LeftParen)
        {
            var close = _tokens.Match(cursor + 1);
            if (close == end || (_tokens.IsAnyWord(close + 1, "OVER", "FILTER") && LastClose(close + 2) == end))
            {
                return _tokens.Text(cursor).ToLowerInvariant();
            }
        }

        if (_tokens.IsWord(start, "CASE") && _tokens.IsWord(end, "END"))
        {
            return "case";
        }

        for (var index = end; index > start; index--)
        {
            if (_tokens.Kind(index) == PgTokenKind.DoubleColon && TryScanType(index + 1, out var type, out var typeEnd, out _) && typeEnd == end)
            {
                return index - 1 == start && _tokens.IsName(start) ? _tokens.Identifier(start) : type.Split(' ')[0];
            }
        }

        return "?column?";
    }

    private int LastClose(int index) => _tokens.Kind(index) == PgTokenKind.LeftParen ? _tokens.Match(index) : index;
}
