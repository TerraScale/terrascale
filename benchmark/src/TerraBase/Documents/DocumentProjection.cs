namespace TerraBase;

/// <summary>The kind of one path segment inside a nested document.</summary>
public enum DocumentPathSegmentKind
{
    /// <summary>An object field name.</summary>
    Field,

    /// <summary>An array index.</summary>
    Index,

    /// <summary>The position immediately after an array's last item.</summary>
    Append,
}

/// <summary>One typed segment of a nested document path.</summary>
public readonly record struct DocumentPathSegment
{
    private readonly string? name;
    private readonly int index;

    private DocumentPathSegment(DocumentPathSegmentKind kind, string? name, int index)
    {
        Kind = kind;
        this.name = name;
        this.index = index;
    }

    /// <summary>Creates a field segment.</summary>
    public static DocumentPathSegment Field(string name)
    {
        ValidateField(name);
        return new(DocumentPathSegmentKind.Field, name, 0);
    }

    /// <summary>Creates an array index segment.</summary>
    public static DocumentPathSegment AtIndex(int index) => index < 0
        ? throw new ArgumentOutOfRangeException(nameof(index))
        : new(DocumentPathSegmentKind.Index, null, index);

    /// <summary>Creates an array index segment.</summary>
    public static DocumentPathSegment FromIndex(int index) => AtIndex(index);

    /// <summary>Creates an array append segment.</summary>
    public static DocumentPathSegment Append() => new(DocumentPathSegmentKind.Append, null, 0);

    /// <summary>The segment kind.</summary>
    public DocumentPathSegmentKind Kind { get; }

    /// <summary>The field name for field segments.</summary>
    public string Name => name ?? throw new InvalidOperationException("This path segment is not a field.");

    /// <summary>The array index for index segments.</summary>
    public int Index => index;

    private static void ValidateField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Length == 0 || name.Any(char.IsControl) || System.Text.Encoding.UTF8.GetByteCount(name) > 255)
        {
            throw new ArgumentException("Path field names must be non-empty, valid UTF-8 text of at most 255 bytes.", nameof(name));
        }
    }
}

/// <summary>A validated path through nested object fields and arrays.</summary>
public sealed class DocumentPath : IEquatable<DocumentPath>
{
    private readonly IReadOnlyList<DocumentPathSegment> segments;

    private DocumentPath(IReadOnlyList<DocumentPathSegment> segments)
    {
        this.segments = segments;
    }

    /// <summary>The root path.</summary>
    public static DocumentPath Root { get; } = new(Array.Empty<DocumentPathSegment>());

    /// <summary>Creates a path from typed segments.</summary>
    public static DocumentPath FromSegments(IEnumerable<DocumentPathSegment> segments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var copy = segments.ToArray();
        if (copy.Length > 64)
        {
            throw new ArgumentException("Document paths cannot exceed 64 segments.", nameof(segments));
        }

        for (var index = 0; index < copy.Length; index++)
        {
            if (copy[index].Kind == DocumentPathSegmentKind.Append && index != copy.Length - 1)
            {
                throw new ArgumentException("An append segment must be the final path segment.", nameof(segments));
            }
        }

        return new(copy);
    }

    /// <summary>Creates a path from dot-separated field names.</summary>
    public static DocumentPath Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            return Root;
        }

        return FromSegments(path.Split('.', StringSplitOptions.None).Select(DocumentPathSegment.Field));
    }

    /// <summary>The path segments in traversal order.</summary>
    public IReadOnlyList<DocumentPathSegment> Segments => segments;

    /// <summary>Whether this path is the document root.</summary>
    public bool IsRoot => segments.Count == 0;

    /// <inheritdoc />
    public bool Equals(DocumentPath? other) => other is not null && segments.SequenceEqual(other.segments);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DocumentPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => segments.Aggregate(17, static (hash, segment) => HashCode.Combine(hash, segment));

    /// <inheritdoc />
    public override string ToString() => string.Join('.', segments.Select(static segment =>
        segment.Kind switch
        {
            DocumentPathSegmentKind.Field => segment.Name,
            DocumentPathSegmentKind.Index => $"[{segment.Index}]",
            DocumentPathSegmentKind.Append => "[]",
            _ => throw new InvalidOperationException(),
        }));
}

