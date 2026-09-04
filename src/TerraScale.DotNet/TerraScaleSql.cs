using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet;

/// <summary>Types that may be declared for an extracted schema field.</summary>
public enum TerraScaleSqlFieldType
{
    /// <summary>UTF-8 string.</summary>
    String,
    /// <summary>64-bit integer.</summary>
    Integer,
    /// <summary>Floating-point number.</summary>
    Float,
    /// <summary>Boolean value.</summary>
    Boolean,
    /// <summary>JSON value.</summary>
    Json,
    /// <summary>Full-text value.</summary>
    Text,
    /// <summary>Binary value.</summary>
    Bytes,
    /// <summary>Timestamp value.</summary>
    Timestamp,
}

/// <summary>A named SQL binding before it is encoded in a request.</summary>
public sealed record TerraScaleSqlParameter(string Name, object? Value);

/// <summary>Canonical named/ordinal parameter object for SQL requests.</summary>
public sealed class TerraScaleSqlParameters : IReadOnlyDictionary<string, JsonElement>
{
    private readonly IReadOnlyDictionary<string, JsonElement> values;

    /// <summary>Creates named bindings from CLR values.</summary>
    public TerraScaleSqlParameters(IEnumerable<KeyValuePair<string, object?>> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var copy = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var pair in values)
        {
            TerraScaleSqlValidation.ValidateParameterName(pair.Key);
            if (!copy.TryAdd(pair.Key, ToJsonValue(pair.Value)))
            {
                throw new ArgumentException($"SQL parameter '{pair.Key}' is repeated.", nameof(values));
            }
        }

        this.values = new ReadOnlyDictionary<string, JsonElement>(copy);
    }

    /// <summary>Creates an empty binding object.</summary>
    public TerraScaleSqlParameters() : this(Array.Empty<KeyValuePair<string, object?>>()) { }

    /// <summary>Creates named bindings from parameter records.</summary>
    public static TerraScaleSqlParameters Named(IEnumerable<TerraScaleSqlParameter> values) =>
        new(values.Select(static value => new KeyValuePair<string, object?>(value.Name, value.Value)));

    /// <summary>Creates named bindings from key-value pairs.</summary>
    public static TerraScaleSqlParameters Named(IEnumerable<KeyValuePair<string, object?>> values) => new(values);

    /// <summary>Creates decimal-named bindings for ordinal SQL markers.</summary>
    public static TerraScaleSqlParameters Ordinal(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return Named(values.Select((value, index) =>
            new KeyValuePair<string, object?>((index + 1).ToString(CultureInfo.InvariantCulture), value)));
    }

    /// <summary>Creates decimal-named bindings for ordinal SQL markers.</summary>
    public static TerraScaleSqlParameters FromOrdinal(params object?[] values) => Ordinal(values);

    /// <summary>Returns a binding by canonical name.</summary>
    public JsonElement this[string key] => values[key];
    /// <summary>Returns canonical binding names.</summary>
    public IEnumerable<string> Keys => values.Keys;
    /// <summary>Returns canonical binding values.</summary>
    public IEnumerable<JsonElement> Values => values.Values;
    /// <summary>Returns the number of bindings.</summary>
    public int Count => values.Count;
    /// <summary>Checks for one binding.</summary>
    public bool ContainsKey(string key) => values.ContainsKey(key);
    /// <summary>Gets one binding when present.</summary>
    public bool TryGetValue(string key, out JsonElement value) => values.TryGetValue(key, out value);
    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, JsonElement>> GetEnumerator() => values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

    internal static JsonElement ToJsonValue(object? value)
    {
        if (value is JsonElement element) return element.Clone();
        try { return JsonSerializer.SerializeToElement(value, TerraScaleSqlJson.Options); }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new ArgumentException("SQL parameter values must be JSON values.", nameof(value), exception);
        }
    }
}

/// <summary>Metadata columns exposed on every SQL collection.</summary>
public static class TerraScaleSqlFields
{
    /// <summary>Stable document identifier.</summary>
    public static TerraScaleSqlField Id { get; } = new("_id", TerraScaleFieldType.String, true, true);
    /// <summary>Document revision.</summary>
    public static TerraScaleSqlField Revision { get; } = new("_rev", TerraScaleFieldType.String, true, true);
    /// <summary>Hybrid logical clock.</summary>
    public static TerraScaleSqlField Hlc { get; } = new("_hlc", TerraScaleFieldType.String, true, true);
    /// <summary>Creation timestamp.</summary>
    public static TerraScaleSqlField Created { get; } = new("_created", TerraScaleFieldType.Timestamp, true, true);
    /// <summary>Update timestamp.</summary>
    public static TerraScaleSqlField Updated { get; } = new("_updated", TerraScaleFieldType.Timestamp, true, true);
    /// <summary>Metadata descriptors in contract order.</summary>
    public static IReadOnlyList<TerraScaleSqlField> Metadata { get; } =
        new ReadOnlyCollection<TerraScaleSqlField>([Id, Revision, Hlc, Created, Updated]);
    internal static bool IsMetadata(string name) => Metadata.Any(field => field.Name == name);
}

/// <summary>Alias grouping the metadata descriptors for discoverability.</summary>
public static class TerraScaleSqlMetadataFields
{
    /// <inheritdoc cref="TerraScaleSqlFields.Id" />
    public static TerraScaleSqlField Id => TerraScaleSqlFields.Id;
    /// <inheritdoc cref="TerraScaleSqlFields.Revision" />
    public static TerraScaleSqlField Revision => TerraScaleSqlFields.Revision;
    /// <inheritdoc cref="TerraScaleSqlFields.Hlc" />
    public static TerraScaleSqlField Hlc => TerraScaleSqlFields.Hlc;
    /// <inheritdoc cref="TerraScaleSqlFields.Created" />
    public static TerraScaleSqlField Created => TerraScaleSqlFields.Created;
    /// <inheritdoc cref="TerraScaleSqlFields.Updated" />
    public static TerraScaleSqlField Updated => TerraScaleSqlFields.Updated;
    /// <inheritdoc cref="TerraScaleSqlFields.Metadata" />
    public static IReadOnlyList<TerraScaleSqlField> All => TerraScaleSqlFields.Metadata;
}

