using System.Buffers;
using System.Collections;
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal ref partial struct PostgresSqlRewriter
{
    [InlineArray(8)]
    private struct InlinePairBuffer
    {
        private (int Key, int Value) _element;
    }

    private sealed class SmallIntDictionary((int Key, int Value)[] entries) : IReadOnlyDictionary<int, int>
    {
        public int Count => entries.Length;

        public IEnumerable<int> Keys => entries.Select(static pair => pair.Key);

        public IEnumerable<int> Values => entries.Select(static pair => pair.Value);

        public int this[int key] => TryGetValue(key, out var value) ? value : throw new KeyNotFoundException();

        public bool ContainsKey(int key) => TryGetValue(key, out _);

        public bool TryGetValue(int key, out int value)
        {
            for (var index = 0; index < entries.Length; index++)
            {
                if (entries[index].Key == key)
                {
                    value = entries[index].Value;
                    return true;
                }
            }

            value = 0;
            return false;
        }

        public IEnumerator<KeyValuePair<int, int>> GetEnumerator()
        {
            foreach (var (key, value) in entries)
            {
                yield return new KeyValuePair<int, int>(key, value);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdefABCDEF");

    private static readonly FrozenSet<string> NamespaceColumns = FrozenSet.Create(StringComparer.Ordinal, ["oid", "nspname", "nspowner", "nspacl"]);

    private static readonly FrozenSet<string> UnsupportedCalls = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
    [
        "array_to_string", "array_length", "array_upper", "array_lower", "pg_get_statisticsobjdef_columns",
        "pg_get_partkeydef", "pg_get_triggerdef", "pg_get_ruledef", "pg_partition_root", "pg_get_function_identity_arguments",
    ]);

    private static readonly string[] ParameterPlaceholders = BuildParameterPlaceholders();

    private readonly PgTokenEditor _tokens;
    private readonly PgRewriteContext _context;
    private InlinePairBuffer _inlineCastStarts;
    private int _inlineCastStartCount;
    private Dictionary<int, int>? _overflowCastStarts;
    private InlinePairBuffer _inlineParamTypes;
    private int _inlineParamTypeCount;
    private Dictionary<int, int>? _overflowParamTypes;
    private Dictionary<int, string>? _literals;
    private List<int>? _deferred;
    private List<int>? _selectLiterals;
    private List<(string Name, string Alias, int Token)>? _catalogRelations;
    private int _parameterCount;
    private bool _usesCatalog;
    private bool _dependsOnSettings;
    private bool _dependsOnSessionIdentity;

    private PostgresSqlRewriter(PgTokenEditor tokens, PgRewriteContext context)
    {
        _tokens = tokens;
        _context = context;
    }

    public static PgRewriteResult Rewrite(PgTokenEditor tokens, PgRewriteContext context)
    {
        if (!tokens.IsBalanced)
        {
            return PgError.Syntax("syntax error: unbalanced parentheses or brackets");
        }

        var rewriter = new PostgresSqlRewriter(tokens, context);
        return rewriter.Run();
    }

    private PgRewriteResult Run()
    {
        if (_tokens.HasFlag(PgSyntaxFlags.Parameter | PgSyntaxFlags.StringLiteral) && RewriteLiterals() is { } literalError)
        {
            return literalError;
        }

        if (_tokens.HasFlag(PgSyntaxFlags.Period | PgSyntaxFlags.PgPrefixWord) && RewriteQualifiedNames() is { } qualifiedError)
        {
            return qualifiedError;
        }

        if (_catalogRelations is { Count: > 0 })
        {
            JoinMissingNamespace();
        }

        if (_tokens.HasFlag(PgSyntaxFlags.UnsupportedCall))
        {
            KillUnsupportedCalls();
        }

        if (_tokens.HasFlag(PgSyntaxFlags.ClauseWord))
        {
            RewriteClauses();
        }

        if (_tokens.HasFlag(PgSyntaxFlags.DoubleColon) && RewriteCasts() is { } castError)
        {
            return castError;
        }

        IReadOnlyList<PgParameterColumn> parameterColumns = [];
        IReadOnlyList<string> referenced = [];
        if (_parameterCount > 0 && HasUntypedParameters())
        {
            parameterColumns = CollectParameterColumns();
            referenced = parameterColumns.Count > 0 ? ReferencedNames(parameterColumns) : [];
        }

        if (_tokens.HasFlag(PgSyntaxFlags.ArrayComparisonWord))
        {
            RewriteArrayComparisons();
        }

        if (_tokens.HasFlag(PgSyntaxFlags.PatternOperator) && RewritePatternOperators() is { } patternError)
        {
            return patternError;
        }

        if (_tokens.HasFlag(PgSyntaxFlags.IsWord))
        {
            RewriteDistinctFrom();
        }

        if (_tokens.HasFlag(PgSyntaxFlags.FunctionCall | PgSyntaxFlags.SpecialKeyword) && RewriteFunctions() is { } functionError)
        {
            return functionError;
        }

        if (_tokens.HasFlag(PgSyntaxFlags.CreateOrAlter))
        {
            RewriteDefinitions();
        }

        if (_deferred is { Count: > 0 })
        {
            RunDeferred();
        }

        if (_tokens.HasFlag(PgSyntaxFlags.SelectStart))
        {
            AliasSelectList();
        }

        var sql = _tokens.Render();
        SqlValue[] boundLiterals = _selectLiterals is { } candidates && !PgRewrite.Fits(sql, describe: true) ? BindSelectLiterals(candidates) : [];
        if (boundLiterals.Length > 0)
        {
            sql = _tokens.Render();
        }

        return new PgRewrite(
            sql,
            _parameterCount,
            BuildParameterTypes(),
            parameterColumns,
            referenced,
            _usesCatalog,
            boundLiterals,
            _dependsOnSettings,
            _dependsOnSessionIdentity);
    }

    private bool HasUntypedParameters() => (_overflowParamTypes?.Count ?? _inlineParamTypeCount) < _parameterCount;

    private IReadOnlyDictionary<int, int> BuildParameterTypes()
    {
        if (_overflowParamTypes is not null)
        {
            return _overflowParamTypes;
        }

        if (_inlineParamTypeCount == 0)
        {
            return FrozenDictionary<int, int>.Empty;
        }

        var entries = new (int Key, int Value)[_inlineParamTypeCount];
        for (var index = 0; index < _inlineParamTypeCount; index++)
        {
            entries[index] = _inlineParamTypes[index];
        }

        return new SmallIntDictionary(entries);
    }

    private void SetParameterType(int parameter, int oid)
    {
        if (_overflowParamTypes is not null)
        {
            _overflowParamTypes[parameter] = oid;
            return;
        }

        for (var index = 0; index < _inlineParamTypeCount; index++)
        {
            if (_inlineParamTypes[index].Key == parameter)
            {
                _inlineParamTypes[index] = (parameter, oid);
                return;
            }
        }

        if (_inlineParamTypeCount < 8)
        {
            _inlineParamTypes[_inlineParamTypeCount++] = (parameter, oid);
            return;
        }

        var map = new Dictionary<int, int>(16);
        for (var index = 0; index < 8; index++)
        {
            map[_inlineParamTypes[index].Key] = _inlineParamTypes[index].Value;
        }

        map[parameter] = oid;
        _overflowParamTypes = map;
    }

    private void SetCastStart(int end, int start)
    {
        if (_overflowCastStarts is not null)
        {
            _overflowCastStarts[end] = start;
            return;
        }

        for (var index = 0; index < _inlineCastStartCount; index++)
        {
            if (_inlineCastStarts[index].Key == end)
            {
                _inlineCastStarts[index] = (end, start);
                return;
            }
        }

        if (_inlineCastStartCount < 8)
        {
            _inlineCastStarts[_inlineCastStartCount++] = (end, start);
            return;
        }

        var map = new Dictionary<int, int>(16);
        for (var index = 0; index < 8; index++)
        {
            map[_inlineCastStarts[index].Key] = _inlineCastStarts[index].Value;
        }

        map[end] = start;
        _overflowCastStarts = map;
    }

    private bool TryGetCastStart(int end, out int start)
    {
        if (_overflowCastStarts is not null)
        {
            return _overflowCastStarts.TryGetValue(end, out start);
        }

        for (var index = 0; index < _inlineCastStartCount; index++)
        {
            if (_inlineCastStarts[index].Key == end)
            {
                start = _inlineCastStarts[index].Value;
                return true;
            }
        }

        start = 0;
        return false;
    }

    internal static bool IsReserved(ReadOnlySpan<char> raw)
    {
        var len = raw.Length;
        if (len is < 2 or > 10)
        {
            return false;
        }

        var first = (char)(raw[0] | 0x20);
        return len switch
        {
            2 => first switch
            {
                'a' => raw.Equals("AS", StringComparison.OrdinalIgnoreCase),
                'b' => raw.Equals("BY", StringComparison.OrdinalIgnoreCase),
                'd' => raw.Equals("DO", StringComparison.OrdinalIgnoreCase),
                'i' => raw.Equals("IN", StringComparison.OrdinalIgnoreCase) || raw.Equals("IS", StringComparison.OrdinalIgnoreCase),
                'o' => raw.Equals("ON", StringComparison.OrdinalIgnoreCase) || raw.Equals("OR", StringComparison.OrdinalIgnoreCase),
                't' => raw.Equals("TO", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            3 => first switch
            {
                'a' => raw.Equals("ALL", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("AND", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("ANY", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("ASC", StringComparison.OrdinalIgnoreCase),
                'e' => raw.Equals("END", StringComparison.OrdinalIgnoreCase),
                'f' => raw.Equals("FOR", StringComparison.OrdinalIgnoreCase),
                'k' => raw.Equals("KEY", StringComparison.OrdinalIgnoreCase),
                'n' => raw.Equals("NOT", StringComparison.OrdinalIgnoreCase),
                's' => raw.Equals("SET", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            4 => first switch
            {
                'c' => raw.Equals("CASE", StringComparison.OrdinalIgnoreCase),
                'd' => raw.Equals("DESC", StringComparison.OrdinalIgnoreCase),
                'e' => raw.Equals("ELSE", StringComparison.OrdinalIgnoreCase),
                'f' => raw.Equals("FROM", StringComparison.OrdinalIgnoreCase) || raw.Equals("FULL", StringComparison.OrdinalIgnoreCase),
                'i' => raw.Equals("INTO", StringComparison.OrdinalIgnoreCase),
                'j' => raw.Equals("JOIN", StringComparison.OrdinalIgnoreCase),
                'l' => raw.Equals("LEFT", StringComparison.OrdinalIgnoreCase) || raw.Equals("LIKE", StringComparison.OrdinalIgnoreCase),
                'n' => raw.Equals("NULL", StringComparison.OrdinalIgnoreCase),
                'o' => raw.Equals("OVER", StringComparison.OrdinalIgnoreCase),
                's' => raw.Equals("SOME", StringComparison.OrdinalIgnoreCase),
                't' => raw.Equals("THEN", StringComparison.OrdinalIgnoreCase),
                'w' => raw.Equals("WHEN", StringComparison.OrdinalIgnoreCase) || raw.Equals("WITH", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            5 => first switch
            {
                'c' => raw.Equals("CHECK", StringComparison.OrdinalIgnoreCase) || raw.Equals("CROSS", StringComparison.OrdinalIgnoreCase),
                'f' => raw.Equals("FETCH", StringComparison.OrdinalIgnoreCase),
                'g' => raw.Equals("GROUP", StringComparison.OrdinalIgnoreCase),
                'i' => raw.Equals("ILIKE", StringComparison.OrdinalIgnoreCase) || raw.Equals("INNER", StringComparison.OrdinalIgnoreCase),
                'l' => raw.Equals("LIMIT", StringComparison.OrdinalIgnoreCase),
                'o' => raw.Equals("ORDER", StringComparison.OrdinalIgnoreCase) || raw.Equals("OUTER", StringComparison.OrdinalIgnoreCase),
                'r' => raw.Equals("RIGHT", StringComparison.OrdinalIgnoreCase),
                't' => raw.Equals("TABLE", StringComparison.OrdinalIgnoreCase),
                'u' => raw.Equals("UNION", StringComparison.OrdinalIgnoreCase) || raw.Equals("USING", StringComparison.OrdinalIgnoreCase),
                'w' => raw.Equals("WHERE", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            6 => first switch
            {
                'c' => raw.Equals("CREATE", StringComparison.OrdinalIgnoreCase),
                'd' => raw.Equals("DELETE", StringComparison.OrdinalIgnoreCase),
                'e' => raw.Equals("ESCAPE", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("EXCEPT", StringComparison.OrdinalIgnoreCase)
                    || raw.Equals("EXISTS", StringComparison.OrdinalIgnoreCase),
                'h' => raw.Equals("HAVING", StringComparison.OrdinalIgnoreCase),
                'i' => raw.Equals("INSERT", StringComparison.OrdinalIgnoreCase) || raw.Equals("ISNULL", StringComparison.OrdinalIgnoreCase),
                'o' => raw.Equals("OFFSET", StringComparison.OrdinalIgnoreCase),
                's' => raw.Equals("SELECT", StringComparison.OrdinalIgnoreCase),
                'u' => raw.Equals("UNIQUE", StringComparison.OrdinalIgnoreCase) || raw.Equals("UPDATE", StringComparison.OrdinalIgnoreCase),
                'v' => raw.Equals("VALUES", StringComparison.OrdinalIgnoreCase),
                'w' => raw.Equals("WINDOW", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            7 => first switch
            {
                'b' => raw.Equals("BETWEEN", StringComparison.OrdinalIgnoreCase),
                'c' => raw.Equals("COLLATE", StringComparison.OrdinalIgnoreCase),
                'd' => raw.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase),
                'f' => raw.Equals("FOREIGN", StringComparison.OrdinalIgnoreCase),
                'l' => raw.Equals("LATERAL", StringComparison.OrdinalIgnoreCase),
                'n' => raw.Equals("NATURAL", StringComparison.OrdinalIgnoreCase) || raw.Equals("NOTNULL", StringComparison.OrdinalIgnoreCase),
                'p' => raw.Equals("PRIMARY", StringComparison.OrdinalIgnoreCase),
                's' => raw.Equals("SIMILAR", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            8 => first == 'd' && raw.Equals("DISTINCT", StringComparison.OrdinalIgnoreCase),
            9 => first switch
            {
                'i' => raw.Equals("INTERSECT", StringComparison.OrdinalIgnoreCase),
                'p' => raw.Equals("PARTITION", StringComparison.OrdinalIgnoreCase),
                'r' => raw.Equals("RETURNING", StringComparison.OrdinalIgnoreCase),
                _ => false,
            },
            10 => (first == 'c' && raw.Equals("CONSTRAINT", StringComparison.OrdinalIgnoreCase))
                || (first == 'r' && raw.Equals("REFERENCES", StringComparison.OrdinalIgnoreCase)),
            _ => false,
        };
    }

    private static bool TryParseParameterNumber(ReadOnlySpan<char> digits, out int number)
    {
        if (digits.IsEmpty || digits.Length > 5)
        {
            number = 0;
            return false;
        }

        var value = 0;
        for (var index = 0; index < digits.Length; index++)
        {
            var digit = digits[index] - '0';
            if ((uint)digit > 9u)
            {
                number = 0;
                return false;
            }

            value = (value * 10) + digit;
        }

        if (value is < 1 or > ushort.MaxValue)
        {
            number = 0;
            return false;
        }

        number = value;
        return true;
    }

    private static string[] BuildParameterPlaceholders()
    {
        var values = new string[65];
        values[0] = string.Empty;
        for (var index = 1; index < values.Length; index++)
        {
            values[index] = "?" + index.ToString(CultureInfo.InvariantCulture);
        }

        return values;
    }

    private static string Placeholder(int number) =>
        (uint)number < (uint)ParameterPlaceholders.Length
            ? ParameterPlaceholders[number]
            : "?" + number.ToString(CultureInfo.InvariantCulture);

    private bool IsStringLiteralToken(int index) =>
        (_literals is not null && _literals.ContainsKey(index))
        || (_tokens.Kind(index) == PgTokenKind.String && _tokens.Raw(index)[0] == '\'');

    private bool TryGetLiteral(int index, out string? literal, out ReadOnlySpan<char> literalSpan)
    {
        if (_literals is not null && _literals.TryGetValue(index, out var cached))
        {
            literal = cached;
            literalSpan = cached;
            return true;
        }

        if (_tokens.Kind(index) == PgTokenKind.String)
        {
            var raw = _tokens.Raw(index);
            if (raw.Length >= 2 && raw[0] == '\'')
            {
                var inner = raw[1..^1];
                if (!inner.Contains('\''))
                {
                    literal = null;
                    literalSpan = inner;
                    return true;
                }

                var decoded = inner.ToString().Replace("''", "'", StringComparison.Ordinal);
                (_literals ??= [])[index] = decoded;
                literal = decoded;
                literalSpan = decoded;
                return true;
            }
        }

        literal = null;
        literalSpan = [];
        return false;
    }

    private bool TryGetLiteralString(int index, out string value)
    {
        if (TryGetLiteral(index, out var cached, out var span))
        {
            value = cached ?? span.ToString();
            return true;
        }

        value = string.Empty;
        return false;
    }

    private SqlValue[] BindSelectLiterals(List<int> candidates)
    {
        List<SqlValue> values = [];
        foreach (var index in candidates)
        {
            var parameter = _parameterCount + values.Count + 1;
            var placeholder = Placeholder(parameter);
            if (parameter <= ushort.MaxValue && placeholder.Length < _tokens.Raw(index).Length && TryGetLiteralString(index, out var literalValue))
            {
                _tokens.Replace(index, placeholder);
                values.Add(SqlValue.FromText(literalValue));
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
                    if (!TryParseParameterNumber(_tokens.Raw(index)[1..], out var number))
                    {
                        return new PgError("42P02", $"there is no parameter {_tokens.Text(index)}");
                    }

                    _parameterCount = Math.Max(_parameterCount, number);
                    _tokens.Replace(index, Placeholder(number));
                    break;
                case PgTokenKind.EscapeString:
                    if (DecodeEscapeString(_tokens.Raw(index)[2..^1]) is not { } escaped)
                    {
                        return new PgError("22025", "invalid Unicode escape value in string literal");
                    }

                    if (escaped.Contains('\0', StringComparison.Ordinal))
                    {
                        return PgRewrite.NulCharacter;
                    }

                    (_literals ??= [])[index] = escaped;
                    _tokens.Replace(index, PgRewriteContext.Literal(escaped));
                    break;
                case PgTokenKind.DollarString:
                    var raw = _tokens.Raw(index);
                    var tag = raw[1..].IndexOf('$') + 2;
                    var content = raw[tag..^tag].ToString();
                    (_literals ??= [])[index] = content;
                    _tokens.Replace(index, PgRewriteContext.Literal(content));
                    break;
                case PgTokenKind.String when _tokens.Raw(index)[0] is 'B' or 'b':
                    return PgError.Unsupported("bit string literals");
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
                if (_tokens.EqualsIdentifier(index, "public"))
                {
                    _tokens.Remove(index);
                    _tokens.Remove(index + 1);
                }
                else if (_tokens.EqualsIdentifier(index, "pg_catalog"))
                {
                    _tokens.Remove(index);
                    _tokens.Remove(index + 1);
                    if (_tokens.Kind(index + 3) != PgTokenKind.LeftParen)
                    {
                        var name = _tokens.Identifier(index + 2);
                        if (PgCatalogSql.TryGetRelation("pg_catalog", name, _context, out var relation, out var dependsOnIdentity))
                        {
                            if (name == "pg_settings")
                            {
                                _dependsOnSettings = true;
                            }

                            _dependsOnSessionIdentity |= dependsOnIdentity;
                            ReplaceRelation(index + 2, name, relation);
                        }
                    }
                }
                else if (_tokens.EqualsIdentifier(index, "information_schema") && _tokens.Kind(index - 1) != PgTokenKind.Period)
                {
                    return PgError.Unsupported("information_schema") with { Hint = "query the pg_catalog relations instead" };
                }

                index += 2;
                continue;
            }

            if (_tokens.Kind(index) == PgTokenKind.Word
                && _tokens.Raw(index).StartsWith("pg_", StringComparison.OrdinalIgnoreCase)
                && _tokens.Kind(index + 1) != PgTokenKind.LeftParen
                && _tokens.Kind(index + 1) != PgTokenKind.Period
                && (_tokens.IsAnyWord(index - 1, "FROM", "JOIN") || _tokens.Kind(index - 1) == PgTokenKind.Comma)
                && _tokens.Kind(index - 1) != PgTokenKind.Period)
            {
                var name = _tokens.Identifier(index);
                if (PgCatalogSql.TryGetRelation("pg_catalog", name, _context, out var bare, out var dependsOnIdentity))
                {
                    if (name == "pg_settings")
                    {
                        _dependsOnSettings = true;
                    }

                    _dependsOnSessionIdentity |= dependsOnIdentity;
                    ReplaceRelation(index, name, bare);
                }
            }
        }

        return null;
    }

    private void ReplaceRelation(int index, string name, string relation)
    {
        _usesCatalog = true;
        _tokens.Replace(index, string.Concat("(", relation, ")"));
        var next = index + 1;
        var aliased = _tokens.IsWord(next, "AS")
            || _tokens.Kind(next) == PgTokenKind.QuotedIdentifier
            || (_tokens.Kind(next) == PgTokenKind.Word && !IsReserved(_tokens.Raw(next)));
        var relations = _catalogRelations ??= [];
        if (!aliased)
        {
            _tokens.Append(index, string.Concat(" AS ", name));
            relations.Add((name, name, index));
            return;
        }

        var alias = _tokens.IsWord(next, "AS") ? next + 1 : next;
        if (_tokens.IsName(alias))
        {
            relations.Add((name, _tokens.Identifier(alias), alias));
        }
    }

    private void JoinMissingNamespace()
    {
        if (_catalogRelations is null || _catalogRelations.Find(static relation => relation.Name == "pg_class") is not { Alias: { } owner } pgClass)
        {
            return;
        }

        var namespaceLookup = NamespaceColumns.GetAlternateLookup<ReadOnlySpan<char>>();
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

            var namespaceColumn = _tokens.IsName(index + 2)
                && (_tokens.Kind(index + 2) == PgTokenKind.Word
                    ? namespaceLookup.Contains(_tokens.Raw(index + 2)) || NamespaceColumns.Contains(_tokens.Identifier(index + 2))
                    : NamespaceColumns.Contains(_tokens.Identifier(index + 2)));
            qualifiers[identifier] = (!qualifiers.TryGetValue(identifier, out var known) || known) && namespaceColumn;
        }

        foreach (var (qualifier, onlyNamespace) in qualifiers)
        {
            if (onlyNamespace && !declared.Contains(qualifier) && !_catalogRelations.Exists(relation => relation.Alias == qualifier)
                && PgCatalogSql.TryGetRelation("pg_catalog", "pg_namespace", _context, out var relation, out var dependsOnIdentity))
            {
                _dependsOnSessionIdentity |= dependsOnIdentity;
                var quoted = "\"" + qualifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                var source = "\"" + owner.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
                _tokens.Append(pgClass.Token, $" LEFT JOIN ({relation}) AS {quoted} ON {quoted}.oid = {source}.relnamespace");
                return;
            }
        }
    }

    private IReadOnlyList<string> ReferencedNames(IReadOnlyList<PgParameterColumn> parameterColumns)
    {
        Span<int> uniqueTokens = stackalloc int[32];
        var uniqueCount = 0;
        var hasQuoted = false;
        for (var index = 0; index < _tokens.Count; index++)
        {
            var kind = _tokens.Kind(index);
            if (kind == PgTokenKind.QuotedIdentifier)
            {
                hasQuoted = true;
                break;
            }

            if (kind != PgTokenKind.Word)
            {
                continue;
            }

            var raw = _tokens.Raw(index);
            if (IsReserved(raw))
            {
                continue;
            }

            var rawLen = raw.Length;
            var rawFirst = raw[0] | 0x20;
            var duplicate = false;
            for (var existing = 0; existing < uniqueCount; existing++)
            {
                var prev = _tokens.Raw(uniqueTokens[existing]);
                if (prev.Length == rawLen && (prev[0] | 0x20) == rawFirst && prev.Equals(raw, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate = true;
                    break;
                }
            }

            if (duplicate)
            {
                continue;
            }

            if (uniqueCount == uniqueTokens.Length)
            {
                hasQuoted = true;
                break;
            }

            uniqueTokens[uniqueCount++] = index;
        }

        if (!hasQuoted)
        {
            if (uniqueCount == 0)
            {
                return [];
            }

            var result = new string[uniqueCount];
            var paramColCount = parameterColumns.Count;
            for (var index = 0; index < uniqueCount; index++)
            {
                var tokenIndex = uniqueTokens[index];
                var raw = _tokens.Raw(tokenIndex);
                string? reused = null;
                for (var p = 0; p < paramColCount; p++)
                {
                    var candidate = parameterColumns[p].Column;
                    if (candidate.Length == raw.Length && candidate.AsSpan().Equals(raw, StringComparison.OrdinalIgnoreCase))
                    {
                        reused = candidate;
                        break;
                    }
                }

                result[index] = reused ?? _tokens.Identifier(tokenIndex);
            }

            return result;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        var seenLookup = seen.GetAlternateLookup<ReadOnlySpan<char>>();
        List<string> names = [];
        for (var index = 0; index < _tokens.Count; index++)
        {
            var kind = _tokens.Kind(index);
            if (kind == PgTokenKind.Word)
            {
                var raw = _tokens.Raw(index);
                if (IsReserved(raw) || seenLookup.Contains(raw))
                {
                    continue;
                }

                var identifier = _tokens.Identifier(index);
                seen.Add(identifier);
                names.Add(identifier);
            }
            else if (kind == PgTokenKind.QuotedIdentifier)
            {
                var identifier = _tokens.Identifier(index);
                if (seen.Add(identifier))
                {
                    names.Add(identifier);
                }
            }
        }

        return names;
    }

    private void KillUnsupportedCalls()
    {
        var unsupportedLookup = UnsupportedCalls.GetAlternateLookup<ReadOnlySpan<char>>();
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.Kind(index) != PgTokenKind.Word
                || _tokens.Kind(index + 1) != PgTokenKind.LeftParen
                || _tokens.IsDead(index)
                || !unsupportedLookup.Contains(_tokens.Raw(index)))
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
            if (_tokens.Kind(index) != PgTokenKind.Word || _tokens.IsDead(index))
            {
                continue;
            }

            var raw = _tokens.Raw(index);
            switch (raw.Length)
            {
                case 3:
                    if (raw.Equals("FOR", StringComparison.OrdinalIgnoreCase) && _tokens.IsAnyWord(index + 1, "UPDATE", "SHARE", "NO", "KEY"))
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

                    break;
                case 4:
                    if (raw.Equals("ONLY", StringComparison.OrdinalIgnoreCase) && _tokens.IsAnyWord(index - 1, "FROM", "UPDATE", "JOIN", "TABLE") && _tokens.IsName(index + 1))
                    {
                        _tokens.Remove(index);
                    }
                    else if ((raw.Equals("DATE", StringComparison.OrdinalIgnoreCase) || raw.Equals("TIME", StringComparison.OrdinalIgnoreCase))
                        && _tokens.Kind(index + 1) == PgTokenKind.String && !_tokens.IsName(index - 1))
                    {
                        _tokens.Remove(index);
                    }

                    break;
                case 5:
                    if (raw.Equals("LIMIT", StringComparison.OrdinalIgnoreCase) && _tokens.IsWord(index + 1, "ALL"))
                    {
                        _tokens.Replace(index + 1, "-1");
                    }
                    else if (raw.Equals("ARRAY", StringComparison.OrdinalIgnoreCase) && _tokens.Kind(index + 1) == PgTokenKind.LeftBracket)
                    {
                        var close = _tokens.Match(index + 1);
                        _tokens.Replace(index, "json_array");
                        _tokens.Replace(index + 1, "(");
                        _tokens.Replace(close, ")");
                    }

                    break;
                case 7:
                    if (raw.Equals("COLLATE", StringComparison.OrdinalIgnoreCase) && _tokens.IsName(index + 1))
                    {
                        var name = index + 1;
                        while (_tokens.Kind(name + 1) == PgTokenKind.Period && _tokens.IsName(name + 2))
                        {
                            name += 2;
                        }

                        if (!(_tokens.EqualsIdentifier(name, "binary") || _tokens.EqualsIdentifier(name, "nocase") || _tokens.EqualsIdentifier(name, "rtrim")))
                        {
                            _tokens.RemoveRange(index, name);
                        }

                        index = name;
                    }

                    break;
                case 8 when raw.Equals("INTERVAL", StringComparison.OrdinalIgnoreCase) && _tokens.Kind(index + 1) == PgTokenKind.String && !_tokens.IsName(index - 1):
                case 9 when raw.Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase) && _tokens.Kind(index + 1) == PgTokenKind.String && !_tokens.IsName(index - 1):
                case 11 when raw.Equals("TIMESTAMPTZ", StringComparison.OrdinalIgnoreCase) && _tokens.Kind(index + 1) == PgTokenKind.String && !_tokens.IsName(index - 1):
                    _tokens.Remove(index);
                    break;
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
            var hasLiteral = false;
            string? literalString = null;
            ReadOnlySpan<char> literalSpan = [];
            if (single)
            {
                if (_tokens.Kind(start) == PgTokenKind.Parameter && CastOid(type, isArray) is > 0 and var hint
                    && TryParseParameterNumber(_tokens.Raw(start)[1..], out var paramNum))
                {
                    SetParameterType(paramNum, hint);
                }
                else
                {
                    hasLiteral = TryGetLiteral(start, out literalString, out literalSpan);
                }
            }

            if (!isArray && type == "bytea" && hasLiteral && literalSpan.StartsWith("\\x", StringComparison.Ordinal) && !IsHexBytea(literalSpan[2..]))
            {
                return new PgError("22P02", "invalid input syntax for type bytea");
            }

            ApplyCast(start, index, end, type, isArray, hasLiteral, literalString, literalSpan);
            SetCastStart(end, start);
            index = end;
        }

        return null;
    }

    private void ApplyCast(int start, int operatorIndex, int end, string type, bool isArray, bool hasLiteral, string? literalString, ReadOnlySpan<char> literalSpan)
    {
        var inDefault = _tokens.IsWord(start - 1, "DEFAULT");
        var effectiveType = isArray ? "text" : type;
        switch (effectiveType)
        {
            case "regclass" when hasLiteral:
                _tokens.RemoveRange(operatorIndex, end);
                _usesCatalog = true;
                var regclassLiteral = literalString ?? literalSpan.ToString();
                _tokens.Replace(start, long.TryParse(literalSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var relationOid)
                    ? relationOid.ToString(CultureInfo.InvariantCulture)
                    : $"(SELECT oid FROM ({PgCatalogSql.PgClassRelation}) WHERE relname = {PgRewriteContext.Literal(regclassLiteral.Replace("public.", string.Empty, StringComparison.Ordinal).Trim('"'))})");
                return;
            case "regclass":
                _usesCatalog = true;
                _tokens.Prepend(start, $"(SELECT relname FROM ({PgCatalogSql.PgClassRelation}) AS _tb_rc WHERE _tb_rc.oid = ");
                _tokens.Replace(operatorIndex, ")");
                _tokens.RemoveRange(operatorIndex + 1, end);
                return;
            case "regtype" when hasLiteral:
                _tokens.RemoveRange(operatorIndex, end);
                var regtypeOid = "0";
                foreach (var candidate in PgOid.Types)
                {
                    if (literalSpan.SequenceEqual(candidate.Name) || literalSpan.SequenceEqual(candidate.FormatName))
                    {
                        regtypeOid = candidate.Oid.ToString(CultureInfo.InvariantCulture);
                        break;
                    }
                }

                _tokens.Replace(start, regtypeOid);
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
            case "bool" or "boolean" when hasLiteral && PgValueCodec.TryParseBool(literalSpan, out var flag):
                _tokens.Replace(start, flag ? "TRUE" : "FALSE");
                _tokens.RemoveRange(operatorIndex, end);
                return;
            case "bytea" when hasLiteral && literalSpan.StartsWith("\\x", StringComparison.Ordinal):
                _tokens.Replace(start, string.Concat("X'", literalSpan[2..], "'"));
                _tokens.RemoveRange(operatorIndex, end);
                return;
            case "date" when !hasLiteral:
                WrapCast(start, operatorIndex, end, inDefault ? "(date(" : "date(", inDefault ? "))" : ")");
                return;
            case "time" when !hasLiteral:
                WrapCast(start, operatorIndex, end, inDefault ? "(time(" : "time(", inDefault ? "))" : ")");
                return;
        }

        var (suffix, defaultSuffix, isText) = SqliteCastSuffix(effectiveType);
        if (hasLiteral && isText)
        {
            _tokens.RemoveRange(operatorIndex, end);
            return;
        }

        WrapCast(start, operatorIndex, end, inDefault ? "(CAST(" : "CAST(", inDefault ? defaultSuffix : suffix);
    }

    private void WrapCast(int start, int operatorIndex, int end, string prefix, string suffix)
    {
        _tokens.Prepend(start, prefix);
        _tokens.Replace(operatorIndex, suffix);
        _tokens.RemoveRange(operatorIndex + 1, end);
    }

    private static (string Suffix, string DefaultSuffix, bool IsText) SqliteCastSuffix(string type) => type switch
    {
        "int" or "int2" or "int4" or "int8" or "integer" or "smallint" or "bigint" or "oid" or "xid" or "cid" or "regproc" or "serial" or "bigserial" or "bool" or "boolean" => (" AS INTEGER)", " AS INTEGER))", false),
        "float" or "float4" or "float8" or "real" or "double precision" or "double" => (" AS REAL)", " AS REAL))", false),
        "numeric" or "decimal" or "money" => (" AS NUMERIC)", " AS NUMERIC))", false),
        "bytea" or "blob" => (" AS BLOB)", " AS BLOB))", false),
        _ => (" AS TEXT)", " AS TEXT))", true),
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

    private static bool IsTypeContinuation(ReadOnlySpan<char> raw) => raw.Length switch
    {
        4 => raw.Equals("with", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("time", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("zone", StringComparison.OrdinalIgnoreCase),
        7 => raw.Equals("varying", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("without", StringComparison.OrdinalIgnoreCase),
        9 => raw.Equals("precision", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool TryMatchKnownType(ReadOnlySpan<char> raw, out string type)
    {
        var first = (char)(raw[0] | 0x20);
        string? matched = raw.Length switch
        {
            3 => first switch
            {
                'i' when raw.Equals("int", StringComparison.OrdinalIgnoreCase) => "int",
                'o' when raw.Equals("oid", StringComparison.OrdinalIgnoreCase) => "oid",
                'x' when raw.Equals("xid", StringComparison.OrdinalIgnoreCase) => "xid",
                'c' when raw.Equals("cid", StringComparison.OrdinalIgnoreCase) => "cid",
                _ => null,
            },
            4 => first switch
            {
                't' when raw.Equals("text", StringComparison.OrdinalIgnoreCase) => "text",
                't' when raw.Equals("time", StringComparison.OrdinalIgnoreCase) => "time",
                'u' when raw.Equals("uuid", StringComparison.OrdinalIgnoreCase) => "uuid",
                'i' when raw.Equals("int2", StringComparison.OrdinalIgnoreCase) => "int2",
                'i' when raw.Equals("int4", StringComparison.OrdinalIgnoreCase) => "int4",
                'i' when raw.Equals("int8", StringComparison.OrdinalIgnoreCase) => "int8",
                'b' when raw.Equals("bool", StringComparison.OrdinalIgnoreCase) => "bool",
                'b' when raw.Equals("blob", StringComparison.OrdinalIgnoreCase) => "blob",
                'j' when raw.Equals("json", StringComparison.OrdinalIgnoreCase) => "json",
                'd' when raw.Equals("date", StringComparison.OrdinalIgnoreCase) => "date",
                'r' when raw.Equals("real", StringComparison.OrdinalIgnoreCase) => "real",
                'c' when raw.Equals("char", StringComparison.OrdinalIgnoreCase) => "char",
                'n' when raw.Equals("name", StringComparison.OrdinalIgnoreCase) => "name",
                _ => null,
            },
            5 => first switch
            {
                'j' when raw.Equals("jsonb", StringComparison.OrdinalIgnoreCase) => "jsonb",
                'b' when raw.Equals("bytea", StringComparison.OrdinalIgnoreCase) => "bytea",
                'f' when raw.Equals("float", StringComparison.OrdinalIgnoreCase) => "float",
                'm' when raw.Equals("money", StringComparison.OrdinalIgnoreCase) => "money",
                _ => null,
            },
            6 => first switch
            {
                'b' when raw.Equals("bigint", StringComparison.OrdinalIgnoreCase) => "bigint",
                'b' when raw.Equals("bpchar", StringComparison.OrdinalIgnoreCase) => "bpchar",
                'f' when raw.Equals("float8", StringComparison.OrdinalIgnoreCase) => "float8",
                'f' when raw.Equals("float4", StringComparison.OrdinalIgnoreCase) => "float4",
                'd' when raw.Equals("double", StringComparison.OrdinalIgnoreCase) => "double",
                's' when raw.Equals("serial", StringComparison.OrdinalIgnoreCase) => "serial",
                'c' when raw.Equals("citext", StringComparison.OrdinalIgnoreCase) => "citext",
                _ => null,
            },
            7 => first switch
            {
                'n' when raw.Equals("numeric", StringComparison.OrdinalIgnoreCase) => "numeric",
                'b' when raw.Equals("boolean", StringComparison.OrdinalIgnoreCase) => "boolean",
                'i' when raw.Equals("integer", StringComparison.OrdinalIgnoreCase) => "integer",
                'v' when raw.Equals("varchar", StringComparison.OrdinalIgnoreCase) => "varchar",
                'd' when raw.Equals("decimal", StringComparison.OrdinalIgnoreCase) => "decimal",
                'r' when raw.Equals("regtype", StringComparison.OrdinalIgnoreCase) => "regtype",
                'r' when raw.Equals("regproc", StringComparison.OrdinalIgnoreCase) => "regproc",
                _ => null,
            },
            8 => first switch
            {
                's' when raw.Equals("smallint", StringComparison.OrdinalIgnoreCase) => "smallint",
                'r' when raw.Equals("regclass", StringComparison.OrdinalIgnoreCase) => "regclass",
                _ => null,
            },
            9 => first switch
            {
                't' when raw.Equals("timestamp", StringComparison.OrdinalIgnoreCase) => "timestamp",
                'c' when raw.Equals("character", StringComparison.OrdinalIgnoreCase) => "character",
                'b' when raw.Equals("bigserial", StringComparison.OrdinalIgnoreCase) => "bigserial",
                _ => null,
            },
            11 => first == 't' && raw.Equals("timestamptz", StringComparison.OrdinalIgnoreCase) ? "timestamptz" : null,
            12 => first == 'r' && raw.Equals("regnamespace", StringComparison.OrdinalIgnoreCase) ? "regnamespace" : null,
            _ => null,
        };
        if (matched is not null)
        {
            type = matched;
            return true;
        }

        type = string.Empty;
        return false;
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

        end = index;
        var hasContinuation = false;
        while (true)
        {
            var nextKind = _tokens.Kind(end + 1);
            if (nextKind == PgTokenKind.LeftParen && _tokens.Match(end + 1) > end)
            {
                end = _tokens.Match(end + 1);
                continue;
            }

            if (nextKind == PgTokenKind.Word && IsTypeContinuation(_tokens.Raw(end + 1))
                && !(_tokens.IsWord(end + 1, "with") && !_tokens.IsWord(end + 2, "time")))
            {
                hasContinuation = true;
                end++;
                continue;
            }

            break;
        }

        if (!hasContinuation && _tokens.Kind(index) == PgTokenKind.Word && TryMatchKnownType(_tokens.Raw(index), out var known))
        {
            type = known;
        }
        else if (!hasContinuation)
        {
            type = _tokens.Identifier(index);
        }
        else
        {
            var builder = new StringBuilder(_tokens.Identifier(index));
            for (var cursor = index + 1; cursor <= end; cursor++)
            {
                if (_tokens.Kind(cursor) == PgTokenKind.LeftParen && _tokens.Match(cursor) > cursor)
                {
                    cursor = _tokens.Match(cursor);
                    continue;
                }

                builder.Append(' ').Append(_tokens.Identifier(cursor));
            }

            type = builder.ToString().ToLowerInvariant();
        }

        while (_tokens.Kind(end + 1) == PgTokenKind.LeftBracket && _tokens.Match(end + 1) > end)
        {
            end = _tokens.Match(end + 1);
            isArray = true;
        }

        return true;
    }

    private int OperandStart(int index)
    {
        if (index < 0)
        {
            return -1;
        }

        if (TryGetCastStart(index, out var castStart))
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

                if (open > 0 && _tokens.Kind(open - 1) == PgTokenKind.Word && !IsReserved(_tokens.Raw(open - 1)))
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
            if (_tokens.Kind(index) != PgTokenKind.Word || _tokens.Kind(index + 1) != PgTokenKind.LeftParen || _tokens.IsDead(index))
            {
                continue;
            }

            var raw = _tokens.Raw(index);
            var negated = raw.Equals("ALL", StringComparison.OrdinalIgnoreCase);
            if (!negated && !raw.Equals("ANY", StringComparison.OrdinalIgnoreCase) && !raw.Equals("SOME", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

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

            var kind = _tokens.Kind(index);
            var operatorStart = index;
            var operatorEnd = index;
            ReadOnlySpan<char> symbol = [];
            var isIlike = false;
            if (kind == PgTokenKind.Word)
            {
                var raw = _tokens.Raw(index);
                if (raw.Length == 5 && raw.Equals("ILIKE", StringComparison.OrdinalIgnoreCase))
                {
                    symbol = "~~*";
                    isIlike = true;
                }
                else if (raw.Length == 8 && raw.Equals("OPERATOR", StringComparison.OrdinalIgnoreCase) && _tokens.Kind(index + 1) == PgTokenKind.LeftParen)
                {
                    var close = _tokens.Match(index + 1);
                    if (close > 0 && _tokens.Kind(close - 1) == PgTokenKind.Operator)
                    {
                        symbol = _tokens.Raw(close - 1);
                        operatorEnd = close;
                    }
                }
            }
            else if (kind == PgTokenKind.Operator)
            {
                var raw = _tokens.Raw(index);
                if (raw is "~" or "!~" or "~*" or "!~*" or "~~" or "!~~" or "~~*" or "!~~*")
                {
                    symbol = raw;
                }
            }

            if (symbol.IsEmpty)
            {
                continue;
            }

            var negated = symbol[0] == '!';
            if (isIlike && _tokens.IsWord(index - 1, "NOT"))
            {
                negated = true;
                operatorStart = index - 1;
            }

            var core = negated && symbol[0] == '!' ? symbol[1..] : symbol;
            var lhsEnd = operatorStart - 1;
            var lhsStart = OperandStart(lhsEnd);
            var rhsStart = operatorEnd + 1;
            if (lhsStart < 0 || rhsStart >= _tokens.Count)
            {
                return PgError.Syntax($"syntax error at or near \"{symbol.ToString()}\"");
            }

            var rhsEnd = OperandEnd(rhsStart);
            _tokens.RemoveRange(operatorStart, operatorEnd);
            if (core.SequenceEqual("~~"))
            {
                _tokens.Replace(operatorEnd, negated ? "NOT LIKE" : "LIKE");
            }
            else if (core.SequenceEqual("~~*"))
            {
                var escapeEnd = _tokens.IsWord(rhsEnd + 1, "ESCAPE") ? rhsEnd + 2 : rhsEnd;
                _tokens.Prepend(lhsStart, negated ? "NOT (LOWER(" : "LOWER(");
                _tokens.Append(lhsEnd, ")");
                _tokens.Replace(operatorEnd, "LIKE LOWER(");
                _tokens.Append(rhsEnd, ")");
                if (negated)
                {
                    _tokens.Append(escapeEnd, ")");
                }
            }
            else
            {
                var caseInsensitive = core.SequenceEqual("~*");
                if (rhsStart != rhsEnd || !TryGetLiteral(rhsStart, out _, out var patternSpan)
                    || RegexToPattern(patternSpan, caseInsensitive) is not { } translated)
                {
                    return PgError.Unsupported("regular expression matching beyond anchored literal and wildcard patterns");
                }

                _tokens.Replace(operatorEnd, caseInsensitive ? (negated ? "NOT LIKE" : "LIKE") : (negated ? "NOT GLOB" : "GLOB"));
                _tokens.Replace(rhsStart, caseInsensitive ? string.Concat(PgRewriteContext.Literal(translated), " ESCAPE '\\'") : PgRewriteContext.Literal(translated));
            }

            index = rhsEnd;
        }

        return null;
    }

    private static string? RegexToPattern(ReadOnlySpan<char> body, bool like)
    {
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

        var builder = new StringBuilder(body.Length + 4);
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

            if (like)
            {
                if (current is '%' or '_' or '\\')
                {
                    builder.Append('\\');
                }

                builder.Append(current);
            }
            else if (current is '*' or '?' or '[')
            {
                builder.Append('[').Append(current).Append(']');
            }
            else
            {
                builder.Append(current);
            }
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

    private IReadOnlyList<PgParameterColumn> CollectParameterColumns()
    {
        Span<(int Param, int NameToken)> stackPairs = stackalloc (int, int)[16];
        var pairCount = 0;
        List<PgParameterColumn>? overflow = null;

        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.Kind(index) != PgTokenKind.Parameter || !TryParseParameterNumber(_tokens.Raw(index)[1..], out var number))
            {
                continue;
            }

            var nameToken = -1;
            if (IsComparison(index - 1) && _tokens.IsName(index - 2))
            {
                nameToken = index - 2;
            }
            else if (IsComparison(index + 1) && _tokens.IsName(index + 2) && _tokens.Kind(index + 3) != PgTokenKind.LeftParen)
            {
                nameToken = index + 2;
            }

            if (nameToken >= 0)
            {
                AddParamColumn(_tokens, number, nameToken, stackPairs, ref pairCount, ref overflow);
            }
        }

        if (_tokens.HasFlag(PgSyntaxFlags.IntoWord))
        {
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
                    break;
                }

                var close = _tokens.Match(open);
                Span<int> stackCols = stackalloc int[32];
                var colCount = 0;
                List<int>? overflowCols = null;
                for (var column = open + 1; column < close; column++)
                {
                    if (_tokens.IsName(column))
                    {
                        if (overflowCols is not null)
                        {
                            overflowCols.Add(column);
                        }
                        else if (colCount < stackCols.Length)
                        {
                            stackCols[colCount++] = column;
                        }
                        else
                        {
                            overflowCols = new List<int>(64);
                            for (var c = 0; c < colCount; c++)
                            {
                                overflowCols.Add(stackCols[c]);
                            }

                            overflowCols.Add(column);
                        }
                    }
                }

                var totalCols = overflowCols?.Count ?? colCount;
                if (!_tokens.IsWord(close + 1, "VALUES"))
                {
                    break;
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
                            case PgTokenKind.Parameter when position < totalCols
                                && TryParseParameterNumber(_tokens.Raw(item)[1..], out var parameter):
                                var colToken = overflowCols is not null ? overflowCols[position] : stackCols[position];
                                AddParamColumn(_tokens, parameter, colToken, stackPairs, ref pairCount, ref overflow);
                                break;
                        }
                    }

                    if (_tokens.Kind(tupleEnd + 1) != PgTokenKind.Comma)
                    {
                        break;
                    }
                }

                break;
            }
        }

        if (overflow is not null)
        {
            return overflow;
        }

        if (pairCount == 0)
        {
            return [];
        }

        var result = new PgParameterColumn[pairCount];
        for (var index = 0; index < pairCount; index++)
        {
            var (param, nameToken) = stackPairs[index];
            result[index] = new PgParameterColumn(param, _tokens.Identifier(nameToken));
        }

        return result;
    }

    private static void AddParamColumn(PgTokenEditor tokens, int parameter, int nameToken, Span<(int Param, int NameToken)> stackPairs, ref int pairCount, ref List<PgParameterColumn>? overflow)
    {
        if (overflow is not null)
        {
            overflow.Add(new PgParameterColumn(parameter, tokens.Identifier(nameToken)));
            return;
        }

        if (pairCount < stackPairs.Length)
        {
            stackPairs[pairCount++] = (parameter, nameToken);
            return;
        }

        overflow = new List<PgParameterColumn>(32);
        for (var index = 0; index < pairCount; index++)
        {
            overflow.Add(new PgParameterColumn(stackPairs[index].Param, tokens.Identifier(stackPairs[index].NameToken)));
        }

        overflow.Add(new PgParameterColumn(parameter, tokens.Identifier(nameToken)));
    }

    private bool IsComparison(int index) =>
        (_tokens.Kind(index) == PgTokenKind.Operator && _tokens.Raw(index) is "=" or "<>" or "!=" or "<" or ">" or "<=" or ">=")
        || _tokens.IsAnyWord(index, "LIKE", "ILIKE");

    private static bool IsHexBytea(ReadOnlySpan<char> hex) => hex.Length % 2 == 0 && !hex.ContainsAnyExcept(HexCharacters);

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
                    var octal = escape - '0';
                    var octalLength = 1;
                    while (octalLength < 3 && index + octalLength < body.Length && body[index + octalLength] is >= '0' and <= '7')
                    {
                        octal = (octal * 8) + (body[index + octalLength] - '0');
                        octalLength++;
                    }

                    builder.Append((char)octal);
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
                        || codePoint is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
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
