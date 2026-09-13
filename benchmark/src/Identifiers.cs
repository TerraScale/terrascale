namespace TerraBase;

/// <summary>Identifies an owning customer tenant.</summary>
public readonly record struct TenantId
{
    /// <summary>Creates a tenant identity.</summary>
    /// <param name="value">The non-empty tenant value.</param>
    public TenantId(string value)
    {
        Value = Validate(value, nameof(value), 128);
    }

    /// <summary>The stable tenant value.</summary>
    public string Value { get; }

    /// <summary>Returns the wire representation.</summary>
    public override string ToString() => Value;

    private static string Validate(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength)
        {
            throw new ArgumentException($"{parameterName} exceeds {maximumLength} characters.", parameterName);
        }

        if (value.Any(char.IsControl))
        {
            throw new ArgumentException($"{parameterName} contains a control character.", parameterName);
        }

        return value;
    }
}

/// <summary>Identifies one collection inside a tenant.</summary>
public readonly record struct CollectionId
{
    /// <summary>Creates a collection identity.</summary>
    /// <param name="value">The non-empty collection value.</param>
    public CollectionId(string value)
    {
        Value = Validate(value, nameof(value), 128);
    }

    /// <summary>The stable collection value.</summary>
    public string Value { get; }

    /// <summary>Returns the wire representation.</summary>
    public override string ToString() => Value;

    private static string Validate(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Length > maximumLength || value.Contains('\0'))
        {
            throw new ArgumentException($"{parameterName} is invalid.", parameterName);
        }

        return value;
    }
}

/// <summary>Identifies one document inside a tenant collection.</summary>
public readonly record struct DocumentKey
{
    /// <summary>Creates a document key.</summary>
    /// <param name="tenant">The owning tenant.</param>
    /// <param name="collection">The collection.</param>
    /// <param name="documentId">The document identifier.</param>
    public DocumentKey(TenantId tenant, CollectionId collection, string documentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        if (documentId.Length > 256 || documentId.Any(char.IsControl))
        {
            throw new ArgumentException("The document identifier is invalid.", nameof(documentId));
        }

        Tenant = tenant;
        Collection = collection;
        DocumentId = documentId;
    }

    /// <summary>The tenant owning the document.</summary>
    public TenantId Tenant { get; }

    /// <summary>The document collection.</summary>
    public CollectionId Collection { get; }

    /// <summary>The document identifier.</summary>
    public string DocumentId { get; }
}
