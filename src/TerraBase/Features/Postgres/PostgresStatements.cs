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
internal sealed record PgResult(PgColumn[]? Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, string? Tag, PgError? Notice = null);

/// <summary>An ErrorResponse: SQLSTATE and a message that never quotes secrets or paths.</summary>
internal sealed record PgError(string Code, string Message);

/// <summary>A statement's results, or its error.</summary>
internal readonly union PgOutcome(PgResult[], PgError);

/// <summary>What a statement is, decided from its text.</summary>
internal enum PgKind { Empty, Sql, Catalog, Set, Show, Discard, Deallocate, Begin, Commit, Rollback }

/// <summary>One statement: its PostgreSQL text, and for <see cref="PgKind.Sql"/> the SQLite text with <c>$n</c> as <c>?n</c>.</summary>
internal sealed record PgStatement(PgKind Kind, string Text, string Sql, int Parameters);

/// <summary>The statements of a query string, or why it cannot run.</summary>
internal readonly union PgParse(PgStatement[], PgError);

/// <summary>
/// Maps PostgreSQL onto <see cref="ISqlSession"/>: session commands drivers send, statements split on tokens with
/// <c>$n</c> as <c>?n</c>, SQLSTATEs, and every value as text.
/// </summary>
internal static partial class PostgresStatements
{
    public const int TextOid = 25;
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
    };

    public static string Version => $"PostgreSQL {ServerVersion} (TerraBase)";

    /// <summary>Splits a query string at top-level semicolons and translates each statement.</summary>
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
        PgKind? kind = text switch
        {
            _ when SavepointPattern().IsMatch(text) => null,
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
            null => new PgError("0A000", "savepoints are not supported"),
            PgKind.Sql => Translate(text, tokens),
            _ => new PgStatement(kind.Value, text, text, 0),
        };
    }

    // $n becomes ?n on tokens, so string literals, quoted identifiers and comments are never rewritten.
    private static PgOne Translate(string text, List<Token> tokens)
    {
        var output = tokens.Select(token => token.ToString()).ToArray();
        var parameters = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is Placeholder { Value: ['$', .. var digits] })
            {
                if (!int.TryParse(digits, NumberStyles.None, Invariant, out var number) || number is < 1 or > short.MaxValue)
                {
                    return new PgError("42P02", "parameter numbers must be $1 to $32767");
                }

                parameters = Math.Max(parameters, number);
                output[i] = "?" + number.ToString(Invariant);
            }
        }

        return new PgStatement(PgKind.Sql, text, string.Concat(output).Trim(), parameters);
    }

    /// <summary>Runs SET or SHOW against the session's settings.</summary>
    public static PgOutcome RunCommand(PgKind kind, string text, Dictionary<string, string> settings)
    {
        if (kind == PgKind.Show)
        {
            var name = ShowPattern().Match(text).Groups[1].Value;
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

    /// <summary>A statement's result, every column text.</summary>
    public static PgResult Result(PgStatement statement, SqlResult result)
    {
        PgColumn[]? columns = result.Columns.Count == 0 ? null : [.. result.Columns.Select(column => new PgColumn(column.Name, TextOid))];
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

    /// <summary>Whether a column of the type is sent in binary when the client asks: text sends the same bytes.</summary>
    public static bool Binary(int oid) => oid == TextOid;

    /// <summary>A value as text: integers and reals in their shortest round-trip form.</summary>
    public static bool TryEncode(SqlValue value, int oid, bool binary, out byte[]? bytes)
    {
        bytes = value.Kind switch
        {
            SqlValueKind.Null => null,
            SqlValueKind.Integer => Utf8.GetBytes(value.Integer.ToString(Invariant)),
            SqlValueKind.Real => Utf8.GetBytes(value.Real.ToString("R", Invariant).Replace('E', 'e')),
            _ => Utf8.GetBytes(value.Text ?? ""),
        };
        return oid == TextOid || !binary;
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
                    (3802, > 0) when value[0] == 1 => Text(Utf8.GetString(value.AsSpan(1))),
                    (0 or TextOid or 114 or 705 or 1042 or 1043, _) => Text(Utf8.GetString(value)),
                    _ => null,
                }
                : (oid, Utf8.GetString(value)) switch
                {
                    (16, var text) => Bool(text) is { } flag ? SqlValue.Of(flag ? 1L : 0L) : null,
                    (20 or 21 or 23, var text) => long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var integer) ? SqlValue.Of(integer) : null,
                    (700 or 701, var text) => double.TryParse(text, NumberStyles.Float, Invariant, out var real) ? Real(real) : null,
                    // Exact numerics stay exact: an integer, a double that round-trips, else the text.
                    (1700, var text) => long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var whole) ? SqlValue.Of(whole)
                        : decimal.TryParse(text, NumberStyles.Float, Invariant, out var exact) && (decimal)(double)exact == exact ? SqlValue.Of((double)exact)
                        : Text(text),
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

    [GeneratedRegex(@"\ASET\s+(?:SESSION\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|\s+TO\s+)\s*(.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SetPattern();

    [GeneratedRegex(@"\ASHOW\s+([A-Za-z_][A-Za-z0-9_]*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShowPattern();

    [GeneratedRegex(@"\ADISCARD\s+ALL\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscardPattern();

    /// <summary>The statement <c>DEALLOCATE</c> names, or <see langword="null"/> for <c>ALL</c>.</summary>
    public static string? Deallocated(string text) => DeallocatePattern().Match(text).Groups[1].Value is var name && name.Equals("ALL", StringComparison.OrdinalIgnoreCase)
        ? null
        : name.Trim('"');

    [GeneratedRegex(@"\ADEALLOCATE\s+(?:PREPARE\s+)?(\w+|""[^""]+"")\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeallocatePattern();

    [GeneratedRegex(@"\A(?:SAVEPOINT|RELEASE|ROLLBACK\s+(?:WORK\s+|TRANSACTION\s+)?TO)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SavepointPattern();

    // Isolation and access modes are accepted and ignored: SQL transactions are serializable (relational-sql.md section 5).
    [GeneratedRegex(@"\A(?:BEGIN|START\s+TRANSACTION)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BeginPattern();

    [GeneratedRegex(@"\A(?:COMMIT|END)(?:\s+(?:WORK|TRANSACTION))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CommitPattern();

    [GeneratedRegex(@"\A(?:ROLLBACK|ABORT)(?:\s+(?:WORK|TRANSACTION))?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RollbackPattern();
}
