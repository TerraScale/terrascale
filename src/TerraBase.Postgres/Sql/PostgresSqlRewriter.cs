using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal sealed partial class PostgresSqlRewriter
{
    private static readonly FrozenSet<string> Reserved = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
    [
        "ALL", "AND", "ANY", "AS", "ASC", "BETWEEN", "BY", "CASE", "CHECK", "COLLATE", "CONSTRAINT", "CREATE", "CROSS", "DEFAULT", "DELETE", "DESC",
        "DISTINCT", "DO", "ELSE", "END", "ESCAPE", "EXCEPT", "EXISTS", "FETCH", "FOR", "FOREIGN", "FROM", "FULL", "GROUP", "HAVING", "ILIKE", "IN",
        "INNER", "INSERT", "INTERSECT", "INTO", "IS", "ISNULL", "JOIN", "KEY", "LATERAL", "LEFT", "LIKE", "LIMIT", "NATURAL", "NOT", "NOTNULL", "NULL",
        "OFFSET", "ON", "OR", "ORDER", "OUTER", "OVER", "PARTITION", "PRIMARY", "REFERENCES", "RETURNING", "RIGHT", "SELECT", "SET", "SIMILAR", "SOME",
        "TABLE", "THEN", "TO", "UNION", "UNIQUE", "UPDATE", "USING", "VALUES", "WHEN", "WHERE", "WINDOW", "WITH",
    ]);

    private static readonly FrozenSet<string> NamespaceColumns = FrozenSet.Create(StringComparer.Ordinal, ["oid", "nspname", "nspowner", "nspacl"]);

    private static readonly FrozenSet<string> TypeContinuations = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        ["precision", "varying", "with", "without", "time", "zone"]);

    private readonly PgTokenEditor _tokens;
    private readonly PgRewriteContext _context;
    private readonly Dictionary<int, int> _castStarts = [];
    private readonly Dictionary<int, string> _literals = [];
    private readonly Dictionary<int, int> _parameterTypes = [];
    private readonly List<PgParameterColumn> _parameterColumns = [];
    private readonly List<int> _deferred = [];
    private List<int>? _selectLiterals;
    private readonly List<(string Name, string Alias, int Token)> _catalogRelations = [];
    private int _parameterCount;
    private bool _usesCatalog;

    private PostgresSqlRewriter(PgTokenEditor tokens, PgRewriteContext context)
    {
        _tokens = tokens;
        _context = context;
    }

    public static Result<PgRewrite, PgError> Rewrite(PgTokenEditor tokens, PgRewriteContext context)
    {
        if (!tokens.IsBalanced)
        {
            return PgError.Syntax("syntax error: unbalanced parentheses or brackets");
        }

        var rewriter = new PostgresSqlRewriter(tokens, context);
        return rewriter.Run();
    }

    private Result<PgRewrite, PgError> Run()
    {
        CollectParameterColumns();
        var referenced = _parameterColumns.Count == 0 ? [] : ReferencedNames();
        if (RewriteLiterals() is { } literalError)
        {
            return literalError;
        }

        if (RewriteQualifiedNames() is { } qualifiedError)
        {
            return qualifiedError;
        }

        JoinMissingNamespace();
        KillUnsupportedCalls();
        RewriteClauses();
        if (RewriteCasts() is { } castError)
        {
            return castError;
        }

        RewriteArrayComparisons();
        if (RewritePatternOperators() is { } patternError)
        {
            return patternError;
        }

        RewriteDistinctFrom();
        if (RewriteFunctions() is { } functionError)
        {
            return functionError;
        }

        RewriteDefinitions();
        RunDeferred();
        AliasSelectList();
        var sql = _tokens.Render();
        SqlValue[] boundLiterals = _selectLiterals is { } candidates && !PgRewrite.Fits(sql, describe: true) ? BindSelectLiterals(candidates) : [];
        if (boundLiterals.Length > 0)
        {
            sql = _tokens.Render();
        }

        return new PgRewrite(sql, _parameterCount, _parameterTypes, _parameterColumns, referenced, _usesCatalog, boundLiterals);
    }

    private SqlValue[] BindSelectLiterals(List<int> candidates)
    {
        List<SqlValue> values = [];
        foreach (var index in candidates)
        {
            var parameter = _parameterCount + values.Count + 1;
            var placeholder = "?" + parameter.ToString(CultureInfo.InvariantCulture);
            if (parameter <= ushort.MaxValue && placeholder.Length < _tokens.Raw(index).Length)
            {
                _tokens.Replace(index, placeholder);
                values.Add(SqlValue.FromText(_literals[index]));
            }
        }

        return [.. values];
    }

    private PgError? RewriteLiterals()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            switch (_tokens.Kind(index))
            {
                case PgTokenKind.Parameter:
                    if (!int.TryParse(_tokens.Raw(index)[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number is < 1 or > ushort.MaxValue)
                    {
                        return new PgError("42P02", $"there is no parameter {_tokens.Text(index)}");
                    }

                    _parameterCount = Math.Max(_parameterCount, number);
                    _tokens.Replace(index, "?" + number.ToString(CultureInfo.InvariantCulture));
                    break;
                case PgTokenKind.EscapeString:
                    if (DecodeEscapeString(_tokens.Raw(index)[2..^1]) is not { } escaped)
                    {
                        return new PgError("22025", "invalid Unicode escape value in string literal");
                    }

                    _literals[index] = escaped;
                    _tokens.Replace(index, PgRewriteContext.Literal(escaped));
                    break;
                case PgTokenKind.DollarString:
                    var raw = _tokens.Raw(index);
                    var tag = raw[1..].IndexOf('$') + 2;
                    var content = raw[tag..^tag].ToString();
                    _literals[index] = content;
                    _tokens.Replace(index, PgRewriteContext.Literal(content));
                    break;
                case PgTokenKind.String when _tokens.Raw(index)[0] is 'B' or 'b':
                    return PgError.Unsupported("bit string literals");
                case PgTokenKind.String when _tokens.Raw(index)[0] == '\'':
                    _literals[index] = _tokens.Raw(index)[1..^1].ToString().Replace("''", "'", StringComparison.Ordinal);
                    break;
            }
        }

        return null;
    }

    private PgError? RewriteQualifiedNames()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.IsName(index) && _tokens.Kind(index + 1) == PgTokenKind.Period && _tokens.IsName(index + 2))
            {
                var schema = _tokens.Identifier(index);
                var name = _tokens.Identifier(index + 2);
                switch (schema)
                {
                    case "information_schema" when _tokens.Kind(index - 1) != PgTokenKind.Period:
                        return PgError.Unsupported("information_schema") with { Hint = "query the pg_catalog relations instead" };
                    case "public":
                        _tokens.Remove(index);
                        _tokens.Remove(index + 1);
                        break;
                    case "pg_catalog" when _tokens.Kind(index + 3) != PgTokenKind.LeftParen
                        && PgCatalogSql.TryGetRelation(schema, name, _context, out var relation):
                        _tokens.Remove(index);
                        _tokens.Remove(index + 1);
                        ReplaceRelation(index + 2, name, relation);
                        break;
                    case "pg_catalog":
                        _tokens.Remove(index);
                        _tokens.Remove(index + 1);
                        break;
                }

                index += 2;
                continue;
            }

            if (_tokens.Kind(index) == PgTokenKind.Word
                && _tokens.Raw(index).StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                && _tokens.Kind(index + 1) != PgTokenKind.LeftParen
                && _tokens.Kind(index + 1) != PgTokenKind.Period
                && (_tokens.IsAnyWord(index - 1, "FROM", "JOIN") || _tokens.Kind(index - 1) == PgTokenKind.Comma)
                && _tokens.Kind(index - 1) != PgTokenKind.Period
                && PgCatalogSql.TryGetRelation("pg_catalog", _tokens.Identifier(index), _context, out var bare))
            {
                ReplaceRelation(index, _tokens.Identifier(index), bare);
            }
        }

        return null;
    }

    private void ReplaceRelation(int index, string name, string relation)
    {
        _usesCatalog = true;
        _tokens.Replace(index, "(" + relation + ")");
        var next = index + 1;
        var aliased = _tokens.IsWord(next, "AS")
            || _tokens.Kind(next) == PgTokenKind.QuotedIdentifier
            || (_tokens.Kind(next) == PgTokenKind.Word && !Reserved.Contains(_tokens.Text(next)) && !_tokens.IsAnyWord(next, "WHERE", "ORDER", "GROUP", "LIMIT", "OFFSET", "UNION", "JOIN", "LEFT", "RIGHT", "INNER", "CROSS", "FULL", "ON", "WINDOW", "HAVING", "NATURAL"));
        if (!aliased)
        {
            _tokens.Append(index, " AS " + name);
            _catalogRelations.Add((name, name, index));
            return;
        }

        var alias = _tokens.IsWord(next, "AS") ? next + 1 : next;
        if (_tokens.IsName(alias))
        {
            _catalogRelations.Add((name, _tokens.Identifier(alias), alias));
        }
    }

    private void JoinMissingNamespace()
    {
        if (_catalogRelations.Find(static relation => relation.Name == "pg_class") is not { Alias: { } owner } pgClass)
        {
            return;
        }

        HashSet<string> declared = new(StringComparer.Ordinal);
        Dictionary<string, bool> qualifiers = new(StringComparer.Ordinal);
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (!_tokens.IsName(index) || _tokens.IsEdited(index))
            {
                continue;
            }

            var identifier = _tokens.Identifier(index);
            if (_tokens.Kind(index + 1) != PgTokenKind.Period || _tokens.Kind(index - 1) == PgTokenKind.Period)
            {
                declared.Add(identifier);
                continue;
            }

            var namespaceColumn = _tokens.IsName(index + 2) && NamespaceColumns.Contains(_tokens.Identifier(index + 2));
            qualifiers[identifier] = (!qualifiers.TryGetValue(identifier, out var known) || known) && namespaceColumn;
        }

        foreach (var (qualifier, onlyNamespace) in qualifiers)
        {
            if (onlyNamespace && !declared.Contains(qualifier) && !_catalogRelations.Exists(relation => relation.Alias == qualifier)
                && PgCatalogSql.TryGetRelation("pg_catalog", "pg_namespace", _context, out var relation))
            {
                var quoted = "\"" + qualifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                var source = "\"" + owner.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                _tokens.Append(pgClass.Token, $" LEFT JOIN ({relation}) AS {quoted} ON {quoted}.oid = {source}.relnamespace");
                return;
            }
        }
    }

    private List<string> ReferencedNames()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> names = [];
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.IsName(index) && !(_tokens.Kind(index) == PgTokenKind.Word && Reserved.Contains(_tokens.Text(index)))
                && seen.Add(_tokens.Identifier(index)))
            {
                names.Add(_tokens.Identifier(index));
            }
        }

        return names;
    }

    private void KillUnsupportedCalls()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.Kind(index + 1) != PgTokenKind.LeftParen || _tokens.IsDead(index)
                || !_tokens.IsAnyWord(index, "array_to_string", "array_length", "array_upper", "array_lower", "pg_get_statisticsobjdef_columns", "pg_get_partkeydef", "pg_get_triggerdef", "pg_get_ruledef", "pg_partition_root", "pg_get_function_identity_arguments"))
            {
                continue;
            }

            var close = _tokens.Match(index + 1);
            _tokens.Kill(index + 1, close);
            _tokens.Replace(index, "NULL");
            index = close;
        }
    }

    private void RewriteClauses()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.IsDead(index))
            {
                continue;
            }

            if (_tokens.IsWord(index, "COLLATE") && _tokens.IsName(index + 1))
            {
                var name = index + 1;
                while (_tokens.Kind(name + 1) == PgTokenKind.Period && _tokens.IsName(name + 2))
                {
                    name += 2;
                }

                if (_tokens.Identifier(name) is not ("binary" or "nocase" or "rtrim"))
                {
                    _tokens.RemoveRange(index, name);
                }

                index = name;
            }
            else if (_tokens.IsWord(index, "LIMIT") && _tokens.IsWord(index + 1, "ALL"))
            {
                _tokens.Replace(index + 1, "-1");
            }
            else if (_tokens.IsWord(index, "FOR") && _tokens.IsAnyWord(index + 1, "UPDATE", "SHARE", "NO", "KEY"))
            {
                var end = index + 1;
                while (_tokens.Kind(end + 1) is PgTokenKind.Word or PgTokenKind.QuotedIdentifier or PgTokenKind.Comma or PgTokenKind.Period
                    && !_tokens.IsAnyWord(end + 1, "UNION", "EXCEPT", "INTERSECT", "LIMIT", "OFFSET", "FETCH"))
                {
                    end++;
                }

                _tokens.RemoveRange(index, end);
                index = end;
            }
            else if (_tokens.IsWord(index, "ONLY") && _tokens.IsAnyWord(index - 1, "FROM", "UPDATE", "JOIN", "TABLE") && _tokens.IsName(index + 1))
            {
                _tokens.Remove(index);
            }
            else if (_tokens.IsAnyWord(index, "DATE", "TIMESTAMP", "TIMESTAMPTZ", "TIME", "INTERVAL") && _tokens.Kind(index + 1) == PgTokenKind.String
                && !_tokens.IsName(index - 1))
            {
                _tokens.Remove(index);
            }
            else if (_tokens.IsWord(index, "ARRAY") && _tokens.Kind(index + 1) == PgTokenKind.LeftBracket)
            {
                var close = _tokens.Match(index + 1);
                _tokens.Replace(index, "json_array");
                _tokens.Replace(index + 1, "(");
                _tokens.Replace(close, ")");
            }
        }
    }

    private PgError? RewriteCasts()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.Kind(index) != PgTokenKind.DoubleColon || _tokens.IsDead(index))
            {
                continue;
            }

            var start = OperandStart(index - 1);
            if (start < 0)
            {
                return PgError.Syntax("syntax error at or near \"::\"");
            }

            if (!TryScanType(index + 1, out var type, out var end, out var isArray))
            {
                return PgError.Syntax("syntax error: \"::\" must be followed by a type name");
            }

            var single = start == index - 1;
            var literal = single && _literals.TryGetValue(start, out var literalValue) ? literalValue : null;
            if (single && _tokens.Kind(start) == PgTokenKind.Parameter && CastOid(type, isArray) is > 0 and var hint)
            {
                _parameterTypes[int.Parse(_tokens.Raw(start)[1..], NumberStyles.None, CultureInfo.InvariantCulture)] = hint;
            }

            ApplyCast(start, index, end, type, isArray, literal);
            _castStarts[end] = start;
            index = end;
        }

        return null;
    }

    private void ApplyCast(int start, int operatorIndex, int end, string type, bool isArray, string? literal)
    {
        var inDefault = _tokens.IsWord(start - 1, "DEFAULT");
        switch (isArray ? "text" : type)
        {
            case "regclass" when literal is not null:
                _tokens.RemoveRange(operatorIndex, end);
                _usesCatalog = true;
                _tokens.Replace(start, long.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var relationOid)
                    ? relationOid.ToString(CultureInfo.InvariantCulture)
                    : $"(SELECT oid FROM ({PgCatalogSql.PgClassRelation}) WHERE relname = {PgRewriteContext.Literal(literal.Replace("public.", string.Empty, StringComparison.Ordinal).Trim('"'))})");
                return;
            case "regclass":
                _usesCatalog = true;
                _tokens.Prepend(start, $"(SELECT relname FROM ({PgCatalogSql.PgClassRelation}) AS _tb_rc WHERE _tb_rc.oid = ");
                _tokens.Replace(operatorIndex, ")");
                _tokens.RemoveRange(operatorIndex + 1, end);
                return;
            case "regtype" when literal is not null:
                _tokens.RemoveRange(operatorIndex, end);
                _tokens.Replace(start, PgOid.Types.FirstOrDefault(candidate => candidate.Name == literal || candidate.FormatName == literal) is { } known
                    ? known.Oid.ToString(CultureInfo.InvariantCulture)
                    : "0");
                return;
            case "regtype":
                var formatted = PgCatalogSql.FormatTypeCase("\u0002");
                var split = formatted.IndexOf('\u0002', StringComparison.Ordinal);
                _tokens.Prepend(start, formatted[..split]);
                _tokens.Replace(operatorIndex, formatted[(split + 1)..]);
                _tokens.RemoveRange(operatorIndex + 1, end);
                return;
            case "regnamespace":
                _tokens.Prepend(start, "(CASE ");
                _tokens.Replace(operatorIndex, " WHEN 11 THEN 'pg_catalog' WHEN 2200 THEN 'public' ELSE NULL END)");
                _tokens.RemoveRange(operatorIndex + 1, end);
                return;
            case "bool" or "boolean" when literal is not null && PgValueCodec.TryParseBool(literal, out var flag):
                _tokens.Replace(start, flag ? "TRUE" : "FALSE");
                _tokens.RemoveRange(operatorIndex, end);
                return;
            case "bytea" when literal is not null && literal.StartsWith("\\x", StringComparison.Ordinal):
                _tokens.Replace(start, "X'" + literal[2..] + "'");
                _tokens.RemoveRange(operatorIndex, end);
                return;
            case "date" when literal is null:
                WrapCast(start, operatorIndex, end, "date(", ")", inDefault);
                return;
            case "time" when literal is null:
                WrapCast(start, operatorIndex, end, "time(", ")", inDefault);
                return;
        }

        var target = SqliteCastTarget(isArray ? "text" : type);
        if (literal is not null && target is "TEXT")
        {
            _tokens.RemoveRange(operatorIndex, end);
            return;
        }

        WrapCast(start, operatorIndex, end, "CAST(", $" AS {target})", inDefault);
    }

    private void WrapCast(int start, int operatorIndex, int end, string prefix, string suffix, bool inDefault)
    {
        _tokens.Prepend(start, inDefault ? "(" + prefix : prefix);
        _tokens.Replace(operatorIndex, inDefault ? suffix + ")" : suffix);
        _tokens.RemoveRange(operatorIndex + 1, end);
    }

    private static string SqliteCastTarget(string type) => type switch
    {
        "int" or "int2" or "int4" or "int8" or "integer" or "smallint" or "bigint" or "oid" or "xid" or "cid" or "regproc" or "serial" or "bigserial" => "INTEGER",
        "bool" or "boolean" => "INTEGER",
        "float" or "float4" or "float8" or "real" or "double precision" or "double" => "REAL",
        "numeric" or "decimal" or "money" => "NUMERIC",
        "bytea" or "blob" => "BLOB",
        _ => "TEXT",
    };

    private static int CastOid(string type, bool isArray)
    {
        var oid = type switch
        {
            "int" or "int4" or "integer" => PgOid.Int4,
            "int2" or "smallint" => PgOid.Int2,
            "int8" or "bigint" => PgOid.Int8,
            "bool" or "boolean" => PgOid.Bool,
            "float4" or "real" => PgOid.Float4,
            "float" or "float8" or "double precision" => PgOid.Float8,
            "numeric" or "decimal" => PgOid.Numeric,
            "bytea" => PgOid.Bytea,
            "text" or "name" or "citext" => PgOid.Text,
            "varchar" or "character varying" => PgOid.Varchar,
            "char" or "character" or "bpchar" => PgOid.Bpchar,
            "json" => PgOid.Json,
            "jsonb" => PgOid.Jsonb,
            "uuid" => PgOid.Uuid,
            "date" => PgOid.Date,
            "time" or "time without time zone" => PgOid.Time,
            "timestamp" or "timestamp without time zone" => PgOid.Timestamp,
            "timestamptz" or "timestamp with time zone" => PgOid.Timestamptz,
            "oid" => PgOid.Oid,
            _ => 0,
        };
        return !isArray ? oid : PgOid.TryGet(oid, out var element) ? element.ArrayOid : 0;
    }

    private bool TryScanType(int index, out string type, out int end, out bool isArray)
    {
        type = string.Empty;
        end = index;
        isArray = false;
        if (_tokens.IsName(index) && _tokens.Kind(index + 1) == PgTokenKind.Period && _tokens.IsName(index + 2))
        {
            index += 2;
        }

        if (!_tokens.IsName(index))
        {
            return false;
        }

        var builder = new StringBuilder(_tokens.Identifier(index));
        end = index;
        while (true)
        {
            if (_tokens.Kind(end + 1) == PgTokenKind.LeftParen && _tokens.Match(end + 1) > end)
            {
                end = _tokens.Match(end + 1);
                continue;
            }

            if (_tokens.Kind(end + 1) == PgTokenKind.Word && TypeContinuations.Contains(_tokens.Text(end + 1))
                && !(_tokens.IsWord(end + 1, "with") && !_tokens.IsWord(end + 2, "time")))
            {
                builder.Append(' ').Append(_tokens.Identifier(end + 1));
                end++;
                continue;
            }

            break;
        }

        while (_tokens.Kind(end + 1) == PgTokenKind.LeftBracket && _tokens.Match(end + 1) > end)
        {
            end = _tokens.Match(end + 1);
            isArray = true;
        }

        type = builder.ToString().ToLowerInvariant();
        return true;
    }

    private int OperandStart(int index)
    {
        if (index < 0)
        {
            return -1;
        }

        if (_castStarts.TryGetValue(index, out var castStart))
        {
            return castStart;
        }

        switch (_tokens.Kind(index))
        {
            case PgTokenKind.RightParen:
                var open = _tokens.Match(index);
                if (open < 0)
                {
                    return -1;
                }

                if (open > 0 && _tokens.Kind(open - 1) == PgTokenKind.Word && !Reserved.Contains(_tokens.Text(open - 1)))
                {
                    return QualifiedStart(open - 1);
                }

                if (open > 0 && _tokens.Kind(open - 1) == PgTokenKind.QuotedIdentifier)
                {
                    return QualifiedStart(open - 1);
                }

                return open;
            case PgTokenKind.RightBracket:
                return OperandStart(_tokens.Match(index) - 1);
            case PgTokenKind.Word or PgTokenKind.QuotedIdentifier:
                return QualifiedStart(index);
            case PgTokenKind.String or PgTokenKind.EscapeString or PgTokenKind.DollarString or PgTokenKind.Number or PgTokenKind.Parameter:
                return index;
            default:
                return -1;
        }
    }

    private int QualifiedStart(int index)
    {
        while (index >= 2 && _tokens.Kind(index - 1) == PgTokenKind.Period && _tokens.IsName(index - 2))
        {
            index -= 2;
        }

        return index;
    }

    private int OperandEnd(int index)
    {
        var end = index;
        switch (_tokens.Kind(index))
        {
            case PgTokenKind.Operator when _tokens.IsOperator(index, "-") || _tokens.IsOperator(index, "+"):
                return OperandEnd(index + 1);
            case PgTokenKind.LeftParen:
                end = _tokens.Match(index);
                break;
            case PgTokenKind.Word or PgTokenKind.QuotedIdentifier:
                while (_tokens.Kind(end + 1) == PgTokenKind.Period && _tokens.IsName(end + 2))
                {
                    end += 2;
                }

                if (_tokens.Kind(end + 1) == PgTokenKind.LeftParen)
                {
                    end = _tokens.Match(end + 1);
                }

                break;
        }

        while (_tokens.Kind(end + 1) == PgTokenKind.DoubleColon && TryScanType(end + 2, out _, out var typeEnd, out _))
        {
            end = typeEnd;
        }

        return end;
    }

    private void RewriteArrayComparisons()
    {
        for (var index = 1; index < _tokens.Count; index++)
        {
            if (!_tokens.IsAnyWord(index, "ANY", "SOME", "ALL") || _tokens.Kind(index + 1) != PgTokenKind.LeftParen || _tokens.IsDead(index))
            {
                continue;
            }

            var negated = _tokens.IsWord(index, "ALL");
            if (negated ? !(_tokens.IsOperator(index - 1, "<>") || _tokens.IsOperator(index - 1, "!=")) : !_tokens.IsOperator(index - 1, "="))
            {
                continue;
            }

            var open = index + 1;
            var close = _tokens.Match(open);
            _tokens.Replace(index - 1, negated ? "NOT IN" : "IN");
            if (_tokens.IsAnyWord(open + 1, "SELECT", "VALUES", "WITH"))
            {
                _tokens.Remove(index);
            }
            else if (_tokens.IsWord(open + 1, "ARRAY") || (_tokens.IsWord(open + 1, "json_array") && _tokens.IsEdited(open + 1)))
            {
                _tokens.Remove(index);
                _tokens.Remove(open);
                _tokens.Remove(open + 1);
                _tokens.Remove(close);
            }
            else
            {
                _tokens.Replace(index, "(SELECT value FROM json_each");
                _tokens.Append(close, ")");
            }
        }
    }

    private PgError? RewritePatternOperators()
    {
        for (var index = 1; index < _tokens.Count; index++)
        {
            if (_tokens.IsDead(index))
            {
                continue;
            }

            var operatorStart = index;
            var operatorEnd = index;
            string? symbol = null;
            if (_tokens.IsWord(index, "ILIKE"))
            {
                symbol = "~~*";
            }
            else if (_tokens.Kind(index) == PgTokenKind.Operator && _tokens.Raw(index) is "~" or "!~" or "~*" or "!~*" or "~~" or "!~~" or "~~*" or "!~~*")
            {
                symbol = _tokens.Text(index);
            }
            else if (_tokens.IsWord(index, "OPERATOR") && _tokens.Kind(index + 1) == PgTokenKind.LeftParen)
            {
                var close = _tokens.Match(index + 1);
                if (close > 0 && _tokens.Kind(close - 1) == PgTokenKind.Operator)
                {
                    symbol = _tokens.Text(close - 1);
                    operatorEnd = close;
                }
            }

            if (symbol is null)
            {
                continue;
            }

            var negated = symbol[0] == '!';
            if (_tokens.IsWord(index, "ILIKE") && _tokens.IsWord(index - 1, "NOT"))
            {
                negated = true;
                operatorStart = index - 1;
            }

            var core = symbol.TrimStart('!');
            var lhsEnd = operatorStart - 1;
            var lhsStart = OperandStart(lhsEnd);
            var rhsStart = operatorEnd + 1;
            if (lhsStart < 0 || rhsStart >= _tokens.Count)
            {
                return PgError.Syntax($"syntax error at or near \"{symbol}\"");
            }

            var rhsEnd = OperandEnd(rhsStart);
            _tokens.RemoveRange(operatorStart, operatorEnd);
            switch (core)
            {
                case "~~":
                    _tokens.Replace(operatorEnd, negated ? "NOT LIKE" : "LIKE");
                    break;
                case "~~*":
                    var escapeEnd = _tokens.IsWord(rhsEnd + 1, "ESCAPE") ? rhsEnd + 2 : rhsEnd;
                    _tokens.Prepend(lhsStart, negated ? "NOT (LOWER(" : "LOWER(");
                    _tokens.Append(lhsEnd, ")");
                    _tokens.Replace(operatorEnd, "LIKE LOWER(");
                    _tokens.Append(rhsEnd, ")");
                    if (negated)
                    {
                        _tokens.Append(escapeEnd, ")");
                    }

                    break;
                default:
                    if (rhsStart != rhsEnd || !_literals.TryGetValue(rhsStart, out var pattern)
                        || RegexToPattern(pattern, core == "~*") is not { } translated)
                    {
                        return PgError.Unsupported("regular expression matching beyond anchored literal and wildcard patterns");
                    }

                    var keyword = core == "~*" ? "LIKE" : "GLOB";
                    _tokens.Replace(operatorEnd, negated ? "NOT " + keyword : keyword);
                    _tokens.Replace(rhsStart, PgRewriteContext.Literal(translated) + (core == "~*" ? " ESCAPE '\\'" : string.Empty));
                    break;
            }

            index = rhsEnd;
        }

        return null;
    }

    private static string? RegexToPattern(string regex, bool like)
    {
        var body = regex.AsSpan();
        var anchoredStart = body.StartsWith('^');
        if (anchoredStart)
        {
            body = body[1..];
        }

        var anchoredEnd = body.EndsWith('$') && !body.EndsWith("\\$");
        if (anchoredEnd)
        {
            body = body[..^1];
        }

        if (body.Length >= 2 && body[0] == '(' && body[^1] == ')' && body[1..^1].IndexOfAny("()|") < 0)
        {
            body = body[1..^1];
        }

        var builder = new StringBuilder();
        if (!anchoredStart)
        {
            builder.Append(like ? '%' : '*');
        }

        for (var index = 0; index < body.Length; index++)
        {
            var current = body[index];
            if (current == '.' && index + 1 < body.Length && body[index + 1] == '*')
            {
                builder.Append(like ? '%' : '*');
                index++;
                continue;
            }

            if (current == '.')
            {
                builder.Append(like ? '_' : '?');
                continue;
            }

            if (current == '\\' && index + 1 < body.Length)
            {
                current = body[++index];
            }
            else if ("[](){}+?|^$*".Contains(current, StringComparison.Ordinal))
            {
                return null;
            }

            builder.Append(like
                ? current is '%' or '_' or '\\' ? "\\" + current : current.ToString()
                : current is '*' or '?' or '[' ? "[" + current + "]" : current.ToString());
        }

        if (!anchoredEnd)
        {
            builder.Append(like ? '%' : '*');
        }

        return builder.ToString();
    }

    private void RewriteDistinctFrom()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (!_tokens.IsWord(index, "IS") || _tokens.IsDead(index))
            {
                continue;
            }

            if (_tokens.IsWord(index + 1, "DISTINCT") && _tokens.IsWord(index + 2, "FROM"))
            {
                _tokens.Replace(index + 1, "NOT");
                _tokens.Remove(index + 2);
            }
            else if (_tokens.IsWord(index + 1, "NOT") && _tokens.IsWord(index + 2, "DISTINCT") && _tokens.IsWord(index + 3, "FROM"))
            {
                _tokens.Remove(index + 1);
                _tokens.Remove(index + 2);
                _tokens.Remove(index + 3);
            }
        }
    }

    private void CollectParameterColumns()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.Kind(index) != PgTokenKind.Parameter || !int.TryParse(_tokens.Raw(index)[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                continue;
            }

            if (IsComparison(index - 1) && _tokens.IsName(index - 2))
            {
                _parameterColumns.Add(new PgParameterColumn(number, _tokens.Identifier(index - 2)));
            }
            else if (IsComparison(index + 1) && _tokens.IsName(index + 2) && _tokens.Kind(index + 3) != PgTokenKind.LeftParen)
            {
                _parameterColumns.Add(new PgParameterColumn(number, _tokens.Identifier(index + 2)));
            }
        }

        for (var index = 0; index < _tokens.Count; index++)
        {
            if (!_tokens.IsWord(index, "INTO"))
            {
                continue;
            }

            var open = index + 2;
            while (_tokens.Kind(open) == PgTokenKind.Period)
            {
                open += 2;
            }

            if (_tokens.Kind(open) != PgTokenKind.LeftParen)
            {
                return;
            }

            var close = _tokens.Match(open);
            List<string> columns = [];
            for (var column = open + 1; column < close; column++)
            {
                if (_tokens.IsName(column))
                {
                    columns.Add(_tokens.Identifier(column));
                }
            }

            if (!_tokens.IsWord(close + 1, "VALUES"))
            {
                return;
            }

            for (var tuple = close + 2; _tokens.Kind(tuple) == PgTokenKind.LeftParen; tuple = _tokens.Match(tuple) + 2)
            {
                var position = 0;
                var tupleEnd = _tokens.Match(tuple);
                for (var item = tuple + 1; item < tupleEnd; item++)
                {
                    switch (_tokens.Kind(item))
                    {
                        case PgTokenKind.Comma:
                            position++;
                            break;
                        case PgTokenKind.LeftParen:
                            item = _tokens.Match(item);
                            break;
                        case PgTokenKind.Parameter when position < columns.Count
                            && int.TryParse(_tokens.Raw(item)[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var parameter):
                            _parameterColumns.Add(new PgParameterColumn(parameter, columns[position]));
                            break;
                    }
                }

                if (_tokens.Kind(tupleEnd + 1) != PgTokenKind.Comma)
                {
                    break;
                }
            }

            return;
        }
    }

    private bool IsComparison(int index) =>
        (_tokens.Kind(index) == PgTokenKind.Operator && _tokens.Raw(index) is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=")
        || _tokens.IsAnyWord(index, "LIKE", "ILIKE");

    private static string? DecodeEscapeString(ReadOnlySpan<char> body)
    {
        var builder = new StringBuilder(body.Length);
        for (var index = 0; index < body.Length; index++)
        {
            var current = body[index];
            if (current == '\'' && index + 1 < body.Length && body[index + 1] == '\'')
            {
                builder.Append('\'');
                index++;
                continue;
            }

            if (current != '\\' || index + 1 >= body.Length)
            {
                builder.Append(current);
                continue;
            }

            var escape = body[++index];
            switch (escape)
            {
                case 'b':
                    builder.Append('\b');
                    break;
                case 'f':
                    builder.Append('\f');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                case >= '0' and <= '7':
                    var octalLength = 1;
                    while (octalLength < 3 && index + octalLength < body.Length && body[index + octalLength] is >= '0' and <= '7')
                    {
                        octalLength++;
                    }

                    builder.Append((char)Convert.ToInt32(body.Slice(index, octalLength).ToString(), 8));
                    index += octalLength - 1;
                    break;
                case 'x' when index + 1 < body.Length && char.IsAsciiHexDigit(body[index + 1]):
                    var hexLength = index + 2 < body.Length && char.IsAsciiHexDigit(body[index + 2]) ? 2 : 1;
                    builder.Append((char)int.Parse(body.Slice(index + 1, hexLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                    index += hexLength;
                    break;
                case 'u' or 'U':
                    var digits = escape == 'u' ? 4 : 8;
                    if (index + digits >= body.Length
                        || !int.TryParse(body.Slice(index + 1, digits), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codePoint)
                        || codePoint > 0x10FFFF || codePoint is >= 0xD800 and <= 0xDFFF)
                    {
                        return null;
                    }

                    builder.Append(char.ConvertFromUtf32(codePoint));
                    index += digits;
                    break;
                default:
                    builder.Append(escape);
                    break;
            }
        }

        return builder.ToString();
    }
}