/// <summary>A field projection over a nested document value.</summary>
public sealed class DocumentProjection
{
    private readonly IReadOnlyList<DocumentPath> paths;

    private DocumentProjection(IReadOnlyList<DocumentPath> paths)
    {
        this.paths = paths;
    }

    /// <summary>Returns a projection containing the complete document.</summary>
    public static DocumentProjection WholeDocument { get; } = new(Array.Empty<DocumentPath>());

    /// <summary>Creates a projection from nested paths.</summary>
    public static DocumentProjection Fields(params string[] paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return FromPaths(paths.Select(DocumentPath.Parse));
    }

    /// <summary>Creates a projection from typed paths.</summary>
    public static DocumentProjection FromPaths(IEnumerable<DocumentPath> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var copy = paths.ToArray();
        if (copy.Any(static path => path.IsRoot))
        {
            throw new ArgumentException("A field projection cannot include the root path.", nameof(paths));
        }

        var duplicate = copy.GroupBy(static path => path.ToString(), StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException($"The projection path '{duplicate.Key}' is duplicated.", nameof(paths));
        }

        return new(copy);
    }

    /// <summary>The selected paths in declaration order.</summary>
    public IReadOnlyList<DocumentPath> Paths => paths;

    /// <summary>Whether the complete source document is selected.</summary>
    public bool IsWholeDocument => paths.Count == 0;

    /// <summary>Applies this projection without mutating the source value.</summary>
    public DocumentValue Apply(DocumentValue source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (IsWholeDocument)
        {
            return source;
        }

        var result = DocumentValue.Object(Array.Empty<KeyValuePair<string, DocumentValue>>());
        foreach (var path in paths)
        {
            result = SelectPath(source, result, path.Segments, 0);
        }

        return result;
    }

    private static DocumentValue SelectPath(
        DocumentValue source,
        DocumentValue destination,
        IReadOnlyList<DocumentPathSegment> segments,
        int position)
    {
        if (position == segments.Count)
        {
            return source;
        }

        var segment = segments[position];
        if (segment.Kind == DocumentPathSegmentKind.Field)
        {
            if (source.Kind != DocumentValueKind.Object || !source.ObjectValue.TryGetValue(segment.Name, out var child))
            {
                return destination;
            }

            var childDestination = destination.Kind == DocumentValueKind.Object &&
                destination.ObjectValue.TryGetValue(segment.Name, out var selected)
                ? selected
                : DocumentValue.Object(Array.Empty<KeyValuePair<string, DocumentValue>>());
            var projected = SelectPath(child, childDestination, segments, position + 1);
            return PutField(destination, segment.Name, projected);
        }

        if (segment.Kind == DocumentPathSegmentKind.Index && source.Kind == DocumentValueKind.Array &&
            segment.Index < source.ArrayValue.Count)
        {
            var child = source.ArrayValue[segment.Index];
            var childDestination = destination.Kind == DocumentValueKind.Array && segment.Index < destination.ArrayValue.Count
                ? destination.ArrayValue[segment.Index]
                : child;
            var projected = SelectPath(child, childDestination, segments, position + 1);
            return PutIndex(destination, segment.Index, projected);
        }

        return destination;
    }

    private static DocumentValue PutField(DocumentValue destination, string name, DocumentValue value)
    {
        var fields = destination.Kind == DocumentValueKind.Object
            ? destination.ObjectValue.ToDictionary(StringComparer.Ordinal)
            : new Dictionary<string, DocumentValue>(StringComparer.Ordinal);
        fields[name] = value;
        return DocumentValue.Object(fields);
    }

    private static DocumentValue PutIndex(DocumentValue destination, int index, DocumentValue value)
    {
        var values = destination.Kind == DocumentValueKind.Array
            ? destination.ArrayValue.ToList()
            : [];
        while (values.Count <= index)
        {
            values.Add(DocumentValue.Null);
        }

        values[index] = value;
        return DocumentValue.Array(values);
    }
}
