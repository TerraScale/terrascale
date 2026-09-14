namespace TerraBase;

/// <summary>The declared type of one schema field.</summary>
public enum DocumentFieldType
{
    /// <summary>A null value.</summary>
    Null,

    /// <summary>A Boolean value.</summary>
    Boolean,

    /// <summary>A signed integer value.</summary>
    Integer,

    /// <summary>A floating-point value.</summary>
    Number,

    /// <summary>A string value.</summary>
    String,

    /// <summary>An opaque byte sequence.</summary>
    Binary,

    /// <summary>A nested object.</summary>
    Object,

    /// <summary>An array whose items have a declared type.</summary>
    Array,
}

/// <summary>One typed field declaration in a <see cref="DocumentSchema"/>.</summary>
public sealed record DocumentFieldDefinition
{
    /// <summary>Creates a field declaration.</summary>
    public DocumentFieldDefinition(
        string name,
        DocumentFieldType type,
        bool required = false,
        bool nullable = true,
        DocumentSchema? nestedSchema = null,
        DocumentFieldType? elementType = null,
        DocumentSchema? elementSchema = null)
    {
        ValidateName(name);
        if (type == DocumentFieldType.Array && elementType is null)
        {
            throw new ArgumentException("Array fields must declare an element type.", nameof(elementType));
        }

        if (type != DocumentFieldType.Array && (elementType is not null || elementSchema is not null))
        {
            throw new ArgumentException("Element metadata is only valid for array fields.", nameof(elementType));
        }

        if (type != DocumentFieldType.Object && nestedSchema is not null)
        {
            throw new ArgumentException("Nested schema metadata is only valid for object fields.", nameof(nestedSchema));
        }

        if (elementType is DocumentFieldType.Array && elementSchema is null)
        {
            throw new ArgumentException("Nested array elements must declare an element schema.", nameof(elementSchema));
        }

        Name = name;
        Type = type;
        Required = required;
        Nullable = nullable;
        NestedSchema = nestedSchema;
        ElementType = elementType;
        ElementSchema = elementSchema;
    }

    /// <summary>The field name.</summary>
    public string Name { get; }

    /// <summary>The field type.</summary>
    public DocumentFieldType Type { get; }

    /// <summary>Whether this field must be present.</summary>
    public bool Required { get; }

    /// <summary>Whether an explicit null is accepted.</summary>
    public bool Nullable { get; }

    /// <summary>The nested schema for object fields.</summary>
    public DocumentSchema? NestedSchema { get; }

    /// <summary>The item type for array fields.</summary>
    public DocumentFieldType? ElementType { get; }

    /// <summary>The nested schema for object array items.</summary>
    public DocumentSchema? ElementSchema { get; }

    /// <summary>Creates a required field declaration.</summary>
    public static DocumentFieldDefinition RequiredField(string name, DocumentFieldType type) =>
        new(name, type, required: true, nullable: false);

    private static void ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Any(char.IsControl) || System.Text.Encoding.UTF8.GetByteCount(name) > 255)
        {
            throw new ArgumentException("Schema field names must be non-empty, valid UTF-8 text of at most 255 bytes.", nameof(name));
        }
    }
}

/// <summary>A validation result for one document against a typed schema.</summary>
public sealed record DocumentSchemaValidationResult(IReadOnlyList<string> Errors)
{
    /// <summary>Whether validation succeeded.</summary>
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Immutable typed metadata for a collection's document shape. Additional
/// fields are allowed by default so a schema can describe a stable typed core
/// without turning an otherwise document-oriented collection into a rigid row.
/// </summary>
public sealed class DocumentSchema : IEquatable<DocumentSchema>
{
    private readonly IReadOnlyDictionary<string, DocumentFieldDefinition> fields;

    /// <summary>Creates schema metadata.</summary>
    public DocumentSchema(
        string name,
        int version,
        IEnumerable<DocumentFieldDefinition> fields,
        bool allowAdditionalFields = true)
    {
        ValidateName(name, nameof(name));
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Schema versions start at one.");
        }