/// <summary>A field descriptor used by the schema-bound SQL builder.</summary>
public record TerraScaleSqlField(
    string Name,
    TerraScaleFieldType Type,
    bool Required = false,
    bool Indexed = false)
{
    /// <summary>Creates a field descriptor from the common schema type enum.</summary>
    public TerraScaleSqlField(
        string name,
        TerraScaleSqlFieldType type,
        bool required = false,
        bool indexed = false)
        : this(name, (TerraScaleFieldType)type, required, indexed)
    {
    }

    /// <summary>Adapts a generated schema field descriptor.</summary>
    public static implicit operator TerraScaleSqlField(TerraScaleFieldDefinition field) =>
        new(field.Name, field.Type, field.Required, field.Indexed);

    /// <summary>Creates a SQL field from a generated schema descriptor.</summary>
    public TerraScaleSqlField(TerraScaleFieldDefinition field)
        : this((TerraScaleSqlField)field)
    {
    }
}

/// <summary>A collection schema that can bind a typed SQL query.</summary>
public class TerraScaleSqlSchema
{
    /// <summary>Creates a collection schema from its declared fields.</summary>
    public TerraScaleSqlSchema(string name, IEnumerable<TerraScaleSqlField> fields)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        ArgumentNullException.ThrowIfNull(fields);
        Fields = new ReadOnlyCollection<TerraScaleSqlField>(fields.ToArray());
        TerraScaleSqlValidation.ValidateSchema(Name, Fields);
    }

    /// <summary>Creates a collection schema from its declared fields.</summary>
    public TerraScaleSqlSchema(string name, params TerraScaleSqlField[] fields)
        : this(name, (IEnumerable<TerraScaleSqlField>)fields)
    {
    }

    /// <summary>The collection name emitted in SQL.</summary>
    public string Name { get; }

    /// <summary>The immutable fields declared by this collection.</summary>
    public IReadOnlyList<TerraScaleSqlField> Fields { get; }
}

/// <summary>Sort direction for an SQL order key.</summary>
public enum TerraScaleSqlSortDirection
{
    /// <summary>Ascending order.</summary>
    Ascending,
    /// <summary>Descending order.</summary>
    Descending,
}

/// <summary>Short sort-direction aliases for fluent calls.</summary>
public enum TerraScaleSqlDirection
{
    /// <summary>Ascending order.</summary>
    Asc,
    /// <summary>Descending order.</summary>
    Desc,
}

/// <summary>Comparison operators accepted by the typed builder.</summary>
public enum TerraScaleSqlComparisonOperator
{
    /// <summary>Equal.</summary>
    Equal,
    /// <summary>Not equal.</summary>
    NotEqual,
    /// <summary>Greater than.</summary>
    GreaterThan,
    /// <summary>Greater than or equal.</summary>
    GreaterThanOrEqual,
    /// <summary>Less than.</summary>
    LessThan,
    /// <summary>Less than or equal.</summary>
    LessThanOrEqual,
}

/// <summary>A validated read-only SQL statement and its separately bound values.</summary>
public sealed class TerraScaleSqlQuery
{
    private TerraScaleSqlQuery(string sql, IReadOnlyList<object?> parameters, TerraScaleSqlParameters bindings)
    {
        Sql = sql;
        Parameters = parameters;
        Bindings = bindings;
    }

    /// <summary>Starts a schema-bound SELECT query.</summary>
    public static TerraScaleSqlSelectBuilder Select(TerraScaleSqlSchema schema) => TerraScaleSql.Select(schema);

    /// <summary>Starts a schema-bound SELECT query.</summary>
    public static TerraScaleSqlSelectBuilder Select(TerraScaleCollectionSchema schema) => TerraScaleSql.Select(schema);

    /// <summary>Creates and validates raw parameterized SQL.</summary>
    public static TerraScaleSqlQuery Raw(string sql, IEnumerable<object?>? parameters = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        var values = new ReadOnlyCollection<object?>((parameters ?? []).ToArray());
        TerraScaleSqlValidation.ValidateSql(sql, values);
        return new TerraScaleSqlQuery(sql, values, TerraScaleSqlParameters.Ordinal(values));
    }

    /// <summary>Creates and validates raw parameterized SQL.</summary>
    public static TerraScaleSqlQuery Raw(string sql, params object?[] parameters) => Raw(sql, (IEnumerable<object?>)parameters);

    /// <summary>Creates and validates SQL with named contract bindings.</summary>
    public static TerraScaleSqlQuery Raw(string sql, TerraScaleSqlParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(parameters);
        TerraScaleSqlValidation.ValidateNamedSql(sql, parameters);
        var values = new ReadOnlyCollection<object?>(parameters.Values.Select(static value => (object?)value.Clone()).ToArray());
        return new TerraScaleSqlQuery(sql, values, parameters);
    }

    /// <summary>Creates and validates SQL with named CLR bindings.</summary>
    public static TerraScaleSqlQuery Raw(string sql, IReadOnlyDictionary<string, object?> parameters) =>
        Raw(sql, TerraScaleSqlParameters.Named(parameters));

    /// <summary>Alias for <see cref="Raw(string, IEnumerable{object?})"/>.</summary>
    public static TerraScaleSqlQuery CreateRaw(string sql, IEnumerable<object?>? parameters = null) => Raw(sql, parameters);

    /// <summary>Attempts to create raw parameterized SQL without throwing.</summary>
    public static bool TryRaw(
        string sql,
        IEnumerable<object?>? parameters,
        out TerraScaleSqlQuery? query)
    {
        try
        {
            query = Raw(sql, parameters);
            return true;
        }
        catch (ArgumentException)
        {
            query = null;
            return false;
        }
    }


    /// <summary>The statement text, containing positional placeholders.</summary>
    public string Sql { get; }

    /// <summary>Values in positional placeholder order.</summary>
    public IReadOnlyList<object?> Parameters { get; }

    /// <summary>Alias for <see cref="Sql"/> used by transport adapters.</summary>
    public string Statement => Sql;

    /// <summary>Alias for <see cref="Parameters"/> used by transport adapters.</summary>
    public IReadOnlyList<object?> Params => Parameters;

    /// <summary>Alias for <see cref="Parameters"/> used by query adapters.</summary>
    public IReadOnlyList<object?> QueryParameters => Parameters;

