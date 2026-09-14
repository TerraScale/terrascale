using System.Text.Json;
using System.Text.Json.Nodes;

namespace TerraBase.Schema;

/// <summary>The supported deterministic document migration operations.</summary>
public enum MigrationOperationKind
{
    /// <summary>Add a field when it is absent.</summary>
    AddField,

    /// <summary>Remove a field when it is present.</summary>
    RemoveField,

    /// <summary>Rename a field while preserving its value.</summary>
    RenameField,

    /// <summary>Set a field's value when it is absent.</summary>
    SetDefault,
}

/// <summary>One bounded migration operation.</summary>
public sealed class MigrationOperation
{
    private MigrationOperation(MigrationOperationKind kind, string field, string? targetField, JsonElement? value)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentException("A migration field is required.", nameof(field));
        }

        if (kind is MigrationOperationKind.RenameField && string.IsNullOrWhiteSpace(targetField))
        {
            throw new ArgumentException("A rename operation requires a target field.", nameof(targetField));
        }

        Kind = kind;
        Field = field;
        TargetField = targetField;
        Value = value?.Clone();
    }

    /// <summary>The operation kind.</summary>
    public MigrationOperationKind Kind { get; }

    /// <summary>The source or destination field.</summary>
    public string Field { get; }

    /// <summary>The rename destination, when applicable.</summary>
    public string? TargetField { get; }

    /// <summary>The default JSON value, when applicable.</summary>
    public JsonElement? Value { get; }

    /// <summary>Creates an add-field operation.</summary>
    public static MigrationOperation AddField(string field, JsonElement? defaultValue = null) => new(MigrationOperationKind.AddField, field, null, defaultValue);

    /// <summary>Creates a remove-field operation.</summary>
    public static MigrationOperation RemoveField(string field) => new(MigrationOperationKind.RemoveField, field, null, null);

    /// <summary>Creates a rename-field operation.</summary>
    public static MigrationOperation RenameField(string field, string targetField) => new(MigrationOperationKind.RenameField, field, targetField, null);

    /// <summary>Creates a set-default operation.</summary>
    public static MigrationOperation SetDefault(string field, JsonElement value) => new(MigrationOperationKind.SetDefault, field, null, value);
}

/// <summary>The result of applying a schema migration to one document.</summary>
public sealed class SchemaMigrationResult
{
    private SchemaMigrationResult(bool succeeded, JsonElement? document, IReadOnlyList<string> errors, ulong fromVersion, ulong toVersion)
    {
        Succeeded = succeeded;
        Document = document;
        Errors = errors;
        FromVersion = fromVersion;
        ToVersion = toVersion;
    }

    /// <summary>Whether all operations completed.</summary>
    public bool Succeeded { get; }

    /// <summary>The transformed document when successful.</summary>
    public JsonElement? Document { get; }

    /// <summary>Content-safe migration errors.</summary>
    public IReadOnlyList<string> Errors { get; }

    /// <summary>The source schema version.</summary>
    public ulong FromVersion { get; }

    /// <summary>The target schema version.</summary>
    public ulong ToVersion { get; }

    internal static SchemaMigrationResult Success(JsonElement document, ulong fromVersion, ulong toVersion) => new(true, document.Clone(), [], fromVersion, toVersion);

    internal static SchemaMigrationResult Failure(IEnumerable<string> errors, ulong fromVersion, ulong toVersion) => new(false, null, errors.ToArray(), fromVersion, toVersion);
}

/// <summary>A versioned, deterministic migration between two schema versions.</summary>
public sealed class SchemaMigration
{
    /// <summary>Creates a migration.</summary>
    public SchemaMigration(ulong fromVersion, ulong toVersion, IEnumerable<MigrationOperation> operations, string? name = null)
    {
        if (fromVersion == 0 || toVersion == 0 || toVersion <= fromVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(toVersion), "Migration versions must be positive and strictly increase.");
        }