        ArgumentNullException.ThrowIfNull(fields);
        var copy = new SortedDictionary<string, DocumentFieldDefinition>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            ArgumentNullException.ThrowIfNull(field);
            if (!copy.TryAdd(field.Name, field))
            {
                throw new ArgumentException($"The schema field '{field.Name}' is duplicated.", nameof(fields));
            }
        }

        Name = name;
        Version = version;
        AllowAdditionalFields = allowAdditionalFields;
        this.fields = new System.Collections.ObjectModel.ReadOnlyDictionary<string, DocumentFieldDefinition>(copy);
    }

    /// <summary>Creates unnamed schema metadata at version one.</summary>
    public DocumentSchema(IEnumerable<DocumentFieldDefinition> fields, bool allowAdditionalFields = true)
        : this("document", 1, fields, allowAdditionalFields)
    {
    }

    /// <summary>The stable schema name.</summary>
    public string Name { get; }

    /// <summary>The schema version.</summary>
    public int Version { get; }

    /// <summary>Whether undeclared fields are accepted.</summary>
    public bool AllowAdditionalFields { get; }

    /// <summary>The declarations in ordinal name order.</summary>
    public IReadOnlyList<DocumentFieldDefinition> Fields => fields.Values.ToArray();

    /// <summary>Finds one field declaration.</summary>
    public DocumentFieldDefinition? GetField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return fields.GetValueOrDefault(name);
    }

    /// <summary>Validates a document and returns every discovered error.</summary>
    public DocumentSchemaValidationResult Validate(DocumentValue document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var errors = new List<string>();
        ValidateValue(document, this, "$", errors);
        return new DocumentSchemaValidationResult(errors.AsReadOnly());
    }

    /// <summary>Validates a document without allocating an error result on success.</summary>
    public bool TryValidate(DocumentValue document, out IReadOnlyList<string> errors)
    {
        var result = Validate(document);
        errors = result.Errors;
        return result.IsValid;
    }

    /// <summary>Throws when a document does not satisfy this schema.</summary>
    public void EnsureValid(DocumentValue document)
    {
        var result = Validate(document);
        if (!result.IsValid)
        {
            throw new DocumentSchemaValidationException(result.Errors);
        }
    }

    /// <inheritdoc />
    public bool Equals(DocumentSchema? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null && Name == other.Name && Version == other.Version &&
            AllowAdditionalFields == other.AllowAdditionalFields && Fields.SequenceEqual(other.Fields);
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DocumentSchema other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Name, Version, AllowAdditionalFields, fields.Count);

    private static void ValidateValue(
        DocumentValue value,
        DocumentSchema schema,
        string path,
        ICollection<string> errors)
    {
        if (value.Kind != DocumentValueKind.Object)
        {
            errors.Add($"{path} must be an object.");
            return;
        }

        foreach (var declaration in schema.fields.Values)
        {
            if (!value.ObjectValue.TryGetValue(declaration.Name, out var fieldValue))
            {
                if (declaration.Required)
                {
                    errors.Add($"{path}.{declaration.Name} is required.");
                }

                continue;
            }

            ValidateField(fieldValue, declaration, $"{path}.{declaration.Name}", errors);
        }

        if (!schema.AllowAdditionalFields)
        {
            foreach (var field in value.ObjectValue.Keys)
            {
                if (!schema.fields.ContainsKey(field))
                {
                    errors.Add($"{path}.{field} is not declared by the schema.");
                }
            }
        }
    }

    private static void ValidateField(
        DocumentValue value,
        DocumentFieldDefinition declaration,
        string path,
        ICollection<string> errors)
    {
        if (value.Kind == DocumentValueKind.Null)
        {
            if (!declaration.Nullable)
            {
                errors.Add($"{path} cannot be null.");
            }

            return;
        }

        if (declaration.Type == DocumentFieldType.Array)
        {
            if (value.Kind != DocumentValueKind.Array)
            {
                errors.Add($"{path} must be an array.");
                return;
            }

            for (var index = 0; index < value.ArrayValue.Count; index++)
            {
                var item = value.ArrayValue[index];
                var itemPath = $"{path}[{index}]";
                if (item.Kind == DocumentValueKind.Null && declaration.Nullable)
                {
                    continue;
                }

                ValidateKind(item, declaration.ElementType!.Value, itemPath, errors);
                if (item.Kind == DocumentValueKind.Object && declaration.ElementSchema is not null)
                {
                    ValidateValue(item, declaration.ElementSchema, itemPath, errors);
                }
            }

            return;
        }

        ValidateKind(value, declaration.Type, path, errors);
        if (value.Kind == DocumentValueKind.Object && declaration.NestedSchema is not null)
        {
            ValidateValue(value, declaration.NestedSchema, path, errors);
        }
    }

    private static void ValidateKind(
        DocumentValue value,
        DocumentFieldType expected,
        string path,
        ICollection<string> errors)
    {
        var actual = value.Kind switch
        {
            DocumentValueKind.Null => DocumentFieldType.Null,
            DocumentValueKind.Boolean => DocumentFieldType.Boolean,
            DocumentValueKind.Integer => DocumentFieldType.Integer,
            DocumentValueKind.Number => DocumentFieldType.Number,
            DocumentValueKind.String => DocumentFieldType.String,
            DocumentValueKind.Binary => DocumentFieldType.Binary,
            DocumentValueKind.Array => DocumentFieldType.Array,
            DocumentValueKind.Object => DocumentFieldType.Object,
            _ => throw new InvalidOperationException(),
        };
        if (actual != expected)
        {
            errors.Add($"{path} must be {expected}, but is {actual}.");
        }
    }

    private static void ValidateName(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value.Length == 0 || value.Any(char.IsControl) || System.Text.Encoding.UTF8.GetByteCount(value) > 255)
        {
            throw new ArgumentException("Schema names must be non-empty, valid UTF-8 text of at most 255 bytes.", parameterName);
        }
    }
}

