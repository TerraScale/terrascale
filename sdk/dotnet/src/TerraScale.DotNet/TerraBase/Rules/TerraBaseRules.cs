using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>A complete replacement of the database's Rules.</summary>
public sealed record TerraBaseRulesSet(IReadOnlyDictionary<string, TerraBaseRulesTable> Tables);

/// <summary>Conditions, column access and server-assigned values for one table.</summary>
public sealed record TerraBaseRulesTable(string? Read = null, string? Create = null, string? Update = null,
    string? Delete = null, IReadOnlyList<string>? Columns = null, IReadOnlyList<string>? Writable = null,
    IReadOnlyList<string>? Validate = null, TerraBaseRulesValues? Set = null);

/// <summary>Expressions assigned by the server during create and update.</summary>
public sealed record TerraBaseRulesValues(IReadOnlyDictionary<string, string>? Create = null,
    IReadOnlyDictionary<string, string>? Update = null);

/// <summary>A compiler diagnostic identifying its table, field and stable code.</summary>
public sealed record TerraBaseRulesProblem(string Table, string Field, string Code, string Message);

/// <summary>A Rules version with current-schema problems or deployment warnings.</summary>
public sealed record TerraBaseRules(string Version, IReadOnlyDictionary<string, TerraBaseRulesTable> Tables,
    DateTimeOffset UpdatedAt, string UpdatedBy, IReadOnlyList<TerraBaseRulesProblem>? Problems = null,
    IReadOnlyList<TerraBaseRulesProblem>? Warnings = null);

/// <summary>Metadata for a retained Rules version.</summary>
public sealed record TerraBaseRulesVersion(string Version, DateTimeOffset UpdatedAt, string UpdatedBy, string Digest);

/// <summary>Simulator identity; trusted claims are values under claims.</summary>
public sealed record TerraBaseRulesTestAuth(string Uid, IReadOnlyDictionary<string, JsonElement>? Claims = null,
    [property: JsonPropertyName("org_id")] string? OrgId = null,
    [property: JsonPropertyName("org_role")] string? OrgRole = null,
    [property: JsonPropertyName("is_anonymous")] bool IsAnonymous = false);

/// <summary>A simulated operation; New is the create body or update patch. Time is Unix milliseconds.</summary>
public sealed record TerraBaseRulesTestRequest(string Operation, string Table, TerraBaseRulesTestAuth Auth,
    IReadOnlyDictionary<string, JsonElement>? Row = null, IReadOnlyDictionary<string, JsonElement>? New = null,
    long? Time = null, TerraBaseRulesSet? Rules = null);

/// <summary>The result of evaluating one clause.</summary>
public sealed record TerraBaseRulesTestValue(string Rule, bool Value);

/// <summary>A simulator decision. Draft compilation problems deny the request.</summary>
public sealed record TerraBaseRulesTestResult(bool Allowed, string DecidedBy, string? Clause,
    IReadOnlyList<TerraBaseRulesTestValue> Results, IReadOnlyList<string> Columns, IReadOnlyList<TerraBaseRulesProblem> Problems)
{
    [JsonConstructor]
    internal TerraBaseRulesTestResult(bool Allowed, string DecidedBy, IReadOnlyList<TerraBaseRulesTestValue> Results,
        IReadOnlyList<string> Columns, IReadOnlyList<TerraBaseRulesProblem> Problems, string? Clause = null)
        : this(Allowed, DecidedBy, Clause, Results, Columns, Problems)
    {
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraBaseRulesSet))]
[JsonSerializable(typeof(TerraBaseRules))]
[JsonSerializable(typeof(TerraBasePage<TerraBaseRulesVersion>))]
[JsonSerializable(typeof(TerraBaseRulesTestRequest))]
[JsonSerializable(typeof(TerraBaseRulesTestResult))]
[JsonSerializable(typeof(IReadOnlyList<TerraBaseRulesProblem>))]
internal sealed partial class TerraBaseRulesJson : JsonSerializerContext;

public sealed partial class TerraBaseDatabase
{
    /// <summary>Reads current Rules. Requires databases:read; 404 no_rules means none have been deployed.</summary>
    public async Task<TerraBaseRules> GetRulesAsync(CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/rules", null, TerraBaseRulesJson.Default.TerraBaseRules,
            null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Replaces Rules with If-Match, or If-None-Match: * when currentVersion is null. Requires databases:write.</summary>
    public async Task<TerraBaseRules> DeployRulesAsync(TerraBaseRulesSet rules, string? currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var headers = RulesPrecondition(currentVersion);
        return (await connection.SendAsync(HttpMethod.Put, path + "/rules",
            TerraBaseConnection.Json(rules, TerraBaseRulesJson.Default.TerraBaseRulesSet), TerraBaseRulesJson.Default.TerraBaseRules,
            headers, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Lists newest versions first. Limit is 1–100; pass Next as after. Requires databases:read.</summary>
    public async Task<TerraBasePage<TerraBaseRulesVersion>> ListRulesVersionsAsync(string? after = null, int? limit = null,
        CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/rules/versions" + TerraBaseConnection.Query(
            ("after", after), ("limit", TerraBaseConnection.Number(limit))), null,
            TerraBaseRulesJson.Default.TerraBasePageTerraBaseRulesVersion, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Reads one retained version. Requires databases:read.</summary>
    public async Task<TerraBaseRules> GetRulesVersionAsync(string version, CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(version);
        return (await connection.SendAsync(HttpMethod.Get, path + "/rules/versions/" + version, null,
            TerraBaseRulesJson.Default.TerraBaseRules, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Revalidates and restores a retained version as a new version, with the same precondition as deploy.</summary>
    public async Task<TerraBaseRules> RestoreRulesAsync(string version, string? currentVersion,
        CancellationToken cancellationToken = default)
    {
        TerraScaleIds.Validate(version);
        var headers = RulesPrecondition(currentVersion);
        return (await connection.SendAsync(HttpMethod.Post, path + "/rules/versions/" + version + "/restore", null,
            TerraBaseRulesJson.Default.TerraBaseRules, headers, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Simulates deployed or draft Rules without reading or writing rows. Requires databases:read.</summary>
    public async Task<TerraBaseRulesTestResult> TestRulesAsync(TerraBaseRulesTestRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return (await connection.SendAsync(HttpMethod.Post, path + "/rules:test",
            TerraBaseConnection.Json(request, TerraBaseRulesJson.Default.TerraBaseRulesTestRequest),
            TerraBaseRulesJson.Default.TerraBaseRulesTestResult, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    private static Action<HttpRequestHeaders> RulesPrecondition(string? version)
    {
        if (version is null)
            return headers => headers.IfNoneMatch.Add(EntityTagHeaderValue.Any);
        TerraScaleIds.Validate(version);
        return headers => headers.IfMatch.Add(new EntityTagHeaderValue($"\"{version}\""));
    }
}
