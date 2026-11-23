using System.Collections.Frozen;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Rules;

public enum ConditionSource
{
    Uid,
    OrgId,
    OrgRole,
    IsAnonymous,
    Claim,
    ClaimList,
    RequestTime,
}

public sealed record ConditionParameter(string Name, ConditionSource Source, string? Key = null);

public readonly union ConditionPart(string, ConditionParameter);

public sealed record CompiledCondition(IReadOnlyList<ConditionPart> Parts, bool UsesNew);

internal static class RulesCompiler
{
    public const int MaxCondition = 4096;

    private static readonly FrozenSet<string> Operators = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "AND", "OR", "NOT", "IS", "NULL", "IN", "BETWEEN", "LIKE", "GLOB", "ESCAPE", "CASE", "WHEN", "THEN", "ELSE", "END", "COLLATE", "CAST", "AS", "TRUE", "FALSE");

    private static readonly FrozenSet<string> Types = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "INTEGER", "INT", "TEXT", "REAL", "BLOB", "NUMERIC");

    private static readonly FrozenSet<string> Collations = FrozenSet.Create(StringComparer.OrdinalIgnoreCase, "BINARY", "NOCASE", "RTRIM");

    private static readonly FrozenSet<string> Functions = FrozenSet.Create(StringComparer.OrdinalIgnoreCase,
        "abs", "coalesce", "ifnull", "nullif", "iif", "length", "lower", "upper", "ltrim", "rtrim", "trim", "substr", "substring", "instr", "replace", "round",
        "typeof", "hex", "like", "glob");

    public static Result<CompiledCondition, Validation> Compile(string condition, IReadOnlyList<string>? columns, RulesOperation operation)
    {
        if (condition.Length > MaxCondition || !SqlText.TryTokenize(condition, out var tokens) || tokens.Count == 0)
        {
            return Refused(string.Concat("a condition is a complete expression of at most ", MaxCondition.ToString(CultureInfo.InvariantCulture), " characters"));
        }

        List<ConditionPart> parts = [new ConditionPart("((")];
        List<bool> lists = [];
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            var previous = index > 0 ? tokens[index - 1] : default;
            var opens = index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Open;
            if (token.Kind == SqlTokenKind.Word && index + 1 < tokens.Count && tokens[index + 1].Kind == SqlTokenKind.Dot)
            {
                var failure = Reference(tokens, ref index, columns, operation, previous.Is("IN"), lists.Contains(true)) switch
                {
                    ConditionPart part => Append(parts, part),
                    Validation refused => refused,
                };
                if (failure is not null)
                {
                    return failure;
                }

                continue;
            }

            if (previous.Is("IN") && token.Kind != SqlTokenKind.Open)
            {
                return Refused("IN takes a parenthesized list or one auth.claims list");
            }

            var piece = token.Kind switch
            {
                SqlTokenKind.String or SqlTokenKind.Number or SqlTokenKind.Blob or SqlTokenKind.Comma => condition[token.Start..token.End],
                SqlTokenKind.Open => "(",
                SqlTokenKind.Close => ")",
                SqlTokenKind.Symbol when token.Value[0] is '=' or '<' or '>' or '!' or '+' or '-' or '*' or '/' or '%' or '|' or '&' or '~' => token.Value,
                SqlTokenKind.Word when previous.Is("AS") => Types.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                SqlTokenKind.Word when previous.Is("COLLATE") => Collations.Contains(token.Value) ? token.Value.ToUpperInvariant() : null,
                SqlTokenKind.Word when opens && Functions.Contains(token.Value) => token.Value.ToLowerInvariant(),
                SqlTokenKind.Word when Operators.Contains(token.Value) => token.Value.ToUpperInvariant(),
                SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.Parameter or SqlTokenKind.Semicolon or SqlTokenKind.Dot or SqlTokenKind.Symbol => null,
                _ => throw new UnreachableException(),
            };
            if (piece is null)
            {
                return Refused(token.Kind switch
                {
                    SqlTokenKind.Parameter => "conditions take no parameters",
                    SqlTokenKind.Semicolon => "a condition is one expression",
                    SqlTokenKind.Word or SqlTokenKind.Quoted when !opens => string.Concat(token.Value, " must be qualified as row.", token.Value, " or new.", token.Value),
                    SqlTokenKind.Word when opens => string.Concat("conditions cannot call ", token.Value, "; only deterministic functions without side effects are allowed"),
                    SqlTokenKind.Word or SqlTokenKind.Quoted or SqlTokenKind.Dot or SqlTokenKind.Symbol or SqlTokenKind.String or SqlTokenKind.Number
                        or SqlTokenKind.Blob or SqlTokenKind.Comma or SqlTokenKind.Open or SqlTokenKind.Close => string.Concat("conditions cannot use ", token.Value),
                    _ => throw new UnreachableException(),
                });
            }

            if (token.Kind == SqlTokenKind.Open)
            {
                lists.Add(previous.Is("IN"));
            }
            else if (token.Kind == SqlTokenKind.Close)
            {
                if (lists.Count == 0)
                {
                    return Refused("the condition has unbalanced parentheses");
                }

                lists.RemoveAt(lists.Count - 1);
            }

            parts.Add(new ConditionPart(" "));
            parts.Add(new ConditionPart(piece));
        }

        parts.Add(new ConditionPart(") IS TRUE)"));
        var usesNew = parts.Any(static part => part switch
        {
            string text => text.StartsWith("new.", StringComparison.Ordinal),
            ConditionParameter => false,
        });
        return lists.Count == 0 ? new CompiledCondition(parts, usesNew) : Refused("the condition has unbalanced parentheses");
    }

    private static Validation? Append(List<ConditionPart> parts, ConditionPart part)
    {
        parts.Add(new ConditionPart(" "));
        parts.Add(part);
        return null;
    }

    public static string Render(CompiledCondition condition, RulesAuth auth, long? requestTime, string? newPrefix = null, string? rowPrefix = null)
    {
        var text = new StringBuilder();
        foreach (var part in condition.Parts)
        {
            text.Append(part switch
            {
                string literal when newPrefix is not null && literal.StartsWith("new.", StringComparison.Ordinal) => string.Concat(newPrefix, literal[4..]),
                string literal when literal.StartsWith("new.", StringComparison.Ordinal) => literal[4..],
                string literal when literal.StartsWith("row.", StringComparison.Ordinal) => rowPrefix is null ? literal[4..] : string.Concat(rowPrefix, literal[4..]),
                string literal => literal,
                ConditionParameter parameter => Literal(parameter, auth, requestTime),
            });
        }

        return text.ToString();
    }

    public static string Quote(string identifier) => string.Concat("\"", identifier.Replace("\"", "\"\"", StringComparison.Ordinal), "\"");

    public static string Literal(string? text) => text is null ? "NULL" : string.Concat("'", text.Replace("'", "''", StringComparison.Ordinal), "'");

    public static bool IsSetValue(string value)
    {
        if (value is "auth.uid" or "auth.org_id" or "auth.org_role" or "auth.is_anonymous" or "request.time" or "request.ulid"
            || (value.StartsWith(ClaimPrefix, StringComparison.Ordinal) && IsClaimName(value[ClaimPrefix.Length..])))
        {
            return true;
        }

        return SqlText.TryTokenize(value, out var tokens) && tokens switch
        {
            [{ Kind: SqlTokenKind.String or SqlTokenKind.Number }] => true,
            [{ Kind: SqlTokenKind.Blob } blob] => IsHex(blob.Value),
            [{ Kind: SqlTokenKind.Word } word] => word.Is("NULL") || word.Is("TRUE") || word.Is("FALSE"),
            [{ Kind: SqlTokenKind.Symbol, Value: "-" }, { Kind: SqlTokenKind.Number }] => true,
            _ => false,
        };
    }

    public static SqlValue SetValue(string expression, RulesAuth auth, long? requestTime, Ulid requestUlid) => expression switch
    {
        "auth.uid" => SqlValue.FromText(auth.Uid),
        "auth.org_id" => auth.OrgId is { } organization ? SqlValue.FromText(organization) : SqlValue.Null,
        "auth.org_role" => auth.OrgRole is { } role ? SqlValue.FromText(role) : SqlValue.Null,
        "auth.is_anonymous" => SqlValue.FromIntegral(auth.IsAnonymous ? 1 : 0),
        "request.time" => requestTime is { } milliseconds ? SqlValue.FromIntegral(milliseconds) : SqlValue.Null,
        "request.ulid" => SqlValue.FromText(requestUlid.ToString()),
        _ when expression.StartsWith(ClaimPrefix, StringComparison.Ordinal) =>
            auth.Claims.TryGetValue(expression[ClaimPrefix.Length..], out var claim) ? ClaimValue(claim) : SqlValue.Null,
        _ => LiteralValue(expression),
    };

    private const string ClaimPrefix = "auth.claims.";

    private static SqlValue ClaimValue(RulesClaim claim) => claim switch
    {
        string text => SqlValue.FromText(text),
        long integer => SqlValue.FromIntegral(integer),
        double real => SqlValue.FromReal(real),
        IReadOnlyList<RulesScalar> => SqlValue.FromText(ClaimJson(claim)),
    };

    private static SqlValue LiteralValue(string expression)
    {
        if (!SqlText.TryTokenize(expression, out var tokens))
        {
            return SqlValue.Null;
        }

        return tokens switch
        {
            [{ Kind: SqlTokenKind.String } text] => SqlValue.FromText(text.Value),
            [{ Kind: SqlTokenKind.Number } number] => Number(number.Value, negative: false),
            [{ Kind: SqlTokenKind.Symbol, Value: "-" }, { Kind: SqlTokenKind.Number } number] => Number(number.Value, negative: true),
            [{ Kind: SqlTokenKind.Blob } blob] when IsHex(blob.Value) => SqlValue.FromBlob(Convert.FromHexString(blob.Value)),
            [{ Kind: SqlTokenKind.Word } word] when word.Is("TRUE") => SqlValue.FromIntegral(1),
            [{ Kind: SqlTokenKind.Word } word] when word.Is("FALSE") => SqlValue.FromIntegral(0),
            _ => SqlValue.Null,
        };
    }

    private static bool IsHex(string text) => text.Length % 2 == 0 && text.All(char.IsAsciiHexDigit);

    private static SqlValue Number(string text, bool negative) =>
        long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var integer) ? SqlValue.FromIntegral(negative ? -integer : integer)
        : double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real) ? SqlValue.FromReal(negative ? -real : real)
        : SqlValue.Null;

    private static string Literal(ConditionParameter parameter, RulesAuth auth, long? requestTime) => parameter.Source switch
    {
        ConditionSource.Uid => Literal(auth.Uid),
        ConditionSource.OrgId => Literal(auth.OrgId),
        ConditionSource.OrgRole => Literal(auth.OrgRole),
        ConditionSource.IsAnonymous => auth.IsAnonymous ? "1" : "0",
        ConditionSource.RequestTime => requestTime is { } milliseconds ? milliseconds.ToString(CultureInfo.InvariantCulture) : "NULL",
        ConditionSource.Claim => parameter.Key is { } key && auth.Claims.TryGetValue(key, out var claim) ? Claim(claim) : "NULL",
        ConditionSource.ClaimList => parameter.Key is { } listKey && auth.Claims.TryGetValue(listKey, out var list) && list is IReadOnlyList<RulesScalar>
            ? string.Concat("(SELECT value FROM json_each(", Literal(ClaimJson(list)), "))")
            : "(SELECT NULL)",
        _ => throw new UnreachableException(),
    };

    private static string Claim(RulesClaim claim) => claim switch
    {
        string text => Literal(text),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double real => real.ToString("R", CultureInfo.InvariantCulture),
        IReadOnlyList<RulesScalar> => Literal(ClaimJson(claim)),
    };

    private static string ClaimJson(RulesClaim claim) => claim switch
    {
        IReadOnlyList<RulesScalar> items => string.Concat("[", string.Join(',', items.Select(Scalar)), "]"),
        string text => string.Concat("[", Scalar((RulesScalar)text), "]"),
        long integer => string.Concat("[", integer.ToString(CultureInfo.InvariantCulture), "]"),
        double real => string.Concat("[", real.ToString("R", CultureInfo.InvariantCulture), "]"),
    };

    private static string Scalar(RulesScalar scalar) => scalar switch
    {
        string text => JsonSerializer.Serialize(text, RulesScalarJson.Default.String),
        long integer => integer.ToString(CultureInfo.InvariantCulture),
        double real => real.ToString("R", CultureInfo.InvariantCulture),
    };

    private static Result<ConditionPart, Validation> Reference(
        List<SqlToken> tokens, ref int index, IReadOnlyList<string>? columns, RulesOperation operation, bool listPosition, bool inList)
    {
        var head = tokens[index].Value;
        List<SqlToken> path = [];
        var at = index;
        while (at + 2 < tokens.Count && tokens[at + 1].Kind == SqlTokenKind.Dot && tokens[at + 2].IsName)
        {
            path.Add(tokens[at + 2]);
            at += 2;
        }

        if (path.Count == 0 || (at + 1 < tokens.Count && tokens[at + 1].Kind is SqlTokenKind.Dot or SqlTokenKind.Open))
        {
            return Refused(string.Concat(head, ". starts no reference a condition can use"));
        }

        index = at;
        var name = string.Join('.', path.Select(static token => token.Value));
        var reference = string.Concat(head, ".", name);
        var isClaim = head == "auth" && path is [{ Kind: SqlTokenKind.Word, Value: "claims" }, { Kind: SqlTokenKind.Word } claim] && IsClaimName(claim.Value);
        if ((listPosition && !isClaim) || (inList && isClaim))
        {
            return Refused(listPosition ? "IN takes a parenthesized list or one auth.claims list" : string.Concat(reference, " is a list only as the whole right side of IN"));
        }

        switch (head)
        {
            case "row" or "new" when path.Count == 1:
                if (columns is not null && !columns.Contains(path[0].Value, StringComparer.OrdinalIgnoreCase))
                {
                    return new Validation(string.Concat(reference, ": the table has no column ", path[0].Value), "unknown_column");
                }

                var allowed = head == "row"
                    ? operation is RulesOperation.Read or RulesOperation.Update or RulesOperation.Delete
                    : operation is RulesOperation.Create or RulesOperation.Update or RulesOperation.Validate;
                return allowed
                    ? new ConditionPart(string.Concat(head == "new" ? "new." : "row.", Quote(path[0].Value)))
                    : new Validation(string.Concat(head, ".* is not allowed in ", Name(operation)), "reference_not_allowed");
            case "auth" when path is [{ Kind: SqlTokenKind.Word } field]:
                return field.Value switch
                {
                    "uid" => new ConditionPart(new ConditionParameter("auth_uid", ConditionSource.Uid)),
                    "org_id" => new ConditionPart(new ConditionParameter("auth_org_id", ConditionSource.OrgId)),
                    "org_role" => new ConditionPart(new ConditionParameter("auth_org_role", ConditionSource.OrgRole)),
                    "is_anonymous" => new ConditionPart(new ConditionParameter("auth_is_anonymous", ConditionSource.IsAnonymous)),
                    _ => Refused(string.Concat(reference, " is not an auth value; use auth.uid, auth.org_id, auth.org_role, auth.is_anonymous or auth.claims.<name>")),
                };
            case "auth" when isClaim && listPosition:
                return new ConditionPart(new ConditionParameter(string.Concat("auth_list_", path[1].Value), ConditionSource.ClaimList, path[1].Value));
            case "auth" when isClaim:
                return new ConditionPart(new ConditionParameter(string.Concat("auth_claim_", path[1].Value), ConditionSource.Claim, path[1].Value));
            case "request" when name == "time":
                return operation == RulesOperation.Read
                    ? new Validation("request.time is not allowed in read", "reference_not_allowed")
                    : new ConditionPart(new ConditionParameter("request_time", ConditionSource.RequestTime));
            case "request" when name == "ulid":
                return new Validation("request.ulid is allowed only as a set.create value", "reference_not_allowed");
            default:
                return Refused(string.Concat(reference, " is not a reference; use row.<column>, new.<column>, auth.<value> or request.time"));
        }
    }

    private static string Name(RulesOperation operation) => operation switch
    {
        RulesOperation.Read => "read",
        RulesOperation.Create => "create",
        RulesOperation.Update => "update",
        RulesOperation.Delete => "delete",
        RulesOperation.Validate => "validate",
        _ => throw new UnreachableException(),
    };

    private static bool IsClaimName(string name) => name is { Length: >= 1 and <= 63 } && name[0] is >= 'a' and <= 'z'
        && name.All(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    private static Validation Refused(string message) => new(message, "unsafe_where");
}

[JsonSerializable(typeof(string))]
internal sealed partial class RulesScalarJson : JsonSerializerContext;