    /// <summary>Canonical named bindings for the HTTP SQL contract.</summary>
    public TerraScaleSqlParameters Bindings { get; }

    /// <summary>Alias for <see cref="Bindings"/>.</summary>
    public TerraScaleSqlParameters NamedParameters => Bindings;

    /// <summary>Re-validates this statement before an adapter sends it.</summary>
    public void Validate() => TerraScaleSqlValidation.ValidateNamedSql(Sql, Bindings);

    /// <summary>Builds the descriptor for one execute operation.</summary>
    public TerraScaleSqlExecuteDescriptor ExecuteDescriptor() => new(this);

    /// <summary>Builds the descriptor for one live subscription.</summary>
    public TerraScaleSqlSubscribeDescriptor SubscribeDescriptor() => new(this);

    /// <summary>Alias for <see cref="ExecuteDescriptor"/>.</summary>
    public TerraScaleSqlExecuteDescriptor AsExecute() => ExecuteDescriptor();

    /// <summary>Alias for <see cref="SubscribeDescriptor"/>.</summary>
    public TerraScaleSqlSubscribeDescriptor AsSubscribe() => SubscribeDescriptor();

    /// <summary>Alias for <see cref="ExecuteDescriptor"/>.</summary>
    public TerraScaleSqlExecuteDescriptor ToExecuteDescriptor() => ExecuteDescriptor();

    /// <summary>Alias for <see cref="SubscribeDescriptor"/>.</summary>
    public TerraScaleSqlSubscribeDescriptor ToSubscribeDescriptor() => SubscribeDescriptor();

    /// <summary>Descriptor shortcut for an execute operation.</summary>
    public TerraScaleSqlExecuteDescriptor Execute => ExecuteDescriptor();

    /// <summary>Descriptor shortcut for a live subscription.</summary>
    public TerraScaleSqlSubscribeDescriptor Subscribe => SubscribeDescriptor();

    /// <summary>Returns the statement text.</summary>
    public override string ToString() => Sql;
}

/// <summary>Statement and parameters for an execute operation.</summary>
public sealed record TerraScaleSqlExecuteDescriptor(TerraScaleSqlQuery Query)
{
    /// <summary>The statement text.</summary>
    public string Sql => Query.Sql;

    /// <summary>The separately bound values.</summary>
    public IReadOnlyList<object?> Parameters => Query.Parameters;

    /// <summary>Alias for <see cref="Parameters"/>.</summary>
    public IReadOnlyList<object?> Params => Parameters;

    /// <summary>Alias for <see cref="Sql"/>.</summary>
    public string Statement => Sql;

    /// <summary>Canonical named bindings for the HTTP request.</summary>
    public TerraScaleSqlParameters Bindings => Query.Bindings;
}

/// <summary>Statement and parameters for a live subscription.</summary>
public sealed record TerraScaleSqlSubscribeDescriptor(TerraScaleSqlQuery Query)
{
    /// <summary>The statement text.</summary>
    public string Sql => Query.Sql;

    /// <summary>The separately bound values.</summary>
    public IReadOnlyList<object?> Parameters => Query.Parameters;

    /// <summary>Alias for <see cref="Parameters"/>.</summary>
    public IReadOnlyList<object?> Params => Parameters;

    /// <summary>Alias for <see cref="Sql"/>.</summary>
    public string Statement => Sql;

    /// <summary>Canonical named bindings for the HTTP request.</summary>
    public TerraScaleSqlParameters Bindings => Query.Bindings;
}

/// <summary>Options for one SQL execute request.</summary>
public sealed record TerraScaleSqlExecuteOptions
{
    /// <summary>Requests the server's lowered plan.</summary>
    public bool Explain { get; init; }
    /// <summary>Requests cancellation before execution.</summary>
    public bool Cancelled { get; init; }
    /// <summary>Optional server deadline, in milliseconds.</summary>
    public int? TimeoutMs { get; init; }
    /// <summary>Alias for <see cref="TimeoutMs"/>.</summary>
    [JsonIgnore]
    public int? TimeoutMilliseconds { get => TimeoutMs; init => TimeoutMs = value; }
    internal void Validate()
    {
        if (TimeoutMs is < 1 or > 30_000)
            throw new ArgumentOutOfRangeException(nameof(TimeoutMs), "SQL timeout must be between 1 and 30000 milliseconds.");
    }
}

/// <summary>One row in a SQL response.</summary>
public sealed record TerraScaleSqlRow(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("value")] JsonElement Value);

/// <summary>Result returned by the read-only SQL endpoint.</summary>
public sealed record TerraScaleSqlResult
{
    /// <summary>Creates an execute result or an explain response.</summary>
    [JsonConstructor]
    public TerraScaleSqlResult(
        long matched = 0,
        IReadOnlyList<TerraScaleSqlRow>? rows = null,
        string? dialect = null,
        string? collection = null,
        IReadOnlyList<TerraScaleSqlOrderKey>? order = null,
        string? access = null)
    {
        Matched = matched;
        Rows = new ReadOnlyCollection<TerraScaleSqlRow>((rows ?? []).ToArray());
        Dialect = dialect;
        Collection = collection;
        Order = new ReadOnlyCollection<TerraScaleSqlOrderKey>((order ?? []).ToArray());
        Access = access;
    }

    /// <summary>Number of matching documents.</summary>
    [JsonPropertyName("matched")]
    public long Matched { get; }

    /// <summary>Rows included in an execute response.</summary>
    [JsonPropertyName("rows")]
    public IReadOnlyList<TerraScaleSqlRow> Rows { get; }

