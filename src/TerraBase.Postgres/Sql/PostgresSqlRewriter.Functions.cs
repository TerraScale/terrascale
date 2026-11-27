using System.Globalization;
using TerraBase.Postgres.Protocol;

namespace TerraBase.Postgres.Sql;

internal sealed partial class PostgresSqlRewriter
{
    public const string NowExpression = NowSql;

    public const string UuidExpression = UuidSql;

    private const string NowSql = "strftime('%Y-%m-%d %H:%M:%f+00:00', 'now')";
    private const string RandomSql = "((random() & 9007199254740991) / 9007199254740992.0)";
    private const string UuidSql = "lower(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-' || substr('89ab', 1 + (abs(random()) % 4), 1) || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6)))";

    private PgError? RewriteFunctions()
    {
        for (var index = 0; index < _tokens.Count; index++)
        {
            if (_tokens.IsDead(index) || _tokens.Kind(index) != PgTokenKind.Word || _tokens.IsEdited(index)
                || (_tokens.Kind(index - 1) == PgTokenKind.Period && !_tokens.IsEdited(index - 1))
                || _tokens.IsAnyWord(index - 1, "TABLE", "INTO", "REFERENCES", "ON", "FROM", "JOIN", "UPDATE", "EXISTS", "INDEX", "VIEW", "TRIGGER"))
            {
                continue;
            }

            var name = _tokens.Text(index).ToLowerInvariant();
            if (_tokens.Kind(index + 1) != PgTokenKind.LeftParen)
            {
                RewriteKeyword(index, name);
                continue;
            }

            if (RewriteCall(index, name, index + 1, _tokens.Match(index + 1)) is { } error)
            {
                return error;
            }
        }

        return null;
    }

    private void RewriteKeyword(int index, string name)
    {
        if (_tokens.IsWord(index - 1, "AS") || _tokens.Kind(index + 1) == PgTokenKind.Period)
        {
            return;
        }

        var replacement = name switch
        {
            "current_schema" => "'public'",
            "current_user" or "session_user" or "current_role" => PgRewriteContext.Literal(_context.UserName),
            "current_catalog" => PgRewriteContext.Literal(_context.DatabaseName),
            "localtimestamp" => NowSql,
            _ => null,
        };
        if (replacement is not null)
        {
            _tokens.Replace(index, _tokens.IsWord(index - 1, "DEFAULT") ? "(" + replacement + ")" : replacement);
        }
    }

