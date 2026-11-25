using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraBase.Features.Rules;

public sealed record RulesSet(IReadOnlyDictionary<string, RulesTable> Tables)
{
    public RulesTable? Table(string name) =>
        Tables.TryGetValue(name, out var exact)
            ? exact
            : Tables.FirstOrDefault(pair => AsciiIgnoreCaseComparer.Instance.Equals(pair.Key, name)).Value;
}

public sealed record RulesTable(
    string? Read = null,
    string? Create = null,
    string? Update = null,
    string? Delete = null,
    IReadOnlyList<string>? Columns = null,
    IReadOnlyList<string>? Writable = null,
    IReadOnlyList<string>? Validate = null,
    RulesValues? Set = null);

public sealed record RulesValues(IReadOnlyDictionary<string, string>? Create = null, IReadOnlyDictionary<string, string>? Update = null);

public sealed record RulesProblem(string Table, string Field, string Code, string Message);

public sealed record StoredRules(string Version, RulesSet Rules, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

public sealed record RulesVersionItem(string Version, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

public sealed record RulesBody(
    string Version,
    IReadOnlyDictionary<string, RulesTable> Tables,
    IReadOnlyList<RulesProblem> Problems,
    IReadOnlyList<RulesProblem> Warnings,
    DateTimeOffset UpdatedAt,
    string UpdatedBy);

public sealed record RulesVersionsBody(IReadOnlyList<RulesVersionItem> Items, string? Next);

public sealed record RulesErrorDetails(IReadOnlyList<RulesProblem> Problems);

public sealed record RulesErrorDetail(string Code, string Message, RulesErrorDetails Details);

public sealed record RulesErrorBody(RulesErrorDetail Error);

public sealed record RulesInput(string? Project, string? Database, IReadOnlyDictionary<string, RulesTable>? Tables);

public sealed record RulesTestAuth(
    string? Uid,
    JsonElement? Claims,
    [property: JsonPropertyName("org_id")] string? OrgId,
    [property: JsonPropertyName("org_role")] string? OrgRole,
    [property: JsonPropertyName("is_anonymous")] bool IsAnonymous = false);

public sealed record RulesTestInput(
    string? Project,
    string? Database,
    RulesSet? Rules,
    RulesTestAuth? Auth,
    string? Table,
    string? Operation,
    IReadOnlyDictionary<string, JsonElement>? Row,
    IReadOnlyDictionary<string, JsonElement>? New,
    long? Time);

public sealed record RulesTestValue(string Rule, bool Value);

public sealed record RulesTestBody(
    bool Allowed,
    string DecidedBy,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Clause,
    IReadOnlyList<RulesTestValue> Results,
    IReadOnlyList<string> Columns,
    IReadOnlyList<RulesProblem> Problems);

public sealed record InvalidRules(IReadOnlyList<RulesProblem> Problems);

public sealed record StaleRules(string? Current);

public enum RulesOperation
{
    Read,
    Create,
    Update,
    Delete,
    Validate,
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RulesSet))]
[JsonSerializable(typeof(RulesBody))]
[JsonSerializable(typeof(RulesVersionsBody))]
[JsonSerializable(typeof(RulesErrorBody))]
[JsonSerializable(typeof(RulesInput))]
[JsonSerializable(typeof(RulesTestInput))]
[JsonSerializable(typeof(RulesTestBody))]
[JsonSerializable(typeof(RulesRouteInput))]
internal sealed partial class RulesJsonContext : JsonSerializerContext;

internal static class RulesRoute
{
    public const string Rules = "/v1/projects/{project}/databases/{database}/rules";

    public const string Versions = Rules + "/versions";

    public const string Version = Versions + "/{version}";

    public const string Restore = Version + "/restore";

    public static IResult Body(HttpContext context, StoredRules rules, IReadOnlyList<RulesProblem> problems, IReadOnlyList<RulesProblem> warnings)
    {
        context.Response.Headers.ETag = string.Concat("\"", rules.Version, "\"");
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Json(new RulesBody(rules.Version, rules.Rules.Tables, problems, warnings, rules.UpdatedAt, rules.UpdatedBy), RulesJsonContext.Default.RulesBody);
    }

    public static IResult Invalid(InvalidRules invalid) =>
        TypedResults.Json(new RulesErrorBody(new RulesErrorDetail("invalid_rules", "the rules do not fit the database; see details.problems",
            new RulesErrorDetails(invalid.Problems))), RulesJsonContext.Default.RulesErrorBody, statusCode: StatusCodes.Status400BadRequest);

    public static string? IfMatch(HttpRequest request) =>
        request.Headers.IfMatch is [{ Length: > 2 } tag] && tag[0] == '"' && tag[^1] == '"' ? tag[1..^1] : request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.ToString() : null;

    public static bool IfNoneMatchAny(HttpRequest request) => request.Headers.IfNoneMatch is ["*"];
}
