using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Engine.Lattices;

/// <summary>
/// The replicated state of one document (docs/terrabase/multi-region-replication.md section 4): a clear register and a
/// map of top-level fields, each a last-writer-wins base, a PN-counter and an add-wins set. The state is a
/// join-semilattice after normalization, so replicas that receive the same deltas in any order, any number of times,
/// reach the same state, the same materialized bytes and the same digest.
/// </summary>
/// <remarks>
/// Operations take a fresh version <c>at</c> whose stamp is at least <see cref="MaxStamp"/>: the write lane mints
/// <c>max(clock.Tick(), MaxStamp + 1)</c> per operation. Several operations of one request share their stamp and apply
/// in order. The caller checks that the document exists before a PATCH-style operation.
/// </remarks>
public sealed class DocumentState : ILattice<DocumentState>, IEquatable<DocumentState>
{
    private static readonly ImmutableSortedDictionary<string, FieldState> NoFields = ImmutableSortedDictionary.Create<string, FieldState>(Utf8Ordinal.Instance);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>How the state parses the JSON it holds: as deep as a stored document may be.</summary>
    internal static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = DocumentJson.MaxStoredDepth };

    internal DocumentState(ClearEntry? clear, ImmutableSortedDictionary<string, FieldState> fields)
    {
        Clear = clear;
        Fields = fields;
    }

    /// <summary>The empty state: no document.</summary>
    public static DocumentState Bottom { get; } = new(null, NoFields);

    /// <summary>The clear register, absent until a PUT or DELETE reached this state.</summary>
    public ClearEntry? Clear { get; }

    /// <summary>The fields, sorted by the UTF-8 bytes of their names.</summary>
    public ImmutableSortedDictionary<string, FieldState> Fields { get; }

    /// <summary>
    /// Whether the document exists: its clear is live, or a live base or live add is newer than its clear. A write after
    /// a delete therefore brings the document back with only the fields written after the delete.
    /// </summary>
    public bool Exists =>
        Clear is { Deleted: false }
        || Fields.Values.Any(entry =>
            (entry.Base is { Value: not null } live && (Clear is not { } c1 || live.Version > c1.Version))
            || entry.Dots.Any(dot => dot.Value.RemovedAt is null && (Clear is not { } c2 || dot.Key.Version > c2.Version)));

    /// <summary>The newest stamp anywhere in the state, bookkeeping included.</summary>
    public Hlc MaxStamp
    {
        get
        {
            var max = Clear is { } clear ? Math.Max(clear.Version.Stamp.Value, clear.Changed.Value) : 0;
            foreach (var entry in Fields.Values)
            {
                if (entry.Base is { } @base)
                {
                    max = Math.Max(max, Math.Max(@base.Version.Stamp.Value, @base.Changed.Value));
                }

                foreach (var counter in entry.Counters.Values)
                {
                    max = Math.Max(max, Math.Max(counter.Epoch.Stamp.Value, counter.Changed.Value));
                }

                foreach (var (key, dot) in entry.Dots)
                {
                    max = Math.Max(max, Math.Max(Math.Max(key.Version.Stamp.Value, dot.Changed.Value), dot.RemovedAt?.Stamp.Value ?? 0));
                }
            }

            return new(max);
        }
    }

    /// <inheritdoc />
    public DocumentState Join(DocumentState other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var fields = Fields.ToBuilder();
        foreach (var (name, theirs) in other.Fields)
        {
            fields[name] = fields.TryGetValue(name, out var mine) ? mine.Join(theirs) : theirs;
        }

        return new DocumentState(ClearEntry.Join(Clear, other.Clear), fields.ToImmutable()).Normalize();
    }

    /// <summary>PUT: a live clear at <paramref name="at"/> and a base for every top-level key of <paramref name="body"/>.</summary>
    public DocumentState Put(EntryVersion at, JsonElement body)
    {
        Fresh(at);
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("a document is a JSON object", nameof(body));
        }

        var fields = NoFields.ToBuilder();
        foreach (var property in body.EnumerateObject())
        {
            fields[property.Name] = new FieldState(new BaseEntry(at, Canonical(property.Value), at.Stamp), FieldState.NoCounters, FieldState.NoDots);
        }

        return new DocumentState(new ClearEntry(at, false, at.Stamp), fields.ToImmutable());
    }

    /// <summary>DELETE: a deleted clear at <paramref name="at"/>.</summary>
    public DocumentState Delete(EntryVersion at)
    {
        Fresh(at);
        return new DocumentState(new ClearEntry(at, true, at.Stamp), NoFields);
    }

    /// <summary>Sets <paramref name="field"/> to <paramref name="value"/> (merge-patch key, ops <c>set</c> and <c>append</c>).</summary>
    public DocumentState Set(EntryVersion at, string field, JsonElement value) => Write(at, field, Canonical(value));

    /// <summary>Removes <paramref name="field"/> (merge-patch null, ops <c>unset</c>): a tombstone base.</summary>
    public DocumentState Unset(EntryVersion at, string field) => Write(at, field, null);

    /// <summary>Adds <paramref name="by"/> to the integer <paramref name="field"/> through this node's counter entry.</summary>
    /// <exception cref="InvalidOperationException">The field holds something other than an integer.</exception>
    public DocumentState Increment(EntryVersion at, string field, long by)
    {
        Fresh(at);
        ArgumentOutOfRangeException.ThrowIfZero(by);
        var current = Fields.GetValueOrDefault(field);
        var value = current?.Value(Clear?.Version) ?? default;
        if (value.Kind == FieldKind.Set || (value.Kind == FieldKind.Base && !IsInteger(value.Json!)))
        {
            throw new InvalidOperationException("only an absent or integer field can be incremented");
        }

        var epoch = FieldState.Epoch(current?.Base, Clear?.Version);
        var counters = current?.Counters ?? FieldState.NoCounters;
        var own = counters.TryGetValue(at.Node, out var entry) && entry.Epoch == epoch ? entry : new CounterEntry(epoch, 0, 0, at.Stamp);
        own = by > 0
            ? own with { Increments = checked(own.Increments + (ulong)by), Changed = at.Stamp }
            : own with { Decrements = checked(own.Decrements + (ulong)-(Int128)by), Changed = at.Stamp };
        return With(field, new FieldState(current?.Base, counters.SetItem(at.Node, own), current?.Dots ?? FieldState.NoDots));
    }

    /// <summary>Adds <paramref name="element"/> to the set <paramref name="field"/>: a new dot at <paramref name="at"/>.</summary>
    /// <exception cref="InvalidOperationException">The field holds something other than an array or a set.</exception>
    public DocumentState Add(EntryVersion at, string field, JsonElement element)
    {
        var current = SetField(at, field);
        return With(field, new FieldState(current?.Base, current?.Counters ?? FieldState.NoCounters,
            (current?.Dots ?? FieldState.NoDots).SetItem(new DotKey(Canonical(element), at), new DotEntry(null, at.Stamp))));
    }

    /// <summary>Removes <paramref name="element"/> from the set <paramref name="field"/>: every add of it this state holds.</summary>
    /// <exception cref="InvalidOperationException">The field holds something other than an array or a set.</exception>
    public DocumentState Remove(EntryVersion at, string field, JsonElement element)
    {
        if (SetField(at, field) is not { } current)
        {
            return this;
        }

        var item = Canonical(element);
        var removed = new DotEntry(at, at.Stamp);
        var dots = current.Dots.ToBuilder();
        foreach (var (key, dot) in current.Dots)
        {
            if (dot.RemovedAt is null && string.Equals(key.Element, item, StringComparison.Ordinal))
            {
                dots[key] = removed;
            }
        }

        // An element of an array base is an implicit add at the base's version: its remove becomes an explicit entry.
        if (current.Base is { } @base && current.BaseElements().Contains(item, StringComparer.Ordinal))
        {
            dots.TryAdd(new DotKey(item, @base.Version), removed);
        }

        return With(field, new FieldState(current.Base, current.Counters, dots.ToImmutable()));
    }

    /// <summary>
    /// The delta of the operation stamped <paramref name="stamp"/>: every entry it wrote. Joined into any replica, it
    /// carries that operation's effect.
    /// </summary>
    public DocumentState DeltaAt(Hlc stamp)
    {
        var fields = NoFields.ToBuilder();
        foreach (var (name, field) in Fields)
        {
            var @base = field.Base is { } b && b.Changed == stamp ? field.Base : null;
            var counters = field.Counters.RemoveRange(field.Counters.Where(entry => entry.Value.Changed != stamp).Select(entry => entry.Key));
            var dots = field.Dots.RemoveRange(field.Dots.Where(entry => entry.Value.Changed != stamp).Select(entry => entry.Key));
            if (@base is not null || !counters.IsEmpty || !dots.IsEmpty)
            {
                fields[name] = new FieldState(@base, counters, dots);
            }
        }

        return new DocumentState(Clear is { } clear && clear.Changed == stamp ? Clear : null, fields.ToImmutable());
    }

    /// <summary>
    /// The document as compact JSON, or null when it does not exist. Fields are sorted by the UTF-8 bytes of their names,
    /// counters are integers and sets are arrays sorted by the UTF-8 bytes of their elements, so equal states give equal
    /// bytes on every node.
    /// </summary>
    public byte[]? Materialize()
    {
        if (!Exists)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            foreach (var (name, field) in Fields)
            {
                var value = field.Value(Clear?.Version);
                switch (value.Kind)
                {
                    case FieldKind.Base:
                        writer.WritePropertyName(name);
                        writer.WriteRawValue(value.Json!, skipInputValidation: true);
                        break;
                    case FieldKind.Counter:
                        writer.WriteNumber(name, value.Count);
                        break;
                    case FieldKind.Set:
                        writer.WriteStartArray(name);
                        foreach (var element in value.Elements!)
                        {
                            writer.WriteRawValue(element, skipInputValidation: true);
                        }

                        writer.WriteEndArray();
                        break;
                    case FieldKind.None:
                        break;
                }
            }

            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    /// <inheritdoc />
    public bool Equals(DocumentState? other) =>
        other is not null && Nullable.Equals(Clear, other.Clear) && Fields.Count == other.Fields.Count
        && Fields.Zip(other.Fields).All(pair => string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) && pair.First.Value.Equals(pair.Second.Value));

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as DocumentState);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(Clear, Fields.Count);

    /// <summary>A JSON value as the state holds it: compact, re-serialized once with relaxed escaping.</summary>
    internal static string Canonical(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    internal DocumentState Normalize()
    {
        var fields = Fields.ToBuilder();
        foreach (var (name, field) in Fields)
        {
            var normal = field.Normalize(Clear?.Version);
            if (normal is null)
            {
                fields.Remove(name);
            }
            else if (!ReferenceEquals(normal, field))
            {
                fields[name] = normal;
            }
        }

        return new DocumentState(Clear, fields.ToImmutable());
    }

    private static bool IsInteger(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions);
        return document.RootElement.ValueKind == JsonValueKind.Number && document.RootElement.TryGetInt64(out _);
    }

    // The field written by a set operation, after checking that it can hold a set.
    private FieldState? SetField(EntryVersion at, string field)
    {
        Fresh(at);
        var current = Fields.GetValueOrDefault(field);
        var value = current?.Value(Clear?.Version) ?? default;
        if (value.Kind == FieldKind.Counter || (value.Kind == FieldKind.Base && value.Json is not ['[', ..]))
        {
            throw new InvalidOperationException("only an absent, array or set field takes set operations");
        }

        return current;
    }

    // A new base replaces the whole field: older entries fall below its epoch, and entries an earlier operation of the
    // same request wrote at this version go too.
    private DocumentState Write(EntryVersion at, string field, string? value)
    {
        Fresh(at);
        return With(field, new FieldState(new BaseEntry(at, value, at.Stamp), FieldState.NoCounters, FieldState.NoDots));
    }

    private DocumentState With(string field, FieldState state) => new DocumentState(Clear, Fields.SetItem(field, state)).Normalize();

    private void Fresh(EntryVersion at)
    {
        if (at.Stamp.Value < MaxStamp.Value)
        {
            throw new ArgumentOutOfRangeException(nameof(at), "an operation needs a stamp no older than the state's newest");
        }
    }
}
