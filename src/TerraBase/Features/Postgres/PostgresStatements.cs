using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlParser;
using SqlParser.Dialects;
using SqlParser.Tokens;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Postgres;

/// <summary>A result column: its name and type OID.</summary>
internal sealed record PgColumn(string Name, int Oid);

/// <summary>One statement's result: <paramref name="Columns"/> null for no rows, <paramref name="Tag"/> null for an empty query.</summary>
internal sealed record PgResult(PgColumn[]? Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, string? Tag, PgError? Notice = null)
{
    internal SqlReadCharge? ReadCharge { get; init; }
}

/// <summary>A SQLSTATE and a message without secrets or paths; a fatal error also ends the session.</summary>
internal sealed record PgError(string Code, string Message, bool Fatal = false);

/// <summary>A statement's results, or its error.</summary>
internal readonly union PgOutcome(PgResult[], PgError);

/// <summary>What a statement is, decided from its text.</summary>
internal enum PgKind { Empty, Sql, Catalog, Set, Show, Discard, Deallocate, Begin, Commit, Rollback, Savepoint, Release, RollbackTo }

/// <summary>One statement: its PostgreSQL text, and for <see cref="PgKind.Sql"/> the SQLite text with <c>$n</c> as <c>?n</c>.</summary>
internal sealed record PgStatement(PgKind Kind, string Text, string Sql, int Parameters);

/// <summary>The statements of a query string, or why it cannot run.</summary>
internal readonly union PgParse(PgStatement[], PgError);

/// <summary>
/// Maps PostgreSQL onto <see cref="ISqlSession"/>: session commands drivers send, a token-level PG-to-SQLite dialect
/// layer, SQLSTATEs, type OIDs and the text and binary value formats.
/// </summary>
internal static partial class PostgresStatements
{
    public const int TextOid = 25, FirstTableOid = 16384;
    public const string ServerVersion = "16.0";
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly PostgreSqlDialect Dialect = new();
    private static readonly string[] Settable = ["client_encoding", "DateStyle", "TimeZone", "extra_float_digits", "application_name", "search_path"];

    /// <summary>The settings ParameterStatus reports at connect.</summary>
    public static readonly string[] Reported =
        ["server_version", "server_encoding", "client_encoding", "DateStyle", "integer_datetimes", "standard_conforming_strings", "TimeZone", "application_name"];

