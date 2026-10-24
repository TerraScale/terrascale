using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Memory;
using TerraBase.Tenancy;

namespace TerraBase.Features.Shapes;

/// <summary>
/// A shape's <c>where</c> filter (shape-reads.md section 2, FR-15): the query v1 filter grammar of
/// <c>POST …/query</c>, its canonical bytes for the shape identity, and the predicate a sealer applies to a
/// document materialized at seal time.
/// </summary>
internal sealed class ShapeFilter
{
    /// <summary>Longest <c>where</c>, in UTF-8 bytes: it travels in the 8 KiB request line.</summary>
    public const int MaxWhereBytes = 8 * 1024;

    private const string Code = "invalid_query";
    private const int MaxChildren = 64;

    private static readonly JsonWriterOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private ShapeFilter(string canonical, DocumentFilter? filter, ShapeAccess? access = null, ShapeFilter? userFilter = null)
    {
        Canonical = canonical;
        Filter = filter;
        Access = access;
        _userFilter = userFilter;
    }

    /// <summary>The shape without a filter: every document.</summary>
    public static ShapeFilter All { get; } = new("", null);

    /// <summary>
    /// Compact JSON of the filter, relaxed escaping, itself a valid <c>where</c>. Equivalent spellings share it, so they
    /// share a handle: <c>and</c>/<c>or</c> are flattened, sorted by canonical bytes and deduplicated (regrouped at
    /// 64 children); <c>in</c>/<c>nin</c> values are sorted and deduplicated, and one value is <c>eq</c>/<c>ne</c>;
    /// <c>not</c> of <c>eq</c>/<c>in</c> is <c>ne</c>/<c>nin</c>; a double <c>not</c> cancels; an integral number is
    /// written as an integer (<c>1.0</c> and <c>-0</c> are <c>1</c> and <c>0</c>). Literals keep their JSON type.
    /// Empty for <see cref="All"/>.
    /// </summary>
    public string Canonical { get; }

    /// <summary>The client filter used in continuation URLs, without server authorization identity.</summary>
    public string RequestCanonical => _userFilter?.Canonical ?? Canonical;

    public ShapeAccess? Access { get; }

    // Authorization hashes hide long filters and policies from the key, but their captured objects still need admission.
    public long AuthorizationBytes => Access is not { } access ? 0 : 2048 + 2L * (RequestCanonical.Length
        + access.Identity.Actor.Length + access.Identity.Roles.Sum(role => role.Length) + (access.Rules.Owner?.Length ?? 0)
        + (access.Rules.Read?.Roles?.Sum(role => role.Length) ?? 0) + (access.Rules.Write?.Roles?.Sum(role => role.Length) ?? 0));

    private readonly ShapeFilter? _userFilter;

    internal ShapeFilter WithAccess(ShapeAccess access)
    {
        var user = _userFilter ?? this;
        return new ShapeFilter(access.Canonical(user.Canonical), user.Filter, access, user);
    }

    /// <summary>The parsed filter, as <c>POST …/query</c> runs it; <see langword="null"/> for <see cref="All"/>.</summary>
    public DocumentFilter? Filter { get; }

