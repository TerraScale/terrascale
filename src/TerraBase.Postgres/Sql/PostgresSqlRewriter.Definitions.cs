using System.Text;

namespace TerraBase.Postgres.Sql;

internal ref partial struct PostgresSqlRewriter
{
    private void RewriteDefinitions()
    {
        if (_tokens.IsWord(0, "CREATE"))
        {
            var index = 1;
            while (_tokens.IsAnyWord(index, "GLOBAL", "LOCAL", "UNLOGGED", "TEMP", "TEMPORARY", "UNIQUE", "OR", "REPLACE"))
            {
                if (_tokens.IsAnyWord(index, "GLOBAL", "LOCAL", "UNLOGGED"))
                {
                    _tokens.Remove(index);
                }

                index++;
            }

            if (_tokens.IsWord(index, "TABLE"))
            {
                RewriteCreateTable(index + 1);
            }
            else if (_tokens.IsWord(index, "INDEX"))
            {
                RewriteCreateIndex(index + 1);
            }

            return;
        }

        if (_tokens.IsWord(0, "ALTER") && _tokens.IsWord(1, "TABLE"))
        {
            RewriteAlterTable(2);
        }
    }

    private void RewriteCreateIndex(int index)
    {
        for (; index < _tokens.Count; index++)
        {
            if (_tokens.IsWord(index, "CONCURRENTLY"))
            {
                _tokens.Remove(index);
            }
            else if (_tokens.IsWord(index, "USING") && _tokens.IsName(index + 1) && _tokens.Kind(index + 2) == PgTokenKind.LeftParen)
            {
                _tokens.Remove(index);
                _tokens.Remove(index + 1);
                return;
            }
            else if (_tokens.Kind(index) == PgTokenKind.LeftParen)
            {
                return;
            }
        }
    }

    private void RewriteCreateTable(int index)
    {
        if (_tokens.IsWord(index, "IF") && _tokens.IsWord(index + 1, "NOT") && _tokens.IsWord(index + 2, "EXISTS"))
        {
            index += 3;
        }

        while (_tokens.IsName(index) || _tokens.Kind(index) == PgTokenKind.Period)
        {
            index++;
        }

        if (_tokens.Kind(index) != PgTokenKind.LeftParen)
        {
            return;
        }

        var close = _tokens.Match(index);
        var strict = false;
        for (var option = close + 1; option < _tokens.Count; option++)
        {
            strict |= _tokens.IsWord(option, "STRICT");
        }

        var elements = Elements(index + 1, close - 1);
        HashSet<string> tablePrimaryKey = new(StringComparer.Ordinal);
        foreach (var (start, end) in elements)
        {
            var primary = _tokens.IsWord(start, "CONSTRAINT") ? start + 2 : start;
            if (_tokens.IsWord(primary, "PRIMARY") && _tokens.IsWord(primary + 1, "KEY") && _tokens.Kind(primary + 2) == PgTokenKind.LeftParen)
            {
                for (var column = primary + 3; column < _tokens.Match(primary + 2); column++)
                {
                    if (_tokens.IsName(column))
                    {
                        tablePrimaryKey.Add(_tokens.Identifier(column));
                    }
                }
            }
        }

        foreach (var (start, end) in elements)
        {
            if (!_tokens.IsAnyWord(start, "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN", "EXCLUDE", "LIKE"))
            {
                RewriteColumn(start, end, tablePrimaryKey, strict, createTable: true);
            }
        }
    }

    private void RewriteAlterTable(int index)
    {
        for (; index < _tokens.Count; index++)
        {
            if (!_tokens.IsWord(index, "ADD"))
            {
                continue;
            }

            var start = index + 1;
            if (_tokens.IsWord(start, "COLUMN"))
            {
                start++;
            }

            if (_tokens.IsAnyWord(start, "CONSTRAINT", "PRIMARY", "UNIQUE", "CHECK", "FOREIGN"))
            {
                continue;
            }

            if (_tokens.IsWord(start, "IF") && _tokens.IsWord(start + 1, "NOT") && _tokens.IsWord(start + 2, "EXISTS"))
            {
                start += 3;
            }

            var end = start;
            while (end + 1 < _tokens.Count && _tokens.Kind(end + 1) != PgTokenKind.Comma)
            {
                end = _tokens.Kind(end + 1) == PgTokenKind.LeftParen ? _tokens.Match(end + 1) : end + 1;
            }

            RewriteColumn(start, end, new HashSet<string>(StringComparer.Ordinal), strict: false, createTable: false);
            index = end;
        }
    }

    private void RewriteColumn(int start, int end, HashSet<string> tablePrimaryKey, bool strict, bool createTable)
    {
        var name = _tokens.Identifier(start);
        var typeStart = start + 1;
        var typeEnd = start;
        for (var index = typeStart; index <= end; index++)
        {
            if (_tokens.IsAnyWord(index, "CONSTRAINT", "PRIMARY", "NOT", "NULL", "UNIQUE", "CHECK", "DEFAULT", "REFERENCES", "GENERATED", "COLLATE", "AS"))
            {
                break;
            }

            typeEnd = _tokens.Kind(index) is PgTokenKind.LeftParen or PgTokenKind.LeftBracket ? _tokens.Match(index) : index;
            index = typeEnd;
        }

        var hasPrimaryKey = false;
        var identity = false;
        for (var index = typeEnd + 1; index <= end; index++)
        {
            hasPrimaryKey |= _tokens.IsWord(index, "PRIMARY") && _tokens.IsWord(index + 1, "KEY");
            if (_tokens.IsWord(index, "GENERATED") && IdentityEnd(index) is var identityEnd and > 0)
            {
                identity = true;
                _tokens.RemoveRange(index, identityEnd);
                index = identityEnd;
            }
        }

        if (typeEnd < typeStart)
        {
            return;
        }

        var words = new StringBuilder();
        var modifier = string.Empty;
        var array = false;
        for (var index = typeStart; index <= typeEnd; index++)
        {
            switch (_tokens.Kind(index))
            {
                case PgTokenKind.LeftParen:
                    modifier = _tokens.RenderSource(index, _tokens.Match(index)).Replace(" ", string.Empty, StringComparison.Ordinal);
                    index = _tokens.Match(index);
                    break;
                case PgTokenKind.LeftBracket:
                    array = true;
                    index = _tokens.Match(index);
                    break;
                case PgTokenKind.Word or PgTokenKind.QuotedIdentifier:
                    words.Append(words.Length > 0 ? " " : string.Empty).Append(_tokens.Identifier(index).ToLowerInvariant());
                    break;
            }
        }

        var baseType = words.ToString();
        identity |= baseType is "serial" or "serial4" or "bigserial" or "serial8" or "smallserial" or "serial2";
        var primary = hasPrimaryKey || (tablePrimaryKey.Count == 1 && tablePrimaryKey.Contains(name));
        string canonical;
        if (identity)
        {
            canonical = createTable && !hasPrimaryKey && !tablePrimaryKey.Contains(name) ? "INTEGER PRIMARY KEY" : "INTEGER";
        }
        else
        {
            canonical = array ? "TEXT" : CanonicalType(baseType, modifier, primary);
        }

        if (strict)
        {
            canonical = StrictType(canonical);
        }

        _tokens.Replace(typeStart, canonical);
        _tokens.RemoveRange(typeStart + 1, typeEnd);
    }

    private int IdentityEnd(int generated)
    {
        var index = generated + 1;
        if (_tokens.IsWord(index, "ALWAYS"))
        {
            index++;
        }
        else if (_tokens.IsWord(index, "BY") && _tokens.IsWord(index + 1, "DEFAULT"))
        {
            index += 2;
        }
        else
        {
            return -1;
        }

        if (!_tokens.IsWord(index, "AS") || !_tokens.IsWord(index + 1, "IDENTITY"))
        {
            return -1;
        }

        index++;
        return _tokens.Kind(index + 1) == PgTokenKind.LeftParen ? _tokens.Match(index + 1) : index;
    }

    private static string CanonicalType(string type, string modifier, bool primary) => type switch
    {
        "int" or "integer" or "int4" => primary ? "INTEGER" : "INT",
        "bigint" or "int8" => primary ? "INTEGER" : "BIGINT",
        "smallint" or "int2" or "tinyint" => "SMALLINT",
        "boolean" or "bool" => "BOOLEAN",
        "real" or "float4" => "REAL",
        "double precision" or "float8" or "float" or "double" => "DOUBLE PRECISION",
        "numeric" or "decimal" => "NUMERIC" + modifier,
        "money" => "NUMERIC",
        "text" or "name" => "TEXT",
        "citext" => "TEXT COLLATE NOCASE",
        "varchar" or "character varying" or "nvarchar" => "VARCHAR" + modifier,
        "char" or "character" or "bpchar" or "nchar" => "CHAR" + modifier,
        "bytea" or "blob" => "BLOB",
        "timestamptz" or "timestamp with time zone" => "TIMESTAMPTZ",
        "timestamp" or "timestamp without time zone" or "datetime" => "TIMESTAMP",
        "date" => "DATE",
        "time" or "time without time zone" or "timetz" or "time with time zone" => "TIME",
        "uuid" => "UUID",
        "json" => "JSON",
        "jsonb" => "JSONB",
        "oid" => "OID",
        _ => "TEXT",
    };

    private static string StrictType(string canonical) => canonical switch
    {
        "INTEGER PRIMARY KEY" => canonical,
        "INTEGER" or "INT" or "BIGINT" or "SMALLINT" or "BOOLEAN" or "OID" => "INTEGER",
        "REAL" or "DOUBLE PRECISION" => "REAL",
        "BLOB" => "BLOB",
        _ when canonical.StartsWith("NUMERIC", StringComparison.Ordinal) => "ANY",
        "TEXT COLLATE NOCASE" => canonical,
        _ => "TEXT",
    };

    private List<(int Start, int End)> Elements(int from, int to)
    {
        List<(int Start, int End)> elements = [];
        var start = from;
        for (var index = from; index <= to; index++)
        {
            if (_tokens.Kind(index) is PgTokenKind.LeftParen or PgTokenKind.LeftBracket)
            {
                index = _tokens.Match(index);
                continue;
            }

            if (_tokens.Kind(index) == PgTokenKind.Comma)
            {
                elements.Add((start, index - 1));
                start = index + 1;
            }
        }

        if (start <= to)
        {
            elements.Add((start, to));
        }

        return elements;
    }
}
