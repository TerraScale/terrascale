using System.Text.Json;
using System.Text.Json.Nodes;
using TerraScale.TerraBase.Hosting;

namespace TerraScale.TerraBase.Features.Documents;

/// <summary>A document patch: an RFC 7396 merge patch or a list of TerraBase patch operations.</summary>
public abstract record DocumentPatch
{
    /// <summary>Media type of RFC 7396 merge patches.</summary>
    public const string MergeMediaType = "application/merge-patch+json";

    /// <summary>Media type of TerraBase patch operations.</summary>
    public const string OpsMediaType = "application/vnd.terrabase.ops+json";

    private const int MaxOps = 256;

    /// <summary>Applies the patch to a live document; fails with <c>patch_conflict</c> when a target has the wrong type.</summary>
    public abstract void Apply(JsonObject document);

    /// <summary>Parses a merge patch, which must be an object.</summary>
    public static DocumentPatch Merge(JsonElement patch)
    {
        if (patch.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("a merge patch must be a JSON object");
        }

        RequestJson.ValidateValue(patch, "invalid_patch");
        return new MergePatch(JsonObject.Create(patch.Clone())!);
    }

    /// <summary>Parses patch operations.</summary>
    public static DocumentPatch Ops(IReadOnlyList<PatchOpRequest>? ops)
    {
        if (ops is not { Count: > 0 and <= MaxOps })
        {
            throw Invalid("ops must hold 1 to 256 operations");
        }

        return new OpsPatch([.. ops.Select(Parse)]);
    }

    private static PatchStep Parse(PatchOpRequest op)
    {
        var path = QueryParser.Pointer(op.Path, "invalid_patch")
            ?? throw Invalid("patch paths are JSON Pointers to object members");
        var hasValue = op.Value.ValueKind != JsonValueKind.Undefined;
        var hasBy = op.By.ValueKind != JsonValueKind.Undefined;
        if (hasValue)
        {
            RequestJson.ValidateValue(op.Value, "invalid_patch");
        }

        if (hasBy)
        {
            RequestJson.ValidateValue(op.By, "invalid_patch");
        }

        return op.Op switch
        {
            "set" or "append" when hasValue && !hasBy => new PatchStep(op.Op, path, op.Value.Clone()),
            "unset" when !hasValue && !hasBy => new PatchStep(op.Op, path, default),
            "inc" when hasBy && !hasValue && op.By.ValueKind == JsonValueKind.Number => new PatchStep(op.Op, path, op.By.Clone()),
            _ => throw Invalid("each op is set or append with value, unset, or inc with a number by"),
        };
    }

    private static ApiException Invalid(string message) => new(StatusCodes.Status400BadRequest, "invalid_patch", message);

    private static ApiException Conflict(string message) => new(StatusCodes.Status409Conflict, "patch_conflict", message);

    private sealed record MergePatch(JsonObject Patch) : DocumentPatch
    {
        public override void Apply(JsonObject document) => MergeInto(document, Patch);

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
        public override void Apply(JsonObject document)
        {
            foreach (var step in Steps)
            {
                step.Apply(document);
            }
        }
    }

    private sealed record PatchStep(string Op, IReadOnlyList<string> Path, JsonElement Value)
    {
        public void Apply(JsonObject document)
        {
            var parent = Parent(document, create: Op != "unset");
            if (parent is null)
            {
                return;
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
                        throw Invalid("inc needs a number by");
                    }

                    var start = default(ExactNumber);
                    if (parent.ContainsKey(name))
                    {
                        if (current is not JsonValue number || number.GetValueKind() != JsonValueKind.Number)
                        {
                            throw Conflict("inc needs a number");
                        }

                        // Stored numbers are within the limits; only an earlier op of this patch can leave one outside.
                        if (!ExactNumber.TryParse(number.ToJsonString(), out start))
                        {
                            throw new ApiException("numbers have at most 128 significant digits and a magnitude of 0 or 1e-307 to below 1e308");
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

                    (current as JsonArray ?? throw Conflict("append needs an array")).Add(JsonNode.Parse(Value.GetRawText()));
                    break;
            }
        }

        // The object holding the last segment; missing parents are created, or null when not creating.
        private JsonObject? Parent(JsonObject document, bool create)
        {
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

                // Missing parents are a no-op for unset; an existing non-object parent is a conflict for every op.
                node = child as JsonObject ?? throw Conflict("a patch path crosses a value that is not an object");
            }

            return node;
        }
    }
}