    /// <summary>
    /// Parses a <c>where</c> parameter; absent is <see cref="All"/>, empty is <c>400 invalid_request</c>, and a bad filter
    /// <c>400 invalid_query</c>.
    /// </summary>
    public static Valid<ShapeFilter> Parse(string? where)
    {
        if (where is null)
        {
            return All;
        }

        if (where.Length == 0)
        {
            return new Validation("where is not empty");
        }

        if (Encoding.UTF8.GetByteCount(where) > MaxWhereBytes)
        {
            return new Validation("where is at most 8 KiB", Code);
        }

        try
        {
            using var input = JsonDocument.Parse(where);
            return QueryParser.CheckFilter(input.RootElement) switch
            {
                Validation invalid => invalid,
                DocumentFilter filter => Canonicalized(filter),
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            // Not JSON, or a string with a lone surrogate escape.
            return new Validation("where is a JSON query filter", Code);
        }
    }

    private static Valid<ShapeFilter> Canonicalized(DocumentFilter filter)
    {
        if (QueryParser.Searches(filter).Any())
        {
            return new Validation("search is supported by document queries, not shape filters", Code);
        }

        if (QueryParser.Leaves(filter).OfType<WithinFilter>().Any())
        {
            return new Validation("within is supported by document queries, not shape filters", Code);
        }

        var canonical = Emit(Normalize(filter)).Bytes;
        if (canonical.Length > MaxWhereBytes)
        {
            return new Validation("canonical where is at most 8 KiB", Code);
        }

        // The canonical form is a redirect's where: it must parse under the same limits.
        using var output = JsonDocument.Parse(canonical);
        return QueryParser.CheckFilter(output.RootElement) switch
        {
            Validation error => error,
            DocumentFilter => new ShapeFilter(Encoding.UTF8.GetString(canonical), filter),
        };
    }

    /// <summary>
    /// Whether the document, as materialized, matches: what <c>POST …/query</c> answers for it. A deleted or hidden document never
    /// matches, so a sealer sends it, or a document that left the filter, as a <c>delete</c>.
    /// </summary>
    public bool Matches(StoredDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Deleted || document.UniqueConflict)
        {
            return false;
        }

        if (Access is { Owner: not null } access && access.Rules.Subject(document.Json) != access.Identity.Actor)
        {
            return false;
        }

        if (Filter is null)
        {
            return true;
        }

        using var json = JsonDocument.Parse(document.Json, RequestJson.StoredDocumentOptions);
        return Match(Filter, document.Id, json.RootElement);
    }

    private static DocumentFilter Normalize(DocumentFilter filter)
    {
        switch (filter)
        {
            case AndFilter or OrFilter:
                var conjunction = filter is AndFilter;
                var children = new List<DocumentFilter>();
                foreach (var child in conjunction ? ((AndFilter)filter).Children : ((OrFilter)filter).Children)
                {
                    var normal = Normalize(child);
                    children.AddRange(normal switch
                    {
                        AndFilter nested when conjunction => nested.Children,
                        OrFilter nested when !conjunction => nested.Children,
                        _ => [normal],
                    });
                }

                var unique = Unique(children, child => Emit(child).Bytes);
                return unique.Count == 1 ? unique[0] : conjunction ? new AndFilter(unique) : new OrFilter(unique);
            case NotFilter not:
                var negated = Normalize(not.Child);
                return negated is NotFilter inner ? inner.Child : new NotFilter(negated);
            case InFilter inside:
                var values = Unique<ScalarValue>([.. inside.Values.Select(Normalize)], value => Write(writer => Value(writer, value)));
                return values.Count == 1 ? new CompareFilter(inside.Path, Comparison.Equal, values[0]) : new InFilter(inside.Path, values);
            case CompareFilter compare:
                return compare with { Value = Normalize(compare.Value) };
            case ContainsFilter contains:
                return contains with { Value = Normalize(contains.Value) };
            default:
                return filter;
        }
    }

    // An integral double within the long range is that long: SQLite compares them as equal numbers.
    private static ScalarValue Normalize(ScalarValue value) =>
        value.Value is double real && real == Math.Floor(real) && real >= -9223372036854775808.0 && real < 9223372036854775808.0
            ? new ScalarValue(ScalarKind.Number, (long)real)
            : value;

    private static List<T> Unique<T>(List<T> items, Func<T, byte[]> key)
    {
        var keyed = items.Select(item => (Item: item, Key: key(item))).ToList();
        keyed.Sort((a, b) => a.Key.AsSpan().SequenceCompareTo(b.Key));
        return [.. keyed.Where((item, i) => i == 0 || !item.Key.AsSpan().SequenceEqual(keyed[i - 1].Key)).Select(item => item.Item)];
    }