    /// <summary>Session settings at connect.</summary>
    public static Dictionary<string, string> Settings(string applicationName) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["server_version"] = ServerVersion,
        ["server_encoding"] = "UTF8",
        ["client_encoding"] = "UTF8",
        ["DateStyle"] = "ISO, MDY",
        ["integer_datetimes"] = "on",
        ["standard_conforming_strings"] = "on",
        ["TimeZone"] = "UTC",
        ["application_name"] = applicationName,
        ["extra_float_digits"] = "1",
        ["search_path"] = "public",
        ["transaction_isolation"] = "serializable",
        ["default_transaction_isolation"] = "serializable",
    };

    public static string Version => $"PostgreSQL {ServerVersion} (TerraBase)";

    /// <summary>Splits a query string at top-level semicolons and translates each statement.</summary>
    public static PgParse Parse(string sql)
    {
        // SqlParserCS uses U+FFFF as EOF; accepting it could discard predicates after a recognised catalog query.
        if (sql.Contains(char.MaxValue))
        {
            return new PgError("42601", "U+FFFF is not supported in SQL text");
        }

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

        if (Savepoint(tokens, text) is { } savepoint)
        {
            return savepoint;
        }

        var kind = text switch
        {
            _ when SetPattern().IsMatch(text) => PgKind.Set,
            _ when ShowPattern().IsMatch(text) => PgKind.Show,
            _ when DiscardPattern().IsMatch(text) => PgKind.Discard,
            _ when DeallocatePattern().IsMatch(text) => PgKind.Deallocate,
            _ when BeginPattern().IsMatch(text) => PgKind.Begin,
            _ when CommitPattern().IsMatch(text) => PgKind.Commit,
            _ when RollbackPattern().IsMatch(text) => PgKind.Rollback,
            _ when PgCatalog.IsCatalog(text, tokens) => PgKind.Catalog,
            _ => PgKind.Sql,
        };
        return kind switch
        {
            PgKind.Sql => Translate(text, tokens),
            _ => new PgStatement(kind, text, text, 0),
        };
    }

    private static PgOne? Savepoint(List<Token> tokens, string text)
    {
        var words = tokens.Where(token => token is not Whitespace).ToArray();
        bool At(int i, string value) => i < words.Length && words[i] is Word { QuoteStyle: null } word && Is(word, value);
        var i = 1;
        PgKind kind;
        if (At(0, "SAVEPOINT"))
        {
            kind = PgKind.Savepoint;
        }
        else if (At(0, "RELEASE"))
        {
            kind = PgKind.Release;
            i += At(i, "SAVEPOINT") ? 1 : 0;
        }
        else
        {
            i += At(i, "WORK") || At(i, "TRANSACTION") ? 1 : 0;
            if (!At(0, "ROLLBACK") || !At(i, "TO"))
            {
                return null;
            }

            kind = PgKind.RollbackTo;
            i++;
            i += At(i, "SAVEPOINT") ? 1 : 0;
        }

        if (i != words.Length - 1 || words[i] is not Word { QuoteStyle: null or '"' } name || name.Value.Length == 0)
        {
            return new PgError("42601", "savepoint command requires one identifier");
        }

        var normalized = name.QuoteStyle is null ? name.Value.ToLowerInvariant() : name.Value;
        // PostgreSQL identifiers occupy at most 63 UTF-8 bytes, without splitting a code point.
        var end = 0;
        for (var bytes = 0; end < normalized.Length;)
        {
            var rune = Rune.GetRuneAt(normalized, end);
            bytes += rune.Utf8SequenceLength;
            if (bytes > 63)
            {
                break;
            }

            end += rune.Utf16SequenceLength;
        }

        return new PgStatement(kind, text, normalized[..end], 0);
    }

    // PG to SQLite on tokens, so string literals, quoted identifiers and comments are never rewritten.
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
                    // SQLite's LIKE is already case-insensitive for ASCII.
                    output[at[s]] = "LIKE";
                    break;
                case Word { QuoteStyle: null } word when create && (Is(word, "SERIAL") || Is(word, "BIGSERIAL") || Is(word, "SMALLSERIAL")):
                    // BIGINT, not PostgreSQL's int4: generated integer keys pass 2^31 (relational-sql.md section 3).
                    output[at[s]] = "BIGINT";
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

        // `operand::type` becomes CAST(operand AS type), or the bare literal for a text-like cast of a string literal.
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

                // A function call: its name touches the parenthesis.
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

    /// <summary>
    /// Fills untyped parameters (OID 0) with the type PostgreSQL would infer from context: the column of an INSERT column
    /// list, or the column a parameter is compared with or assigned to, in the tables the statement names.
    /// </summary>
    public static void InferParameters(PgStatement statement, int[] oids, IReadOnlyList<SqlTableInfo> tables)
    {
        var tokens = new Tokenizer().Tokenize(statement.Text, Dialect).Where(token => token is not Whitespace).ToList();
        var named = tables.Where(table => tokens.Any(token => token is Word word && word.Value.Equals(table.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        // The column's type where its name is unambiguous; numeric binds as float8, dates and times as text.
        int TypeOf(Token? token)
        {
            return token is Word word
                && named.SelectMany(table => table.Columns).Where(column => column.Name.Equals(word.Value, StringComparison.OrdinalIgnoreCase))
                    .Select(column => Oid(column.DeclType) switch { 1700 => 701, 1082 or 1114 or 1184 => TextOid, var oid => oid }).Distinct().ToList() is [var only]
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

        // INSERT INTO t (c1, c2, …) VALUES ($1, $2, …), …: each tuple position takes its column's type.
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

        // column = $n, $n < column, column LIKE $n, SET column = $n.
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

    /// <summary>Runs SET or SHOW against the session's settings.</summary>
    public static PgOutcome RunCommand(PgKind kind, string text, Dictionary<string, string> settings)
    {
        if (kind == PgKind.Show)
        {
            var name = string.Join('_', ShowPattern().Match(text).Groups[1].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            name = name.Equals("transaction_isolation_level", StringComparison.OrdinalIgnoreCase) ? "transaction_isolation"
                : name.Equals("time_zone", StringComparison.OrdinalIgnoreCase) ? "TimeZone" : name;
            return settings.TryGetValue(name, out var shown)
                ? new[] { new PgResult([new(name, TextOid)], [[SqlValue.Of(shown)]], "SHOW") }
                : new PgError("42704", "unrecognized configuration parameter");
        }

        var set = SetPattern().Match(text);
        var setting = Settable.FirstOrDefault(known => known.Equals(set.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
        var value = set.Groups[2].Value is ['\'', .. var quoted, '\''] ? quoted.Replace("''", "'", StringComparison.Ordinal) : set.Groups[2].Value;
        if (setting is null)
        {
            return new PgError("0A000", "SET supports only client_encoding, DateStyle, TimeZone, extra_float_digits, application_name and search_path");
        }

        if (setting == "client_encoding" && !value.Replace("-", "", StringComparison.Ordinal).Equals("UTF8", StringComparison.OrdinalIgnoreCase))
        {
            return new PgError("22023", "only the UTF8 client encoding is supported");
        }

        // Session-local echo: the value changes nothing else.
        settings[setting] = value;
        return new[] { new PgResult(null, [], "SET") };
    }

    /// <summary>A statement's result: types from declared types, else from the first non-null value.</summary>
    public static PgResult Result(PgStatement statement, SqlResult result)
    {
        PgColumn[]? columns = result.Columns.Count == 0 ? null : [.. result.Columns.Select((column, i) => new PgColumn(column.Name,
            Oid(column.DeclType) is > 0 and var oid ? oid
            : result.Rows.Select(row => row[i].Kind).FirstOrDefault(value => value != SqlValueKind.Null) switch
            {
                SqlValueKind.Integer => 20,
                SqlValueKind.Real => 701,
                _ => TextOid,
            }))];
        var verb = VerbPattern().Match(statement.Sql);
        var tag = verb.Groups[1].Value.ToUpperInvariant() switch
        {
            "INSERT" or "REPLACE" => $"INSERT 0 {result.RowsAffected}",
            "UPDATE" => $"UPDATE {result.RowsAffected}",
            "DELETE" => $"DELETE {result.RowsAffected}",
            "CREATE" or "DROP" or "ALTER" when columns is null => $"{verb.Groups[1].Value.ToUpperInvariant()} {verb.Groups[2].Value.ToUpperInvariant()}",
            _ => $"SELECT {result.Rows.Count}",
        };
        return new PgResult(columns, result.Rows, tag);
    }

    /// <summary>The OID of a declared type as PostgreSQL names it, 0 when unknown.</summary>
    public static int Oid(string? declared) => (declared?.Split('(')[0].Trim().ToUpperInvariant() ?? "") switch
    {
        "BIGINT" or "INT8" => 20,
        "SMALLINT" or "INT2" => 21,
        "INTEGER" or "INT" or "INT4" => 23,
        "REAL" or "FLOAT" or "FLOAT4" or "FLOAT8" or "DOUBLE" or "DOUBLE PRECISION" => 701,
        "NUMERIC" or "DECIMAL" => 1700,
        "TEXT" => TextOid,
        "VARCHAR" or "CHAR" or "CHARACTER" or "CHARACTER VARYING" => 1043,
        "BOOLEAN" or "BOOL" => 16,
        "JSON" => 114,
        "JSONB" => 3802,
        "DATE" => 1082,
        "TIMESTAMP" or "TIMESTAMP WITHOUT TIME ZONE" => 1114,
        "TIMESTAMPTZ" or "TIMESTAMP WITH TIME ZONE" => 1184,
        _ => 0,
    };

    /// <summary>
    /// A column's type before the statement runs: its declared type, else (an expression, which SQLite leaves untyped) the
    /// type PostgreSQL gives well-known aggregates and casts, else text.
    /// </summary>
    public static int DescribedOid(SqlColumn column) => Oid(column.DeclType) is > 0 and var declared ? declared
        : ExpressionPattern().Match(column.Name) is { Success: true } match
            ? match.Groups[1].Success ? 20 : match.Groups[2].Success ? 701 : Oid(match.Groups[3].Value) is > 0 and var cast ? cast : TextOid
            : TextOid;

    [GeneratedRegex(@"\A(?:((?:count|length|instr|unicode|row_number|rank|dense_rank|ntile)\()|((?:avg|total)\()|CAST\(.+\bAS\s+(\w+)\)\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ExpressionPattern();

    /// <summary>The SQLSTATE of a <see cref="SqlError"/>; SQLITE_BUSY is always 40001, which drivers and ORMs retry.</summary>
    public static PgError Error(SqlError error) => new(error.Code switch
    {
        "SQLITE_CONSTRAINT_PRIMARYKEY" or "SQLITE_CONSTRAINT_UNIQUE" => "23505",
        "SQLITE_CONSTRAINT_NOTNULL" => "23502",
        var code when code.StartsWith("SQLITE_CONSTRAINT", StringComparison.Ordinal) => "23000",
        "SQLITE_AUTH" => "42501",
        "SQLITE_BUSY" => "40001",
        "SQLITE_TOOBIG" => "54000",
        "SQLITE_INTERRUPT" => "57014",
        _ => error.Kind switch
        {
            "no_such_table" => "42P01",
            "no_such_column" => "42703",
            "already_exists" => "42P07",
            "syntax" => "42601",
            _ => "0A000",
        },
    }, error.Message);

    /// <summary>Whether a column of the type is sent in binary when the client asks; others are always text.</summary>
    public static bool Binary(int oid) => oid is 16 or 20 or 21 or 23 or TextOid or 114 or 701 or 1043 or 1082 or 1114 or 1184 or 3802;

    /// <summary>
    /// A value in the column's format; false when it does not fit the binary type (SQLite keeps 64-bit integers in an int4
    /// or int2 column).
    /// </summary>
    public static bool TryEncode(SqlValue value, int oid, bool binary, out byte[]? bytes)
    {
        bytes = null;
        if (value.Kind == SqlValueKind.Null)
        {
            return true;
        }

        if (oid is 1082 or 1114 or 1184)
        {
            return value.Kind == SqlValueKind.Text && PostgresTemporal.TryEncode(value.Text ?? "", oid, binary, out bytes);
        }

        var text = value.Kind switch
        {
            SqlValueKind.Integer when oid == 16 => value.Integer != 0 ? "t" : "f",
            SqlValueKind.Integer => value.Integer.ToString(Invariant),
            SqlValueKind.Real => double.IsFinite(value.Real) ? value.Real.ToString("R", Invariant).Replace('E', 'e') : value.Real.ToString(Invariant),
            _ => value.Text ?? "",
        };
        if (!binary || oid is TextOid or 114 or 1043)
        {
            bytes = Utf8.GetBytes(text);
            return true;
        }

        if (oid == 3802)
        {
            bytes = [1, .. Utf8.GetBytes(text)];
            return true;
        }

        if (oid == 701)
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
            case 16:
                bytes = [integer != 0 ? (byte)1 : (byte)0];
                return true;
            case 21 when integer is >= short.MinValue and <= short.MaxValue:
                bytes = new byte[2];
                BinaryPrimitives.WriteInt16BigEndian(bytes, (short)integer);
                return true;
            case 23 when integer is >= int.MinValue and <= int.MaxValue:
                bytes = new byte[4];
                BinaryPrimitives.WriteInt32BigEndian(bytes, (int)integer);
                return true;
            case 20:
                bytes = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, integer);
                return true;
            default:
                return false;
        }
    }

    /// <summary>A bound parameter as a SQL value: booleans as 1 or 0. Null when invalid, or binary of an unsupported type.</summary>
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
                    (16, 1) => SqlValue.Of(value[0] == 0 ? 0L : 1L),
                    (21, 2) => SqlValue.Of(BinaryPrimitives.ReadInt16BigEndian(value)),
                    (23, 4) => SqlValue.Of(BinaryPrimitives.ReadInt32BigEndian(value)),
                    (20, 8) => SqlValue.Of(BinaryPrimitives.ReadInt64BigEndian(value)),
                    (700, 4) => Real(BinaryPrimitives.ReadSingleBigEndian(value)),
                    (701, 8) => Real(BinaryPrimitives.ReadDoubleBigEndian(value)),
                    (1082 or 1114 or 1184, _) => PostgresTemporal.Decode(oid, value),
                    (3802, > 0) when value[0] == 1 => Text(Utf8.GetString(value.AsSpan(1))),
                    (0 or TextOid or 114 or 705 or 1042 or 1043, _) => Text(Utf8.GetString(value)),
                    _ => null,
                }
                : (oid, Utf8.GetString(value)) switch
                {
                    (1082 or 1114 or 1184, var text) => PostgresTemporal.Parse(oid, text),
                    (16, var text) => Bool(text) is { } flag ? SqlValue.Of(flag ? 1L : 0L) : null,
                    (20 or 21 or 23, var text) => long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var integer) ? SqlValue.Of(integer) : null,
                    (700 or 701, var text) => double.TryParse(text, NumberStyles.Float, Invariant, out var real) ? Real(real) : null,
                    // Exact numerics stay exact: an integer, a double that round-trips, else the text.
                    (1700, var text) => long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var whole) ? SqlValue.Of(whole)
                        : decimal.TryParse(text, NumberStyles.Float, Invariant, out var exact) && (decimal)(double)exact == exact ? SqlValue.Of((double)exact)
                        : Text(text),
                    // Untyped (psql \bind, node-postgres): PostgreSQL types them from context; a canonical number binds as one,
                    // because the CTE columns of a SQL read carry no affinity to convert '2' for an integer column.
                    (0 or 705, var text) when long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var untyped) && untyped.ToString(Invariant) == text => SqlValue.Of(untyped),
                    (0 or 705, var text) when text.Contains('.', StringComparison.Ordinal) && double.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, Invariant, out var untypedReal)
                        && untypedReal.ToString("R", Invariant) == text => SqlValue.Of(untypedReal),
                    (_, var text) => Text(text),
                };
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

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

    [GeneratedRegex(@"\ASET\s+(?:SESSION\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|\s+TO\s+)\s*(.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SetPattern();

    [GeneratedRegex(@"\ASHOW\s+(transaction\s+isolation\s+level|time\s+zone|[A-Za-z_][A-Za-z0-9_]*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShowPattern();

    [GeneratedRegex(@"\ADISCARD\s+ALL\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscardPattern();

    /// <summary>The statement <c>DEALLOCATE</c> names, or <see langword="null"/> for <c>ALL</c>.</summary>
    public static string? Deallocated(string text) => DeallocatePattern().Match(text).Groups[1].Value is var name && name.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        ? null
        : name.Trim('"');

    [GeneratedRegex(@"\ADEALLOCATE\s+(?:PREPARE\s+)?(\w+|""[^""]+"")\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeallocatePattern();

    // PostgreSQL commands with no SQLite equivalent; without this they would reach the SQLite parser as syntax errors.
    [GeneratedRegex(@"\A(?:((?:LISTEN|NOTIFY|UNLISTEN)\b)|(?:CREATE|ALTER|DROP)\s+EXTENSION\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RefusedPattern();

    // Isolation and access modes are accepted and ignored: SQL transactions are serializable (relational-sql.md section 5).
    [GeneratedRegex(@"\A(?:BEGIN|START\s+TRANSACTION)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BeginPattern();

    [GeneratedRegex(@"\A(?:COMMIT|END)(?:\s+(?:WORK|TRANSACTION))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"\A(?:ROLLBACK|ABORT)(?:\s+(?:WORK|TRANSACTION))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RollbackPattern();
}
