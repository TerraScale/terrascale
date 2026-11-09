using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlParser;
using SqlParser.Dialects;
using SqlParser.Tokens;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Postgres;

internal sealed record PgColumn(string Name, int Oid);

internal sealed record PgResult(PgColumn[]? Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, string? Tag, PgError? Notice = null)
{
    internal SqlReadCharge? ReadCharge { get; init; }
}

internal sealed record PgError(string Code, string Message, bool Fatal = false);

internal readonly union PgOutcome(PgResult[], PgError);

internal enum PgKind { Empty, Sql, Catalog, Set, Show, Discard, Deallocate, Begin, Commit, Rollback, Savepoint }

internal sealed record PgStatement(PgKind Kind, string Text, string Sql, int Parameters);

internal readonly union PgParse(PgStatement[], PgError);

internal static partial class PostgresStatements
{
    public const int FirstTableOid = 16384, ProtocolVersion = 196608;
    public const string ServerVersion = "16.0";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly PostgreSqlDialect Dialect = new();
    private static readonly (string Name, string Value, bool Settable, bool Reported)[] Gucs =
    [
        ("server_version", ServerVersion, false, true),
        ("server_encoding", "UTF8", false, true),
        ("client_encoding", "UTF8", true, true),
        ("DateStyle", "ISO, MDY", true, true),
        ("integer_datetimes", "on", false, true),
        ("standard_conforming_strings", "on", false, true),
        ("TimeZone", "UTC", true, true),
        ("extra_float_digits", "1", true, false),
        ("application_name", "", true, true),
        ("search_path", "public", true, false),
        ("terrascale.write", "auto", true, false),
    ];

    private static readonly string[] Settable = [.. Gucs.Where(guc => guc.Settable).Select(guc => guc.Name)];

    public static readonly string[] Reported = [.. Gucs.Where(guc => guc.Reported).Select(guc => guc.Name)];

