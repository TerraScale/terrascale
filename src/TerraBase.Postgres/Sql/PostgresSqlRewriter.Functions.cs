using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal ref partial struct PostgresSqlRewriter
{
    public const string NowExpression = NowSql;

    public const string UuidExpression = UuidSql;

    private const string NowSql = "strftime('%Y-%m-%d %H:%M:%f+00:00', 'now')";
    private const string RandomSql = "((random() & 9007199254740991) / 9007199254740992.0)";
    private const string UuidSql = "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89ab', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";

    private enum PgFuncKind : byte
    {
        Now,
        CurrentSchema,
        CurrentSchemas,
        CurrentDatabase,
        Version,
        CurrentUser,
        PgGetUserById,
        PgBackendPid,
        AlwaysFalse,
        AlwaysTrue,
        AlwaysNull,
        PgEncodingToChar,
        AlwaysZero,
        GenRandomUuid,
        Random,
        CurrentSetting,
        SetConfig,
        KeepFirstParen,
        PgSizePretty,
        PgGetIndexDef,
        PgGetConstraintDef,
        PgGetViewDef,
        PgPartitionAncestors,
        FormatType,
        StringAgg,
        Greatest,
        Least,
        Strpos,
        CharLength,
        JsonBuildObject,
        JsonBuildArray,
        JsonAgg,
        JsonObjectAgg,
        JsonbArrayLength,
        JsonTypeof,
        ToJson,
        Left,
        Deferred,
        Extract,
        DatePart,
    }

    private static readonly FrozenDictionary<string, PgFuncKind> FunctionKinds = new Dictionary<string, PgFuncKind>(StringComparer.OrdinalIgnoreCase)
    {
        ["now"] = PgFuncKind.Now,
        ["clock_timestamp"] = PgFuncKind.Now,
        ["statement_timestamp"] = PgFuncKind.Now,
        ["transaction_timestamp"] = PgFuncKind.Now,
        ["current_schema"] = PgFuncKind.CurrentSchema,
        ["current_schemas"] = PgFuncKind.CurrentSchemas,
        ["current_database"] = PgFuncKind.CurrentDatabase,
        ["current_catalog"] = PgFuncKind.CurrentDatabase,
        ["version"] = PgFuncKind.Version,
        ["current_user"] = PgFuncKind.CurrentUser,
        ["session_user"] = PgFuncKind.CurrentUser,
        ["pg_get_userbyid"] = PgFuncKind.PgGetUserById,
        ["pg_backend_pid"] = PgFuncKind.PgBackendPid,
        ["pg_is_in_recovery"] = PgFuncKind.AlwaysFalse,
        ["pg_relation_is_publishable"] = PgFuncKind.AlwaysFalse,
        ["pg_table_is_visible"] = PgFuncKind.AlwaysTrue,
        ["pg_type_is_visible"] = PgFuncKind.AlwaysTrue,
        ["pg_function_is_visible"] = PgFuncKind.AlwaysTrue,
        ["has_table_privilege"] = PgFuncKind.AlwaysTrue,
        ["has_schema_privilege"] = PgFuncKind.AlwaysTrue,
        ["has_database_privilege"] = PgFuncKind.AlwaysTrue,
        ["has_column_privilege"] = PgFuncKind.AlwaysTrue,
        ["has_any_column_privilege"] = PgFuncKind.AlwaysTrue,
        ["has_sequence_privilege"] = PgFuncKind.AlwaysTrue,
        ["has_function_privilege"] = PgFuncKind.AlwaysTrue,
        ["pg_advisory_lock"] = PgFuncKind.AlwaysTrue,
        ["pg_advisory_unlock"] = PgFuncKind.AlwaysTrue,
        ["pg_advisory_xact_lock"] = PgFuncKind.AlwaysTrue,
        ["pg_try_advisory_lock"] = PgFuncKind.AlwaysTrue,
        ["pg_try_advisory_xact_lock"] = PgFuncKind.AlwaysTrue,
        ["pg_advisory_unlock_all"] = PgFuncKind.AlwaysTrue,
        ["obj_description"] = PgFuncKind.AlwaysNull,
        ["col_description"] = PgFuncKind.AlwaysNull,
        ["shobj_description"] = PgFuncKind.AlwaysNull,
        ["pg_get_serial_sequence"] = PgFuncKind.AlwaysNull,
        ["inet_server_addr"] = PgFuncKind.AlwaysNull,
        ["inet_client_addr"] = PgFuncKind.AlwaysNull,
        ["pg_postmaster_start_time"] = PgFuncKind.AlwaysNull,
        ["pg_conf_load_time"] = PgFuncKind.AlwaysNull,
        ["pg_tablespace_location"] = PgFuncKind.AlwaysNull,
        ["pg_encoding_to_char"] = PgFuncKind.PgEncodingToChar,
        ["pg_relation_size"] = PgFuncKind.AlwaysZero,
        ["pg_total_relation_size"] = PgFuncKind.AlwaysZero,
        ["pg_table_size"] = PgFuncKind.AlwaysZero,
        ["pg_indexes_size"] = PgFuncKind.AlwaysZero,
        ["pg_database_size"] = PgFuncKind.AlwaysZero,
        ["gen_random_uuid"] = PgFuncKind.GenRandomUuid,
        ["uuid_generate_v4"] = PgFuncKind.GenRandomUuid,
        ["random"] = PgFuncKind.Random,
        ["current_setting"] = PgFuncKind.CurrentSetting,
        ["set_config"] = PgFuncKind.SetConfig,
        ["pg_get_expr"] = PgFuncKind.KeepFirstParen,
        ["quote_ident"] = PgFuncKind.KeepFirstParen,
        ["pg_size_pretty"] = PgFuncKind.PgSizePretty,
        ["pg_get_indexdef"] = PgFuncKind.PgGetIndexDef,
        ["pg_get_constraintdef"] = PgFuncKind.PgGetConstraintDef,
        ["pg_get_viewdef"] = PgFuncKind.PgGetViewDef,
        ["pg_partition_ancestors"] = PgFuncKind.PgPartitionAncestors,
        ["format_type"] = PgFuncKind.FormatType,
        ["string_agg"] = PgFuncKind.StringAgg,
        ["greatest"] = PgFuncKind.Greatest,
        ["least"] = PgFuncKind.Least,
        ["strpos"] = PgFuncKind.Strpos,
        ["char_length"] = PgFuncKind.CharLength,
        ["character_length"] = PgFuncKind.CharLength,
        ["json_build_object"] = PgFuncKind.JsonBuildObject,
        ["jsonb_build_object"] = PgFuncKind.JsonBuildObject,
        ["json_build_array"] = PgFuncKind.JsonBuildArray,
        ["jsonb_build_array"] = PgFuncKind.JsonBuildArray,
        ["json_agg"] = PgFuncKind.JsonAgg,
        ["jsonb_agg"] = PgFuncKind.JsonAgg,
        ["array_agg"] = PgFuncKind.JsonAgg,
        ["json_object_agg"] = PgFuncKind.JsonObjectAgg,
        ["jsonb_object_agg"] = PgFuncKind.JsonObjectAgg,
        ["jsonb_array_length"] = PgFuncKind.JsonbArrayLength,
        ["json_typeof"] = PgFuncKind.JsonTypeof,
        ["jsonb_typeof"] = PgFuncKind.JsonTypeof,
        ["to_json"] = PgFuncKind.ToJson,
        ["to_jsonb"] = PgFuncKind.ToJson,
        ["left"] = PgFuncKind.Left,
        ["right"] = PgFuncKind.Deferred,
        ["split_part"] = PgFuncKind.Deferred,
        ["extract"] = PgFuncKind.Extract,
        ["date_part"] = PgFuncKind.DatePart,
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> NonFunctionPredecessors = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        ["TABLE", "INTO", "REFERENCES", "ON", "FROM", "JOIN", "UPDATE", "EXISTS", "INDEX", "VIEW", "TRIGGER"]);

    private PgError? RewriteFunctions()
    {
        var funcLookup = FunctionKinds.GetAlternateLookup<ReadOnlySpan<char>>();
        var predecessorLookup = NonFunctionPredecessors.GetAlternateLookup<ReadOnlySpan<char>>();
        var hasSpecial = _tokens.HasFlag(PgSyntaxFlags.SpecialKeyword);
        var hasCall = _tokens.HasFlag(PgSyntaxFlags.FunctionCall);
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.Kind(index) != PgTokenKind.Word)
            {
                continue;
            }

            var isCall = _tokens.Kind(index + 1) == PgTokenKind.LeftParen;
            if (isCall)
            {
                if (!hasCall)
                {
                    continue;
                }

                var raw = _tokens.Raw(index);
                if (!funcLookup.TryGetValue(raw, out var funcKind))
                {
                    continue;
                }

                if (IsSkippedFunctionTarget(index, predecessorLookup))
                {
                    continue;
                }

                if (RewriteCall(index, funcKind, index + 1, _tokens.Match(index + 1)) is { } error)
                {
                    return error;
                }
            }
            else if (hasSpecial)
            {
                var raw = _tokens.Raw(index);
                if (raw.Length is < 12 or > 15 || (raw[0] | 0x20) is not ('c' or 's' or 'l'))
                {
                    continue;
                }

                if (IsSkippedFunctionTarget(index, predecessorLookup))
                {
                    continue;
                }

                RewriteKeyword(index, raw);
            }
        }

        return null;
    }

    private bool IsSkippedFunctionTarget(int index, FrozenSet<string>.AlternateLookup<ReadOnlySpan<char>> predecessorLookup) =>
        _tokens.IsDead(index)
        || _tokens.IsEdited(index)
        || (_tokens.Kind(index - 1) == PgTokenKind.Period && !_tokens.IsEdited(index - 1))
        || (_tokens.Kind(index - 1) == PgTokenKind.Word && predecessorLookup.Contains(_tokens.Raw(index - 1)));

    private void RewriteKeyword(int index, ReadOnlySpan<char> raw)
    {
        if (_tokens.IsWord(index - 1, "AS") || _tokens.Kind(index + 1) == PgTokenKind.Period)
        {
            return;
        }

        string? replacement;
        if (raw.Equals("current_schema", StringComparison.OrdinalIgnoreCase))
        {
            replacement = "'public'";
        }
        else if (raw.Equals("current_user", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("session_user", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("current_role", StringComparison.OrdinalIgnoreCase))
        {
            replacement = PgRewriteContext.Literal(_context.UserName);
        }
        else if (raw.Equals("current_catalog", StringComparison.OrdinalIgnoreCase))
        {
            replacement = PgRewriteContext.Literal(_context.DatabaseName);
        }
        else if (raw.Equals("localtimestamp", StringComparison.OrdinalIgnoreCase))
        {
            replacement = NowSql;
        }
        else
        {
            return;
        }

        _tokens.Replace(index, _tokens.IsWord(index - 1, "DEFAULT") ? string.Concat("(", replacement, ")") : replacement);
    }

    private PgError? RewriteCall(int index, PgFuncKind kind, int open, int close)
    {
        switch (kind)
        {
            case PgFuncKind.Now:
                ReplaceCall(index, open, close, NowSql);
                break;
            case PgFuncKind.CurrentSchema:
                ReplaceCall(index, open, close, "'public'");
                break;
            case PgFuncKind.CurrentSchemas:
                ReplaceCall(index, open, close, "'{public}'");
                break;
            case PgFuncKind.CurrentDatabase:
                ReplaceCall(index, open, close, PgRewriteContext.Literal(_context.DatabaseName));
                break;
            case PgFuncKind.Version:
                ReplaceCall(index, open, close, PgRewriteContext.Literal(PgRewriteContext.Version));
                break;
            case PgFuncKind.CurrentUser:
                ReplaceCall(index, open, close, PgRewriteContext.Literal(_context.UserName));
                break;
            case PgFuncKind.PgGetUserById:
                ReplaceCall(index, open, close, PgRewriteContext.Literal(_context.UserName));
                break;
            case PgFuncKind.PgBackendPid:
                ReplaceCall(index, open, close, _context.ProcessId.ToString(CultureInfo.InvariantCulture));
                break;
            case PgFuncKind.AlwaysFalse:
                ReplaceCall(index, open, close, "FALSE");
                break;
            case PgFuncKind.AlwaysTrue:
                ReplaceCall(index, open, close, "TRUE");
                break;
            case PgFuncKind.AlwaysNull:
                ReplaceCall(index, open, close, "NULL");
                break;
            case PgFuncKind.PgEncodingToChar:
                ReplaceCall(index, open, close, "'UTF8'");
                break;
            case PgFuncKind.AlwaysZero:
                ReplaceCall(index, open, close, "0");
                break;
            case PgFuncKind.GenRandomUuid:
                ReplaceCall(index, open, close, UuidSql);
                break;
            case PgFuncKind.Random when close == open + 1:
                ReplaceCall(index, open, close, RandomSql);
                break;
            case PgFuncKind.Random:
                break;
            case PgFuncKind.CurrentSetting:
                _dependsOnSettings = true;
                var setting = FirstLiteral(open, close) is { } settingName && _context.Settings.TryGetValue(settingName, out var settingValue)
                    ? PgRewriteContext.Literal(settingValue)
                    : "NULL";
                ReplaceCall(index, open, close, setting);
                break;
            case PgFuncKind.SetConfig:
                var arguments = Arguments(open, close);
                ReplaceCall(index, open, close, arguments.Count == 3 && TryGetLiteralString(arguments[1].Start, out var configured)
                    ? PgRewriteContext.Literal(configured)
                    : "NULL");
                break;
            case PgFuncKind.KeepFirstParen:
                KeepFirstArgument(index, open, close, "(", ")");
                break;
            case PgFuncKind.PgSizePretty:
                KeepFirstArgument(index, open, close, "(CAST(", " AS TEXT) || ' bytes')");
                break;
            case PgFuncKind.PgGetIndexDef:
                _usesCatalog = true;
                KeepFirstArgument(index, open, close, $"(SELECT _tb_indexdef FROM ({PgCatalogSql.PgIndexRelation}) WHERE indexrelid = (", "))");
                break;
            case PgFuncKind.PgGetConstraintDef:
                _usesCatalog = true;
                KeepFirstArgument(index, open, close, $"(SELECT _tb_def FROM ({PgCatalogSql.PgConstraintRelation}) WHERE oid = (", "))");
                break;
            case PgFuncKind.PgGetViewDef:
                KeepFirstArgument(index, open, close, $"(SELECT sql FROM sqlite_schema WHERE {PgCatalogSql.FirstObjectOid} + rowid = (", "))");
                break;
            case PgFuncKind.PgPartitionAncestors:
                KeepFirstArgument(index, open, close, "(SELECT CAST(", " AS INTEGER) AS relid)");
                break;
            case PgFuncKind.FormatType:
                RewriteFormatType(index, open, close);
                break;
            case PgFuncKind.StringAgg:
                _tokens.Replace(index, "group_concat");
                break;
            case PgFuncKind.Greatest:
                _tokens.Replace(index, "max");
                break;
            case PgFuncKind.Least:
                _tokens.Replace(index, "min");
                break;
            case PgFuncKind.Strpos:
                _tokens.Replace(index, "instr");
                break;
            case PgFuncKind.CharLength:
                _tokens.Replace(index, "length");
                break;
            case PgFuncKind.JsonBuildObject:
                _tokens.Replace(index, "json_object");
                break;
            case PgFuncKind.JsonBuildArray:
                _tokens.Replace(index, "json_array");
                break;
            case PgFuncKind.JsonAgg:
                _tokens.Replace(index, "json_group_array");
                break;
            case PgFuncKind.JsonObjectAgg:
                _tokens.Replace(index, "json_group_object");
                break;
            case PgFuncKind.JsonbArrayLength:
                _tokens.Replace(index, "json_array_length");
                break;
            case PgFuncKind.JsonTypeof:
                _tokens.Replace(index, "json_type");
                break;
            case PgFuncKind.ToJson:
                _tokens.Replace(index, "json_quote");
                break;
            case PgFuncKind.Left:
                if (Arguments(open, close) is [_, var count])
                {
                    _tokens.Replace(index, "substr");
                    _tokens.Replace(count.Start - 1, ", 1, ");
                }

                break;
            case PgFuncKind.Deferred:
                (_deferred ??= []).Add(index);
                break;
            case PgFuncKind.Extract:
                return RewriteExtract(index, open, close, datePart: false);
            case PgFuncKind.DatePart:
                return RewriteExtract(index, open, close, datePart: true);
            default:
                throw new UnreachableException();
        }

        return null;
    }

    private void ReplaceCall(int index, int open, int close, string replacement)
    {
        _tokens.Replace(index, replacement);
        _tokens.RemoveRange(open, close);
        WrapDefault(index, close);
    }

    private void KeepFirstArgument(int index, int open, int close, string prefix, string suffix)
    {
        var arguments = Arguments(open, close);
        _tokens.Replace(index, prefix);
        _tokens.Remove(open);
        if (arguments.Count > 0)
        {
            _tokens.RemoveRange(arguments[0].End + 1, close - 1);
        }

        _tokens.Replace(close, suffix);
        WrapDefault(index, close);
    }

    private void WrapDefault(int index, int close)
    {
        if (_tokens.IsWord(index - 1, "DEFAULT"))
        {
            _tokens.Prepend(index, "(");
            _tokens.Append(close, ")");
        }
    }

    private void RewriteFormatType(int index, int open, int close)
    {
        var arguments = Arguments(open, close);
        if (arguments.Count > 0 && _tokens.IsWord(arguments[0].End, "atttypid"))
        {
            _tokens.Replace(arguments[0].End, "_tb_format");
            KeepFirstArgument(index, open, close, "(", ")");
            return;
        }

        var formatted = PgCatalogSql.FormatTypeCase("\u0002");
        var split = formatted.IndexOf('\u0002', StringComparison.Ordinal);
        KeepFirstArgument(index, open, close, formatted[..split], formatted[(split + 1)..]);
    }

    private PgError? RewriteExtract(int index, int open, int close, bool datePart)
    {
        var arguments = Arguments(open, close);
        var fieldIndex = open + 1;
        var separator = datePart ? (arguments.Count == 2 ? arguments[1].Start - 1 : -1) : fieldIndex + 1;
        if (separator < 0 || (!datePart && !_tokens.IsWord(separator, "FROM")))
        {
            return PgError.Syntax("syntax error in EXTRACT");
        }

        var field = TryGetLiteralString(fieldIndex, out var literalField) ? literalField : _tokens.Text(fieldIndex);
        (string Prefix, string Suffix)? shape = field.ToLowerInvariant() switch
        {
            "epoch" => ("((julianday(", ") - 2440587.5) * 86400.0)"),
            "year" => ("CAST(strftime('%Y', ", ") AS INTEGER)"),
            "month" => ("CAST(strftime('%m', ", ") AS INTEGER)"),
            "day" => ("CAST(strftime('%d', ", ") AS INTEGER)"),
            "hour" => ("CAST(strftime('%H', ", ") AS INTEGER)"),
            "minute" => ("CAST(strftime('%M', ", ") AS INTEGER)"),
            "second" => ("CAST(strftime('%f', ", ") AS REAL)"),
            "dow" => ("CAST(strftime('%w', ", ") AS INTEGER)"),
            "doy" => ("CAST(strftime('%j', ", ") AS INTEGER)"),
            "quarter" => ("((CAST(strftime('%m', ", ") AS INTEGER) + 2) / 3)"),
            _ => null,
        };
        if (shape is not { } parts)
        {
            return PgError.Unsupported($"EXTRACT field \"{field}\"");
        }

        _tokens.Replace(index, parts.Prefix);
        _tokens.Remove(open);
        _tokens.RemoveRange(fieldIndex, separator);
        _tokens.Replace(close, parts.Suffix);
        return null;
    }

    private void RunDeferred()
    {
        if (_deferred is null)
        {
            return;
        }

        _deferred.Sort();
        for (var position = _deferred.Count - 1; position >= 0; position--)
        {
            var index = _deferred[position];
            var open = index + 1;
            var close = _tokens.Match(open);
            var arguments = Arguments(open, close);
            if (_tokens.IsWord(index, "right") && arguments is [_, var count])
            {
                var rendered = _tokens.Render(count.Start, count.End);
                _tokens.Replace(index, "substr");
                _tokens.Replace(count.Start - 1, ", -(");
                _tokens.Replace(close, "), " + rendered + ")");
            }
            else if (_tokens.IsWord(index, "split_part") && arguments is [var text, var delimiter, var partPosition])
            {
                var sql = "(WITH RECURSIVE _tb_sp(n, d, rest, part, i) AS (SELECT q.n, q.d, q.s || q.d, NULL, 0 FROM (SELECT "
                    + _tokens.Render(partPosition.Start, partPosition.End) + " AS n, "
                    + _tokens.Render(delimiter.Start, delimiter.End) + " AS d, "
                    + _tokens.Render(text.Start, text.End) + " AS s) AS q UNION ALL SELECT n, d, substr(rest, instr(rest, d) + length(d)), "
                    + "substr(rest, 1, instr(rest, d) - 1), i + 1 FROM _tb_sp WHERE i < n AND instr(rest, d) > 0) "
                    + "SELECT coalesce((SELECT part FROM _tb_sp WHERE i = n), ''))";
                _tokens.Replace(index, sql);
                _tokens.RemoveRange(open, close);
            }
        }
    }

    private string? FirstLiteral(int open, int close) =>
        Arguments(open, close) is [var first, ..] && first.Start == first.End && TryGetLiteralString(first.Start, out var literal) ? literal : null;

    private List<(int Start, int End)> Arguments(int open, int close)
    {
        List<(int Start, int End)> arguments = [];
        if (close <= open + 1)
        {
            return arguments;
        }

        var start = open + 1;
        for (var index = open + 1; index < close; index++)
        {
            if (_tokens.Kind(index) is PgTokenKind.LeftParen or PgTokenKind.LeftBracket)
            {
                index = _tokens.Match(index);
                continue;
            }

            if (_tokens.Kind(index) == PgTokenKind.Comma)
            {
                arguments.Add((start, index - 1));
                start = index + 1;
            }
        }

        arguments.Add((start, close - 1));
        return arguments;
    }
}
