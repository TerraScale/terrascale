using System.Diagnostics.Metrics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Json.Schema;
using Json.Schema.Keywords;
using TerraBase.Primitives;

namespace TerraBase.Engine;

// JsonSchema.Net 8.0.5 rounds integer checks through double and equality through decimal.
// It also counts graphemes for string lengths and restarts items indices after prefixItems.
// Keep stock semantics and annotation ordering while checking cancellation at every keyword boundary.
internal static class ExactSchemaKeywords
{
    private static Dialect CreateDialect(Counter<long>? evaluations, bool metaSchema, CancellationToken cancellationToken)
    {
        IKeywordHandler[] stock = [
            .. Vocabulary.Draft202012_Core.Keywords,
            .. Vocabulary.Draft202012_Applicator.Keywords,
            .. Vocabulary.Draft202012_Validation.Keywords,
            .. Vocabulary.Draft202012_Unevaluated.Keywords,
            .. Vocabulary.Draft202012_FormatAnnotation.Keywords,
            .. Vocabulary.Draft202012_MetaData.Keywords,
            .. Vocabulary.Draft202012_Content.Keywords,
            // The library's applicator vocabulary omits then, although its stock dialect includes it.
            ThenKeyword.Instance,
        ];
        return new Dialect(stock.Select(Wrap))
        {
            Id = MetaSchemas.Draft202012Id,
            AllowUnknownKeywords = true,
        };

        IKeywordHandler Wrap(IKeywordHandler handler)
        {
            return handler.Name switch
            {
                "$id" or "$dynamicAnchor" when metaSchema => handler,
                "$ref" => new CancelableReference(evaluations, cancellationToken),
                "$dynamicRef" when metaSchema => new CancelableDynamicReference(evaluations, cancellationToken),
                "if" => new IfHandler(handler, evaluations, cancellationToken),
                "then" or "else" => new ConditionalHandler(handler, evaluations, cancellationToken),
                "properties" => new PropertiesHandler(handler, evaluations, cancellationToken),
                "patternProperties" => new PatternPropertiesHandler(handler, evaluations, cancellationToken),
                "additionalProperties" => new AdditionalPropertiesHandler(handler, evaluations, cancellationToken),
                _ => new ExactKeyword(handler, evaluations, cancellationToken),
            };
        }
    }

    internal static BuildOptions Options(Counter<long>? evaluations = null, bool metaSchema = false, CancellationToken cancellationToken = default) =>
        new()
        {
            Dialect = CreateDialect(evaluations, metaSchema, cancellationToken),
            SchemaRegistry = new SchemaRegistry(),
        };

    private sealed class IfHandler(IKeywordHandler stock, Counter<long>? evaluations, CancellationToken token)
        : ExactKeyword(stock, evaluations, token);

    [DependsOnAnnotationsFrom(typeof(IfHandler))]
    private sealed class ConditionalHandler(IKeywordHandler stock, Counter<long>? evaluations, CancellationToken token)
        : ExactKeyword(stock, evaluations, token);

    private sealed class PropertiesHandler(IKeywordHandler stock, Counter<long>? evaluations, CancellationToken token)
        : ExactKeyword(stock, evaluations, token);

    private sealed class PatternPropertiesHandler(IKeywordHandler stock, Counter<long>? evaluations, CancellationToken token)
        : ExactKeyword(stock, evaluations, token);

    [DependsOnAnnotationsFrom(typeof(PropertiesHandler))]
    [DependsOnAnnotationsFrom(typeof(PatternPropertiesHandler))]
    private sealed class AdditionalPropertiesHandler(IKeywordHandler stock, Counter<long>? evaluations, CancellationToken token)
        : ExactKeyword(stock, evaluations, token);

    // Reference resolution recognizes this concrete base type before calling its internal resolver.
    private sealed class CancelableReference(Counter<long>? evaluations, CancellationToken token) : RefKeyword
    {
        public override object? ValidateKeywordValue(JsonElement value)
        {
            token.ThrowIfCancellationRequested();
            var result = base.ValidateKeywordValue(value);
            token.ThrowIfCancellationRequested();
            return result;
        }

        public override void BuildSubschemas(KeywordData keyword, BuildContext context)
        {
            token.ThrowIfCancellationRequested();
            base.BuildSubschemas(keyword, context);
            token.ThrowIfCancellationRequested();
        }