    public static Dictionary<string, string> Settings(string applicationName)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transaction_isolation"] = "serializable",
            ["default_transaction_isolation"] = "serializable",
        };
        foreach (var (name, value, _, _) in Gucs)
        {
            settings[name] = name == "application_name" ? applicationName : value;
        }

        return settings;
    }

    public static string Version => $"PostgreSQL {ServerVersion} (TerraBase)";

    public static PgParse Parse(string sql)
    {
        IList<Token> tokens;
        try
        {
            tokens = new Tokenizer().Tokenize(sql, Dialect);
        }
        catch (TokenizeException)
        {
            return new PgError("42601", "SQL syntax error: unterminated string, quoted identifier or comment");
        }

        var statements = new List<PgStatement>();
        for (int i = 0, start = 0; i <= tokens.Count; i++)
        {
            if (i < tokens.Count && tokens[i] is not SemiColon)
            {
                continue;
            }

            var piece = tokens.Skip(start).Take(i - start).ToList();
            start = i + 1;
            if (piece.Any(token => token is not Whitespace))
            {
                switch (Statement(piece))
                {
                    case PgError error:
                        return error;
                    case PgStatement statement:
                        statements.Add(statement);
                        break;
                }
            }
        }

        return statements.ToArray();
    }

    private readonly union PgOne(PgStatement, PgError);

    private static PgOne Statement(List<Token> tokens)
    {
        var text = string.Concat(tokens.Select(token => token.ToString())).Trim();
        if (RefusedPattern().Match(text) is { Success: true } refused)
        {
            return Unsupported(refused.Groups[1].Success ? "LISTEN, NOTIFY and UNLISTEN" : "extensions");
        }

        var kind = text switch
        {
            _ when SavepointPattern().IsMatch(text) => PgKind.Savepoint,
            _ when SetPattern().IsMatch(text) => PgKind.Set,
            _ when ShowPattern().IsMatch(text) => PgKind.Show,
            _ when DiscardPattern().IsMatch(text) => PgKind.Discard,
            _ when DeallocatePattern().IsMatch(text) => PgKind.Deallocate,
            _ when BeginPattern().IsMatch(text) => PgKind.Begin,
            _ when CommitPattern().IsMatch(text) => PgKind.Commit,
            _ when RollbackPattern().IsMatch(text) => PgKind.Rollback,
            _ when PgCatalog.IsCatalog(text) => PgKind.Catalog,
            _ => PgKind.Sql,
        };
        return kind switch
        {
            PgKind.Sql => Translate(text, tokens),
            _ => new PgStatement(kind, text, text, 0),
        };
    }

    private static PgOne Translate(string text, List<Token> tokens)
    {
        var output = tokens.Select(token => token.ToString()).ToArray();
        int[] at = [.. Enumerable.Range(0, tokens.Count).Where(i => tokens[i] is not Whitespace)];
        var create = tokens[at[0]] is Word { Keyword: Keyword.CREATE };
        var castStart = new Dictionary<int, int>();
        var parameters = 0;
        for (var s = 0; s < at.Length; s++)
        {
            var token = tokens[at[s]];
            switch (token)
            {
                case Placeholder { Value: ['$', .. var digits] }:
                    if (!int.TryParse(digits, NumberStyles.None, Invariant, out var number) || number is < 1 or > short.MaxValue)
                    {
                        return new PgError("42P02", "parameter numbers must be $1 to $32767");
                    }

                    parameters = Math.Max(parameters, number);
                    output[at[s]] = "?" + number.ToString(Invariant);
                    break;
                case Word { QuoteStyle: null } word when Is(word, "ILIKE"):
                    output[at[s]] = "LIKE";
                    break;
                case Word { QuoteStyle: null } word when create && (Is(word, "SERIAL") || Is(word, "BIGSERIAL") || Is(word, "SMALLSERIAL")):
                    output[at[s]] = "INTEGER";
                    break;
                case Word { QuoteStyle: null } word when Is(word, "now") && s + 2 < at.Length && tokens[at[s + 1]] is LeftParen && tokens[at[s + 2]] is RightParen:
                    output[at[s]] = "CURRENT_TIMESTAMP";
                    output[at[++s]] = output[at[++s]] = "";
                    break;
                case DollarQuotedString:
                    return Unsupported("dollar-quoted strings");
                case EscapedStringLiteral:
                    return Unsupported("E'' escape strings");
                case DoubleColon:
                    if (Cast(ref s) is { } error)
                    {
                        return error;
                    }

                    break;
            }
        }

        return new PgStatement(PgKind.Sql, text, string.Concat(output).Trim(), parameters);

        PgError? Cast(ref int s)
        {
            var q = s - 1;
            if (q < 0)
            {
                return Unsupported("this :: cast");
            }

            if (castStart.TryGetValue(q, out var chained))
            {
                q = chained;
            }
            else if (tokens[at[q]] is RightParen)
            {
                for (var depth = 0; q >= 0; q--)
                {
                    depth += tokens[at[q]] is RightParen ? 1 : tokens[at[q]] is LeftParen ? -1 : 0;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                if (q < 0)
                {
                    return new PgError("42601", "SQL syntax error: unbalanced parentheses");
                }

                q -= q > 0 && tokens[at[q - 1]] is Word && at[q - 1] == at[q] - 1 ? 1 : 0;
            }
            else if (tokens[at[q]] is Word or SingleQuotedString or Number or Placeholder)
            {
                for (; q >= 2 && tokens[at[q - 1]] is Period && tokens[at[q - 2]] is Word; q -= 2)
                {
                }
            }
            else
            {
                return Unsupported("this :: cast; use CAST(expression AS type)");
            }

            var t = s + 1;
            t += t + 1 < at.Length && tokens[at[t]] is Word schema && Is(schema, "pg_catalog") && tokens[at[t + 1]] is Period ? 2 : 0;
            if (t >= at.Length || tokens[at[t]] is not Word type)
            {
                return new PgError("42601", "SQL syntax error: :: needs a type name");
            }

            var name = type.Value.ToLowerInvariant();
            for (; t + 1 < at.Length && tokens[at[t + 1]] is Word { QuoteStyle: null } more && TypeWords().IsMatch(more.Value); t++)
            {
                name += " " + more.Value.ToLowerInvariant();
            }

            if (t + 1 < at.Length && tokens[at[t + 1]] is LeftParen)
            {
                for (t++; t < at.Length && tokens[at[t]] is not RightParen; t++)
                {
                }
            }

            if (t + 1 < at.Length && tokens[at[t + 1]] is LeftBracket || t >= at.Length)
            {
                return Unsupported("array types");
            }

            var target = name switch
            {
                "int2" or "int4" or "int8" or "smallint" or "integer" or "int" or "bigint" => "INTEGER",
                "float4" or "float8" or "real" or "double precision" or "float" => "REAL",
                "numeric" or "decimal" => "NUMERIC",
                "bool" or "boolean" => "BOOLEAN",
                "text" or "varchar" or "character varying" or "char" or "character" or "bpchar" or "name" or "json" or "jsonb" or "uuid" or "date"
                    or "timestamp" or "timestamptz" or "timestamp without time zone" or "timestamp with time zone" => "TEXT",
                _ => null,
            };
            if (target is null)
            {
                return Unsupported($"casts to {name}");
            }

            var literal = q == s - 1 && tokens[at[q]] is SingleQuotedString ? (SingleQuotedString)tokens[at[q]] : null;
            if (literal is not null && target == "BOOLEAN")
            {
                if (Bool(literal.Value) is not { } flag)
                {
                    return new PgError("22P02", "invalid input syntax for type boolean");
                }

                output[at[q]] = flag ? "TRUE" : "FALSE";
            }

            for (var k = at[s]; k <= at[t]; k++)
            {
                output[k] = "";
            }

            if (literal is null || target is "INTEGER" or "REAL" or "NUMERIC")
            {
                output[at[q]] = "CAST(" + output[at[q]];
                output[at[s]] = $" AS {target})";
            }

            castStart[t] = q;
            s = t;
            return null;
        }
    }

    public static void InferParameters(PgStatement statement, int[] oids, IReadOnlyList<SqlTableInfo> tables)
    {
        var tokens = new Tokenizer().Tokenize(statement.Text, Dialect).Where(token => token is not Whitespace).ToList();
        var named = tables.Where(table => tokens.Any(token => token is Word word && word.Value.Equals(table.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        int TypeOf(Token? token)
        {
            return token is Word word
                && named.SelectMany(table => table.Columns).Where(column => column.Name.Equals(word.Value, StringComparison.OrdinalIgnoreCase))
                    .Select(column => PgOid.Of(column.DeclType) switch { PgOid.Numeric => PgOid.Float8, 1082 or 1114 or 1184 => PgOid.Text, var oid => oid }).Distinct().ToList() is [var only]
                ? only
                : 0;
        }
        void Set(Token token, int oid)
        {
            if (token is Placeholder { Value: ['$', .. var digits] } && int.TryParse(digits, NumberStyles.None, Invariant, out var n) && n <= oids.Length && oids[n - 1] == 0 && oid > 0)
            {
                oids[n - 1] = oid;
            }
        }

        var open = tokens.FindIndex(token => token is LeftParen);
        if (tokens is [Word { Keyword: Keyword.INSERT }, ..] && open > 0)
        {
            var close = tokens.FindIndex(open, token => token is RightParen);
            var columns = tokens[(open + 1)..Math.Max(open + 1, close)].Where(token => token is Word).ToList();
            for (int i = close + 1, depth = 0, position = 0; close > 0 && i < tokens.Count; i++)
            {
                (depth, position) = tokens[i] switch
                {
                    LeftParen => (depth + 1, depth == 0 ? 0 : position),
                    RightParen => (depth - 1, position),
                    Comma when depth == 1 => (depth, position + 1),
                    _ => (depth, position),
                };
                if (depth == 1 && position < columns.Count)
                {
                    Set(tokens[i], TypeOf(columns[position]));
                }
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is Placeholder)
            {
                Set(tokens[i], i >= 2 && Comparison(tokens[i - 1]) ? TypeOf(tokens[i - 2]) : 0);
                Set(tokens[i], i + 2 < tokens.Count && Comparison(tokens[i + 1]) ? TypeOf(tokens[i + 2]) : 0);
            }
        }
    }

    private static bool Comparison(Token token) => token is Equal or NotEqual or LessThan or GreaterThan or LessThanOrEqual or GreaterThanOrEqual
        || token is Word { QuoteStyle: null } word && (Is(word, "LIKE") || Is(word, "ILIKE"));

    private static bool Is(Word word, string value) => word.Value.Equals(value, StringComparison.OrdinalIgnoreCase);

    private static PgError Unsupported(string feature) => new("0A000", $"not supported: {feature}");

    public static PgOutcome RunCommand(PgKind kind, string text, Dictionary<string, string> settings)
    {
        if (kind == PgKind.Show)
        {
            var name = string.Join('_', ShowPattern().Match(text).Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            name = name.Equals("transaction_isolation_level", StringComparison.OrdinalIgnoreCase) ? "transaction_isolation"
                : name.Equals("time_zone", StringComparison.OrdinalIgnoreCase) ? "TimeZone" : name;
            return settings.TryGetValue(name, out var shown)
                ? new[] { new PgResult([new(name, PgOid.Text)], [[SqlValue.Of(shown)]], "SHOW") }
                : new PgError("42704", "unrecognized configuration parameter");
        }

        var set = SetPattern().Match(text);
        var setting = Settable.FirstOrDefault(known => known.Equals(
            set.Groups[1].Success ? set.Groups[1].Value : set.Groups[3].Value, StringComparison.OrdinalIgnoreCase));
        var value = set.Groups[3].Success ? "DEFAULT"
            : set.Groups[2].Value is ['\'', .. var quoted, '\''] ? quoted.Replace("''", "'", StringComparison.Ordinal) : set.Groups[2].Value;
        if (setting is null)
        {
            return new PgError("0A000", $"SET supports only {string.Join(", ", Settable[..^1])} and {Settable[^1]}");
        }

        if (setting == "client_encoding" && !value.Replace("-", "", StringComparison.Ordinal).Equals("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            return new PgError("22023", "only the UTF8 client encoding is supported");
        }

        if (setting == "terrascale.write" && value.ToLowerInvariant() is not ("home" or "local" or "auto"))
        {
            return new PgError("22023", "terrascale.write is home, local or auto");
        }

        var reset = value.Equals("DEFAULT", StringComparison.OrdinalIgnoreCase);
        if (setting == "TimeZone" && !reset && !IsUtcZone(value))
        {
            return new PgError("22023", "only UTC and its aliases are supported time zones");
        }

        if (setting == "DateStyle" && !reset && !IsIsoDateStyle(value))
        {
            return new PgError("22023", "DateStyle is ISO, optionally with MDY, DMY or YMD");
        }

        settings[setting] = setting switch
        {
            "TimeZone" => "UTC",
            "DateStyle" => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) is [_, var order]
                ? $"ISO, {order.ToUpperInvariant()}"
                : "ISO, MDY",
            _ => value,
        };
        return new[] { new PgResult(null, [], "SET") };
    }

    private static bool IsUtcZone(string value) => value.Trim().ToLowerInvariant() is
        "utc" or "etc/utc" or "etc/uct" or "uct" or "gmt" or "etc/gmt" or "z" or "zulu" or "universal" or "etc/universal" or "greenwich";

    private static bool IsIsoDateStyle(string value) => value.ToLowerInvariant().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        is ["iso"] or ["iso", "mdy" or "dmy" or "ymd"];

    public static PgResult Result(PgStatement statement, SqlResult result)
    {
        PgColumn[]? columns = result.Columns.Count == 0 ? null : [.. result.Columns.Select((column, i) => new PgColumn(column.Name,
            PgOid.Of(column.DeclType) is > 0 and var oid ? oid
            : result.Rows.Select(row => row[i].Kind).FirstOrDefault(value => value != SqlValueKind.Null) switch
            {
                SqlValueKind.Integer => PgOid.Int8,
                SqlValueKind.Real => PgOid.Float8,
                SqlValueKind.Blob => PgOid.Bytea,
                _ => PgOid.Text,
            }))];
        var verb = VerbPattern().Match(statement.Sql);
        var tag = verb.Groups[1].Value.ToUpperInvariant() switch
        {
            "INSERT" or "REPLACE" => $"INSERT 0 {result.RowsAffected}",
            "UPDATE" => $"UPDATE {result.RowsAffected}",
            "DELETE" => $"DELETE {result.RowsAffected}",
            "CREATE" or "DROP" when columns is null => $"{verb.Groups[1].Value.ToUpperInvariant()} {verb.Groups[2].Value.ToUpperInvariant()}",
            _ => $"SELECT {result.Rows.Count}",
        };
        return new PgResult(columns, result.Rows, tag);
    }

    public static int DescribedOid(SqlColumn column) => PgOid.Of(column.DeclType) is > 0 and var declared ? declared
        : ExpressionPattern().Match(column.Name) is { Success: true } match
            ? match.Groups[1].Success ? 20 : match.Groups[2].Success ? 701 : PgOid.Of(match.Groups[3].Value) is > 0 and var cast ? cast : PgOid.Text
            : PgOid.Text;

    [GeneratedRegex(@"\A(?:((?:count|length|instr|unicode|row_number|rank|dense_rank|ntile)\()|((?:avg|total)\()|CAST\(.+\bAS\s+(\w+)\)\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ExpressionPattern();

    public static bool RollsBackTo(string text) => text.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase);

    public static PgError Error(SqlError error) => new(error.Sqlstate, error.Message);

    public static bool TryEncode(SqlValue value, int oid, bool binary, out byte[]? bytes)
    {
        bytes = null;
        if (value.Kind == SqlValueKind.Null)
        {
            return true;
        }

        if (value.Kind == SqlValueKind.Blob)
        {
            bytes = oid == PgOid.Bytea && binary ? value.Blob : Utf8.GetBytes("\\x" + Convert.ToHexStringLower(value.Blob!));
            return true;
        }

        if (oid is PgOid.Date or PgOid.Timestamp or PgOid.Timestamptz)
        {
            return value.Kind == SqlValueKind.Text && PostgresTemporal.TryEncode(value.Text ?? "", oid, binary, out bytes);
        }

        if (oid == PgOid.Uuid)
        {
            if (value.Kind != SqlValueKind.Text || !Guid.TryParseExact(value.Text, "D", out var uuid))
            {
                return false;
            }

            bytes = binary ? Convert.FromHexString(uuid.ToString("N", Invariant)) : Utf8.GetBytes(uuid.ToString("D", Invariant));
            return true;
        }

        if (oid == PgOid.Numeric)
        {
            string? s;
            switch (value.Kind)
            {
                case SqlValueKind.Integer:
                    s = value.Integer.ToString(Invariant);
                    break;
                case SqlValueKind.Real:
                    if (!double.IsFinite(value.Real))
                    {
                        return false;
                    }

                    s = Math.Abs(value.Real) < 7.9e28 ? ((decimal)value.Real).ToString(Invariant) : value.Real.ToString("F0", Invariant);
                    break;
                case SqlValueKind.Text when PgNumeric.Matches(value.Text ?? ""):
                    s = value.Text!;
                    break;
                default:
                    return false;
            }

            if (binary)
            {
                if (PgNumeric.Encode(s) is not { } numeric)
                {
                    return false;
                }

                bytes = numeric;
            }
            else
            {
                bytes = Utf8.GetBytes(s);
            }

            return true;
        }

        var text = value.Kind switch
        {
            SqlValueKind.Integer when oid == PgOid.Bool => value.Integer != 0 ? "t" : "f",
            SqlValueKind.Integer => value.Integer.ToString(Invariant),
            SqlValueKind.Real => double.IsFinite(value.Real) ? value.Real.ToString("R", Invariant).Replace('E', 'e') : value.Real.ToString(Invariant),
            _ => value.Text ?? "",
        };
        if (!binary || oid is PgOid.Text or PgOid.Json or PgOid.Varchar)
        {
            bytes = Utf8.GetBytes(text);
            return true;
        }

        if (oid == PgOid.Jsonb)
        {
            bytes = [1, .. Utf8.GetBytes(text)];
            return true;
        }

        if (oid == PgOid.Float8)
        {
            double real;
            switch (value.Kind)
            {
                case SqlValueKind.Integer:
                    real = value.Integer;
                    break;
                case SqlValueKind.Real:
                    real = value.Real;
                    break;
                case SqlValueKind.Text when double.TryParse(value.Text, NumberStyles.Float, Invariant, out real):
                    break;
                default:
                    return false;
            }

            bytes = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(bytes, real);
            return true;
        }

        long integer;
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                integer = value.Integer;
                break;
            case SqlValueKind.Real when value.Real == Math.Truncate(value.Real) && Math.Abs(value.Real) < 9e18:
                integer = (long)value.Real;
                break;
            case SqlValueKind.Text when long.TryParse(value.Text, NumberStyles.AllowLeadingSign, Invariant, out integer):
                break;
            default:
                return false;
        }

        switch (oid)
        {
            case PgOid.Bool:
                bytes = [integer != 0 ? (byte)1 : (byte)0];
                return true;
            case PgOid.Int2 when integer is >= short.MinValue and <= short.MaxValue:
                bytes = new byte[2];
                BinaryPrimitives.WriteInt16BigEndian(bytes, (short)integer);
                return true;
            case PgOid.Int4 when integer is >= int.MinValue and <= int.MaxValue:
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, (int)integer);
                return true;
            case PgOid.Int8:
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, integer);
                return true;
            default:
                return false;
        }
    }

    public static SqlValue? Decode(int oid, int format, byte[]? value)
    {
        if (value is null)
        {
            return SqlValue.Null;
        }

        try
        {
            return format == 1
                ? (oid, value.Length) switch
                {
                    (PgOid.Bool, 1) => SqlValue.Of(value[0] == 0 ? 0L : 1L),
                    (PgOid.Int2, 2) => SqlValue.Of(BinaryPrimitives.ReadInt16BigEndian(value)),
                    (PgOid.Int4, 4) => SqlValue.Of(BinaryPrimitives.ReadInt32BigEndian(value)),
                    (PgOid.Int8, 8) => SqlValue.Of(BinaryPrimitives.ReadInt64BigEndian(value)),
                    (PgOid.Float4, 4) => Real(BinaryPrimitives.ReadSingleBigEndian(value)),
                    (PgOid.Float8, 8) => Real(BinaryPrimitives.ReadDoubleBigEndian(value)),
                    (PgOid.Date or PgOid.Timestamp or PgOid.Timestamptz, _) => PostgresTemporal.Decode(oid, value),
                    (PgOid.Uuid, 16) => Uuid(value),
                    (PgOid.Numeric, _) => PgNumeric.Decode(value) is { } n ? Numeric(n) : null,
                    (PgOid.Jsonb, > 0) when value[0] == 1 => Text(Utf8.GetString(value.AsSpan(1))),
                    (PgOid.Bytea, _) => SqlValue.Of(value),
                    (0 or PgOid.Text or PgOid.Json or PgOid.Unknown or PgOid.Bpchar or PgOid.Varchar, _) => Text(Utf8.GetString(value)),
                    _ => null,
                }
                : (oid, Utf8.GetString(value)) switch
                {
                    (PgOid.Date or PgOid.Timestamp or PgOid.Timestamptz, var text) => PostgresTemporal.Parse(oid, text),
                    (PgOid.Uuid, var text) => Uuid(text),
                    (PgOid.Bytea, var text) => Bytea(text),
                    (PgOid.Bool, var text) => Bool(text) is { } flag ? SqlValue.Of(flag ? 1L : 0L) : null,
                    (PgOid.Int8 or PgOid.Int2 or PgOid.Int4, var text) => long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var integer) ? SqlValue.Of(integer) : null,
                    (PgOid.Float4 or PgOid.Float8, var text) => double.TryParse(text, NumberStyles.Float, Invariant, out var real) ? Real(real) : null,
                    (PgOid.Numeric, var text) => PgNumeric.Matches(text) ? Numeric(text)
                        : long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var whole) ? SqlValue.Of(whole)
                        : decimal.TryParse(text, NumberStyles.Float, Invariant, out var exact) && (decimal)(double)exact == exact ? SqlValue.Of((double)exact)
                        : Text(text),
                    (0 or PgOid.Unknown, var text) when long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var untyped) && untyped.ToString(Invariant) == text => SqlValue.Of(untyped),
                    (0 or PgOid.Unknown, var text) when text.Contains('.', StringComparison.Ordinal) && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out var untypedReal)
                        && untypedReal.ToString("R", Invariant) == text => SqlValue.Of(untypedReal),
                    (_, var text) => Text(text),
                };
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static SqlValue? Bytea(string text)
    {
        if (!text.StartsWith("\\x", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return SqlValue.Of(Convert.FromHexString(text.AsSpan(2)));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static SqlValue? Uuid(byte[] value)
    {
        var hex = Convert.ToHexStringLower(value);
        return SqlValue.Of($"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}");
    }

    private static SqlValue? Uuid(string text) =>
        Guid.TryParseExact(text.AsSpan(), "D", out var uuid) ? SqlValue.Of(uuid.ToString("D", Invariant)) : null;

    private static SqlValue? Numeric(string n) =>
        !n.Contains('.', StringComparison.Ordinal) && long.TryParse(n, NumberStyles.AllowLeadingSign, Invariant, out var whole)
            ? SqlValue.Of(whole)
            : SqlValue.Of(n);

    private static SqlValue? Real(double value) => double.IsFinite(value) ? SqlValue.Of(value) : null;

    private static SqlValue? Text(string text) => text.Contains('\0', StringComparison.Ordinal) ? null : SqlValue.Of(text);

    private static bool? Bool(string text) => text.Trim().ToUpperInvariant() switch
    {
        "T" or "TRUE" or "1" or "ON" or "YES" or "Y" => true,
        "F" or "FALSE" or "0" or "OFF" or "NO" or "N" => false,
        _ => null,
    };

    [GeneratedRegex(@"\A(\w+)(?:\s+(?:UNIQUE\s+)?(\w+))?", RegexOptions.CultureInvariant)]
    private static partial Regex VerbPattern();

    [GeneratedRegex(@"\A(?:precision|varying|with|without|time|zone)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TypeWords();

    [GeneratedRegex(@"\A(?:SET\s+(?:SESSION\s+)?([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*(?:=|\s+TO\s+)\s*(.*)|RESET\s+([A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\s*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SetPattern();

    [GeneratedRegex(@"\ASHOW\s+(transaction\s+isolation\s+level|time\s+zone|[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)?)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShowPattern();

    [GeneratedRegex(@"\ADISCARD\s+ALL\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscardPattern();

    public static string? Deallocated(string text) => DeallocatePattern().Match(text).Groups[1].Value is var name && name.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        ? null
        : name.Trim('"');

    [GeneratedRegex(@"\ADEALLOCATE\s+(?:PREPARE\s+)?(\w+|""[^""]+"")\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeallocatePattern();

    [GeneratedRegex(@"\A(?:((?:LISTEN|NOTIFY|UNLISTEN)\b)|(?:CREATE|ALTER|DROP)\s+EXTENSION\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefusedPattern();

    [GeneratedRegex(@"\A(?:SAVEPOINT|RELEASE|ROLLBACK\s+(?:WORK\s+|TRANSACTION\s+)?TO)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SavepointPattern();

    [GeneratedRegex(@"\A(?:BEGIN|START\s+TRANSACTION)(?:\s+(?:WORK|TRANSACTION))?(?:\s*,?\s*(?:ISOLATION\s+LEVEL\s+)?(?:NOT\s+DEFERRED|DEFERRED|IMMEDIATE|EXCLUSIVE|SERIALIZABLE|REPEATABLE\s+READ|READ\s+COMMITTED|READ\s+UNCOMMITTED|READ\s+WRITE|READ\s+ONLY|READONLY))*\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BeginPattern();

    public static bool BeginReadOnly(string text)
    {
        var words = text.Split([',', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Select(word => word.ToUpperInvariant()).ToList();
        for (var i = 0; i < words.Count; i++)
        {
            switch (words[i])
            {
                case "READONLY":
                case "READ" when i + 1 < words.Count && words[i + 1] == "ONLY":
                    return true;
                case "READ" when i + 1 < words.Count && words[i + 1] is "COMMITTED" or "UNCOMMITTED":
                    i++;
                    break;
            }
        }

        return false;
    }

    [GeneratedRegex(@"\A(?:COMMIT|END)(?:\s+(?:WORK|TRANSACTION))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"\A(?:ROLLBACK|ABORT)(?:\s+(?:WORK|TRANSACTION))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RollbackPattern();
}