    /// <summary>Dialect marker included by an explain response.</summary>
    [JsonPropertyName("dialect")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Dialect { get; }

    /// <summary>Collection identified by an explain response.</summary>
    [JsonPropertyName("collection")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Collection { get; }

    /// <summary>Ordering keys identified by an explain response.</summary>
    [JsonPropertyName("order")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<TerraScaleSqlOrderKey> Order { get; }

    /// <summary>Access strategy identified by an explain response.</summary>
    [JsonPropertyName("access")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Access { get; }

    /// <summary>Whether this response is an explain plan.</summary>
    [JsonIgnore]
    public bool IsExplain => Dialect is not null;

    /// <summary>Number of returned rows.</summary>
    public int Count => Rows.Count;
}

/// <summary>Ordering key in an explain-plan response.</summary>
public sealed record TerraScaleSqlOrderKey(
    [property: JsonPropertyName("field")] string Field,
    [property: JsonPropertyName("direction")] string Direction);

/// <summary>Client for <c>POST /api/v1/databases/{name}/sql</c>.</summary>
public sealed class TerraScaleSqlClient
{
    private readonly TerraScaleClient client;

    /// <summary>Creates a SQL client over a configured TerraScale client.</summary>
    public TerraScaleSqlClient(TerraScaleClient client) => this.client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Executes one validated query.</summary>
    public Task<TerraScaleSqlResult> ExecuteAsync(
        string databaseName,
        TerraScaleSqlQuery query,
        TerraScaleSqlExecuteOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ArgumentNullException.ThrowIfNull(query);
        var executeOptions = options ?? new TerraScaleSqlExecuteOptions();
        executeOptions.Validate();
        query.Validate();
        var request = new TerraScaleSqlRequest(
            query.Sql,
            query.Bindings.Count == 0 ? null : query.Bindings,
            executeOptions.Explain ? true : null,
            executeOptions.Cancelled ? true : null,
            executeOptions.TimeoutMs);
        // SqlResultResponse is a published oneOf (rows or explain plan), so
        // decode the envelope first and project either branch into the one
        // immutable SDK result type.
        return ExecuteResponseAsync(databaseName, request, cancellationToken);
    }

    private async Task<TerraScaleSqlResult> ExecuteResponseAsync(
        string databaseName,
        TerraScaleSqlRequest request,
        CancellationToken cancellationToken)
    {
        var payload = await client.SendJsonAsync<TerraScaleSqlRequest, JsonElement>(
            HttpMethod.Post,
            $"/api/v1/databases/{Uri.EscapeDataString(databaseName.Trim())}/sql",
            request,
            TerraScaleSqlJsonContext.Default.TerraScaleSqlRequest,
            TerraScaleSqlJsonContext.Default.JsonElement,
            cancellationToken);
        return ParseResponse(payload);
    }

    /// <summary>Executes SQL with named bindings.</summary>
    public Task<TerraScaleSqlResult> ExecuteAsync(
        string databaseName,
        string sql,
        TerraScaleSqlParameters? parameters = null,
        TerraScaleSqlExecuteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(databaseName, TerraScaleSqlQuery.Raw(sql, parameters ?? new TerraScaleSqlParameters()), options, cancellationToken);

    /// <summary>Executes SQL with ordinal bindings.</summary>
    public Task<TerraScaleSqlResult> ExecuteAsync(
        string databaseName,
        string sql,
        IEnumerable<object?> parameters,
        TerraScaleSqlExecuteOptions? options = null,
        CancellationToken cancellationToken = default) =>
        ExecuteAsync(databaseName, TerraScaleSqlQuery.Raw(sql, parameters), options, cancellationToken);

    private static TerraScaleSqlResult ParseResponse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new JsonException("TerraScale SQL responses must be JSON objects.");

        if (payload.TryGetProperty("matched", out _))
        {
            return payload.Deserialize(TerraScaleSqlJsonContext.Default.TerraScaleSqlResult)
                ?? throw new JsonException("TerraScale SQL result was empty.");
        }

        var explain = payload.Deserialize(TerraScaleSqlJsonContext.Default.TerraScaleSqlExplainResponse)
            ?? throw new JsonException("TerraScale SQL explain response was empty.");
        if (!string.Equals(explain.Dialect, "sql/sqlite-readonly", StringComparison.Ordinal) ||
            !string.Equals(explain.Access, "scan", StringComparison.Ordinal))
            throw new JsonException("TerraScale SQL explain response has an invalid contract marker.");
        return new TerraScaleSqlResult(
            dialect: explain.Dialect,
            collection: explain.Collection,
            order: explain.Order,
            access: explain.Access);
    }
}

/// <summary>Wire request for the SQL endpoint.</summary>
public sealed record TerraScaleSqlRequest(
    [property: JsonPropertyName("sql")] string Sql,
    [property: JsonPropertyName("parameters"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, JsonElement>? Parameters = null,
    [property: JsonPropertyName("explain"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Explain = null,
    [property: JsonPropertyName("cancelled"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? Cancelled = null,
    [property: JsonPropertyName("timeout_ms"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? TimeoutMs = null);

internal sealed record TerraScaleSqlExplainResponse(
    [property: JsonPropertyName("access")] string Access,
    [property: JsonPropertyName("collection")] string? Collection,
    [property: JsonPropertyName("dialect")] string Dialect,
    [property: JsonPropertyName("order")] IReadOnlyList<TerraScaleSqlOrderKey> Order);

/// <summary>Entry points for schema-bound SQL and raw SQL.</summary>
public static class TerraScaleSql
{
    /// <summary>Starts a schema-bound SELECT query.</summary>
    public static TerraScaleSqlSelectBuilder Select(TerraScaleSqlSchema schema) =>
        new(schema ?? throw new ArgumentNullException(nameof(schema)));

    /// <summary>Starts a schema-bound SELECT query.</summary>
    public static TerraScaleSqlSelectBuilder Select(TerraScaleCollectionSchema schema) =>
        new(schema ?? throw new ArgumentNullException(nameof(schema)));

    /// <summary>Creates and validates raw parameterized SQL.</summary>
    public static TerraScaleSqlQuery Raw(string sql, IEnumerable<object?>? parameters = null) => TerraScaleSqlQuery.Raw(sql, parameters);

    /// <summary>Creates and validates raw parameterized SQL.</summary>
    public static TerraScaleSqlQuery Raw(string sql, params object?[] parameters) => TerraScaleSqlQuery.Raw(sql, parameters);

    /// <summary>Creates SQL with named contract bindings.</summary>
    public static TerraScaleSqlQuery Raw(string sql, TerraScaleSqlParameters parameters) => TerraScaleSqlQuery.Raw(sql, parameters);

    /// <summary>Creates SQL with named CLR bindings.</summary>
    public static TerraScaleSqlQuery Raw(string sql, IReadOnlyDictionary<string, object?> parameters) => TerraScaleSqlQuery.Raw(sql, parameters);

    /// <summary>Builds an execute descriptor without selecting a transport route.</summary>
    public static TerraScaleSqlExecuteDescriptor Execute(TerraScaleSqlQuery query) =>
        (query ?? throw new ArgumentNullException(nameof(query))).ExecuteDescriptor();

    /// <summary>Builds a subscription descriptor without selecting a transport route.</summary>
    public static TerraScaleSqlSubscribeDescriptor Subscribe(TerraScaleSqlQuery query) =>
        (query ?? throw new ArgumentNullException(nameof(query))).SubscribeDescriptor();

    /// <summary>Attempts to create raw parameterized SQL without throwing.</summary>
    public static bool TryRaw(string sql, IEnumerable<object?>? parameters, out TerraScaleSqlQuery? query) =>
        TerraScaleSqlQuery.TryRaw(sql, parameters, out query);


    /// <summary>Validates raw read-only SQL and its positional bindings.</summary>
    public static void Validate(string sql, IEnumerable<object?>? parameters = null) =>
        TerraScaleSqlValidation.ValidateSql(sql, new ReadOnlyCollection<object?>((parameters ?? []).ToArray()));

    /// <summary>Validates SQL with named contract bindings.</summary>
    public static void Validate(string sql, TerraScaleSqlParameters parameters) => TerraScaleSqlValidation.ValidateNamedSql(sql, parameters);
}

/// <summary>Public entry point for validating raw read-only SQL.</summary>
public static class TerraScaleSqlValidator
{
    /// <summary>Validates SQL text and its positional bindings.</summary>
    public static void Validate(string sql, IEnumerable<object?>? parameters = null) => TerraScaleSql.Validate(sql, parameters);

    /// <summary>Validates SQL with named contract bindings.</summary>
    public static void Validate(string sql, TerraScaleSqlParameters parameters) => TerraScaleSql.Validate(sql, parameters);

    /// <summary>Attempts named SQL validation without throwing.</summary>
    public static bool TryValidate(string sql, TerraScaleSqlParameters parameters)
    {
        try
        {
            Validate(sql, parameters);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Attempts validation without throwing.</summary>
    public static bool TryValidate(string sql, IEnumerable<object?>? parameters = null)
    {
        try
        {
            Validate(sql, parameters);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>Fluent schema-bound SELECT builder.</summary>
public sealed class TerraScaleSqlSelectBuilder
{
    private readonly TerraScaleSqlSchema schema;
    private readonly List<string> columns = ["*"];
    private readonly List<string> predicates = [];
    private readonly List<(string Name, TerraScaleSqlSortDirection Direction)> order = [];
    private readonly List<object?> parameters = [];
    private int? limit;
    private int? offset;

    internal TerraScaleSqlSelectBuilder(TerraScaleSqlSchema schema)
    {
        this.schema = schema;
    }

    internal TerraScaleSqlSelectBuilder(TerraScaleCollectionSchema schema)
    {
        this.schema = new TerraScaleSqlSchema(
            schema.Name,
            schema.Fields.Select(static field => (TerraScaleSqlField)field));
    }

    /// <summary>Selects one declared field.</summary>
    public TerraScaleSqlSelectBuilder Column(TerraScaleSqlField field)
    {
        var name = CheckedField(field, "column");
        if (columns is ["*"])
        {
            columns.Clear();
        }

        columns.Add(name);
        return this;
    }

    /// <summary>Selects several declared fields.</summary>
    public TerraScaleSqlSelectBuilder Columns(params TerraScaleSqlField[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        foreach (var field in fields)
        {
            Column(field);
        }

        return this;
    }

    /// <summary>Adds an equality comparison.</summary>
    public TerraScaleSqlSelectBuilder WhereEquals(TerraScaleSqlField field, object? value) => WhereComparison(field, "=", value);

    /// <summary>Adds an inequality comparison.</summary>
    public TerraScaleSqlSelectBuilder WhereNotEquals(TerraScaleSqlField field, object? value) => WhereComparison(field, "<>", value);

    /// <summary>Adds a greater-than comparison.</summary>
    public TerraScaleSqlSelectBuilder WhereGreaterThan(TerraScaleSqlField field, object? value) => WhereComparison(field, ">", value);

    /// <summary>Adds a greater-than-or-equal comparison.</summary>
    public TerraScaleSqlSelectBuilder WhereGreaterThanOrEqual(TerraScaleSqlField field, object? value) => WhereComparison(field, ">=", value);

    /// <summary>Adds a less-than comparison.</summary>
    public TerraScaleSqlSelectBuilder WhereLessThan(TerraScaleSqlField field, object? value) => WhereComparison(field, "<", value);

    /// <summary>Adds a less-than-or-equal comparison.</summary>
    public TerraScaleSqlSelectBuilder WhereLessThanOrEqual(TerraScaleSqlField field, object? value) => WhereComparison(field, "<=", value);

    /// <summary>Short alias for <see cref="WhereEquals"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereEq(TerraScaleSqlField field, object? value) => WhereEquals(field, value);

    /// <summary>Short alias for <see cref="WhereNotEquals"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereNe(TerraScaleSqlField field, object? value) => WhereNotEquals(field, value);

    /// <summary>Alias for <see cref="WhereNotEquals"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereNotEqual(TerraScaleSqlField field, object? value) => WhereNotEquals(field, value);

    /// <summary>Short alias for <see cref="WhereGreaterThan"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereGt(TerraScaleSqlField field, object? value) => WhereGreaterThan(field, value);

    /// <summary>Short alias for <see cref="WhereGreaterThanOrEqual"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereGte(TerraScaleSqlField field, object? value) => WhereGreaterThanOrEqual(field, value);

    /// <summary>Short alias for <see cref="WhereLessThan"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereLt(TerraScaleSqlField field, object? value) => WhereLessThan(field, value);

    /// <summary>Short alias for <see cref="WhereLessThanOrEqual"/>.</summary>
    public TerraScaleSqlSelectBuilder WhereLte(TerraScaleSqlField field, object? value) => WhereLessThanOrEqual(field, value);

    /// <summary>Adds a comparison selected from the SQL comparison enum.</summary>
    public TerraScaleSqlSelectBuilder Where(
        TerraScaleSqlField field,
        TerraScaleSqlComparisonOperator comparison,
        object? value) =>
        WhereComparison(field, comparison switch
        {
            TerraScaleSqlComparisonOperator.Equal => "=",
            TerraScaleSqlComparisonOperator.NotEqual => "<>",
            TerraScaleSqlComparisonOperator.GreaterThan => ">",
            TerraScaleSqlComparisonOperator.GreaterThanOrEqual => ">=",
            TerraScaleSqlComparisonOperator.LessThan => "<",
            TerraScaleSqlComparisonOperator.LessThanOrEqual => "<=",
            _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
        }, value);

    /// <summary>Adds an FTS5 MATCH comparison on a string or text field.</summary>
    public TerraScaleSqlSelectBuilder WhereMatch(TerraScaleSqlField field, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var checkedField = CheckedField(field, "match");
        if (field.Type is not (TerraScaleFieldType.String or TerraScaleFieldType.Text))
        {
            throw new ArgumentException("FTS5 MATCH requires a string or text field.", nameof(field));
        }

        predicates.Add($"{checkedField} MATCH {AddParameter(query)}");
        return this;
    }

    /// <summary>Alias for <see cref="WhereMatch"/>.</summary>
    public TerraScaleSqlSelectBuilder Match(TerraScaleSqlField field, string query) => WhereMatch(field, query);

    /// <summary>Adds a comparison against a JSON value in the document body.</summary>
    public TerraScaleSqlSelectBuilder WhereJsonEquals(string jsonPath, object? value) =>
        WhereJsonComparison(jsonPath, "=", value);

    /// <summary>Adds a comparison against a JSON value in the document body.</summary>
    public TerraScaleSqlSelectBuilder WhereJsonNotEquals(string jsonPath, object? value) =>
        WhereJsonComparison(jsonPath, "<>", value);

    /// <summary>Adds a greater-than comparison against a JSON value.</summary>
    public TerraScaleSqlSelectBuilder WhereJsonGreaterThan(string jsonPath, object? value) =>
        WhereJsonComparison(jsonPath, ">", value);

    /// <summary>Adds a less-than comparison against a JSON value.</summary>
    public TerraScaleSqlSelectBuilder WhereJsonLessThan(string jsonPath, object? value) =>
        WhereJsonComparison(jsonPath, "<", value);

    /// <summary>Adds a comparison against a JSON value in the document body.</summary>
    public TerraScaleSqlSelectBuilder WhereJsonPathEquals(string jsonPath, object? value) =>
        WhereJsonEquals(jsonPath, value);

    /// <summary>Adds an order key for a declared field.</summary>
    public TerraScaleSqlSelectBuilder OrderBy(
        TerraScaleSqlField field,
        TerraScaleSqlSortDirection direction = TerraScaleSqlSortDirection.Ascending)
    {
        order.Add((CheckedField(field, "order_by"), direction));
        return this;
    }

    /// <summary>Adds an order key for a declared field.</summary>
    public TerraScaleSqlSelectBuilder OrderBy(
        TerraScaleSqlField field,
        TerraScaleSqlDirection direction) =>
        OrderBy(field, direction == TerraScaleSqlDirection.Desc
            ? TerraScaleSqlSortDirection.Descending
            : TerraScaleSqlSortDirection.Ascending);

    /// <summary>Alias for ordering a declared field.</summary>
    public TerraScaleSqlSelectBuilder OrderByField(
        TerraScaleSqlField field,
        TerraScaleSqlSortDirection direction = TerraScaleSqlSortDirection.Ascending) => OrderBy(field, direction);

    /// <summary>Adds an order key for a JSON path.</summary>
    public TerraScaleSqlSelectBuilder OrderByJson(
        string jsonPath,
        TerraScaleSqlSortDirection direction = TerraScaleSqlSortDirection.Ascending)
    {
        var path = TerraScaleSqlValidation.ValidateJsonPath(jsonPath);
        order.Add(($"body ->> '{path}'", direction));
        return this;
    }

    /// <summary>Sets a maximum row count. The value is bound as a parameter.</summary>
    public TerraScaleSqlSelectBuilder Limit(int value)
    {
        if (value is < 1 or > TerraScaleSqlValidation.MaxPageLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(value), $"LIMIT must be between 1 and {TerraScaleSqlValidation.MaxPageLimit}.");
        }

        limit = value;
        return this;
    }

    /// <summary>Sets a row offset. The value is bound as a parameter.</summary>
    public TerraScaleSqlSelectBuilder Offset(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "OFFSET cannot be negative.");
        }

        offset = value;
        return this;
    }

    /// <summary>Sets page number and page size, using zero-based page numbering.</summary>
    public TerraScaleSqlSelectBuilder Page(int page, int pageSize)
    {
        if (page < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(page));
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize));
        }

        var calculatedOffset = (long)page * pageSize;
        if (calculatedOffset > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "The page offset is too large.");
        }

        Limit(pageSize);
        Offset((int)calculatedOffset);
        return this;
    }

    /// <summary>Alias for <see cref="Page"/>.</summary>
    public TerraScaleSqlSelectBuilder Paginate(int page, int pageSize) => Page(page, pageSize);

    /// <summary>Alias for <see cref="Limit"/>.</summary>
    public TerraScaleSqlSelectBuilder Take(int value) => Limit(value);

    /// <summary>Alias for <see cref="Offset"/>.</summary>
    public TerraScaleSqlSelectBuilder Skip(int value) => Offset(value);

    /// <summary>Renders and validates the parameterized SELECT.</summary>
    public TerraScaleSqlQuery Build()
    {
        var sql = new StringBuilder($"SELECT {string.Join(", ", columns)} FROM {schema.Name}");
        if (predicates.Count > 0)
        {
            sql.Append(" WHERE ").Append(string.Join(" AND ", predicates));
        }

        if (order.Count > 0)
        {
            sql.Append(" ORDER BY ").Append(string.Join(", ", order.Select(static key =>
                $"{key.Name} {(key.Direction == TerraScaleSqlSortDirection.Descending ? "DESC" : "ASC")}")));
        }

        var finalParameters = new List<object?>(parameters);
        if (limit.HasValue)
        {
            sql.Append(" LIMIT $").Append(finalParameters.Count + 1);
            finalParameters.Add(limit.Value);
        }

        if (offset.HasValue)
        {
            sql.Append(" OFFSET $").Append(finalParameters.Count + 1);
            finalParameters.Add(offset.Value);
        }

        return TerraScaleSqlQuery.Raw(sql.ToString(), finalParameters);
    }

    /// <summary>Builds the execute descriptor for this query.</summary>
    public TerraScaleSqlExecuteDescriptor ExecuteDescriptor() => Build().ExecuteDescriptor();

    /// <summary>Builds the subscription descriptor for this query.</summary>
    public TerraScaleSqlSubscribeDescriptor SubscribeDescriptor() => Build().SubscribeDescriptor();

    /// <summary>Alias for <see cref="ExecuteDescriptor"/>.</summary>
    public TerraScaleSqlExecuteDescriptor ToExecuteDescriptor() => ExecuteDescriptor();

    /// <summary>Alias for <see cref="SubscribeDescriptor"/>.</summary>
    public TerraScaleSqlSubscribeDescriptor ToSubscribeDescriptor() => SubscribeDescriptor();

    private TerraScaleSqlSelectBuilder WhereComparison(TerraScaleSqlField field, string op, object? value)
    {
        predicates.Add($"{CheckedField(field, "predicate")} {op} {AddParameter(value)}");
        return this;
    }

    private TerraScaleSqlSelectBuilder WhereJsonComparison(string jsonPath, string op, object? value)
    {
        var path = TerraScaleSqlValidation.ValidateJsonPath(jsonPath);
        predicates.Add($"body ->> '{path}' {op} {AddParameter(value)}");
        return this;
    }

    private string AddParameter(object? value)
    {
        parameters.Add(value);
        return $"${parameters.Count.ToString(CultureInfo.InvariantCulture)}";
    }

    private string CheckedField(TerraScaleSqlField field, string context)
    {
        ArgumentNullException.ThrowIfNull(field);
        TerraScaleSqlValidation.ValidateIdentifier(field.Name, context);
        if (!schema.Fields.Any(declared => declared == field) && !TerraScaleSqlFields.IsMetadata(field.Name))
        {
            throw new ArgumentException($"The {context} field is not declared by this collection.", nameof(field));
        }

        return field.Name;
    }
}

internal static class TerraScaleSqlValidation
{
    internal const int MaxSqlTextBytes = 16 * 1024;
    internal const int MaxPageLimit = 1024;
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "attach", "alter", "analyze", "begin", "commit", "create", "delete", "detach", "drop", "end",
        "insert", "pragma", "reindex", "replace", "rollback", "savepoint", "transaction", "truncate",
        "update", "vacuum", "upsert",
    };

    private static readonly HashSet<string> UnsafeFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "random", "randomblob", "changes", "total_changes", "last_insert_rowid", "load_extension", "readfile",
        "writefile", "datetime", "date", "time", "strftime", "julianday", "unixepoch",
    };

    internal static void ValidateSchema(string name, IReadOnlyList<TerraScaleSqlField> fields)
    {
        ValidateIdentifier(name, "collection");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            ValidateIdentifier(field.Name, "field");
            if (!seen.Add(field.Name))
            {
                throw new ArgumentException($"The schema declares field '{field.Name}' more than once.", nameof(fields));
            }
        }
    }

    internal static void ValidateIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Skip(1).Any(static character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException(
                $"{parameterName} must match [A-Za-z_][A-Za-z0-9_]*.",
                parameterName);
        }
    }

    internal static void ValidateParameterName(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 255 ||
            !(char.IsAsciiLetter(value[0]) || char.IsAsciiDigit(value[0]) || value[0] == '_') ||
            value.Skip(1).Any(static character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new ArgumentException("SQL parameter names must be plain identifiers or decimal ordinals.", nameof(value));
        }
    }

    internal static string ValidateJsonPath(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var path = value.Trim();
        if (!path.StartsWith("$.", StringComparison.Ordinal) && path != "$")
        {
            throw new ArgumentException("JSON paths must start with '$.' or be '$'.", nameof(value));
        }

        if (path.Any(static character => char.IsWhiteSpace(character) || character is '\'' or '"' or ';'))
        {
            throw new ArgumentException("JSON paths cannot contain whitespace, quotes, or semicolons.", nameof(value));
        }

        return path.Replace("'", "''", StringComparison.Ordinal);
    }

    internal static void ValidateSql(string text, IReadOnlyList<object?> parameters)
    {
        var tokens = ValidateSqlShape(text);
        ValidateBindings(tokens, parameters.Count);
        ValidateLiteralPagingBounds(tokens);
    }

    internal static void ValidateNamedSql(string text, TerraScaleSqlParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var tokens = ValidateSqlShape(text);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var next = 1;
        foreach (var token in tokens)
        {
            string? name = null;
            if (token == "?")
            {
                while (parameters.ContainsKey(next.ToString(CultureInfo.InvariantCulture))) next++;
                name = next++.ToString(CultureInfo.InvariantCulture);
            }
            else if (token.StartsWith("?") && token.Length > 1)
            {
                name = token[1..];
                if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
                    throw SqlError("Only ? or numbered ?n parameters are allowed.");
            }
            else if (token.StartsWith("$") || token.StartsWith(":") || token.StartsWith("@"))
            {
                name = token[1..];
            }
            if (name is null) continue;
            ValidateParameterName(name);
            if (!parameters.ContainsKey(name)) throw SqlError($"SQL parameter '{name}' was not supplied.");
            used.Add(name);
        }
        foreach (var name in parameters.Keys)
            if (!used.Contains(name)) throw SqlError($"SQL parameter '{name}' is not used by the statement.");
        ValidateLiteralPagingBounds(tokens);
    }

    private static void ValidateLiteralPagingBounds(IReadOnlyList<string> tokens)
    {
        for (var index = 0; index + 1 < tokens.Count; index++)
        {
            if (!tokens[index].Equals("limit", StringComparison.OrdinalIgnoreCase) &&
                !tokens[index].Equals("offset", StringComparison.OrdinalIgnoreCase)) continue;
            var value = tokens[index + 1];
            if (value == "-") throw SqlError("LIMIT and OFFSET cannot be negative.");
            if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) continue;
            if (number < 0 || (tokens[index].Equals("limit", StringComparison.OrdinalIgnoreCase) &&
                (number == 0 || number > MaxPageLimit)))
                throw SqlError($"{tokens[index].ToUpperInvariant()} is outside the supported bounds.");
        }
    }

    private static List<string> ValidateSqlShape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > MaxSqlTextBytes)
        {
            throw SqlError($"SQL exceeds the {MaxSqlTextBytes} byte query text limit.");
        }
        var tokens = Tokenize(text);
        if (tokens.Count == 0)
        {
            throw SqlError("SQL must not be empty.");
        }

        var first = tokens[0];
        var bodyStart = 1;
        if (first.Equals("with", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count > 1 && tokens[1].Equals("recursive", StringComparison.OrdinalIgnoreCase))
            {
                throw SqlError("WITH RECURSIVE is not allowed by the read-only SQL contract.");
            }
        }
        else if (first.Equals("explain", StringComparison.OrdinalIgnoreCase))
        {
            if (tokens.Count > 1 && tokens[1].Equals("query", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Count < 3 || !tokens[2].Equals("plan", StringComparison.OrdinalIgnoreCase))
                {
                    throw SqlError("EXPLAIN may only use EXPLAIN QUERY PLAN or EXPLAIN SELECT.");
                }

                bodyStart = 3;
            }

            if (tokens.Count <= bodyStart ||
                (!tokens[bodyStart].Equals("select", StringComparison.OrdinalIgnoreCase) &&
                 !tokens[bodyStart].Equals("with", StringComparison.OrdinalIgnoreCase)))
            {
                throw SqlError("Only EXPLAIN SELECT and EXPLAIN QUERY PLAN SELECT are allowed.");
            }
        }
        else if (!first.Equals("select", StringComparison.OrdinalIgnoreCase))
        {
            throw SqlError("Only SELECT, WITH, and EXPLAIN statements are allowed.");
        }

        for (var index = bodyStart; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (Forbidden.Contains(token))
            {
                throw SqlError("SQL contains a DDL, DML, or transaction keyword.");
            }

            if (token.Equals("current_date", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("current_time", StringComparison.OrdinalIgnoreCase) ||
                token.Equals("current_timestamp", StringComparison.OrdinalIgnoreCase))
            {
                throw SqlError("Non-deterministic or unsafe SQL functions are not allowed.");
            }

            if ((UnsafeFunctions.Contains(token) && index + 1 < tokens.Count && tokens[index + 1] == "(") ||
                token.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase) ||
                token.StartsWith("pragma_", StringComparison.OrdinalIgnoreCase))
            {
                throw SqlError("Non-deterministic or unsafe SQL functions are not allowed.");
            }
        }

        return tokens;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        for (var index = 0; index < text.Length;)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (current == ';')
            {
                if (text[(index + 1)..].Any(static character => !char.IsWhiteSpace(character)))
                {
                    throw SqlError("Multiple SQL statements are not allowed.");
                }

                index++;
                continue;
            }

            if ((current == '-' && index + 1 < text.Length && text[index + 1] == '-') ||
                (current == '/' && index + 1 < text.Length && text[index + 1] == '*'))
            {
                throw SqlError("SQL comments are not allowed.");
            }

            if (current is '\'' or '"' or '`' or '[')
            {
                var closing = current == '[' ? ']' : current;
                var closed = false;
                index++;
                while (index < text.Length)
                {
                    if (text[index] == closing)
                    {
                        if (index + 1 < text.Length && text[index + 1] == closing && closing != ']')
                        {
                            index += 2;
                        }
                        else
                        {
                            index++;
                            closed = true;
                            break;
                        }
                    }
                    else
                    {
                        index++;
                    }
                }

                if (!closed)
                {
                    throw SqlError("SQL contains an unterminated quoted value.");
                }

                tokens.Add("<quoted>");
                continue;
            }

            if (current is ':' or '@' or '$' or '?')
            {
                var start = index++;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }

                if (index == start + 1 && current is ('$' or ':' or '@'))
                {
                    throw SqlError("SQL parameter markers must name a binding.");
                }
                if (current == '?' && index > start + 1 && text[(start + 1)..index].Any(static character => !char.IsAsciiDigit(character)))
                {
                    throw SqlError("Only ? or numbered ?n parameters are allowed.");
                }

                tokens.Add(text[start..index]);
                continue;
            }

            if (char.IsAsciiLetterOrDigit(current) || current == '_')
            {
                var start = index++;
                while (index < text.Length && (char.IsAsciiLetterOrDigit(text[index]) || text[index] == '_'))
                {
                    index++;
                }

                tokens.Add(text[start..index]);
                continue;
            }

            tokens.Add(current.ToString());
            index++;
        }

        return tokens;
    }

    private static void ValidateBindings(IReadOnlyList<string> tokens, int parameterCount)
    {
        var next = 1;
        var highest = 0;
        var seen = new HashSet<int>();
        foreach (var token in tokens)
        {
            int number;
            if (token.StartsWith('$'))
            {
                if (!int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out number))
                {
                    throw SqlError("Invalid $n parameter.");
                }
            }
            else if (token == "?")
            {
                number = next++;
            }
            else if (token.StartsWith('?'))
            {
                if (!int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out number))
                {
                    throw SqlError("Invalid ?n parameter.");
                }
            }
            else
            {
                if (token.StartsWith(":") || token.StartsWith("@"))
                    throw SqlError("Named SQL parameters require a TerraScaleSqlParameters object.");
                continue;
            }

            if (number <= 0)
            {
                throw SqlError("Parameter numbering starts at 1.");
            }

            seen.Add(number);
            highest = Math.Max(highest, number);
        }

        if (highest != parameterCount || seen.Count != parameterCount)
        {
            throw SqlError($"SQL has {highest} positional parameter slots but {parameterCount} values were supplied.");
        }
    }

    private static ArgumentException SqlError(string message) => new(message, "sql");
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(TerraScaleSqlRequest))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(TerraScaleSqlResult))]
[JsonSerializable(typeof(TerraScaleSqlRow))]
[JsonSerializable(typeof(TerraScaleSqlOrderKey))]
[JsonSerializable(typeof(TerraScaleSqlExplainResponse))]
internal partial class TerraScaleSqlJsonContext : JsonSerializerContext;

internal static class TerraScaleSqlJson
{
    internal static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
