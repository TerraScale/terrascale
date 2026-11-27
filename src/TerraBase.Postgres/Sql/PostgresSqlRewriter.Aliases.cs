using System.Collections.Frozen;

namespace TerraBase.Postgres.Sql;

internal ref partial struct PostgresSqlRewriter
{
    private static readonly FrozenSet<string> SelectTerminators = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
    [
        "FROM", "WHERE", "GROUP", "ORDER", "LIMIT", "UNION", "INTO", "HAVING", "WINDOW", "OFFSET", "FETCH", "FOR", "EXCEPT", "INTERSECT",
    ]);

    private void AliasSelectList()
    {
        if (!_tokens.IsWord(0, "SELECT"))
        {
            return;
        }

        var terminatorLookup = SelectTerminators.GetAlternateLookup<ReadOnlySpan<char>>();
        var index = 1;
        while (_tokens.IsAnyWord(index, "DISTINCT", "ALL"))
        {
            index = _tokens.IsWord(index + 1, "ON") && _tokens.Kind(index + 2) == PgTokenKind.LeftParen ? _tokens.Match(index + 2) + 1 : index + 1;
        }

        var start = index;
        for (; index <= _tokens.Count; index++)
        {
            var atEnd = index == _tokens.Count
                || (_tokens.Kind(index) == PgTokenKind.Word && terminatorLookup.Contains(_tokens.Raw(index)));
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

        if (start == end && IsStringLiteralToken(start))
        {
            (_selectLiterals ??= []).Add(start);
        }

        var name = ItemName(start, end);
        if (name is not null)
        {
            _tokens.Append(end, name.Contains('"', StringComparison.Ordinal)
                ? string.Concat(" AS \"", name.Replace("\"", "\"\"", StringComparison.Ordinal), "\"")
                : string.Concat(" AS \"", name, "\""));
        }
    }

    private bool HasAlias(int start, int end)
    {
        if (end <= start)
        {
            return false;
        }

        if (_tokens.IsWord(end - 1, "AS"))
        {
            return true;
        }

        if (!_tokens.IsName(end) || _tokens.Kind(end - 1) == PgTokenKind.Period)
        {
            return false;
        }

        if (_tokens.Kind(end) == PgTokenKind.Word)
        {
            var endRaw = _tokens.Raw(end);
            if (IsReserved(endRaw) || endRaw.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || endRaw.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var prevKind = _tokens.Kind(end - 1);
        return prevKind is PgTokenKind.RightParen or PgTokenKind.Word or PgTokenKind.QuotedIdentifier or PgTokenKind.String or PgTokenKind.Number
            && !(prevKind == PgTokenKind.Word && IsReserved(_tokens.Raw(end - 1)));
    }

    private string? ItemName(int start, int end)
    {
        if (start == end && _tokens.Kind(start) == PgTokenKind.Word)
        {
            var raw = _tokens.Raw(start);
            if (raw.Length is >= 12 and <= 17 && (raw[0] | 0x20) is 'c' or 's' or 'l')
            {
                if (raw.Equals("current_user", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_user";
                }

                if (raw.Equals("session_user", StringComparison.OrdinalIgnoreCase))
                {
                    return "session_user";
                }

                if (raw.Equals("current_role", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_role";
                }

                if (raw.Equals("current_schema", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_schema";
                }

                if (raw.Equals("current_catalog", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_catalog";
                }

                if (raw.Equals("localtimestamp", StringComparison.OrdinalIgnoreCase))
                {
                    return "localtimestamp";
                }

                if (raw.Equals("current_timestamp", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_timestamp";
                }

                if (raw.Equals("current_date", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_date";
                }

                if (raw.Equals("current_time", StringComparison.OrdinalIgnoreCase))
                {
                    return "current_time";
                }
            }
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

        if (_tokens.HasFlag(PgSyntaxFlags.DoubleColon))
        {
            for (var index = end; index > start; index--)
            {
                if (_tokens.Kind(index) == PgTokenKind.DoubleColon && TryScanType(index + 1, out var type, out var typeEnd, out _) && typeEnd == end)
                {
                    if (index - 1 == start && _tokens.IsName(start))
                    {
                        return _tokens.Identifier(start);
                    }

                    var space = type.IndexOf(' ', StringComparison.Ordinal);
                    return space < 0 ? type : type[..space];
                }
            }
        }

        return "?column?";
    }

    private int LastClose(int index) => _tokens.Kind(index) == PgTokenKind.LeftParen ? _tokens.Match(index) : index;
}
