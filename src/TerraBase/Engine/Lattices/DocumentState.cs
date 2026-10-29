using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Primitives;

namespace TerraBase.Engine.Lattices;

public sealed class DocumentState : IEquatable<DocumentState>
{
    private static readonly ImmutableSortedDictionary<string, FieldState> NoFields = ImmutableSortedDictionary.Create<string, FieldState>(Utf8Ordinal.Instance);

    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = DocumentJson.MaxStoredDepth };

    internal DocumentState(ClearEntry? clear, ImmutableSortedDictionary<string, FieldState> fields)
    {
        Clear = clear;
        Fields = fields;
    }

    public static DocumentState Bottom { get; } = new(null, NoFields);

    public ClearEntry? Clear { get; }

    public ImmutableSortedDictionary<string, FieldState> Fields { get; }

    public bool Exists =>
    Clear is { Deleted: false }
    || Fields.Values.Any(entry =>
        (entry.Base is { Value: not null } live && (Clear is not { } c1 || live.Version > c1.Version))
        || entry.Dots.Any(dot => dot.Value.RemovedAt is null && (Clear is not { } c2 || dot.Key.Version > c2.Version)));

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

    internal DocumentState ClearThrough(ClearEntry clear) =>
        new DocumentState(Clear is { } current && (current.Version, current.Deleted).CompareTo((clear.Version, clear.Deleted)) >= 0
            ? current : clear, Fields).Normalize();

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

    public DocumentState Delete(EntryVersion at)
    {
        Fresh(at);
        return new DocumentState(new ClearEntry(at, true, at.Stamp), NoFields);
    }

    public DocumentState Set(EntryVersion at, string field, JsonElement value) => Write(at, field, Canonical(value));

    public DocumentState Unset(EntryVersion at, string field) => Write(at, field, null);

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

    public DocumentState Add(EntryVersion at, string field, JsonElement element)
    {
        var current = SetField(at, field);
        return With(field, new FieldState(current?.Base, current?.Counters ?? FieldState.NoCounters,
            (current?.Dots ?? FieldState.NoDots).SetItem(new DotKey(Canonical(element), at), new DotEntry(null, at.Stamp))));
    }

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

        if (current.Base is { } @base && current.BaseElements().Contains(item, StringComparer.Ordinal))
        {
            dots.TryAdd(new DotKey(item, @base.Version), removed);
        }

        return With(field, new FieldState(current.Base, current.Counters, dots.ToImmutable()));
    }

    private byte[]? _materialized;

    public byte[]? Materialize() => Exists ? _materialized ??= Build() : null;

    private byte[] Build()
    {
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

    public bool Equals(DocumentState? other) =>
    other is not null && Nullable.Equals(Clear, other.Clear) && Fields.Count == other.Fields.Count
    && Fields.Zip(other.Fields).All(pair => string.Equals(pair.First.Key, pair.Second.Key, StringComparison.Ordinal) && pair.First.Value.Equals(pair.Second.Value));

    public override bool Equals(object? obj) => Equals(obj as DocumentState);

    public override int GetHashCode() => HashCode.Combine(Clear, Fields.Count);

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
