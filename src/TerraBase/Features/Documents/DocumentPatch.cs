using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TerraBase.Engine.Lattices;
using TerraBase.Errors;
using TerraBase.Hosting;

namespace TerraBase.Features.Documents;

/// <summary>An applied patch, or why it does not apply: an invalid operand (400) or a target of the wrong type (409 <c>patch_conflict</c>).</summary>
public readonly union PatchOutcome(Done, Validation, Conflict);

/// <summary>
/// A document patch: an RFC 7396 merge patch or a list of TerraBase patch operations. Applying it to the document also
/// lists what the document's state records (docs/terrabase/multi-region-replication.md sections 4 and 13): a base for
/// every top-level field a merge key or a <c>set</c>, <c>unset</c> or <c>append</c> changed, and the counter and set
/// entries of <c>inc</c>, <c>add</c> and <c>remove</c>, in order.
/// </summary>
public abstract record DocumentPatch
{
    /// <summary>Media type of RFC 7396 merge patches.</summary>
    public const string MergeMediaType = "application/merge-patch+json";

    /// <summary>Media type of TerraBase patch operations.</summary>
    public const string OpsMediaType = "application/vnd.terrabase.ops+json";

    private const int MaxOps = 256;

    private const int MaxElementBytes = 1024;

    // The version a patch applies its counter and set operations at on a one-field copy of the document.
    private static readonly EntryVersion Local = new(new Hlc(1), default);

    /// <summary>
    /// Applies the patch to a live document and appends what it writes to <paramref name="operations"/>;
    /// <c>patch_conflict</c> when a target has the wrong type.
    /// </summary>
    public abstract PatchOutcome Apply(JsonObject document, List<FieldOperation> operations);

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

        if (op.Op is "inc" or "add" or "remove" && path.Count != 1)
        {
            return new Validation("inc, add and remove take a top-level field", "nested_lattice_path");
        }

        return op.Op switch
        {
            "set" or "append" when hasValue && !hasBy => new PatchStep(op.Op, path, op.Value.Clone()),
            "unset" when !hasValue && !hasBy => new PatchStep(op.Op, path, default),
            "inc" when hasBy && !hasValue => op.By.ValueKind == JsonValueKind.Number && op.By.TryGetInt64(out var by) && by != 0
                ? new PatchStep(op.Op, path, default, by)
                : Invalid("inc needs a non-zero 64-bit integer by"),
            "add" or "remove" when hasValue && !hasBy => op.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array
                    || Encoding.UTF8.GetByteCount(DocumentState.Canonical(op.Value)) > MaxElementBytes
                ? Invalid("add and remove take a JSON scalar of at most 1 KiB")
                : new PatchStep(op.Op, path, op.Value.Clone()),
            _ => Invalid("each op is set, append, add or remove with value, unset, or inc with an integer by"),
        };
    }

    private static Validation Invalid(string message) => new(message, "invalid_patch");

    private static Conflict PatchConflict(string message) => new(message, "patch_conflict");

    private sealed record MergePatch(JsonObject Patch) : DocumentPatch
    {
        public override PatchOutcome Apply(JsonObject document, List<FieldOperation> operations)
        {
            MergeInto(document, Patch);
            foreach (var (name, _) in Patch)
            {
                operations.Add(Plain(document, name));
            }

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
        public override PatchOutcome Apply(JsonObject document, List<FieldOperation> operations)
        {
            // Plain steps write a field's base once, with its value before the next counter or set step on that field
            // or at the end: steps on different fields commute.
            var changed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var step in Steps)
            {
                var field = step.Path[0];
                if (step.Op is "inc" or "add" or "remove" && changed.Remove(field))
                {
                    operations.Add(Plain(document, field));
                }

                var outcome = step.Apply(document, operations, out var written);
                if (outcome is not Done)
                {
                    return outcome;
                }

                if (written)
                {
                    changed.Add(field);
                }
            }

            operations.AddRange(changed.Select(field => Plain(document, field)));
            return new Done();
        }
    }

    // The base a plain write leaves in a top-level field: its value, or a tombstone.
    private static FieldOperation Plain(JsonObject document, string field) =>
        document.TryGetPropertyValue(field, out var value)
            ? new FieldOperation(FieldOperationKind.Set, field, JsonSerializer.SerializeToElement(value, TerraBaseJsonContext.Default.JsonNode))
            : new FieldOperation(FieldOperationKind.Unset, field);

    private sealed record PatchStep(string Op, IReadOnlyList<string> Path, JsonElement Value, long By = 0)
    {
        // Written: a plain step changed its top-level field, to be listed by the caller. Counter and set steps list themselves.
        public PatchOutcome Apply(JsonObject document, List<FieldOperation> operations, out bool written)
        {
            written = false;
            if (Op is "inc" or "add" or "remove")
            {
                return ApplyLattice(document, operations);
            }

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

            written = true;
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

        // A counter or set step, applied as the field's state would apply it to the field's current value: an integer
        // count, or a set's distinct elements in UTF-8 order.
        private PatchOutcome ApplyLattice(JsonObject document, List<FieldOperation> operations)
        {
            var field = Path[0];
            document.TryGetPropertyValue(field, out var current);
            if (Op == "inc" && current is JsonValue number && number.TryGetValue<JsonElement>(out var start) && start.ValueKind == JsonValueKind.Number
                && start.TryGetInt64(out var count) && ((Int128)count + By < long.MinValue || (Int128)count + By > long.MaxValue))
            {
                return PatchConflict("inc leaves the 64-bit integer range");
            }

            var operation = Op switch
            {
                "inc" => new FieldOperation(FieldOperationKind.Increment, field, By: By),
                "add" => new FieldOperation(FieldOperationKind.Add, field, Value),
                _ => new FieldOperation(FieldOperationKind.Remove, field, Value),
            };
            var copy = new JsonObject();
            if (document.ContainsKey(field))
            {
                copy[field] = current?.DeepClone();
            }

            DocumentState state;
            try
            {
                state = operation.Apply(DocumentState.Bottom.Put(default, JsonSerializer.SerializeToElement(copy, TerraBaseJsonContext.Default.JsonObject)), Local);
            }
            catch (InvalidOperationException)
            {
                return PatchConflict(Op == "inc" ? "inc needs an integer" : "add and remove need an array");
            }

            // The document limits, checked on the patched document, bound a set (at most 4096 values in a document).
            var after = JsonNode.Parse(state.Materialize(), documentOptions: RequestJson.StoredDocumentOptions)!.AsObject();
            if (after.TryGetPropertyValue(field, out var value))
            {
                after.Remove(field);
                document[field] = value;
            }
            else
            {
                document.Remove(field);
            }

            operations.Add(operation);
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
