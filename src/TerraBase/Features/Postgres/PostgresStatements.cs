using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TerraBase.Errors;
using TerraBase.Features.Management.Queries;

namespace TerraBase.Features.Postgres;

/// <summary>A result column: its name and type OID.</summary>
internal sealed record PgColumn(string Name, int Oid);

/// <summary>One statement's result: <paramref name="Columns"/> null for no rows, <paramref name="Tag"/> null for an empty query.</summary>
internal sealed record PgResult(PgColumn[]? Columns, IReadOnlyList<string?[]> Rows, string? Tag, string? Notice = null);

/// <summary>An ErrorResponse: SQLSTATE and a message that never quotes secrets or paths.</summary>
internal sealed record PgError(string Code, string Message);

/// <summary>A statement's results, or its error.</summary>
internal readonly union PgOutcome(PgResult[], PgError);

/// <summary>What a statement is, decided from its text.</summary>
internal enum PgKind { Empty, Select, Set, Show, Version, Discard, Transaction }

/// <summary>
/// Maps PostgreSQL statements onto the shared SQL path: session commands drivers send, parameter literals, and
/// <see cref="SqlResultBody"/> rows as <c>id text, value json</c>. The one place to change when that path grows.
/// </summary>
internal static partial class PostgresStatements
{
    public const int TextOid = 25;
    public const string ServerVersion = "16.0";
    private const int JsonOid = 114;
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly PgColumn[] SelectColumns = [new("id", TextOid), new("value", JsonOid)];
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

    public static PgResult Version() => new([new("version", TextOid)], [[$"PostgreSQL {ServerVersion} (TerraBase)"]], "SELECT 1");

    /// <summary>The statement's kind and its text without surrounding blanks and trailing semicolons.</summary>
    public static PgKind Classify(string sql, out string text)
    {
        text = sql.Trim().TrimEnd(';', ' ', '\t', '\r', '\n');
        return text switch
        {
            "" => PgKind.Empty,
            _ when VersionPattern().IsMatch(text) => PgKind.Version,
            _ when SetPattern().IsMatch(text) => PgKind.Set,
            _ when ShowPattern().IsMatch(text) => PgKind.Show,
            _ when DiscardPattern().IsMatch(text) => PgKind.Discard,
            _ when TransactionPattern().IsMatch(text) => PgKind.Transaction,
            _ => PgKind.Select,
        };
    }

    /// <summary>The columns a statement returns, or null when it returns no rows.</summary>
    public static PgColumn[]? Describe(string sql) => Classify(sql, out var text) switch
    {
        PgKind.Select => SelectColumns,
        PgKind.Version => Version().Columns,
        PgKind.Show => [new(ShowPattern().Match(text).Groups[1].Value, TextOid)],
        _ => null,
    };

    /// <summary>Runs a session command (every kind but <see cref="PgKind.Select"/>) against the session's settings.</summary>
    public static PgOutcome RunCommand(PgKind kind, string text, Dictionary<string, string> settings)
    {
        switch (kind)
        {
            case PgKind.Empty:
                return new[] { new PgResult(null, [], null) };
            case PgKind.Version:
                return new[] { Version() };
            case PgKind.Discard:
                return new[] { new PgResult(null, [], "DISCARD ALL") };
            case PgKind.Show:
                var name = ShowPattern().Match(text).Groups[1].Value;
                return settings.TryGetValue(name, out var shown)
                    ? new[] { new PgResult([new(name, TextOid)], [[shown]], "SHOW") }
                    : new PgError("42704", "unrecognized configuration parameter");
            case PgKind.Set:
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
            default:
                return new PgError("0A000", "transactions are not supported yet; each statement runs on its own");
        }
    }

    /// <summary>The rows of a SQL subset result; a notice when the bounds cut it below its LIMIT.</summary>
    public static PgResult Rows(SqlStatement statement, SqlResultBody body) => new(
        SelectColumns,
        [.. body.Rows.Select(row => new string?[] { row.Id, row.Value.GetRawText() })],
        $"SELECT {body.Rows.Count}",
        body.Truncated && (!statement.HasLimit || body.Rows.Count < statement.Query.Limit)
            ? $"result truncated at {body.Rows.Count} rows: {SqlSubset.DefaultLimit} without LIMIT, at most {SqlSubset.MaxLimit} and 8 MiB"
            : null);

    /// <summary>The SQLSTATE of a refusal of the SQL path.</summary>
    public static PgError Error(Validation error) => error.Message switch
    {
        "query too expensive" => new("54000", error.Message),
        "only SELECT statements are supported" => new("0A000", "this statement is not supported yet: only SELECT is"),
        var message when message.StartsWith("SQL syntax error", StringComparison.Ordinal) => new("42601", message),
        var message when message.StartsWith("sql must be", StringComparison.Ordinal) => new("54000", message),
        var message => new("0A000", message),
    };

    /// <summary>The highest <c>$n</c> the statement uses, or -1 when one is out of range.</summary>
    public static int MaxParameter(string sql) => Scan(sql, null, null);