        FromVersion = fromVersion;
        ToVersion = toVersion;
        Operations = (operations ?? throw new ArgumentNullException(nameof(operations))).ToArray();
        Name = name ?? $"{fromVersion}-{toVersion}";
    }

    /// <summary>The source version.</summary>
    public ulong FromVersion { get; }

    /// <summary>The target version.</summary>
    public ulong ToVersion { get; }

    /// <summary>The stable optional migration name.</summary>
    public string Name { get; }

    /// <summary>The ordered operations.</summary>
    public IReadOnlyList<MigrationOperation> Operations { get; }

    /// <summary>Applies this migration without mutating the source JSON.</summary>
    public SchemaMigrationResult Apply(JsonElement document)
    {
        if (document.ValueKind != JsonValueKind.Object)
        {
            return SchemaMigrationResult.Failure(["The document must be a JSON object."], FromVersion, ToVersion);
        }

        var node = JsonNode.Parse(document.GetRawText())?.AsObject();
        if (node is null)
        {
            return SchemaMigrationResult.Failure(["The document must be a JSON object."], FromVersion, ToVersion);
        }

        var errors = new List<string>();
        foreach (var operation in Operations)
        {
            try
            {
                ApplyOperation(node, operation);
            }
            catch (InvalidOperationException exception)
            {
                errors.Add(exception.Message);
                break;
            }
        }

        if (errors.Count > 0)
        {
            return SchemaMigrationResult.Failure(errors, FromVersion, ToVersion);
        }

        using var parsed = JsonDocument.Parse(node.ToJsonString());
        return SchemaMigrationResult.Success(parsed.RootElement, FromVersion, ToVersion);
    }

    /// <summary>Creates a fluent migration builder.</summary>
    public static SchemaMigrationBuilder From(ulong fromVersion, ulong toVersion) => new(fromVersion, toVersion);

    private static void ApplyOperation(JsonObject document, MigrationOperation operation)
    {
        switch (operation.Kind)
        {
            case MigrationOperationKind.AddField:
                if (!document.ContainsKey(operation.Field))
                {
                    document[operation.Field] = operation.Value is { } value ? JsonNode.Parse(value.Value.GetRawText()) : null;
                }

                break;
            case MigrationOperationKind.RemoveField:
                document.Remove(operation.Field);
                break;
            case MigrationOperationKind.RenameField:
                if (document.ContainsKey(operation.TargetField!))
                {
                    throw new InvalidOperationException("A migration rename target already exists.");
                }

                if (document.Remove(operation.Field, out var existing))
                {
                    document[operation.TargetField!] = existing;
                }

                break;
            case MigrationOperationKind.SetDefault:
                if (!document.ContainsKey(operation.Field))
                {
                    document[operation.Field] = JsonNode.Parse(operation.Value!.Value.GetRawText());
                }

                break;
            default:
                throw new InvalidOperationException("The migration operation is unsupported.");
        }
    }
}

/// <summary>Fluent builder for one versioned migration.</summary>
public sealed class SchemaMigrationBuilder
{
    private readonly ulong fromVersion;
    private readonly ulong toVersion;
    private readonly List<MigrationOperation> operations = [];
    private string? name;

    internal SchemaMigrationBuilder(ulong fromVersion, ulong toVersion)
    {
        this.fromVersion = fromVersion;
        this.toVersion = toVersion;
    }

    /// <summary>Sets the stable migration name.</summary>
    public SchemaMigrationBuilder Named(string migrationName)
    {
        name = string.IsNullOrWhiteSpace(migrationName) ? throw new ArgumentException("A migration name is required.", nameof(migrationName)) : migrationName;
        return this;
    }

    /// <summary>Adds a field operation.</summary>
    public SchemaMigrationBuilder AddField(string field, JsonElement? defaultValue = null)
    {
        operations.Add(MigrationOperation.AddField(field, defaultValue));
        return this;
    }

    /// <summary>Adds a remove-field operation.</summary>
    public SchemaMigrationBuilder RemoveField(string field)
    {
        operations.Add(MigrationOperation.RemoveField(field));
        return this;
    }

    /// <summary>Adds a rename-field operation.</summary>
    public SchemaMigrationBuilder RenameField(string field, string targetField)
    {
        operations.Add(MigrationOperation.RenameField(field, targetField));
        return this;
    }

    /// <summary>Adds a set-default operation.</summary>
    public SchemaMigrationBuilder SetDefault(string field, JsonElement value)
    {
        operations.Add(MigrationOperation.SetDefault(field, value));
        return this;
    }

    /// <summary>Builds the immutable migration.</summary>
    public SchemaMigration Build() => new(fromVersion, toVersion, operations, name);
}

/// <summary>Maintains a contiguous migration chain for one collection.</summary>
public sealed class SchemaMigrationSet
{
    private readonly SortedDictionary<ulong, SchemaMigration> migrations = [];

    /// <summary>Adds a migration, rejecting conflicting source versions.</summary>
    public void Add(SchemaMigration migration)
    {
        ArgumentNullException.ThrowIfNull(migration);
        if (migrations.TryGetValue(migration.FromVersion, out var current) && (current.ToVersion != migration.ToVersion || !string.Equals(current.Name, migration.Name, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("A different migration already starts at this version.");
        }

        migrations[migration.FromVersion] = migration;
    }

    /// <summary>Returns a migration chain between two versions.</summary>
    public IReadOnlyList<SchemaMigration> Plan(ulong fromVersion, ulong toVersion)
    {
        if (fromVersion == toVersion)
        {
            return [];
        }

        if (fromVersion > toVersion)
        {
            throw new InvalidOperationException("Migration targets must be newer than the source version.");
        }

        var result = new List<SchemaMigration>();
        var version = fromVersion;
        while (version < toVersion)
        {
            if (!migrations.TryGetValue(version, out var migration))
            {
                throw new InvalidOperationException("The migration chain is not contiguous.");
            }

            result.Add(migration);
            version = migration.ToVersion;
        }

        if (version != toVersion)
        {
            throw new InvalidOperationException("The migration chain overshoots its target version.");
        }

        return result;
    }

    /// <summary>Applies a contiguous chain without mutating the source document.</summary>
    public SchemaMigrationResult Apply(JsonElement document, ulong fromVersion, ulong toVersion)
    {
        var current = document.Clone();
        foreach (var migration in Plan(fromVersion, toVersion))
        {
            var result = migration.Apply(current);
            if (!result.Succeeded)
            {
                return result;
            }

            current = result.Document!.Value.Clone();
        }

        return SchemaMigrationResult.Success(current, fromVersion, toVersion);
    }
}