/// <summary>Thrown when a value fails document schema validation.</summary>
public sealed class DocumentSchemaValidationException : ArgumentException
{
    /// <summary>Creates a validation exception.</summary>
    public DocumentSchemaValidationException(IReadOnlyList<string> errors)
        : base($"The document does not satisfy its schema: {string.Join("; ", errors)}")
    {
        Errors = errors;
    }

    /// <summary>The validation errors.</summary>
    public IReadOnlyList<string> Errors { get; }
}

/// <summary>A fluent builder for immutable document schema metadata.</summary>
public sealed class DocumentSchemaBuilder
{
    private readonly List<DocumentFieldDefinition> fields = [];

    /// <summary>Creates a builder.</summary>
    public DocumentSchemaBuilder(string name = "document", int version = 1)
    {
        Name = name;
        Version = version;
    }

    /// <summary>The schema name.</summary>
    public string Name { get; }

    /// <summary>The schema version.</summary>
    public int Version { get; }

    /// <summary>Adds a field declaration.</summary>
    public DocumentSchemaBuilder Add(DocumentFieldDefinition field)
    {
        ArgumentNullException.ThrowIfNull(field);
        fields.Add(field);
        return this;
    }

    /// <summary>Adds a field declaration.</summary>
    public DocumentSchemaBuilder Add(
        string name,
        DocumentFieldType type,
        bool required = false,
        bool nullable = true,
        DocumentSchema? nestedSchema = null,
        DocumentFieldType? elementType = null,
        DocumentSchema? elementSchema = null) =>
        Add(new DocumentFieldDefinition(name, type, required, nullable, nestedSchema, elementType, elementSchema));

    /// <summary>Builds immutable schema metadata.</summary>
    public DocumentSchema Build(bool allowAdditionalFields = true) =>
        new(Name, Version, fields, allowAdditionalFields);
}
