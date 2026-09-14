namespace TerraBase;

/// <summary>The operation kinds supported by a partial document update.</summary>
public enum DocumentPatchOperationKind
{
    /// <summary>Sets or replaces a field.</summary>
    Set,

    /// <summary>Removes an existing field or array item.</summary>
    Remove,

    /// <summary>Adds a signed integer delta.</summary>
    Increment,

    /// <summary>Appends one item to an array.</summary>
    Append,
}

/// <summary>One immutable path-based partial update operation.</summary>
public sealed record DocumentPatchOperation
{
    private DocumentPatchOperation(DocumentPatchOperationKind kind, DocumentPath path, DocumentValue? value, long delta)
    {
        Kind = kind;
        Path = path;
        Value = value;
        Delta = delta;
    }

    /// <summary>Creates a set operation.</summary>
    public static DocumentPatchOperation Set(DocumentPath path, DocumentValue value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        return new(DocumentPatchOperationKind.Set, path, value, 0);
    }

    /// <summary>Creates a remove operation.</summary>
    public static DocumentPatchOperation Remove(DocumentPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(DocumentPatchOperationKind.Remove, path, null, 0);
    }

    /// <summary>Creates an increment operation.</summary>
    public static DocumentPatchOperation Increment(DocumentPath path, long delta)
    {
        ArgumentNullException.ThrowIfNull(path);
        return new(DocumentPatchOperationKind.Increment, path, null, delta);
    }

    /// <summary>Creates an array append operation.</summary>
    public static DocumentPatchOperation Append(DocumentPath path, DocumentValue value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(value);
        return new(DocumentPatchOperationKind.Append, path, value, 0);
    }

    /// <summary>The operation kind.</summary>
    public DocumentPatchOperationKind Kind { get; }

    /// <summary>The addressed path.</summary>
    public DocumentPath Path { get; }

    /// <summary>The value for set and append operations.</summary>
    public DocumentValue? Value { get; }

    /// <summary>The delta for increment operations.</summary>
    public long Delta { get; }
}

/// <summary>A bounded, ordered collection of atomic document updates.</summary>
public sealed class DocumentPatch
{
    /// <summary>The maximum operation count in one partial update.</summary>
    public const int MaxOperations = 32;

    private readonly IReadOnlyList<DocumentPatchOperation> operations;

    /// <summary>Creates a validated patch.</summary>
    public DocumentPatch(IEnumerable<DocumentPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);
        var copy = operations.ToArray();
        if (copy.Length > MaxOperations)
        {
            throw new ArgumentException($"A document patch cannot contain more than {MaxOperations} operations.", nameof(operations));
        }

        if (copy.Any(static operation => operation is null))
        {
            throw new ArgumentException("A document patch cannot contain null operations.", nameof(operations));
        }

