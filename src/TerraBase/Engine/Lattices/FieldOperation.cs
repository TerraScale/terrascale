using System.Text.Json;

namespace TerraBase.Engine.Lattices;

/// <summary>What a <see cref="FieldOperation"/> writes (docs/terrabase/multi-region-replication.md section 4, Operations).</summary>
public enum FieldOperationKind
{
    /// <summary>A last-writer-wins base: merge-patch keys, ops <c>set</c> and <c>append</c>.</summary>
    Set,

    /// <summary>A tombstone base: merge-patch null, ops <c>unset</c>.</summary>
    Unset,

    /// <summary>This node's PN-counter entry: ops <c>inc</c>.</summary>
    Increment,

    /// <summary>A new add-wins dot: ops <c>add</c>.</summary>
    Add,

    /// <summary>Every dot of the element this node sees, removed: ops <c>remove</c>.</summary>
    Remove,
}

/// <summary>One operation of a PATCH on one top-level field, as the document's state records it.</summary>
/// <param name="Kind">The entry it writes.</param>
/// <param name="Field">The top-level field.</param>
/// <param name="Value">The value of <c>Set</c>, or the element of <c>Add</c> and <c>Remove</c>.</param>
/// <param name="By">The non-zero amount of <c>Increment</c>.</param>
public readonly record struct FieldOperation(FieldOperationKind Kind, string Field, JsonElement Value = default, long By = 0)
{
    /// <summary>Applies the operation to <paramref name="state"/> at <paramref name="at"/>.</summary>
    /// <exception cref="InvalidOperationException">The field holds a value the operation does not take.</exception>
    public DocumentState Apply(DocumentState state, EntryVersion at)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Kind switch
        {
            FieldOperationKind.Set => state.Set(at, Field, Value),
            FieldOperationKind.Unset => state.Unset(at, Field),
            FieldOperationKind.Increment => state.Increment(at, Field, By),
            FieldOperationKind.Add => state.Add(at, Field, Value),
            FieldOperationKind.Remove => state.Remove(at, Field, Value),
            _ => throw new ArgumentOutOfRangeException(nameof(state), Kind, "unknown field operation"),
        };
    }
}