    // Canonical JSON of a normalized filter and its nesting depth in filter levels.
    private static (byte[] Bytes, int Depth) Emit(DocumentFilter filter)
    {
        switch (filter)
        {
            case AndFilter or OrFilter:
                var op = filter is AndFilter ? "and" : "or";
                var children = (filter is AndFilter all ? all.Children : ((OrFilter)filter).Children).Select(Emit).ToList();
                return Group(op, children.Count <= MaxChildren ? children : Regroup(op, children));
            case NotFilter { Child: CompareFilter { Op: Comparison.Equal } equal }:
                return Scalar("ne", equal.Path, equal.Value);
            case NotFilter { Child: InFilter inside }:
                return Values("nin", inside);
            case NotFilter not:
                var (childBytes, childDepth) = Emit(not.Child);
                return (Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName("not");
                    writer.WriteRawValue(childBytes, skipInputValidation: true);
                    writer.WriteEndObject();
                }), childDepth + 1);
            case CompareFilter compare:
                return Scalar(compare.Op switch
                {
                    Comparison.Equal => "eq",
                    Comparison.Less => "lt",
                    Comparison.LessOrEqual => "lte",
                    Comparison.Greater => "gt",
                    _ => "gte",
                }, compare.Path, compare.Value);
            case InFilter inside:
                return Values("in", inside);
            case ExistsFilter exists:
                return (Write(writer =>
                {
                    writer.WriteStartObject();
                    writer.WriteString("exists", Pointer(exists.Path));
                    writer.WriteEndObject();
                }), 1);
            case PrefixFilter prefix:
                return Scalar("prefix", prefix.Path, new ScalarValue(ScalarKind.Text, prefix.Prefix));
            case ContainsFilter contains:
                return Scalar("contains", contains.Path, contains.Value);
            default:
                throw new ArgumentOutOfRangeException(nameof(filter), filter.GetType().Name, "unknown filter");
        }
    }

    // More than 64 children: the deepest stay in the root and the rest go into as few groups as the node count of
    // the input already paid for (it nested at least that many groups), so the canonical form stays within the limits.
    private static List<(byte[] Bytes, int Depth)> Regroup(string op, List<(byte[] Bytes, int Depth)> children)
    {
        var groups = (children.Count - MaxChildren + (MaxChildren - 2)) / (MaxChildren - 1);
        var ordered = children.OrderByDescending(child => child.Depth).ThenBy(child => child.Bytes, ByteOrder.Instance).ToList();
        var root = ordered.Take(MaxChildren - groups).ToList();
        root.AddRange(ordered.Skip(MaxChildren - groups).Chunk(MaxChildren).Select(chunk => Group(op, [.. chunk.OrderBy(child => child.Bytes, ByteOrder.Instance)])));
        return [.. root.OrderBy(child => child.Bytes, ByteOrder.Instance)];
    }

    private static (byte[] Bytes, int Depth) Group(string op, List<(byte[] Bytes, int Depth)> children) => (Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray(op);
        children.ForEach(child => writer.WriteRawValue(child.Bytes, skipInputValidation: true));
        writer.WriteEndArray();
        writer.WriteEndObject();
    }), 1 + children.Max(child => child.Depth));

    private static (byte[] Bytes, int Depth) Scalar(string op, DocumentPath path, ScalarValue value) => (Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray(op);
        writer.WriteStringValue(Pointer(path));
        Value(writer, value);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }), 1);

    private static (byte[] Bytes, int Depth) Values(string op, InFilter inside) => (Write(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray(op);
        writer.WriteStringValue(Pointer(inside.Path));
        writer.WriteStartArray();
        foreach (var value in inside.Values)
        {
            Value(writer, value);
        }

        writer.WriteEndArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }), 1);

    private static string Pointer(DocumentPath path) => path.IsId ? "$id"
        : string.Concat(path.Segments!.Select(segment => "/" + segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal)));

    private static void Value(Utf8JsonWriter writer, ScalarValue value)
    {
        switch (value.Value)
        {
            case bool flag:
                writer.WriteBooleanValue(flag);
                break;
            case long integer:
                writer.WriteNumberValue(integer);
                break;
            case double real:
                writer.WriteNumberValue(real);
                break;
            case string text:
                writer.WriteStringValue(text);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static byte[] Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, Compact))
        {
            write(writer);
        }

        return buffer.ToArray();
    }

    // Mirrors TursoQueryCompiler: every predicate is true or false, so NOT is a plain complement. $id is text.
    private static bool Match(DocumentFilter filter, string id, JsonElement document) => filter switch
    {
        AndFilter and => and.Children.All(child => Match(child, id, document)),
        OrFilter or => or.Children.Any(child => Match(child, id, document)),
        NotFilter not => !Match(not.Child, id, document),
        CompareFilter compare when compare.Path.IsId => CompareId(id, compare.Op, compare.Value),
        CompareFilter compare => Lookup(compare.Path, document) is { } value && Compare(value, compare.Op, compare.Value),
        InFilter inside when inside.Path.IsId => inside.Values.Any(item => CompareId(id, Comparison.Equal, item)),
        InFilter inside => Lookup(inside.Path, document) is { } value && inside.Values.Any(item => Compare(value, Comparison.Equal, item)),
        ExistsFilter exists => exists.Path.IsId || Lookup(exists.Path, document) is not null,
        PrefixFilter prefix when prefix.Path.IsId => id.StartsWith(prefix.Prefix, StringComparison.Ordinal),
        PrefixFilter prefix => Lookup(prefix.Path, document) is { ValueKind: JsonValueKind.String } value
            && value.GetString()!.StartsWith(prefix.Prefix, StringComparison.Ordinal),
        ContainsFilter contains => !contains.Path.IsId && Lookup(contains.Path, document) is { ValueKind: JsonValueKind.Array } array
            && array.EnumerateArray().Any(element => Compare(element, Comparison.Equal, contains.Value)),
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter.GetType().Name, "unknown filter"),
    };

    // The value json_extract reads: object members only, the first of duplicate names, as SQLite does.
    private static JsonElement? Lookup(DocumentPath path, JsonElement document)
    {
        var current = document;
        foreach (var segment in path.Segments!)
        {
            if (current.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            JsonElement? found = null;
            foreach (var member in current.EnumerateObject())
            {
                if (member.NameEquals(segment))
                {
                    found = member.Value;
                    break;
                }
            }

            if (found is not { } next)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static bool CompareId(string id, Comparison op, ScalarValue expected) =>
        expected.Kind == ScalarKind.Text && Holds(CompareUtf8(id, (string)expected.Value!), op);

    // Values compare only within their JSON type: booleans as 0 and 1, numbers exactly, strings in UTF-8 byte order.
    private static bool Compare(JsonElement value, Comparison op, ScalarValue expected)
    {
        int order;
        switch (expected.Kind)
        {
            case ScalarKind.Null:
                return value.ValueKind == JsonValueKind.Null;
            case ScalarKind.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                order = value.GetBoolean().CompareTo((bool)expected.Value!);
                break;
            case ScalarKind.Number when value.ValueKind == JsonValueKind.Number:
                order = CompareNumbers(value.TryGetInt64(out var integer) ? (object)integer : value.GetDouble(), expected.Value!);
                break;
            case ScalarKind.Text when value.ValueKind == JsonValueKind.String:
                order = CompareUtf8(value.GetString()!, (string)expected.Value!);
                break;
            default:
                return false;
        }

        return Holds(order, op);
    }

    private static bool Holds(int order, Comparison op) => op switch
    {
        Comparison.Equal => order == 0,
        Comparison.Less => order < 0,
        Comparison.LessOrEqual => order <= 0,
        Comparison.Greater => order > 0,
        _ => order >= 0,
    };

    // SQLite compares an integer with a real exactly, never through a lossy conversion of the integer to double.
    private static int CompareNumbers(object left, object right) => (left, right) switch
    {
        (long a, long b) => a.CompareTo(b),
        (double a, double b) => a.CompareTo(b),
        (long a, double b) => CompareExact(a, b),
        (double a, long b) => -CompareExact(b, a),
        _ => throw new InvalidOperationException("numbers are long or double"),
    };

    private static int CompareExact(long integer, double real)
    {
        // 2^63 is exactly representable; every long is below it and at or above -2^63.
        if (real >= 9223372036854775808.0)
        {
            return -1;
        }

        if (real < -9223372036854775808.0)
        {
            return 1;
        }

        var truncated = Math.Truncate(real);
        var whole = (long)truncated;
        return integer != whole ? integer.CompareTo(whole) : 0.0.CompareTo(real - truncated);
    }

    // UTF-16 ordinal order puts U+E000..U+FFFF after surrogate pairs; UTF-8 bytes (SQLite BINARY) order by code point.
    private static int CompareUtf8(string left, string right)
    {
        var length = Math.Min(left.Length, right.Length);
        for (var i = 0; i < length; i++)
        {
            if (left[i] != right[i])
            {
                return CodePointOrder(left[i]).CompareTo(CodePointOrder(right[i]));
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    private static int CodePointOrder(char unit) => unit < 0xD800 ? unit : unit >= 0xE000 ? unit - 0x800 : unit + 0x2000;

    private sealed class ByteOrder : IComparer<byte[]>
    {
        public static ByteOrder Instance { get; } = new();

        public int Compare(byte[]? x, byte[]? y) => x.AsSpan().SequenceCompareTo(y);
    }
}

/// <summary>One filtered shape of a database: its collection and canonical filter.</summary>
/// <param name="DatabaseId">Database id, unique across tenants.</param>
/// <param name="Collection">Collection name.</param>
/// <param name="CanonicalFilter"><see cref="ShapeFilter.Canonical"/>.</param>
internal sealed record ShapeFilterKey(string DatabaseId, string Collection, string CanonicalFilter);

/// <summary>A filtered shape admitted, with the shapes it evicted; the caller retires their active sealing work.</summary>
/// <remarks>Keep persisted immutable bytes until retention: a later bootstrap may reuse the same deterministic handle.</remarks>
/// <param name="Evicted">Evicted shapes, least recently used first.</param>
internal sealed record ShapeAdmitted(IReadOnlyList<ShapeFilterKey> Evicted);

/// <summary>An admission, or <c>429 shape_busy</c> when the memory budget cannot hold another shape.</summary>
internal readonly union ShapeAdmission(ShapeAdmitted, QuotaExceeded);

/// <summary>
/// The filtered shapes a machine serves: at most 64 per database, least recently used evicted first (FR-15).
/// Unfiltered shapes are not tracked. An evicted shape answers <c>409 must_refetch</c> until a new bootstrap admits it.
/// One database never evicts another's shapes.
/// </summary>
internal interface IShapeFilterRegistry
{
    /// <summary>Admits or touches a shape on bootstrap (<c>offset=-1</c> without a handle).</summary>
    ShapeAdmission Admit(TenantContext database, string collection, ShapeFilter filter);

    /// <summary>
    /// Touches a shape on continuation; an evicted or unknown filtered shape is <c>409 must_refetch</c> and is not admitted.
    /// </summary>
    Conflict? Check(TenantContext database, string collection, ShapeFilter filter);
}

/// <inheritdoc cref="IShapeFilterRegistry" />
internal sealed class ShapeFilterRegistry(IMemoryBudget budget) : IShapeFilterRegistry, IDisposable
{
    /// <summary>Filtered shapes per database.</summary>
    public const int MaxPerDatabase = 64;

    // Bounds every database together, besides the organization's memory share: a full database takes about 1 MiB.
    // shortcut: a fixed process-wide bound refuses new shapes rather than evicting other tenants; size it from the memory budget if it binds.
    private const long MaxBytes = 32L * 1024 * 1024;

    // The entry, its list node, the key record and their dictionary slots, beyond the key strings.
    private const int EntryOverhead = 512;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedList<Entry>> _databases = [with(StringComparer.Ordinal)];
    private readonly Dictionary<ShapeFilterKey, LinkedListNode<Entry>> _entries = [];
    private long _bytes;

    /// <inheritdoc />
    public ShapeAdmission Admit(TenantContext database, string collection, ShapeFilter filter)
    {
        if (Key(database, collection, filter) is not { } key)
        {
            return new ShapeAdmitted([]);
        }

        lock (_gate)
        {
            if (Touch(key))
            {
                return new ShapeAdmitted([]);
            }

            // A full database replaces its own least recently used shape; nothing changes until the reservation holds.
            var shapes = _databases.GetValueOrDefault(key.DatabaseId);
            var victim = shapes is { Count: >= MaxPerDatabase } ? shapes.Last : null;
            var bytes = EntryOverhead + (2L * (key.DatabaseId.Length + key.Collection.Length + key.CanonicalFilter.Length));
            // Never evicts another database's shapes: past the process-wide bound, admission waits for room.
            if (_bytes + bytes - (victim?.Value.Reservation.Bytes ?? 0) > MaxBytes
                || budget.TryReserve(MemoryLane.Foreground, bytes, database) is not { } reservation)
            {
                return new QuotaExceeded("the memory budget cannot hold another filtered shape", "shape_busy");
            }

            List<ShapeFilterKey> evicted = victim is null ? [] : [Evict(victim)];
            if (!_databases.TryGetValue(key.DatabaseId, out shapes))
            {
                _databases[key.DatabaseId] = shapes = new LinkedList<Entry>();
            }

            _entries[key] = shapes.AddFirst(new Entry(key, reservation));
            _bytes += reservation.Bytes;
            return new ShapeAdmitted(evicted);
        }
    }

    /// <inheritdoc />
    public Conflict? Check(TenantContext database, string collection, ShapeFilter filter)
    {
        if (Key(database, collection, filter) is not { } key)
        {
            return null;
        }

        lock (_gate)
        {
            return Touch(key) ? null : new Conflict("the filtered shape was evicted; refetch from offset -1", "must_refetch");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var node in _entries.Values)
            {
                node.Value.Reservation.Dispose();
            }

            _entries.Clear();
            _databases.Clear();
            _bytes = 0;
        }
    }

    private static ShapeFilterKey? Key(TenantContext database, string collection, ShapeFilter filter)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(filter);
        if (database.DatabaseId is not { } id)
        {
            throw new ArgumentException("a shape addresses one database", nameof(database));
        }

        return filter.Canonical.Length == 0 ? null : new ShapeFilterKey(id, collection, filter.Canonical);
    }

    private bool Touch(ShapeFilterKey key)
    {
        if (!_entries.TryGetValue(key, out var node))
        {
            return false;
        }

        var shapes = node.List!;
        shapes.Remove(node);
        shapes.AddFirst(node);
        return true;
    }

    private ShapeFilterKey Evict(LinkedListNode<Entry> node)
    {
        var shapes = node.List!;
        shapes.Remove(node);
        if (shapes.Count == 0)
        {
            _databases.Remove(node.Value.Key.DatabaseId);
        }

        _entries.Remove(node.Value.Key);
        _bytes -= node.Value.Reservation.Bytes;
        node.Value.Reservation.Dispose();
        return node.Value.Key;
    }

    private sealed class Entry(ShapeFilterKey key, IMemoryReservation reservation)
    {
        public ShapeFilterKey Key { get; } = key;

        public IMemoryReservation Reservation { get; } = reservation;
    }
}
