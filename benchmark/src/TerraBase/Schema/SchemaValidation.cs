using System.Text.Json;

namespace TerraBase.Schema;

/// <summary>Stable categories returned by schema validation.</summary>
public enum SchemaViolationCode
{
    /// <summary>The root document is not an object.</summary>
    DocumentNotObject,

    /// <summary>A required field was absent.</summary>
    MissingRequiredField,

    /// <summary>A field was explicitly null although null is not accepted.</summary>
    NullNotAllowed,

    /// <summary>A value has a different JSON type from the declared field.</summary>
    TypeMismatch,

    /// <summary>A string is shorter than the declared minimum.</summary>
    StringTooShort,

    /// <summary>A string is longer than the declared maximum.</summary>
    StringTooLong,

    /// <summary>A number is below the declared minimum.</summary>
    NumberTooSmall,

    /// <summary>A number is above the declared maximum.</summary>
    NumberTooLarge,

    /// <summary>A relationship value has the wrong cardinality shape.</summary>
    RelationshipCardinalityMismatch,
}

/// <summary>A content-safe schema validation violation.</summary>
public sealed record SchemaViolation(string Field, SchemaViolationCode Code, string Message)
{
    /// <summary>The path alias used by callers that validate nested values.</summary>
    public string Path => Field;

    /// <summary>The machine-readable rule alias.</summary>
    public string Kind => Code.ToString();
}

/// <summary>The result of validating one JSON document.</summary>
public sealed class SchemaValidationResult
{
    /// <summary>Creates a validation result.</summary>
    public SchemaValidationResult(ulong schemaVersion, IReadOnlyList<SchemaViolation> violations)
    {
        SchemaVersion = schemaVersion;
        Violations = violations ?? throw new ArgumentNullException(nameof(violations));
    }

    /// <summary>The enforced schema version.</summary>
    public ulong SchemaVersion { get; }

    /// <summary>The violations in deterministic field order.</summary>
    public IReadOnlyList<SchemaViolation> Violations { get; }

    /// <summary>Whether no violations were found.</summary>
    public bool IsValid => Violations.Count == 0;

    /// <summary>Alias suitable for API payloads.</summary>
    public bool Valid => IsValid;
}

/// <summary>Evaluates generated metadata against a JSON document.</summary>
public static class SchemaValidator
{
    /// <summary>Validates one document with bounded deterministic work.</summary>
    public static SchemaValidationResult Validate(SchemaMetadata schema, JsonElement document)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var violations = new List<SchemaViolation>();
        if (document.ValueKind != JsonValueKind.Object)
        {
            return new SchemaValidationResult(schema.Version, [new SchemaViolation(string.Empty, SchemaViolationCode.DocumentNotObject, "The document must be a JSON object.")]);
        }

        foreach (var field in schema.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
        {
            if (!document.TryGetProperty(field.Name, out var value) && !string.IsNullOrEmpty(field.ClrName))
            {
                document.TryGetProperty(field.ClrName, out value);
            }

            if (value.ValueKind == JsonValueKind.Undefined)
            {
                if (field.Required)
                {
                    violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.MissingRequiredField, $"The field '{field.Name}' is required."));
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (field.Required || !field.Nullable)
                {
                    violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.NullNotAllowed, $"The field '{field.Name}' does not allow null."));
                }

                continue;
            }

            if (!Matches(field.ValueType, value))
            {
                violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.TypeMismatch, $"The field '{field.Name}' has the wrong type."));
                continue;
            }

            ValidateBounds(field, value, violations);
        }

        foreach (var relationship in schema.Relationships.OrderBy(relationship => relationship.Name, StringComparer.Ordinal))
        {
            if (!document.TryGetProperty(relationship.Name, out var value))
            {
                if (relationship.Required)
                {
                    violations.Add(new SchemaViolation(relationship.Name, SchemaViolationCode.MissingRequiredField, $"The relationship '{relationship.Name}' is required."));
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (relationship.Required)
                {
                    violations.Add(new SchemaViolation(relationship.Name, SchemaViolationCode.NullNotAllowed, $"The relationship '{relationship.Name}' does not allow null."));
                }

                continue;
            }

            var collection = relationship.Kind is RelationshipKind.OneToMany or RelationshipKind.ManyToMany;
            if (collection != (value.ValueKind == JsonValueKind.Array))
            {
                violations.Add(new SchemaViolation(relationship.Name, SchemaViolationCode.RelationshipCardinalityMismatch, $"The relationship '{relationship.Name}' has the wrong cardinality."));
            }
        }

        return new SchemaValidationResult(schema.Version, violations);
    }

    private static bool Matches(SchemaValueType expected, JsonElement value) => expected switch
    {
        SchemaValueType.Any => true,
        SchemaValueType.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        SchemaValueType.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        SchemaValueType.Number => value.ValueKind == JsonValueKind.Number,
        SchemaValueType.String => value.ValueKind == JsonValueKind.String,
        SchemaValueType.Object => value.ValueKind == JsonValueKind.Object,
        SchemaValueType.Array => value.ValueKind == JsonValueKind.Array,
        SchemaValueType.Null => value.ValueKind == JsonValueKind.Null,
        _ => false,
    };

    private static void ValidateBounds(FieldMetadata field, JsonElement value, ICollection<SchemaViolation> violations)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var length = value.GetString()?.Length ?? 0;
            if (field.MinLength is { } minimum && length < minimum)
            {
                violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.StringTooShort, $"The field '{field.Name}' is shorter than its minimum length."));
            }

            if (field.MaxLength is { } maximum && length > maximum)
            {
                violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.StringTooLong, $"The field '{field.Name}' is longer than its maximum length."));
            }
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number))
        {
            if (field.Minimum is { } minimum && number < minimum)
            {
                violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.NumberTooSmall, $"The field '{field.Name}' is below its minimum."));
            }

            if (field.Maximum is { } maximum && number > maximum)
            {
                violations.Add(new SchemaViolation(field.Name, SchemaViolationCode.NumberTooLarge, $"The field '{field.Name}' is above its maximum."));
            }
        }
    }
}