    private PgError? RewriteCall(int index, string name, int open, int close)
    {
        switch (name)
        {
            case "now" or "clock_timestamp" or "statement_timestamp" or "transaction_timestamp":
                ReplaceCall(index, open, close, NowSql);
                break;
            case "current_schema":
                ReplaceCall(index, open, close, "'public'");
                break;
            case "current_schemas":
                ReplaceCall(index, open, close, "'{public}'");
                break;
            case "current_database" or "current_catalog":
                ReplaceCall(index, open, close, PgRewriteContext.Literal(_context.DatabaseName));
                break;
            case "version":
                ReplaceCall(index, open, close, PgRewriteContext.Literal(PgRewriteContext.Version));
                break;
            case "current_user" or "session_user":
                ReplaceCall(index, open, close, PgRewriteContext.Literal(_context.UserName));
                break;
            case "pg_get_userbyid":
                ReplaceCall(index, open, close, PgRewriteContext.Literal(_context.UserName));
                break;
            case "pg_backend_pid":
                ReplaceCall(index, open, close, _context.ProcessId.ToString(CultureInfo.InvariantCulture));
                break;
            case "pg_is_in_recovery" or "pg_relation_is_publishable":
                ReplaceCall(index, open, close, "FALSE");
                break;
            case "pg_table_is_visible" or "pg_type_is_visible" or "pg_function_is_visible" or "has_table_privilege" or "has_schema_privilege"
                or "has_database_privilege" or "has_column_privilege" or "has_any_column_privilege" or "has_sequence_privilege" or "has_function_privilege"
                or "pg_advisory_lock" or "pg_advisory_unlock" or "pg_advisory_xact_lock" or "pg_try_advisory_lock" or "pg_try_advisory_xact_lock" or "pg_advisory_unlock_all":
                ReplaceCall(index, open, close, "TRUE");
                break;
            case "obj_description" or "col_description" or "shobj_description" or "pg_get_serial_sequence" or "inet_server_addr" or "inet_client_addr"
                or "pg_postmaster_start_time" or "pg_conf_load_time" or "pg_tablespace_location":
                ReplaceCall(index, open, close, "NULL");
                break;
            case "pg_encoding_to_char":
                ReplaceCall(index, open, close, "'UTF8'");
                break;
            case "pg_relation_size" or "pg_total_relation_size" or "pg_table_size" or "pg_indexes_size" or "pg_database_size":
                ReplaceCall(index, open, close, "0");
                break;
            case "gen_random_uuid" or "uuid_generate_v4":
                ReplaceCall(index, open, close, UuidSql);
                break;
            case "random" when close == open + 1:
                ReplaceCall(index, open, close, RandomSql);
                break;
            case "current_setting":
                var setting = FirstLiteral(open, close) is { } settingName && _context.Settings.TryGetValue(settingName, out var settingValue)
                    ? PgRewriteContext.Literal(settingValue)
                    : "NULL";
                ReplaceCall(index, open, close, setting);
                break;
            case "set_config":
                var arguments = Arguments(open, close);
                ReplaceCall(index, open, close, arguments.Count == 3 && _literals.TryGetValue(arguments[1].Start, out var configured)
                    ? PgRewriteContext.Literal(configured)
                    : "NULL");
                break;
            case "pg_get_expr" or "quote_ident":
                KeepFirstArgument(index, open, close, "(", ")");
                break;
            case "pg_size_pretty":
                KeepFirstArgument(index, open, close, "(CAST(", " AS TEXT) || ' bytes')");
                break;
            case "pg_get_indexdef":
                _usesCatalog = true;
                KeepFirstArgument(index, open, close, $"(SELECT _tb_indexdef FROM ({PgCatalogSql.PgIndexRelation}) WHERE indexrelid = (", "))");
                break;
            case "pg_get_constraintdef":
                _usesCatalog = true;
                KeepFirstArgument(index, open, close, $"(SELECT _tb_def FROM ({PgCatalogSql.PgConstraintRelation}) WHERE oid = (", "))");
                break;
            case "pg_get_viewdef":
                KeepFirstArgument(index, open, close, $"(SELECT sql FROM sqlite_schema WHERE {PgCatalogSql.FirstObjectOid} + rowid = (", "))");
                break;
            case "pg_partition_ancestors":
                KeepFirstArgument(index, open, close, "(SELECT CAST(", " AS INTEGER) AS relid)");
                break;
            case "format_type":
                RewriteFormatType(index, open, close);
                break;
            case "string_agg":
                _tokens.Replace(index, "group_concat");
                break;
            case "greatest":
                _tokens.Replace(index, "max");
                break;
            case "least":
                _tokens.Replace(index, "min");
                break;
            case "strpos":
                _tokens.Replace(index, "instr");
                break;
            case "char_length" or "character_length":
                _tokens.Replace(index, "length");
                break;
            case "json_build_object" or "jsonb_build_object":
                _tokens.Replace(index, "json_object");
                break;
            case "json_build_array" or "jsonb_build_array":
                _tokens.Replace(index, "json_array");
                break;
            case "json_agg" or "jsonb_agg" or "array_agg":
                _tokens.Replace(index, "json_group_array");
                break;
            case "json_object_agg" or "jsonb_object_agg":
                _tokens.Replace(index, "json_group_object");
                break;
            case "jsonb_array_length":
                _tokens.Replace(index, "json_array_length");
                break;
            case "json_typeof" or "jsonb_typeof":
                _tokens.Replace(index, "json_type");
                break;
            case "to_json" or "to_jsonb":
                _tokens.Replace(index, "json_quote");
                break;
            case "left":
                if (Arguments(open, close) is [_, var count])
                {
                    _tokens.Replace(index, "substr");
                    _tokens.Replace(count.Start - 1, ", 1, ");
                }

                break;
            case "right" or "split_part":
                _deferred.Add(index);
                break;
            case "extract" or "date_part":
                return RewriteExtract(index, open, close, name == "date_part");
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

        var field = _literals.TryGetValue(fieldIndex, out var literalField) ? literalField : _tokens.Text(fieldIndex);
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
        foreach (var index in _deferred.OrderDescending())
        {
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
            else if (_tokens.IsWord(index, "split_part") && arguments is [var text, var delimiter, var position])
            {
                var sql = "(WITH RECURSIVE _tb_sp(n, d, rest, part, i) AS (SELECT q.n, q.d, q.s || q.d, NULL, 0 FROM (SELECT "
                    + _tokens.Render(position.Start, position.End) + " AS n, "
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
        Arguments(open, close) is [var first, ..] && first.Start == first.End && _literals.TryGetValue(first.Start, out var literal) ? literal : null;

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