        public override KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            BeginEvaluation(Name, evaluations, token);
            var result = base.Evaluate(keyword, context);
            token.ThrowIfCancellationRequested();
            return result;
        }
    }

    // Preserve the Draft 2020-12 resolver that marks #meta references as dynamic during compilation.
    private sealed class CancelableDynamicReference(Counter<long>? evaluations, CancellationToken token)
        : Json.Schema.Keywords.Draft202012.DynamicRefKeyword
    {
        public override object? ValidateKeywordValue(JsonElement value)
        {
            token.ThrowIfCancellationRequested();
            var result = base.ValidateKeywordValue(value);
            token.ThrowIfCancellationRequested();
            return result;
        }

        public override void BuildSubschemas(KeywordData keyword, BuildContext context)
        {
            token.ThrowIfCancellationRequested();
            base.BuildSubschemas(keyword, context);
            token.ThrowIfCancellationRequested();
        }

        public override KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            BeginEvaluation(Name, evaluations, token);
            var result = base.Evaluate(keyword, context);
            token.ThrowIfCancellationRequested();
            return result;
        }
    }

    private static void BeginEvaluation(string name, Counter<long>? evaluations, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        evaluations?.Add(1, new KeyValuePair<string, object?>("keyword", name));
        token.ThrowIfCancellationRequested();
    }

    private class ExactKeyword(IKeywordHandler stock, Counter<long>? evaluations, CancellationToken cancellationToken) : IKeywordHandler
    {
        public string Name => stock.Name;

        public object? ValidateKeywordValue(JsonElement value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = stock.ValidateKeywordValue(value);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }
            catch (JsonSchemaException)
            {
                throw new JsonSchemaException($"invalid {Name} keyword");
            }
        }

        public void BuildSubschemas(KeywordData keyword, BuildContext context)
        {
            cancellationToken.ThrowIfCancellationRequested();
            stock.BuildSubschemas(keyword, context);
            cancellationToken.ThrowIfCancellationRequested();
        }

        public KeywordEvaluation Evaluate(KeywordData keyword, EvaluationContext context)
        {
            BeginEvaluation(Name, evaluations, cancellationToken);
            if (Name == "items")
            {
                return EvaluateItems(keyword, context, cancellationToken);
            }

            if ((Name is "minLength" or "maxLength") && context.Instance.ValueKind != JsonValueKind.String)
            {
                return KeywordEvaluation.Ignore;
            }

            if (Name == "uniqueItems" && (context.Instance.ValueKind != JsonValueKind.Array
                || keyword.RawValue.ValueKind == JsonValueKind.False))
            {
                return KeywordEvaluation.Ignore;
            }

            if (Name is not ("type" or "const" or "enum" or "uniqueItems" or "minLength" or "maxLength"))
            {
                // Boolean/empty children have no handlers; document and schema admission bound their traversal.
                var result = stock.Evaluate(keyword, context);
                cancellationToken.ThrowIfCancellationRequested();
                return result;
            }

            // DeepEquals normalizes the UTF-8 decimal significand and exponent, without numeric rounding.
            // Admission bounds exponents, depth and values before these handlers run.
            var valid = Name switch
            {
                "type" => MatchesType(context.Instance, (SchemaValueType)keyword.Value!),
                "const" => JsonElement.DeepEquals(keyword.RawValue, context.Instance),
                "enum" => MatchesEnum(keyword.RawValue, context.Instance, cancellationToken),
                "uniqueItems" => IsUnique(context.Instance, cancellationToken),
                "minLength" => CountCodePoints(context.Instance, cancellationToken) >= (long)keyword.Value!,
                "maxLength" => CountCodePoints(context.Instance, cancellationToken) <= (long)keyword.Value!,
                _ => throw new InvalidOperationException("unsupported exact schema keyword"),
            };

            cancellationToken.ThrowIfCancellationRequested();
            return new KeywordEvaluation
            {
                Keyword = Name,
                IsValid = valid,
                Error = valid ? null : $"{Name} validation failed",
            };
        }
    }

    private static bool MatchesEnum(JsonElement choices, JsonElement instance, CancellationToken cancellationToken)
    {
        foreach (var choice in choices.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (JsonElement.DeepEquals(choice, instance))
            {
                return true;
            }
        }

        return false;
    }

    private static int CountCodePoints(JsonElement instance, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var rune in instance.GetString()!.EnumerateRunes())
        {
            cancellationToken.ThrowIfCancellationRequested();
            count++;
        }

        return count;
    }

    private static KeywordEvaluation EvaluateItems(KeywordData keyword, EvaluationContext context, CancellationToken cancellationToken)
    {
        if (context.Instance.ValueKind != JsonValueKind.Array)
        {
            return KeywordEvaluation.Ignore;
        }

        // Stock BuildSubschemas stores the prefix count and builds the child on our private dialect.
        var prefixItemsCount = (int?)keyword.Value ?? 0;
        var subschema = keyword.Subschemas[0];
        var evaluationPath = context.EvaluationPath.Combine("items");
        var details = new List<EvaluationResults>();
        var index = prefixItemsCount;
        foreach (var instance in context.Instance.EnumerateArray().Skip(prefixItemsCount))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemContext = context with
            {
                InstanceLocation = context.InstanceLocation.Combine(index),
                Instance = instance,
                EvaluationPath = evaluationPath,
            };
            var evaluated = subschema.Evaluate(itemContext);
            details.Add(evaluated);
            if (context.CanOptimize && !evaluated.IsValid)
            {
                return new KeywordEvaluation
                {
                    Keyword = "items",
                    IsValid = false,
                };
            }

            index++;
        }

        return new KeywordEvaluation
        {
            Keyword = "items",
            IsValid = details.All(evaluated => evaluated.IsValid),
            Details = [.. details],
            Annotation = Json.Schema.JsonElementExtensions.True,
        };
    }

    private static bool MatchesType(JsonElement instance, SchemaValueType expected)
    {
        var actual = instance.ValueKind switch
        {
            JsonValueKind.Object => SchemaValueType.Object,
            JsonValueKind.Array => SchemaValueType.Array,
            JsonValueKind.String => SchemaValueType.String,
            JsonValueKind.True or JsonValueKind.False => SchemaValueType.Boolean,
            JsonValueKind.Null => SchemaValueType.Null,
            JsonValueKind.Number => SchemaValueType.Number,
            _ => (SchemaValueType)0,
        };
        if ((expected & actual) != 0)
        {
            return true;
        }

        return actual == SchemaValueType.Number && (expected & SchemaValueType.Integer) != 0
            && ExactNumber.TryParse(JsonMarshal.GetRawUtf8Value(instance), out var number)
            && (number.Mantissa.IsZero || number.Exponent >= 0);
    }

    private static bool IsUnique(JsonElement instance, CancellationToken cancellationToken)
    {
        var seen = new HashSet<JsonElement>(new ExactJsonComparer(cancellationToken));
        foreach (var item in instance.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(item))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class ExactJsonComparer(CancellationToken cancellationToken) : IEqualityComparer<JsonElement>
    {
        public bool Equals(JsonElement x, JsonElement y)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return JsonElement.DeepEquals(x, y);
        }

        public int GetHashCode(JsonElement value)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    // Object equality ignores member order; each name/value pair contributes independently.
                    var membersHash = 0;
                    var count = 0;
                    foreach (var member in value.EnumerateObject())
                    {
                        membersHash = unchecked(membersHash + HashCode.Combine(
                            StringComparer.Ordinal.GetHashCode(member.Name), GetHashCode(member.Value)));
                        count++;
                    }

                    return HashCode.Combine(value.ValueKind, count, membersHash);
                case JsonValueKind.Array:
                    var arrayHash = new HashCode();
                    arrayHash.Add(value.ValueKind);
                    foreach (var item in value.EnumerateArray())
                    {
                        arrayHash.Add(GetHashCode(item));
                    }

                    return arrayHash.ToHashCode();
                case JsonValueKind.String:
                    return HashCode.Combine(value.ValueKind, StringComparer.Ordinal.GetHashCode(value.GetString()!));
                case JsonValueKind.Number:
                    // Admission has already required a bounded exact number, including normalization of zero.
                    if (!ExactNumber.TryParse(JsonMarshal.GetRawUtf8Value(value), out var number))
                    {
                        throw new InvalidOperationException("an admitted JSON number must be exact");
                    }

                    return HashCode.Combine(value.ValueKind, number);
                default:
                    return (int)value.ValueKind;
            }
        }
    }
}
