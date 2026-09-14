using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TerraBase.Schema;

/// <summary>Describes the JSON value represented by a schema field.</summary>
public enum SchemaValueType
{
    /// <summary>Any JSON value.</summary>
    Any,

    /// <summary>A JSON boolean.</summary>
    Boolean,

    /// <summary>A JSON integral number.</summary>
    Integer,

    /// <summary>A JSON floating point or decimal number.</summary>
    Number,

    /// <summary>A JSON string.</summary>
    String,

    /// <summary>A JSON object.</summary>
    Object,

    /// <summary>A JSON array.</summary>
    Array,

    /// <summary>A JSON null value.</summary>
    Null,
}

/// <summary>Controls how a generated schema names CLR members on the wire.</summary>
public enum SchemaNamingPolicy
{
    /// <summary>Use the CLR member name unchanged.</summary>
    Exact,

    /// <summary>Use the standard lower camel case JSON name.</summary>
    CamelCase,
}

/// <summary>Marks a CLR type as a schema root.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false)]
public sealed class SchemaAttribute : Attribute
{
    /// <summary>Creates a schema marker with a default collection name.</summary>
    public SchemaAttribute()
    {
    }

    /// <summary>Creates a schema marker with an explicit collection name.</summary>
    /// <param name="name">The collection name.</param>
    public SchemaAttribute(string name) => Name = name;

    /// <summary>The collection name override.</summary>
    public string? Name { get; }

    /// <summary>The first version emitted for this type.</summary>
    public ulong Version { get; init; } = 1;
}

/// <summary>Excludes a CLR member from generated schema metadata.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class SchemaIgnoreAttribute : Attribute
{
}

/// <summary>Adds wire-name and validation options to a schema field.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class SchemaFieldAttribute : Attribute
{
    /// <summary>Creates a field using the generated name.</summary>
    public SchemaFieldAttribute()
    {
    }

    /// <summary>Creates a field with a wire-name override.</summary>
    /// <param name="name">The wire name.</param>
    public SchemaFieldAttribute(string name) => Name = name;

    /// <summary>The wire-name override.</summary>
    public string? Name { get; }

    /// <summary>Whether the field must be present and non-null.</summary>
    public bool Required { get; init; }

    /// <summary>Whether values must be unique in the collection.</summary>
    public bool Unique { get; init; }

    /// <summary>The minimum string length, when configured.</summary>
    public int? MinLength { get; init; }

    /// <summary>The maximum string length, when configured.</summary>
    public int? MaxLength { get; init; }

    /// <summary>The inclusive numeric minimum, when configured.</summary>
    public decimal? Minimum { get; init; }

    /// <summary>The inclusive numeric maximum, when configured.</summary>
    public decimal? Maximum { get; init; }
}

/// <summary>Declares a first-class relationship from a CLR member.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class RelationshipAttribute : Attribute
{
    /// <summary>Creates a relationship named after the member.</summary>
    /// <param name="targetType">The target schema CLR type.</param>
    public RelationshipAttribute(Type targetType) => TargetType = targetType;

    /// <summary>Creates a relationship with an explicit name.</summary>
    /// <param name="name">The forward relationship name.</param>
    /// <param name="targetType">The target schema CLR type.</param>
    public RelationshipAttribute(string name, Type targetType)
    {
        Name = name;
        TargetType = targetType;
    }

    /// <summary>The relationship name override.</summary>
    public string? Name { get; }

    /// <summary>The target schema CLR type.</summary>
    public Type TargetType { get; }

    /// <summary>The relationship cardinality.</summary>
    public RelationshipKind Kind { get; init; } = RelationshipKind.ManyToOne;

    /// <summary>The target-side reverse relationship name.</summary>
    public string? Inverse { get; init; }

    /// <summary>The foreign-key member holding the target identifier.</summary>
    public string? ForeignKey { get; init; }

    /// <summary>Whether this relationship must have a target.</summary>
    public bool Required { get; init; }

    /// <summary>The policy applied when the target is deleted.</summary>
    public DeletePolicy OnDelete { get; init; } = DeletePolicy.Preserve;
}

/// <summary>Controls relationship cardinality.</summary>
public enum RelationshipKind
{
    /// <summary>One source points to one target.</summary>
    OneToOne,

    /// <summary>One source points to many targets.</summary>
    OneToMany,

    /// <summary>Many sources point to one target.</summary>
    ManyToOne,

    /// <summary>Many sources point to many targets.</summary>
    ManyToMany,
}

