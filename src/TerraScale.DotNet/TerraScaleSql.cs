using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

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
    private TerraScaleSqlQuery(string sql, IReadOnlyList<object?> parameters)
    {
        Sql = sql;
        Parameters = parameters;
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
        return new TerraScaleSqlQuery(sql, values);
    }

    /// <summary>Creates and validates raw parameterized SQL.</summary>
    public static TerraScaleSqlQuery Raw(string sql, params object?[] parameters) => Raw(sql, (IEnumerable<object?>)parameters);

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

    /// <summary>Re-validates this statement before an adapter sends it.</summary>
    public void Validate() => TerraScaleSqlValidation.ValidateSql(Sql, Parameters);

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
}

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
}

/// <summary>Public entry point for validating raw read-only SQL.</summary>
public static class TerraScaleSqlValidator
{
    /// <summary>Validates SQL text and its positional bindings.</summary>
    public static void Validate(string sql, IEnumerable<object?>? parameters = null) => TerraScaleSql.Validate(sql, parameters);

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
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), "LIMIT cannot be negative.");
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
        if (!schema.Fields.Any(declared => declared == field))
        {
            throw new ArgumentException($"The {context} field is not declared by this collection.", nameof(field));
        }

        return field.Name;
    }
}

internal static class TerraScaleSqlValidation
{
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
        if (Encoding.UTF8.GetByteCount(text) > 1_048_576)
        {
            throw SqlError("SQL exceeds the 1 MiB query text limit.");
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

        ValidateBindings(tokens, parameters.Count);
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

            if (current == '$' || current == '?')
            {
                var start = index++;
                while (index < text.Length && char.IsAsciiDigit(text[index]))
                {
                    index++;
                }

                if (index == start + 1 && current == '$')
                {
                    throw SqlError("Only numbered $n parameters are allowed.");
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
