using System.Buffers;
using System.Text;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Engine;

/// <summary>A unique scalar or compound key in a collection.</summary>
/// <param name="Name">Stable constraint name.</param>
/// <param name="Fields">One to four distinct dotted member paths.</param>
/// <param name="Normalization">String normalization: none, case_fold, or trim_case_fold.</param>
public sealed record UniqueConstraint(string Name, IReadOnlyList<string> Fields, string Normalization = "none");

/// <summary>A write conflicts with a collection's unique keys; no part of its transaction may commit.</summary>
public sealed class UniqueConstraintException : Exception
{
    /// <summary>Creates a conflict without exposing another document's values.</summary>
    public UniqueConstraintException() : base("a unique field or compound key is already taken") { }
}

/// <summary>The persisted, replicated uniqueness declaration.</summary>
/// <param name="Constraints">At most 32 named unique keys.</param>
public sealed record UniqueSchema(IReadOnlyList<UniqueConstraint> Constraints);

internal static class UniqueKeys
{
    internal const int MaxConstraints = 32;
    internal const int MaxKeyBytes = 16 * 1024;
    internal const int MaxDocumentKeyBytes = 64 * 1024;

    internal static bool Valid(IReadOnlyList<UniqueConstraint>? constraints) =>
        constraints is { Count: <= MaxConstraints }
        && constraints.All(constraint => constraint is not null
            && DatabaseName.TryParse(constraint.Name, out _)
            && constraint.Normalization is "none" or "case_fold" or "trim_case_fold"
            && constraint.Fields is { Count: >= 1 and <= 4 }
            && constraint.Fields.All(field => DocumentField.TryParse(field, out var parsed) && !parsed.IsMetadata)
            && constraint.Fields.Distinct(StringComparer.Ordinal).Count() == constraint.Fields.Count)
        && constraints.Select(constraint => constraint.Name).Distinct(StringComparer.Ordinal).Count() == constraints.Count;

    internal static byte[]? Key(JsonElement document, UniqueConstraint constraint)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartArray();
        foreach (var path in constraint.Fields)
        {
            var value = document;
            foreach (var member in path.Split('.'))
            {
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(member, out value))
                {
                    return null;
                }
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    var text = value.GetString()!;
                    if (Encoding.UTF8.GetByteCount(text) > MaxKeyBytes)
                    {
                        throw new DocumentValidationException("a unique key is at most 16 KiB", tooLarge: true);
                    }

                    text = constraint.Normalization switch
                    {
                        "case_fold" => text.ToLowerInvariant(),
                        "trim_case_fold" => text.Trim().ToLowerInvariant(),
                        _ => text,
                    };
                    writer.WriteStringValue("s:" + text);
                    break;
                case JsonValueKind.Number:
                    if (!ExactNumber.TryParse(value.GetRawText(), out var number))
                    {
                        throw new DocumentValidationException("a unique number must satisfy the document number limits");
                    }

                    writer.WriteStringValue("n:" + number);
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    writer.WriteBooleanValue(value.GetBoolean());
                    break;
                default:
                    throw new DocumentValidationException("unique fields must be scalar values, null, or missing");
            }

            writer.Flush();
            if (buffer.WrittenCount > MaxKeyBytes)
            {
                throw new DocumentValidationException("a unique key is at most 16 KiB", tooLarge: true);
            }
        }

        writer.WriteEndArray();
        writer.Flush();
        return buffer.WrittenCount <= MaxKeyBytes
            ? buffer.WrittenSpan.ToArray()
            : throw new DocumentValidationException("a unique key is at most 16 KiB", tooLarge: true);
    }
}