/// <summary>Controls the effect of deleting a relationship endpoint.</summary>
public enum DeletePolicy
{
    /// <summary>Keep the edge or dependent document.</summary>
    Preserve,

    /// <summary>Clear the dependent relationship field.</summary>
    Nullify,

    /// <summary>Delete dependent records with a bounded mutation plan.</summary>
    Cascade,

    /// <summary>Reject deletion while dependents remain.</summary>
    Restrict,
}

/// <summary>Controls how a constraint is enforced.</summary>
public enum ConstraintPolicy
{
    /// <summary>Validate records locally when they are written.</summary>
    Soft,

    /// <summary>Require a transactionally checked local relationship.</summary>
    Strict,
}

/// <summary>The kinds of constraints supported by generated metadata.</summary>
public enum ConstraintKind
{
    /// <summary>A field must be present.</summary>
    Required,

    /// <summary>A field or field tuple must be unique.</summary>
    Unique,

    /// <summary>A field references another collection.</summary>
    ForeignKey,

    /// <summary>A user-defined check supplied by the application.</summary>
    Check,
}

/// <summary>Declares an explicit schema constraint.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = true)]
public sealed class SchemaConstraintAttribute : Attribute
{
    /// <summary>Creates a named constraint.</summary>
    /// <param name="kind">The constraint kind.</param>
    /// <param name="fields">The participating CLR or wire fields.</param>
    public SchemaConstraintAttribute(ConstraintKind kind, params string[] fields)
    {
        Kind = kind;
        Fields = fields ?? [];
    }

    /// <summary>The constraint kind.</summary>
    public ConstraintKind Kind { get; }

    /// <summary>The participating fields.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Whether the constraint uses strict local checking.</summary>
    public ConstraintPolicy Policy { get; init; } = ConstraintPolicy.Soft;

    /// <summary>An optional stable constraint name.</summary>
    public string? Name { get; init; }
}

/// <summary>Options used by the deterministic metadata generator.</summary>
public sealed record SchemaGenerationOptions
{
    /// <summary>The member naming policy.</summary>
    public SchemaNamingPolicy NamingPolicy { get; init; } = SchemaNamingPolicy.CamelCase;

    /// <summary>Whether public fields participate in metadata.</summary>
    public bool IncludePublicFields { get; init; } = true;

    /// <summary>Whether public properties participate in metadata.</summary>
    public bool IncludePublicProperties { get; init; } = true;
}

/// <summary>Metadata for one generated schema field.</summary>
public sealed class FieldMetadata
{
    /// <summary>Creates field metadata.</summary>
    public FieldMetadata(
        string name,
        Type clrType,
        SchemaValueType valueType,
        bool required = false,
        bool unique = false,
        bool nullable = false,
        int? minLength = null,
        int? maxLength = null,
        decimal? minimum = null,
        decimal? maximum = null,
        SchemaValueType? elementType = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A schema field name is required.", nameof(name));
        }

        Name = name;
        ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
        ValueType = valueType;
        Required = required;
        Unique = unique;
        Nullable = nullable;
        MinLength = minLength;
        MaxLength = maxLength;
        Minimum = minimum;
        Maximum = maximum;
        ElementType = elementType;
    }

    /// <summary>The JSON wire name.</summary>
    public string Name { get; }

    /// <summary>The original CLR member name.</summary>
    public string ClrName { get; internal set; } = string.Empty;

    /// <summary>The CLR field/property type.</summary>
    public Type ClrType { get; }

    /// <summary>The JSON value type.</summary>
    public SchemaValueType ValueType { get; }

    /// <summary>The array element type, when <see cref="ValueType"/> is Array.</summary>
    public SchemaValueType? ElementType { get; }

    /// <summary>Whether this value must be present.</summary>
    public bool Required { get; }

    /// <summary>Whether this value participates in uniqueness enforcement.</summary>
    public bool Unique { get; }

    /// <summary>Whether null is accepted.</summary>
    public bool Nullable { get; }

    /// <summary>The inclusive minimum string length.</summary>
    public int? MinLength { get; }

    /// <summary>The inclusive maximum string length.</summary>
    public int? MaxLength { get; }

    /// <summary>The inclusive numeric minimum.</summary>
    public decimal? Minimum { get; }

    /// <summary>The inclusive numeric maximum.</summary>
    public decimal? Maximum { get; }
}

