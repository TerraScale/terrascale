using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Errors;
using TerraBase.Primitives;

namespace TerraBase.Features.Documents;

/// <summary>An applied patch, or why it does not apply: an invalid operand (400) or a target of the wrong type (409 <c>patch_conflict</c>).</summary>
public readonly union PatchOutcome(Done, Validation, Conflict);

/// <summary>A document patch: an RFC 7396 merge patch or a list of TerraBase patch operations.</summary>
public abstract record DocumentPatch
{
    /// <summary>Media type of RFC 7396 merge patches.</summary>
    public const string MergeMediaType = "application/merge-patch+json";

    /// <summary>Media type of TerraBase patch operations.</summary>
    public const string OpsMediaType = "application/vnd.terrabase.ops+json";

    private const int MaxOps = 256;

    /// <summary>Applies the patch to a live document; <c>patch_conflict</c> when a target has the wrong type.</summary>
    public abstract PatchOutcome Apply(JsonObject document);

    /// <summary>Parses a merge patch, which must be an object.</summary>
    public static Valid<DocumentPatch> CheckMerge(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            return Invalid("a merge patch must be a JSON object");
        }

        if (RequestJson.CheckValue(patch, "invalid_patch") is Validation error)
        {
            return error;
        }

        DocumentPatch merge = new MergePatch(JsonObject.Create(patch.Clone())!);
        return merge;
    }

    /// <summary>Parses patch operations; the first invalid one names the rule it breaks.</summary>
    public static Valid<DocumentPatch> CheckOps(IReadOnlyList<PatchOpRequest>? ops)
    {
        if (ops is not { Count: > 0 and <= MaxOps })
        {
            return Invalid("ops must hold 1 to 256 operations");
        }

        var steps = new List<PatchStep>(ops.Count);
        foreach (var op in ops)
        {
            switch (Step(op))
            {
                case Validation error:
                    return error;
                case PatchStep step:
                    steps.Add(step);
                    break;
            }
        }

        DocumentPatch patch = new OpsPatch([.. steps]);
        return patch;
    }

    private static Valid<PatchStep> Step(PatchOpRequest op) =>
        QueryParser.CheckPointer(op.Path, "invalid_patch", "patch paths are JSON Pointers to object members") switch
        {
            Validation error => error,
            IReadOnlyList<string> path => Step(op, path),
        };

    private static Valid<PatchStep> Step(PatchOpRequest op, IReadOnlyList<string> path)
    {
        var hasValue = op.Value.ValueKind != JsonValueKind.Undefined;
        var hasBy = op.By.ValueKind != JsonValueKind.Undefined;
        if (hasValue && RequestJson.CheckValue(op.Value, "invalid_patch") is Validation badValue)
        {
            return badValue;
        }

        if (hasBy && RequestJson.CheckValue(op.By, "invalid_patch") is Validation badBy)
        {
            return badBy;
        }

        return op.Op switch
        {
            "set" or "append" when hasValue && !hasBy => new PatchStep(op.Op, path, op.Value.Clone()),
            "unset" when !hasValue && !hasBy => new PatchStep(op.Op, path, default),
            "inc" when hasBy && !hasValue && op.By.ValueKind == JsonValueKind.Number => new PatchStep(op.Op, path, op.By.Clone()),
            _ => Invalid("each op is set or append with value, unset, or inc with a number by"),
        };
    }

    private static Validation Invalid(string message) => new(message, "invalid_patch");

    private static Conflict PatchConflict(string message) => new(message, "patch_conflict");

    private sealed record MergePatch(JsonObject Patch) : DocumentPatch
    {
        public override PatchOutcome Apply(JsonObject document)
        {
            MergeInto(document, Patch);
            return new Done();
        }

        // RFC 7396: null removes a member, an object merges recursively, anything else replaces.
        private static void MergeInto(JsonObject target, JsonObject patch)
        {
            foreach (var (name, value) in patch)
            {
                if (value is null)
                {
                    target.Remove(name);
                }
                else if (value is JsonObject child)
                {
                    if (target[name] is not JsonObject existing)
                    {
                        existing = [];
                        target[name] = existing;
                    }

                    MergeInto(existing, child);
                }
                else
                {
                    target[name] = value.DeepClone();
                }
            }
        }
    }

    private sealed record OpsPatch(IReadOnlyList<PatchStep> Steps) : DocumentPatch
    {
        public override PatchOutcome Apply(JsonObject document)
        {
            foreach (var step in Steps)
            {
                var outcome = step.Apply(document);
                if (outcome is not Done)
                {
                    return outcome;
                }
            }

            return new Done();
        }
    }

    private sealed record PatchStep(string Op, IReadOnlyList<string> Path, JsonElement Value)
    {
        public PatchOutcome Apply(JsonObject document)
        {
            var parent = Parent(document, create: Op != "unset", out var crossed);
            if (crossed)
            {
                // Missing parents are a no-op for unset; an existing non-object parent is a conflict for every op.
                return PatchConflict("a patch path crosses a value that is not an object");
            }

            if (parent is null)
            {
                return new Done();
            }

            var name = Path[^1];
            parent.TryGetPropertyValue(name, out var current);
            switch (Op)
            {
                case "set":
                    parent[name] = JsonNode.Parse(Value.GetRawText());
                    break;
                case "unset":
                    parent.Remove(name);
                    break;
                case "inc":
                    if (!ExactNumber.TryParse(Value.GetRawText(), out var by))
                    {
                        return Invalid("inc needs a number by");
                    }

                    var start = default(ExactNumber);
                    if (parent.ContainsKey(name))
                    {
                        if (current is not JsonValue number || number.GetValueKind() != JsonValueKind.Number)
                        {
                            return PatchConflict("inc needs a number");
                        }

                        // Stored numbers are within the limits; only an earlier op of this patch can leave one outside.
                        if (!ExactNumber.TryParse(number.ToJsonString(), out start))
                        {
                            return new Validation("numbers have at most 128 significant digits and a magnitude of 0 or 1e-307 to below 1e308");
                        }
                    }

                    // The document limits (128 significant digits, magnitude) then decide whether the exact sum is stored.
                    parent[name] = JsonNode.Parse(ExactNumber.Add(start, by).ToString());
                    break;
                default:
                    if (current is null && !parent.ContainsKey(name))
                    {
                        current = new JsonArray();
                        parent[name] = current;
                    }

                    if (current is not JsonArray array)
                    {
                        return PatchConflict("append needs an array");
                    }

                    array.Add(JsonNode.Parse(Value.GetRawText()));
                    break;
            }

            return new Done();
        }

        // The object holding the last segment; missing parents are created, or null when not creating. Crossed when a
        // parent on the path exists but is not an object.
        private JsonObject? Parent(JsonObject document, bool create, out bool crossed)
        {
            crossed = false;
            var node = document;
            foreach (var segment in Path.Take(Path.Count - 1))
            {
                if (!node.TryGetPropertyValue(segment, out var child))
                {
                    if (!create)
                    {
                        return null;
                    }

                    child = new JsonObject();
                    node[segment] = child;
                }

                if (child is not JsonObject next)
                {
                    crossed = true;
                    return null;
                }

                node = next;
            }

            return node;
        }
    }
}