    /// <summary>The statement with each <c>$n</c> outside quotes and comments replaced by its encoded literal.</summary>
    public static string Substitute(string sql, IReadOnlyList<string> literals)
    {
        var output = new StringBuilder(sql.Length);
        Scan(sql, literals, output);
        return output.ToString();
    }

    /// <summary>
    /// A parameter as a SQL literal: numbers and booleans validated and re-formatted, text quoted with quotes doubled. Null
    /// when the value is invalid for its type, or binary of a type without a binary decoder.
    /// </summary>
    public static string? Literal(int oid, int format, byte[]? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        try
        {
            return format == 1
                ? (oid, value.Length) switch
                {
                    (16, 1) => value[0] == 0 ? "FALSE" : "TRUE",
                    (21, 2) => BinaryPrimitives.ReadInt16BigEndian(value).ToString(Invariant),
                    (23, 4) => BinaryPrimitives.ReadInt32BigEndian(value).ToString(Invariant),
                    (20, 8) => BinaryPrimitives.ReadInt64BigEndian(value).ToString(Invariant),
                    (700, 4) => Number(BinaryPrimitives.ReadSingleBigEndian(value)),
                    (701, 8) => Number(BinaryPrimitives.ReadDoubleBigEndian(value)),
                    (0 or TextOid or 705 or 1043, _) => Quote(Utf8.GetString(value)),
                    _ => null,
                }
                : (oid, Utf8.GetString(value)) switch
                {
                    (16, var text) => text.ToUpperInvariant() switch
                    {
                        "T" or "TRUE" or "1" or "ON" or "YES" or "Y" => "TRUE",
                        "F" or "FALSE" or "0" or "OFF" or "NO" or "N" => "FALSE",
                        _ => null,
                    },
                    (20 or 21 or 23, var text) => long.TryParse(text, NumberStyles.AllowLeadingSign, Invariant, out var integer) ? integer.ToString(Invariant) : null,
                    (700 or 701 or 1700, var text) => double.TryParse(text, NumberStyles.Float, Invariant, out var real) ? Number(real) : null,
                    (_, var text) => Quote(text),
                };
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    private static string? Number(double value) => double.IsFinite(value) ? value.ToString("R", Invariant) : null;

    // Standard strings (the SQLite dialect has no backslash escapes): doubling every quote keeps the value one literal.
    private static string? Quote(string text) => text.Contains('\0', StringComparison.Ordinal) ? null : $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";

    // Walks the SQL skipping quoted strings, quoted identifiers and comments; returns the highest $n, and with literals
    // writes the statement with each $n replaced, padded with spaces so "-$1" never becomes a "--" comment.
    private static int Scan(string sql, IReadOnlyList<string>? literals, StringBuilder? output)
    {
        var max = 0;
        var i = 0;
        while (i < sql.Length)
        {
            var start = i;
            var c = sql[i];
            var next = i + 1 < sql.Length ? sql[i + 1] : '\0';
            if (c is '\'' or '"' or '`' or '[')
            {
                // A doubled quote closes and reopens the same token, so it needs no special case.
                var close = sql.IndexOf(c == '[' ? ']' : c, i + 1);
                i = close < 0 ? sql.Length : close + 1;
            }
            else if (c == '-' && next == '-')
            {
                var newline = sql.IndexOf('\n', i);
                i = newline < 0 ? sql.Length : newline + 1;
            }
            else if (c == '/' && next == '*')
            {
                var close = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? sql.Length : close + 2;
            }
            else if (c == '$' && char.IsAsciiDigit(next) && (i == 0 || !(char.IsAsciiLetterOrDigit(sql[i - 1]) || sql[i - 1] == '_')))
            {
                for (i++; i < sql.Length && char.IsAsciiDigit(sql[i]); i++)
                {
                }

                if (!int.TryParse(sql.AsSpan(start + 1, i - start - 1), NumberStyles.None, Invariant, out var index) || index is < 1 or > short.MaxValue)
                {
                    return -1;
                }

                max = Math.Max(max, index);
                output?.Append(' ').Append(literals![index - 1]).Append(' ');
                continue;
            }
            else
            {
                i++;
            }

            output?.Append(sql, start, i - start);
        }

        return max;
    }

    [GeneratedRegex(@"\ASELECT\s+(pg_catalog\.)?version\s*\(\s*\)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"\ASET\s+(?:SESSION\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*(?:=|\s+TO\s+)\s*(.*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex SetPattern();

    [GeneratedRegex(@"\ASHOW\s+([A-Za-z_][A-Za-z0-9_]*)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ShowPattern();

    [GeneratedRegex(@"\ADISCARD\s+ALL\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiscardPattern();

    [GeneratedRegex(@"\A(BEGIN|START\s+TRANSACTION|COMMIT|END|ROLLBACK|ABORT|SAVEPOINT|RELEASE)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionPattern();
}