/// <summary>Metadata for a forward or reverse relationship.</summary>
public sealed class RelationshipMetadata
{
    /// <summary>Creates relationship metadata.</summary>
    public RelationshipMetadata(
        string name,
        string sourceCollection,
        string targetCollection,
        RelationshipKind kind,
        DeletePolicy onDelete = DeletePolicy.Preserve,
        string? inverseName = null,
        string? foreignKey = null,
        bool required = false,
        bool isReverse = false)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        SourceCollection = sourceCollection ?? throw new ArgumentNullException(nameof(sourceCollection));
        TargetCollection = targetCollection ?? throw new ArgumentNullException(nameof(targetCollection));
        Kind = kind;
        OnDelete = onDelete;
        InverseName = inverseName;
        ForeignKey = foreignKey;
        Required = required;
        IsReverse = isReverse;
    }

    /// <summary>The relationship name on this side.</summary>
    public string Name { get; }

    /// <summary>The source collection.</summary>
    public string SourceCollection { get; }

    /// <summary>The target collection.</summary>
    public string TargetCollection { get; }

    /// <summary>The relationship cardinality from the source side.</summary>
    public RelationshipKind Kind { get; }

    /// <summary>The endpoint delete policy.</summary>
    public DeletePolicy OnDelete { get; }

    /// <summary>The target-side inverse relationship name.</summary>
    public string? InverseName { get; }

    /// <summary>The source foreign-key field.</summary>
    public string? ForeignKey { get; }

    /// <summary>Whether an edge is required.</summary>
    public bool Required { get; }

    /// <summary>Whether this metadata was generated as the reverse side.</summary>
    public bool IsReverse { get; }

    /// <summary>Creates immutable reverse metadata for this relationship.</summary>
    public RelationshipMetadata Reverse() => new(
        InverseName ?? Name,
        TargetCollection,
        SourceCollection,
        ReverseKind(Kind),
        OnDelete,
        Name,
        null,
        false,
        true);

    private static RelationshipKind ReverseKind(RelationshipKind kind) => kind switch
    {
        RelationshipKind.OneToMany => RelationshipKind.ManyToOne,
        RelationshipKind.ManyToOne => RelationshipKind.OneToMany,
        _ => kind,
    };
}

/// <summary>Metadata for a collection or type constraint.</summary>
public sealed class ConstraintMetadata
{
    /// <summary>Creates constraint metadata.</summary>
    public ConstraintMetadata(
        string name,
        ConstraintKind kind,
        IEnumerable<string> fields,
        ConstraintPolicy policy = ConstraintPolicy.Soft)
    {
        Name = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A constraint name is required.", nameof(name)) : name;
        Kind = kind;
        Fields = (fields ?? throw new ArgumentNullException(nameof(fields))).Where(field => !string.IsNullOrWhiteSpace(field)).Distinct(StringComparer.Ordinal).ToArray();
        if (Fields.Count == 0)
        {
            throw new ArgumentException("A constraint must name at least one field.", nameof(fields));
        }

        Policy = policy;
    }

    /// <summary>The stable constraint name.</summary>
    public string Name { get; }

    /// <summary>The constraint kind.</summary>
    public ConstraintKind Kind { get; }

    /// <summary>The fields participating in the constraint.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>The enforcement policy.</summary>
    public ConstraintPolicy Policy { get; }
}

/// <summary>Options for constructing explicit schema metadata.</summary>
public sealed record SchemaMetadataOptions
{
    /// <summary>The schema collection name.</summary>
    public string? Name { get; init; }

    /// <summary>The schema version.</summary>
    public ulong Version { get; init; } = 1;

    /// <summary>The naming policy used by generated members.</summary>
    public SchemaNamingPolicy NamingPolicy { get; init; } = SchemaNamingPolicy.CamelCase;
}

/// <summary>Immutable metadata generated from one CLR document type.</summary>
public sealed class SchemaMetadata
{
    private static readonly ConcurrentDictionary<(Type Type, SchemaNamingPolicy Policy), SchemaMetadata> Cache = new();

    /// <summary>Creates explicit metadata. Generated metadata should use <see cref="SchemaGenerator"/>.</summary>
    public SchemaMetadata(
        string name,
        ulong version,
        Type clrType,
        IEnumerable<FieldMetadata> fields,
        IEnumerable<RelationshipMetadata>? relationships = null,
        IEnumerable<ConstraintMetadata>? constraints = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("A schema name is required.", nameof(name));
        }

        if (version == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Schema versions start at one.");
        }

