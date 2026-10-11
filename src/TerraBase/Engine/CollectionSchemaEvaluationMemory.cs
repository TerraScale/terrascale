using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Json.Pointer;
using Json.Schema;
using TerraBase.Features.Memory;

namespace TerraBase.Engine;

// Reserve potentially applied schema/instance pairs, including boolean nodes that invoke no handlers.
internal sealed class CollectionSchemaEvaluationMemory(IMemoryBudget memory, CancellationToken cancellationToken) : IDisposable
{
    private readonly List<IMemoryReservation> _reservations = [];

    internal static CollectionSchemaEvaluationMemory? Reserve(
        JsonSchema schema, JsonElement instance, IMemoryBudget? memory, CancellationToken cancellationToken)
    {
        if (memory is null)
        {
            return null;
        }

        var admission = new CollectionSchemaEvaluationMemory(memory, cancellationToken);
        try
        {
            admission.Visit(schema.Root, instance, 0, 0, 0);
            return admission;
        }
        catch
        {
            admission.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (var reservation in _reservations)
        {
            reservation.Dispose();
        }
    }

    private void Charge(long bytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var reservation = memory.TryReserve(MemoryLane.Foreground, bytes) ?? throw new StorageBusyException();
        _reservations.Add(reservation);
    }

    private void Visit(JsonSchemaNode schema, JsonElement instance, int definitionBytes, int evaluationBytes, int instanceBytes, int? propertyNameBytes = null)
    {
        // Includes result objects, keyword arrays/dictionaries, child edges, and list flattening's queue/list.
        // Pointer accounting covers UTF-16 strings, JSON Pointer escaping, and percent-escaped schema URIs.
        Charge(1152L + (512L * schema.Keywords.Length) + (16L * (definitionBytes + evaluationBytes + instanceBytes)));
        var kind = propertyNameBytes is null ? instance.ValueKind : JsonValueKind.String;
        if (propertyNameBytes is { } nameBytes)
        {
            // propertyNames materializes a JSON string even when its child is a boolean schema.
            Charge(128L + (48L * nameBytes));
        }

        foreach (var keyword in schema.Keywords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = keyword.Handler.Name;
            var keywordPathBytes = 1 + Encoding.UTF8.GetByteCount(name);
            var childDefinitionBytes = definitionBytes + keywordPathBytes;
            var childEvaluationBytes = evaluationBytes + keywordPathBytes;
            if (name is "const" or "enum" || (name == "uniqueItems" && kind == JsonValueKind.Array && keyword.RawValue.ValueKind == JsonValueKind.True)
                || (name is "minLength" or "maxLength" && kind == JsonValueKind.String))
            {
                // These handlers materialize or compare instance data; annotations reuse compiled schema values.
                Charge(propertyNameBytes is { } bytes
                    ? 128L + (48L * bytes)
                    : (8L * JsonMarshal.GetRawUtf8Value(instance).Length) + (128L * CountValues(instance)));
            }

            if (name is "required" or "dependentRequired" && kind == JsonValueKind.Object)
            {
                Charge(8L * JsonMarshal.GetRawUtf8Value(keyword.RawValue).Length);
            }

            if (kind == JsonValueKind.Number && (name is "minimum" or "maximum"
                or "exclusiveMinimum" or "exclusiveMaximum" or "multipleOf"
                || (name == "type" && ((SchemaValueType)keyword.Value! & (SchemaValueType.Integer | SchemaValueType.Number)) == SchemaValueType.Integer)))
            {
                // Comparisons materialize number text; integer-only type checks allocate a significand buffer.
                // Types accepting number or rejecting it without an integer check never parse its text.
                Charge(8L * (JsonMarshal.GetRawUtf8Value(instance).Length
                    + (name == "type" ? 0 : JsonMarshal.GetRawUtf8Value(keyword.RawValue).Length)));
            }

            switch (name)
            {
                case "$ref":
                    var pointer = JsonPointer.Parse(keyword.RawValue.GetString()!);
                    Visit(keyword.Subschemas[0], instance, Encoding.UTF8.GetByteCount(pointer.ToString()), childEvaluationBytes, instanceBytes, propertyNameBytes);
                    break;
                case "allOf" or "anyOf" or "oneOf":
                    foreach (var child in keyword.Subschemas)
                    {
                        var relativeBytes = Encoding.UTF8.GetByteCount(child.RelativePath.ToString());
                        Visit(child, instance, childDefinitionBytes + relativeBytes, childEvaluationBytes + relativeBytes, instanceBytes, propertyNameBytes);
                    }

                    break;
                case "not" or "if":
                case "then" or "else" when schema.Source.TryGetProperty("if", out _):
                    Visit(keyword.Subschemas[0], instance, childDefinitionBytes, childEvaluationBytes, instanceBytes, propertyNameBytes);
                    break;
                case "properties" or "dependentSchemas" when kind == JsonValueKind.Object:
                    foreach (var child in keyword.Subschemas)
                    {
                        if (child.RelativePath.Evaluate(instance) is not { } value)
                        {
                            continue;
                        }

                        var relativeBytes = Encoding.UTF8.GetByteCount(child.RelativePath.ToString());
                        Visit(child, name == "properties" ? value : instance,
                            childDefinitionBytes + relativeBytes, childEvaluationBytes + relativeBytes,
                            instanceBytes + (name == "properties" ? relativeBytes : 0));
                    }

                    break;
                case "additionalProperties" or "unevaluatedProperties" or "propertyNames" when kind == JsonValueKind.Object:
                    foreach (var member in instance.EnumerateObject())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (name == "additionalProperties" && schema.Source.TryGetProperty("properties", out var properties)
                            && properties.TryGetProperty(member.Name, out _))
                        {
                            continue;
                        }

                        // Unevaluated handlers may apply to every child, depending on preceding annotations.
                        Visit(keyword.Subschemas[0], member.Value, childDefinitionBytes, childEvaluationBytes,
                            instanceBytes + PointerSegmentBytes(member.Name),
                            name == "propertyNames" ? Encoding.UTF8.GetByteCount(member.Name) : null);
                    }

                    break;
                case "prefixItems" or "items" or "contains" or "unevaluatedItems" when kind == JsonValueKind.Array:
                    var index = 0;
                    var prefixCount = name == "items" ? (int?)keyword.Value ?? 0 : 0;
                    foreach (var value in instance.EnumerateArray())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (name == "prefixItems" && index >= keyword.Subschemas.Length)
                        {
                            break;
                        }

                        var relativeBytes = IndexSegmentBytes(index);
                        if (index >= prefixCount)
                        {
                            var child = keyword.Subschemas[name == "prefixItems" ? index : 0];
                            Visit(child, value,
                                childDefinitionBytes + (name == "prefixItems" ? relativeBytes : 0),
                                childEvaluationBytes + (name == "prefixItems" ? relativeBytes : 0), instanceBytes + relativeBytes);
                        }

                        index++;
                    }

                    break;
            }
        }
    }

    private int CountValues(JsonElement instance)
    {
        var values = 0;
        var reader = new Utf8JsonReader(JsonMarshal.GetRawUtf8Value(instance));
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.TokenType is not (JsonTokenType.PropertyName or JsonTokenType.EndObject or JsonTokenType.EndArray))
            {
                values++;
            }
        }

        return values;
    }

    private int PointerSegmentBytes(string name)
    {
        var bytes = 1 + Encoding.UTF8.GetByteCount(name);
        foreach (var character in name)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (character is '~' or '/')
            {
                bytes++;
            }
        }

        return bytes;
    }

    private static int IndexSegmentBytes(int index) => index switch
    {
        < 10 => 2,
        < 100 => 3,
        < 1000 => 4,
        _ => 5,
    };
}
