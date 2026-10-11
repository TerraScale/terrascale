using System.Diagnostics.Metrics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Json.Pointer;
using Json.Schema;
using TerraBase.Errors;
using TerraBase.Features.Memory;
using TerraBase.Primitives;

namespace TerraBase.Engine;

/// <summary>A local document write failed its collection's declared schema.</summary>
/// <param name="details">Bounded failed rules without document values.</param>
public sealed class CollectionSchemaException(IReadOnlyList<ValidationDetail> details)
    : Exception("the document does not satisfy the collection schema")
{
    /// <summary>Bounded JSON Pointer locations and failed keywords.</summary>
    public IReadOnlyList<ValidationDetail> Details { get; } = details;
}

// Schema admission bounds the evaluator's graph before the database writer executes it.
internal static class CollectionSchemaValidation
{
    public const int MaxBytes = 64 * 1024;
    // Leave one level for the replicated collection metadata envelope.
    public const int MaxDepth = 15;
    // Metadata also counts its root, created_at, and schema_dialect.
    public const int MaxValues = 4093;
    public const string Dialect = "https://json-schema.org/draft/2020-12/schema";
    private const int MaxExpandedSchemas = 128;
    private const int MaxErrors = 16;
    private const int MaxErrorPathBytes = 8192;

    public static Validation? Check(JsonElement schema, IMemoryBudget? memory = null, MemoryLane lane = MemoryLane.Foreground,
        Counter<long>? evaluations = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var decoding = Reserve(memory, lane, 16L * MaxBytes);
        try
        {
            if (JsonMarshal.GetRawUtf8Value(schema).Length > MaxBytes)
            {
                return Invalid("a collection schema is at most 64 KiB");
            }

            using var parsed = JsonDocument.Parse(JsonMarshal.GetRawUtf8Value(schema).ToArray(), new JsonDocumentOptions { MaxDepth = MaxDepth, AllowDuplicateProperties = false });
            schema = parsed.RootElement;
            cancellationToken.ThrowIfCancellationRequested();
            DocumentJson.ValidateValue(schema, MaxValues);
            cancellationToken.ThrowIfCancellationRequested();
            var nodes = 0;
            var referencedTargets = new HashSet<JsonElement>();
            Visit(schema, schema, [], ref nodes, referencedTargets, cancellationToken);
            using var compilation = Reserve(memory, lane, 8L * nodes * JsonMarshal.GetRawUtf8Value(schema).Length);
            // Each schema invokes the trusted vocabularies; also charge every evaluated input view.
            var evaluationBytes = (32768L * nodes) + MetaInputBytes(schema, cancellationToken);
            foreach (var target in referencedTargets)
            {
                evaluationBytes += MetaInputBytes(target, cancellationToken);
            }

            using var metaEvaluation = Reserve(memory, lane, evaluationBytes);
            using var metaCompilation = Reserve(memory, lane, 16L * MaxBytes);
            var metaSchema = BuildMetaSchema(evaluations, cancellationToken);
            if (!metaSchema.Evaluate(schema, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid)
            {
                return Invalid("the schema must satisfy the Draft 2020-12 meta-schema");
            }

            foreach (var target in referencedTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!metaSchema.Evaluate(target, new EvaluationOptions { OutputFormat = OutputFormat.Flag }).IsValid)
                {
                    throw new InvalidDataException("a reference must target a valid schema");
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = Build(schema, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        catch (Exception error) when (error is JsonSchemaException or JsonException or InvalidDataException or DocumentValidationException
            or FormatException or ArgumentException or InvalidOperationException or OverflowException)
        {
            return Invalid("the schema must be a bounded, self-contained Draft 2020-12 schema without recursive references or regular expressions");
        }
    }

    public static void Validate(JsonElement schema, ReadOnlyMemory<byte> json, CancellationToken cancellationToken, IMemoryBudget? memory = null, Counter<long>? evaluations = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var decoding = Reserve(memory, MemoryLane.Foreground, 16L * MaxBytes);
        var expandedSchemas = 0;
        Visit(schema, schema, [], ref expandedSchemas, cancellationToken: cancellationToken);
        using var compilation = Reserve(memory, MemoryLane.Foreground, 8L * expandedSchemas * JsonMarshal.GetRawUtf8Value(schema).Length);
        // Admission precedes the document parse as well as the evaluator's result allocations.
        using var documentMemory = Reserve(memory, MemoryLane.Foreground, 8L * json.Length);
        using var document = JsonDocument.Parse(json);
        var compiled = Build(schema, evaluations, cancellationToken);
        using var evaluation = CollectionSchemaEvaluationMemory.Reserve(compiled, document.RootElement, memory, cancellationToken);
        // List output preserves annotations without the stock handlers' concrete-type optimization guards.
        var result = compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        cancellationToken.ThrowIfCancellationRequested();
        if (result.IsValid)
        {
            return;
        }

        var details = new List<ValidationDetail>();
        var pathBytes = 0;
        Collect(result);
        if (details.Count == 0)
        {
            details.Add(new ValidationDetail("", "schema"));
        }

        throw new CollectionSchemaException(details);

        void Collect(EvaluationResults evaluated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (details.Count == MaxErrors)
            {
                return;
            }

            if (!evaluated.IsValid && evaluated.Errors is { } errors)
            {
                foreach (var keyword in errors.Keys)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = evaluated.InstanceLocation.ToString();
                    var bytes = System.Text.Encoding.UTF8.GetByteCount(path);
                    if (details.Count == MaxErrors || pathBytes + bytes > MaxErrorPathBytes)
                    {
                        return;
                    }

                    var detail = new ValidationDetail(path, string.IsNullOrEmpty(keyword) ? "schema" : keyword);
                    if (!details.Contains(detail))
                    {
                        details.Add(detail);
                        pathBytes += bytes;
                    }
                }
            }

            if (evaluated.Details is { } children)
            {
                foreach (var child in children)
                {
                    Collect(child);
                }
            }
        }
    }

    private static JsonSchema Build(JsonElement schema, Counter<long>? evaluations = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = JsonSchema.Build(schema, ExactSchemaKeywords.Options(evaluations, cancellationToken: cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static JsonSchema BuildMetaSchema(Counter<long>? evaluations, CancellationToken cancellationToken)
    {
        // Rebuild trusted resources privately so recursive meta-schema evaluation uses this operation's token.
        var options = ExactSchemaKeywords.Options(evaluations, metaSchema: true, cancellationToken: cancellationToken);
        JsonSchema[] resources = [MetaSchemas.Core202012, MetaSchemas.Applicator202012, MetaSchemas.Unevaluated202012,
            MetaSchemas.Validation202012, MetaSchemas.Metadata202012, MetaSchemas.FormatAnnotation202012,
            MetaSchemas.Content202012, MetaSchemas.Draft202012];
        JsonSchema? result = null;
        foreach (var resource in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result = JsonSchema.Build(resource.Root.Source, options, resource.BaseUri);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return result!;
    }

    private static IMemoryReservation? Reserve(IMemoryBudget? memory, MemoryLane lane, long bytes)
    {
        var reservation = memory?.TryReserve(lane, bytes);
        if (memory is not null && reservation is null)
        {
            throw new StorageBusyException();
        }

        return reservation;
    }

    // Trusted meta-schemas also evaluate annotation arrays; Flag still copies instance pointers.
    private static long MetaInputBytes(JsonElement schema, CancellationToken cancellationToken)
    {
        var bytes = 16L * JsonMarshal.GetRawUtf8Value(schema).Length;
        Count(schema, 0);
        return bytes;

        void Count(JsonElement value, int pointerBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bytes += 1024L + (16L * pointerBytes);
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in value.EnumerateObject())
                    {
                        var name = property.Name;
                        var segmentBytes = 1 + Encoding.UTF8.GetByteCount(name);
                        foreach (var character in name)
                        {
                            if (character is '~' or '/')
                            {
                                segmentBytes++;
                            }
                        }

                        Count(property.Value, pointerBytes + segmentBytes);
                    }

                    break;
                case JsonValueKind.Array:
                    var index = 0;
                    foreach (var item in value.EnumerateArray())
                    {
                        Count(item, pointerBytes + 1 + index.ToString(CultureInfo.InvariantCulture).Length);
                        index++;
                    }

                    break;
            }
        }
    }

    // Inspect schema locations only: const/enum/example payloads can legitimately contain keyword-looking data.
    private static void Visit(JsonElement schema, JsonElement root, HashSet<JsonElement> ancestors, ref int nodes, HashSet<JsonElement>? referencedTargets = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (++nodes > MaxExpandedSchemas || !ancestors.Add(schema))
        {
            throw new InvalidDataException("schema expansion or recursion exceeds admission");
        }

        if (schema.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            ancestors.Remove(schema);
            return;
        }

        foreach (var property in schema.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (property.Name is "minLength" or "maxLength" or "minItems" or "maxItems" or "minContains" or "maxContains"
                or "minProperties" or "maxProperties")
            {
                if (property.Value.ValueKind != JsonValueKind.Number
                    || !ExactNumber.TryParse(JsonMarshal.GetRawUtf8Value(property.Value), out var integer)
                    || integer.Mantissa.Sign < 0 || (!integer.Mantissa.IsZero && integer.Exponent < 0))
                {
                    throw new InvalidDataException("schema count keywords require exact nonnegative integers");
                }
            }

            switch (property.Name)
            {
                case "$schema" when property.Value.GetString() != Dialect:
                case "$id" or "$anchor" or "$dynamicAnchor" or "$dynamicRef" or "$recursiveRef" or "$recursiveAnchor" or "$vocabulary":
                case "pattern" or "patternProperties" or "definitions" or "dependencies":
                    throw new InvalidDataException("unsupported schema keyword or dialect");
                case "$ref":
                    if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } reference
                        || !reference.StartsWith('#') || !JsonPointer.TryParse(reference, out var pointer)
                        || pointer.Evaluate(root) is not { } target)
                    {
                        throw new InvalidDataException("a reference must resolve to a local JSON Pointer");
                    }

                    referencedTargets?.Add(target);
                    Visit(target, root, ancestors, ref nodes, referencedTargets, cancellationToken);
                    break;
                case "$defs":
                    // Definitions are compiled and admitted, but evaluated only through a reachable $ref.
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        Visit(child.Value, root, ancestors, ref nodes, referencedTargets, cancellationToken);
                    }

                    break;
                case "properties" or "dependentSchemas":
                    foreach (var child in property.Value.EnumerateObject())
                    {
                        Visit(child.Value, root, ancestors, ref nodes, referencedTargets, cancellationToken);
                    }

                    break;
                case "allOf" or "anyOf" or "oneOf" or "prefixItems":
                    foreach (var child in property.Value.EnumerateArray())
                    {
                        Visit(child, root, ancestors, ref nodes, referencedTargets, cancellationToken);
                    }

                    break;
                case "items" or "contains" or "additionalProperties" or "unevaluatedProperties" or "unevaluatedItems" or "propertyNames"
                    or "not" or "if" or "then" or "else" or "contentSchema":
                    Visit(property.Value, root, ancestors, ref nodes, referencedTargets, cancellationToken);
                    break;
            }
        }

        ancestors.Remove(schema);
    }

    private static Validation Invalid(string message) => new(message, "invalid_schema");
}