        Name = name;
        Version = version;
        ClrType = clrType ?? throw new ArgumentNullException(nameof(clrType));
        Fields = (fields ?? throw new ArgumentNullException(nameof(fields))).ToArray();
        Relationships = (relationships ?? []).ToArray();
        Constraints = (constraints ?? []).ToArray();
        ReverseRelationships = Relationships.Select(relationship => relationship.Reverse()).ToArray();
        Fingerprint = ComputeFingerprint();
    }

    /// <summary>The collection name represented by this schema.</summary>
    public string Name { get; }

    /// <summary>The positive schema version.</summary>
    public ulong Version { get; }

    /// <summary>The CLR document type.</summary>
    public Type ClrType { get; }

    /// <summary>The generated fields in deterministic name order.</summary>
    public IReadOnlyList<FieldMetadata> Fields { get; }

    /// <summary>The declared forward relationships.</summary>
    public IReadOnlyList<RelationshipMetadata> Relationships { get; }

    /// <summary>The generated reverse relationship metadata.</summary>
    public IReadOnlyList<RelationshipMetadata> ReverseRelationships { get; }

    /// <summary>The declared constraints.</summary>
    public IReadOnlyList<ConstraintMetadata> Constraints { get; }

    /// <summary>A stable digest of the schema metadata.</summary>
    public string Fingerprint { get; }

    /// <summary>Returns a field by wire name or CLR name.</summary>
    public FieldMetadata? FindField(string name) => Fields.FirstOrDefault(field =>
        string.Equals(field.Name, name, StringComparison.Ordinal)
        || string.Equals(field.ClrName, name, StringComparison.Ordinal));

    /// <summary>Validates one JSON document against this metadata.</summary>
    public SchemaValidationResult Validate(JsonElement document) => SchemaValidator.Validate(this, document);

    /// <summary>Serializes and validates one CLR document.</summary>
    public SchemaValidationResult Validate<T>(T document) => Validate(JsonSerializer.SerializeToElement(document, SchemaGenerator.JsonOptions));

    /// <summary>Gets cached generated metadata for a CLR type.</summary>
    public static SchemaMetadata For<T>(SchemaGenerationOptions? options = null) => For(typeof(T), options);

    /// <summary>Gets cached generated metadata for a CLR type.</summary>
    public static SchemaMetadata For(Type type, SchemaGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        var policy = options?.NamingPolicy ?? SchemaNamingPolicy.CamelCase;
        return Cache.GetOrAdd((type, policy), key => SchemaGenerator.Generate(key.Type, new SchemaGenerationOptions { NamingPolicy = key.Policy }));
    }

    private string ComputeFingerprint()
    {
        var builder = new StringBuilder(Name).Append('|').Append(Version).Append('|');
        foreach (var field in Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            builder.Append(field.Name).Append(':').Append(field.ValueType).Append(':').Append(field.Required).Append(':').Append(field.Unique).Append(';');
        }

        foreach (var relationship in Relationships.OrderBy(relationship => relationship.Name, StringComparer.Ordinal))
        {
            builder.Append(relationship.Name).Append(':').Append(relationship.SourceCollection).Append('>').Append(relationship.TargetCollection).Append(':').Append(relationship.Kind).Append(':').Append(relationship.OnDelete).Append(';');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }
}