        this.operations = Array.AsReadOnly(copy);
    }

    /// <summary>Creates an empty patch.</summary>
    public static DocumentPatch Empty { get; } = new([]);

    /// <summary>Creates a patch from ordered operations.</summary>
    public static DocumentPatch Create(params DocumentPatchOperation[] operations) => new(operations);

    /// <summary>The operations in application order.</summary>
    public IReadOnlyList<DocumentPatchOperation> Operations => operations;

    /// <summary>Applies all operations atomically to an immutable source value.</summary>
    public DocumentValue Apply(DocumentValue source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var current = source;
        foreach (var operation in operations)
        {
            current = ApplyOperation(current, operation);
        }

        return current;
    }

    /// <summary>Applies the patch and reports a typed failure instead of throwing.</summary>
    public bool TryApply(DocumentValue source, out DocumentValue? result, out DocumentPatchException? error)
    {
        try
        {
            result = Apply(source);
            error = null;
            return true;
        }
        catch (DocumentPatchException exception)
        {
            result = null;
            error = exception;
            return false;
        }
    }

    private static DocumentValue ApplyOperation(DocumentValue source, DocumentPatchOperation operation)
    {
        try
        {
            return operation.Kind switch
            {
                DocumentPatchOperationKind.Set => SetAt(source, operation.Path.Segments, 0, operation.Value!),
                DocumentPatchOperationKind.Remove => RemoveAt(source, operation.Path.Segments, 0),
                DocumentPatchOperationKind.Increment => IncrementAt(source, operation.Path.Segments, 0, operation.Delta),
                DocumentPatchOperationKind.Append => AppendAt(source, operation.Path.Segments, 0, operation.Value!),
                _ => throw new DocumentPatchException(DocumentPatchErrorKind.InvalidOperation, "The patch operation is not recognized."),
            };
        }
        catch (DocumentPatchException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new DocumentPatchException(DocumentPatchErrorKind.ArithmeticOverflow, exception.Message, exception);
        }
    }

    private static DocumentValue SetAt(
        DocumentValue source,
        IReadOnlyList<DocumentPathSegment> path,
        int position,
        DocumentValue replacement)
    {
        if (position == path.Count)
        {
            return replacement;
        }

        var segment = path[position];
        if (segment.Kind == DocumentPathSegmentKind.Field)
        {
            var fields = AsObject(source);
            var child = fields.GetValueOrDefault(segment.Name) ??
                DocumentValue.Object(Array.Empty<KeyValuePair<string, DocumentValue>>());
            fields[segment.Name] = SetAt(child, path, position + 1, replacement);
            return DocumentValue.Object(fields);
        }

        if (segment.Kind == DocumentPathSegmentKind.Index)
        {
            var values = AsArray(source);
            if (segment.Index >= values.Count)
            {
                throw MissingPath();
            }

            values[segment.Index] = SetAt(values[segment.Index], path, position + 1, replacement);
            return DocumentValue.Array(values);
        }

        if (position != path.Count - 1)
        {
            throw InvalidPath();
        }

        var appended = AsArray(source);
        appended.Add(replacement);
        return DocumentValue.Array(appended);
    }

    private static DocumentValue RemoveAt(DocumentValue source, IReadOnlyList<DocumentPathSegment> path, int position)
    {
        if (position >= path.Count)
        {
            throw new DocumentPatchException(DocumentPatchErrorKind.InvalidPath, "Removing the document root is not supported.");
        }

        var segment = path[position];
        if (segment.Kind == DocumentPathSegmentKind.Field)
        {
            var fields = AsObject(source);
            if (!fields.TryGetValue(segment.Name, out var child))
            {
                throw MissingPath();
            }

            if (position == path.Count - 1)
            {
                fields.Remove(segment.Name);
            }
            else
            {
                fields[segment.Name] = RemoveAt(child, path, position + 1);
            }

            return DocumentValue.Object(fields);
        }

        var values = AsArray(source);
        if (segment.Kind != DocumentPathSegmentKind.Index || segment.Index >= values.Count)
        {
            throw MissingPath();
        }

        if (position == path.Count - 1)
        {
            values.RemoveAt(segment.Index);
        }
        else
        {
            values[segment.Index] = RemoveAt(values[segment.Index], path, position + 1);
        }

        return DocumentValue.Array(values);
    }

    private static DocumentValue IncrementAt(
        DocumentValue source,
        IReadOnlyList<DocumentPathSegment> path,
        int position,
        long delta)
    {
        if (position >= path.Count)
        {
            throw InvalidPath();
        }

        var segment = path[position];
        if (segment.Kind == DocumentPathSegmentKind.Field)
        {
            var fields = AsObject(source);
            if (position == path.Count - 1 && !fields.TryGetValue(segment.Name, out var absent))
            {
                fields[segment.Name] = DocumentValue.Integer(delta);
                return DocumentValue.Object(fields);
            }

            if (!fields.TryGetValue(segment.Name, out var child))
            {
                throw MissingPath();
            }

            fields[segment.Name] = position == path.Count - 1
                ? AddInteger(child, delta)
                : IncrementAt(child, path, position + 1, delta);
            return DocumentValue.Object(fields);
        }

        if (segment.Kind != DocumentPathSegmentKind.Index)
        {
            throw InvalidPath();
        }

        var values = AsArray(source);
        if (segment.Index >= values.Count)
        {
            throw MissingPath();
        }

        values[segment.Index] = position == path.Count - 1
            ? AddInteger(values[segment.Index], delta)
            : IncrementAt(values[segment.Index], path, position + 1, delta);
        return DocumentValue.Array(values);
    }

    private static DocumentValue AppendAt(
        DocumentValue source,
        IReadOnlyList<DocumentPathSegment> path,
        int position,
        DocumentValue value)
    {
        if (position == path.Count)
        {
            if (source.Kind != DocumentValueKind.Array)
            {
                throw TypeMismatch("The append target must be an array.");
            }

            var values = source.ArrayValue.ToList();
            values.Add(value);
            return DocumentValue.Array(values);
        }

        var segment = path[position];
        if (segment.Kind == DocumentPathSegmentKind.Field)
        {
            var fields = AsObject(source);
            if (!fields.TryGetValue(segment.Name, out var child))
            {
                throw MissingPath();
            }

            fields[segment.Name] = AppendAt(child, path, position + 1, value);
            return DocumentValue.Object(fields);
        }

        if (segment.Kind == DocumentPathSegmentKind.Index)
        {
            var values = AsArray(source);
            if (segment.Index >= values.Count)
            {
                throw MissingPath();
            }

            values[segment.Index] = AppendAt(values[segment.Index], path, position + 1, value);
            return DocumentValue.Array(values);
        }

        throw InvalidPath();
    }

    private static DocumentValue AddInteger(DocumentValue current, long delta)
    {
        if (current.Kind != DocumentValueKind.Integer)
        {
            throw TypeMismatch("The increment target must be an integer.");
        }

        return DocumentValue.Integer(checked(current.IntegerValue + delta));
    }

    private static Dictionary<string, DocumentValue> AsObject(DocumentValue value) => value.Kind == DocumentValueKind.Object
        ? value.ObjectValue.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal)
        : throw TypeMismatch("The path requires an object.");

    private static List<DocumentValue> AsArray(DocumentValue value) => value.Kind == DocumentValueKind.Array
        ? value.ArrayValue.ToList()
        : throw TypeMismatch("The path requires an array.");

    private static DocumentPatchException MissingPath() =>
        new(DocumentPatchErrorKind.PathNotFound, "The patch path does not exist.");

    private static DocumentPatchException InvalidPath() =>
        new(DocumentPatchErrorKind.InvalidPath, "The patch path is invalid.");

    private static DocumentPatchException TypeMismatch(string message) =>
        new(DocumentPatchErrorKind.TypeMismatch, message);
}

/// <summary>The reason a partial document update was refused.</summary>
public enum DocumentPatchErrorKind
{
    /// <summary>The path does not exist.</summary>
    PathNotFound,

    /// <summary>A path segment has the wrong shape.</summary>
    InvalidPath,

    /// <summary>A value is not the type required by the operation.</summary>
    TypeMismatch,

    /// <summary>An integer increment overflowed.</summary>
    ArithmeticOverflow,

    /// <summary>The operation kind is invalid.</summary>
    InvalidOperation,
}

/// <summary>Thrown when an atomic partial update cannot be applied.</summary>
public sealed class DocumentPatchException : InvalidOperationException
{
    /// <summary>Creates a patch exception.</summary>
    public DocumentPatchException(DocumentPatchErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>The typed failure kind.</summary>
    public DocumentPatchErrorKind Kind { get; }
}

/// <summary>Convenience entry point for applying an immutable partial update.</summary>
public static class DocumentPartialUpdate
{
    /// <summary>Applies a patch atomically.</summary>
    public static DocumentValue Apply(DocumentValue source, DocumentPatch patch)
    {
        ArgumentNullException.ThrowIfNull(patch);
        return patch.Apply(source);
    }
}