/// <summary>Generates deterministic schema metadata from CLR types.</summary>
public static class SchemaGenerator
{
    /// <summary>Options used when serializing CLR objects for validation.</summary>
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web);

    /// <summary>Generates metadata for a CLR type.</summary>
    public static SchemaMetadata Generate<T>(SchemaGenerationOptions? options = null) => Generate(typeof(T), options);

    /// <summary>Generates metadata for a CLR type.</summary>
    public static SchemaMetadata Generate(Type type, SchemaGenerationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(type);
        options ??= new SchemaGenerationOptions();
        var marker = type.GetCustomAttribute<SchemaAttribute>();
        var name = marker?.Name ?? type.Name;
        var version = marker?.Version ?? 1;
        var fields = new List<FieldMetadata>();
        var relationships = new List<RelationshipMetadata>();

        if (options.IncludePublicProperties)
        {
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(property => property.GetMethod is not null && property.GetIndexParameters().Length == 0).OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                AddMember(property, property.PropertyType, property.GetCustomAttributes(), fields, relationships, name, options);
            }
        }

        if (options.IncludePublicFields)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(field => !field.IsStatic).OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                AddMember(field, field.FieldType, field.GetCustomAttributes(), fields, relationships, name, options);
            }
        }

        var constraints = type.GetCustomAttributes<SchemaConstraintAttribute>()
            .Select(attribute => new ConstraintMetadata(attribute.Name ?? $"{attribute.Kind}:{string.Join(',', attribute.Fields)}", attribute.Kind, attribute.Fields, attribute.Policy))
            .ToList();
        foreach (var field in fields.Where(field => field.Required))
        {
            constraints.Add(new ConstraintMetadata($"required:{field.Name}", ConstraintKind.Required, [field.Name]));
        }

        return new SchemaMetadata(name, version, type, fields, relationships, constraints);
    }

    private static void AddMember(
        MemberInfo member,
        Type clrType,
        IEnumerable<Attribute> attributes,
        ICollection<FieldMetadata> fields,
        ICollection<RelationshipMetadata> relationships,
        string collection,
        SchemaGenerationOptions options)
    {
        if (attributes.OfType<SchemaIgnoreAttribute>().Any())
        {
            return;
        }

        var relationship = attributes.OfType<RelationshipAttribute>().SingleOrDefault();
        var fieldAttribute = attributes.OfType<SchemaFieldAttribute>().SingleOrDefault();
        var name = fieldAttribute?.Name ?? (options.NamingPolicy == SchemaNamingPolicy.CamelCase ? JsonNamingPolicy.CamelCase.ConvertName(member.Name) : member.Name);
        var required = fieldAttribute?.Required == true || attributes.OfType<RequiredAttribute>().Any();
        var unique = fieldAttribute?.Unique == true || attributes.OfType<KeyAttribute>().Any();
        var stringLength = attributes.OfType<StringLengthAttribute>().SingleOrDefault();
        var range = attributes.OfType<RangeAttribute>().SingleOrDefault();
        var (valueType, elementType) = GetValueType(clrType);
        var nullable = !clrType.IsValueType || Nullable.GetUnderlyingType(clrType) is not null;
        fields.Add(new FieldMetadata(
            name,
            clrType,
            valueType,
            required,
            unique,
            nullable,
            fieldAttribute?.MinLength ?? stringLength?.MinimumLength,
            fieldAttribute?.MaxLength ?? stringLength?.MaximumLength,
            fieldAttribute?.Minimum ?? ToDecimal(range?.Minimum),
            fieldAttribute?.Maximum ?? ToDecimal(range?.Maximum),
            elementType)
        {
            ClrName = member.Name,
        });

        if (relationship is not null)
        {
            var relationshipName = relationship.Name ?? name;
            var targetCollection = relationship.TargetType.GetCustomAttribute<SchemaAttribute>()?.Name ?? relationship.TargetType.Name;
            relationships.Add(new RelationshipMetadata(relationshipName, collection, targetCollection, relationship.Kind, relationship.OnDelete, relationship.Inverse, relationship.ForeignKey, relationship.Required));
        }
    }

    private static decimal? ToDecimal(object? value)
    {
        if (value is null)
        {
            return null;
        }

        return decimal.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static (SchemaValueType ValueType, SchemaValueType? ElementType) GetValueType(Type type)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
        {
            type = nullable;
        }

        if (type == typeof(bool))
        {
            return (SchemaValueType.Boolean, null);
        }

        if (type == typeof(byte) || type == typeof(sbyte) || type == typeof(short) || type == typeof(ushort) || type == typeof(int) || type == typeof(uint) || type == typeof(long) || type == typeof(ulong))
        {
            return (SchemaValueType.Integer, null);
        }

        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return (SchemaValueType.Number, null);
        }

        if (type == typeof(string) || type == typeof(Guid) || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type.IsEnum)
        {
            return (SchemaValueType.String, null);
        }

        if (type == typeof(JsonElement) || type == typeof(object))
        {
            return (SchemaValueType.Any, null);
        }

        if (type != typeof(string) && typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
        {
            var element = type.IsArray ? type.GetElementType() : type.GetInterfaces().Append(type).FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))?.GetGenericArguments()[0];
            return (SchemaValueType.Array, element is null ? null : GetValueType(element).ValueType);
        }

        return (SchemaValueType.Object, null);
    }
}

/// <summary>Convenience entry point for generated schema metadata.</summary>
public static class Schema
{
    /// <summary>Generates metadata for a CLR type.</summary>
    public static SchemaMetadata For<T>(SchemaGenerationOptions? options = null) => SchemaMetadata.For<T>(options);

    /// <summary>Generates metadata for a CLR type.</summary>
    public static SchemaMetadata For(Type type, SchemaGenerationOptions? options = null) => SchemaMetadata.For(type, options);
}
